
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Diagnostics;
using System.Threading.Tasks;
using TokenMonitor.ProxyConsole;

var builder = WebApplication.CreateBuilder(args);

// Настраиваем Kestrel на порт 8181
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(8181);
});

var app = builder.Build();

// === ПАРСИНГ ВНЕШНЕГО ПРОКСИ ===
// URL прокси берётся из конфигурации (ключ Proxy:Url):
//   - user-secrets:  dotnet user-secrets set "Proxy:Url" "http://user:pass@host:port"
//   - переменная окружения: Proxy__Url
//   - appsettings.json (не коммитить с реальными данными!)
var proxyRawString = builder.Configuration["Proxy:Url"];
ProxySettings? upstreamProxy = null;

if (!string.IsNullOrWhiteSpace(proxyRawString))
{
    try
    {
        upstreamProxy = new ProxySettings(proxyRawString!);
        Console.WriteLine($"Chain Proxy: {upstreamProxy.Scheme}://{upstreamProxy.Host}:{upstreamProxy.Port}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Proxy] Некорректный Proxy:Url, upstream-прокси отключён: {ex.Message}");
    }
}

// Сообщаем о сконфигурированной цепочке при старте
if (upstreamProxy != null)
    Console.WriteLine($"Chain Proxy: {upstreamProxy.Scheme}://{upstreamProxy.Host}:{upstreamProxy.Port} (настройки из: {DescribeProxySource()})");
else
    Console.WriteLine("Chain Proxy: не задан — прямое подключение без промежуточного прокси");

// Настраиваем HttpClient для работы через внешний прокси (для обычных HTTP запросов).
// Если upstream-прокси не задан/невалиден или это socks5 — клиент идёт напрямую.
using var forwarderClient = upstreamProxy?.WebProxyObject != null
    ? new HttpClient(new HttpClientHandler
    {
        Proxy = upstreamProxy.WebProxyObject,
        UseProxy = true
    })
    : new HttpClient();

// === Hop-by-hop заголовки: не должны пересылаться прокси дальше ===
var hopByHopHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
    "Proxy-Connection", "TE", "Trailer", "Transfer-Encoding", "Upgrade"
};

app.MapFallback(async (HttpContext context) =>
{
    // === ОБРАБОТКА HTTPS ТУННЕЛИРОВАНИЯ (МЕТОД CONNECT) ===
    if (HttpMethods.IsConnect(context.Request.Method))
    {
        var targetHostWithPort = context.Request.Path.Value?.TrimStart('/');
        if (string.IsNullOrEmpty(targetHostWithPort) && context.Request.Host.HasValue)
        {
            targetHostWithPort = context.Request.Host.Value;
        }

        if (string.IsNullOrEmpty(targetHostWithPort))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Bad Request: Missing target host.");
            return;
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\n[Proxy] Создание HTTPS туннеля к: {targetHostWithPort}");
        Console.ResetColor();

        // Разбиваем хост и порт
        var parts = targetHostWithPort.Split(':');
        var host = parts[0];
        var port = parts.Length > 1 ? int.Parse(parts[1]) : 443;

        try
        {
            // Kestrel не предоставляет IConnectionResponseBodyFeature, поэтому
            // получаем сырой Socket клиента и работаем с ним напрямую
            var socket = context.Features.Get<IConnectionSocketFeature>()?.Socket;
            if (socket == null)
            {
                throw new NotSupportedException("Kestrel не предоставляет доступ к IConnectionSocketFeature.");
            }

            // Подключаемся к цели напрямую или через upstream-прокси (CONNECT-цепочка)
            var (targetClient, targetStream) = await OpenTunnelAsync(host, port);
            using (targetClient)
            using (targetStream)

            // Отправляем клиенту (IDE) подтверждение успешного создания туннеля
            context.Response.StatusCode = 200;
            await context.Response.Body.FlushAsync();

            // Получаем доступ к сырому TCP-потоку сокета клиента
            using var clientStream = new NetworkStream(socket, ownsSocket: false);

            // Запускаем двусторонний обмен байтами
            var clientToTarget = clientStream.CopyToAsync(targetStream);
            var targetToClient = targetStream.CopyToAsync(clientStream);

            // Ожидаем завершения передачи с любой стороны, затем корректно
            // прокидываем shutdown в противоположном направлении
            var firstDone = await Task.WhenAny(clientToTarget, targetToClient);
            if (firstDone == clientToTarget)
            {
                try { targetClient.Client.Shutdown(SocketShutdown.Send); } catch { }
            }
            else
            {
                try { socket.Shutdown(SocketShutdown.Send); } catch { }
            }
            await Task.WhenAny(clientToTarget, targetToClient);

            // Закрываем сокет клиента: туннель завершён, Kestrel больше не нужен
            try { socket.Disconnect(true); } catch { }
            try { socket.Close(); } catch { }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[Proxy] Туннель {targetHostWithPort} успешно закрыт.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Proxy Error] Ошибка туннелирования для {targetHostWithPort}: {ex.Message}");
            Console.ResetColor();

            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 502;
                await context.Response.WriteAsync($"Proxy Tunnel Error: {ex.Message}");
            }
        }
        return;
    }

    // === ОБРАБОТКА ОБЫЧНЫХ HTTP ЗАПРОСОВ (ДЛЯ ПОДСЧЕТА ТОКЕНОВ) ===
    context.Request.EnableBuffering();
    using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
    var requestBody = await reader.ReadToEndAsync();
    context.Request.Body.Position = 0;

    if (!string.IsNullOrEmpty(requestBody))
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n[TokenMonitor] Перехвачен HTTP запрос! Длина: {requestBody.Length} симв.");
        Console.ResetColor();
    }

    var targetUrl = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}{context.Request.QueryString}";
    var forwardMessage = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUrl);

    // Заголовки, перечисленные в Connection, тоже hop-by-hop
    var connectionTokens = context.Request.Headers.Connection
        .SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    foreach (var header in context.Request.Headers)
    {
        if (IsHopByHopHeader(header.Key) || connectionTokens.Contains(header.Key))
            continue;

        if (!forwardMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()) && forwardMessage.Content != null)
        {
            forwardMessage.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
    }

    if (context.Request.ContentLength > 0)
    {
        forwardMessage.Content = new StreamContent(context.Request.Body);
    }

    try
    {
        var responseMessage = await forwarderClient.SendAsync(forwardMessage, HttpCompletionOption.ResponseHeadersRead);
        context.Response.StatusCode = (int)responseMessage.StatusCode;

        var responseConnectionTokens = responseMessage.Headers.Connection
            .SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var header in responseMessage.Headers)
        {
            if (IsHopByHopHeader(header.Key) || responseConnectionTokens.Contains(header.Key))
                continue;
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }
        foreach (var header in responseMessage.Content.Headers)
        {
            if (IsHopByHopHeader(header.Key) || responseConnectionTokens.Contains(header.Key))
                continue;
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        await responseMessage.Content.CopyToAsync(context.Response.Body);
    }
    catch (Exception ex)
    {
        context.Response.StatusCode = 502;
        await context.Response.WriteAsync($"Proxy error: {ex.Message}");
    }
});

Console.WriteLine("TokenMonitor.Proxy запущен на http://localhost:8181");

// Проверяем заранее, свободен ли порт, чтобы выдать понятное сообщение с PID владельца
var portOwner = FindPortOwner(8181);
if (portOwner != null)
{
    Console.WriteLine($"[Proxy] Порт 8181 уже занят процессом '{portOwner.Value.Name}' (PID {portOwner.Value.Pid}).");
    Console.WriteLine($"Освободите порт, например: Stop-Process -Id {portOwner.Value.Pid} — и запустите снова.");
    return;
}

try
{
    await app.RunAsync();
}
catch (Exception ex) when (IsAddressInUse(ex))
{
    // Запасной вариант: порт успели занять между проверкой и bind
    var owner = FindPortOwner(8181);
    if (owner != null)
        Console.WriteLine($"[Proxy] Порт 8181 уже занят процессом '{owner.Value.Name}' (PID {owner.Value.Pid}).");
    else
        Console.WriteLine("[Proxy] Порт 8181 уже занят другой программой.");
}

static bool IsAddressInUse(Exception ex)
{
    for (var e = ex; e != null; e = e.InnerException)
    {
        if (e is SocketException se && (se.ErrorCode == 10048 || se.SocketErrorCode == SocketError.AddressAlreadyInUse))
            return true;
        if (e.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
            return true;
    }
    return false;
}

// Ищет процесс, слушающий указанный порт (через netstat -ano)
static (int Pid, string Name)? FindPortOwner(int port)
{
    try
    {
        var psi = new ProcessStartInfo("netstat", "-ano -p tcp")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc == null) return null;

        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(5000);

        foreach (var line in output.Split('\n'))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // Формат: Proto  LocalAddress  ForeignAddress  State  PID
            if (parts.Length >= 5 &&
                parts[^2] == "LISTENING" &&
                parts[1].EndsWith($":{port}", StringComparison.Ordinal) &&
                int.TryParse(parts[^1], out var pid) && pid > 0)
            {
                string name;
                try { name = Process.GetProcessById(pid).ProcessName; }
                catch { name = "unknown"; }
                return (pid, name);
            }
        }
    }
    catch { }
    return null;
}

// Определяет, откуда взята настройка Proxy:Url
string DescribeProxySource()
{
    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("Proxy__Url")))
        return "переменная окружения Proxy__Url";

    var secretsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "UserSecrets", "tokenmonitor-proxyconsole", "secrets.json");
    if (File.Exists(secretsPath) && File.ReadAllText(secretsPath).Contains("Proxy:Url"))
        return $"User Secrets: {secretsPath}";

    var appSettingsPath = Path.Combine(builder.Environment.ContentRootPath, "appsettings.json");
    if (File.Exists(appSettingsPath) &&
        File.ReadAllText(appSettingsPath).Contains("\"Proxy\"") &&
        File.ReadAllText(appSettingsPath).Contains("\"Url\""))
        return $"файл: {appSettingsPath}";

    return "конфигурация (источник не определён)";
}

bool IsHopByHopHeader(string name) => hopByHopHeaders.Contains(name);

// === Установление TCP-туннеля к host:port — напрямую или через upstream-прокси ===
// Поддерживаются схемы upstream: http, https, socks5/socks5h
async Task<(TcpClient Client, Stream Stream)> OpenTunnelAsync(string host, int port)
{
    var client = new TcpClient();
    try
    {
        using var setupCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        if (upstreamProxy == null)
        {
            await client.ConnectAsync(host, port, setupCts.Token);
            return (client, client.GetStream());
        }

        // Коннектимся к upstream-прокси
        await client.ConnectAsync(upstreamProxy.Host, upstreamProxy.Port, setupCts.Token);
        Stream stream = client.GetStream();

        // HTTPS-прокси: оборачиваем соединение в TLS до начала handshake прокси
        if (upstreamProxy.Scheme == "https")
        {
            var sslStream = new SslStream(stream, leaveInnerStreamOpen: false);
            await sslStream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = upstreamProxy.Host },
                setupCts.Token);
            stream = sslStream;
        }

        if (upstreamProxy.Scheme == "socks5")
            await Socks5ConnectAsync(stream, host, port, upstreamProxy.Credentials, setupCts.Token);
        else
            await HttpConnectAsync(stream, host, port, upstreamProxy.Credentials, setupCts.Token);

        return (client, stream);
    }
    catch
    {
        client.Dispose();
        throw;
    }
}

// HTTP/HTTPS-прокси: CONNECT host:port + Proxy-Authorization
async Task HttpConnectAsync(Stream stream, string host, int port, NetworkCredential? credentials, CancellationToken ct)
{
    var auth = credentials != null
        ? $"Proxy-Authorization: Basic {Convert.ToBase64String(Encoding.ASCII.GetBytes($"{credentials.UserName}:{credentials.Password}"))}\r\n"
        : string.Empty;

    var connectRequest = $"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n{auth}\r\n";
    await stream.WriteAsync(Encoding.ASCII.GetBytes(connectRequest), ct);

    // Читаем заголовки ответа прокси побайтово, чтобы не захватить
    // лишние байты, принадлежащие уже установленному туннелю
    var responseHead = await ReadHeadersAsync(stream, ct);
    var statusLine = responseHead.Split('\n')[0].Trim();
    if (!statusLine.Contains(" 200"))
        throw new IOException($"Upstream-прокси отклонил CONNECT: {statusLine}");
}

// SOCKS5: greeting -> auth -> CONNECT (доменная форма, DNS резолвит прокси)
async Task Socks5ConnectAsync(Stream stream, string host, int port, NetworkCredential? credentials, CancellationToken ct)
{
    var hasAuth = credentials != null;
    var greeting = hasAuth ? new byte[] { 0x05, 0x02, 0x00, 0x02 } : new byte[] { 0x05, 0x01, 0x00 };
    await stream.WriteAsync(greeting, ct);

    var methodReply = await ReadExactAsync(stream, 2, ct);
    if (methodReply[0] != 0x05)
        throw new IOException("SOCKS5: некорректный ответ на greeting.");

    if (methodReply[1] == 0xFF)
        throw new IOException("SOCKS5: прокси не поддерживает предложенные методы авторизации.");

    if (methodReply[1] == 0x02)
    {
        if (!hasAuth)
            throw new IOException("SOCKS5: прокси требует логин/пароль, но они не заданы.");

        var userBytes = Encoding.UTF8.GetBytes(credentials!.UserName);
        var passBytes = Encoding.UTF8.GetBytes(credentials.Password);
        var authRequest = new byte[3 + userBytes.Length + passBytes.Length];
        authRequest[0] = 0x01;
        authRequest[1] = (byte)userBytes.Length;
        userBytes.CopyTo(authRequest, 2);
        authRequest[2 + userBytes.Length] = (byte)passBytes.Length;
        passBytes.CopyTo(authRequest, 3 + userBytes.Length);
        await stream.WriteAsync(authRequest, ct);

        var authReply = await ReadExactAsync(stream, 2, ct);
        if (authReply[1] != 0x00)
            throw new IOException("SOCKS5: авторизация отклонена прокси.");
    }
    else if (methodReply[1] != 0x00)
    {
        throw new IOException($"SOCKS5: прокси выбрал неподдерживаемый метод 0x{methodReply[1]:X2}.");
    }

    // CONNECT в доменной форме (ATYP=0x03): DNS резолвит сам прокси
    var hostBytes = Encoding.UTF8.GetBytes(host);
    var connectRequest = new byte[4 + 1 + hostBytes.Length + 2];
    connectRequest[0] = 0x05;
    connectRequest[1] = 0x01; // CONNECT
    connectRequest[2] = 0x00; // RSV
    connectRequest[3] = 0x03; // ATYP = domain
    connectRequest[4] = (byte)hostBytes.Length;
    hostBytes.CopyTo(connectRequest, 5);
    connectRequest[^2] = (byte)(port >> 8);
    connectRequest[^1] = (byte)(port & 0xFF);
    await stream.WriteAsync(connectRequest, ct);

    var replyHeader = await ReadExactAsync(stream, 4, ct);
    if (replyHeader[0] != 0x05)
        throw new IOException("SOCKS5: некорректный ответ на CONNECT.");
    if (replyHeader[1] != 0x00)
        throw new IOException($"SOCKS5: CONNECT отклонён прокси, код 0x{replyHeader[1]:X2}.");

    // Дочитываем привязанный адрес и порт из ответа
    var atyp = replyHeader[3];
    var addrLen = atyp switch
    {
        0x01 => 4,
        0x03 => (await ReadExactAsync(stream, 1, ct))[0],
        0x04 => 16,
        _ => throw new IOException($"SOCKS5: неизвестный ATYP 0x{atyp:X2}.")
    };
    await ReadExactAsync(stream, addrLen + 2, ct);
}

async Task<string> ReadHeadersAsync(Stream stream, CancellationToken ct)
{
    var buffer = new MemoryStream();
    var oneByte = new byte[1];
    var tail = new byte[4];
    var tailPos = 0;

    while (true)
    {
        var read = await stream.ReadAsync(oneByte.AsMemory(0, 1), ct);
        if (read == 0)
            throw new IOException("Прокси закрыл соединение до завершения заголовков.");

        buffer.WriteByte(oneByte[0]);
        tail[tailPos] = oneByte[0];
        tailPos = (tailPos + 1) % 4;
        if (tail[0] == '\r' && tail[1] == '\n' && tail[2] == '\r' && tail[3] == '\n')
            break;
    }

    return Encoding.ASCII.GetString(buffer.ToArray());
}

async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken ct)
{
    var buffer = new byte[count];
    var offset = 0;
    while (offset < count)
    {
        var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
        if (read == 0)
            throw new IOException("Прокси закрыл соединение раньше времени.");
        offset += read;
    }
    return buffer;
}
