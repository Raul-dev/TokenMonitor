using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace TokenMonitor.Proxy
{
    public class ProxyServer
    {
        private readonly string _listenUrl;
        private readonly ProxySettings? _upstreamProxy;
        private readonly CancellationToken _externalCt;
        private readonly HashSet<string> _hopByHopHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
            "Proxy-Connection", "TE", "Trailer", "Transfer-Encoding", "Upgrade"
        };

        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private bool _hasConnection;

        public ProxyServer(string listenUrl, ProxySettings? upstreamProxy)
        {
            _listenUrl = listenUrl;
            _upstreamProxy = upstreamProxy;
        }

        public async Task RunAsync(CancellationToken externalCt = default)
        {
            var listenUri = new Uri(_listenUrl);
            var listenHost = listenUri.Host;
            var listenPort = listenUri.Port > 0 ? listenUri.Port : 8181;
            IPAddress listenIp;
            if (listenHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) || listenHost == "127.0.0.1" || listenHost == "::1")
                listenIp = IPAddress.Loopback;
            else if (listenHost == "0.0.0.0" || listenHost == "*" || listenHost == "+")
                listenIp = IPAddress.Any;
            else
                listenIp = IPAddress.Parse(listenHost);

            var portOwner = FindPortOwner(listenPort);
            if (portOwner != null)
            {
                Log.Warning("[Proxy] Порт {Port} уже занят процессом '{Name}' (PID {Pid}).", listenPort, portOwner.Value.Name, portOwner.Value.Pid);
                Log.Warning("Освободите порт, например: Stop-Process -Id {Pid} — и запустите снова.", portOwner.Value.Pid);
                return;
            }

            _listener = new TcpListener(listenIp, listenPort);
            try
            {
                _listener.Start();
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                var owner = FindPortOwner(listenPort);
                if (owner != null)
                {
                    Log.Warning("[Proxy] Порт {Port} уже занят процессом '{Name}' (PID {Pid}).", listenPort, owner.Value.Name, owner.Value.Pid);
                    Log.Warning("Освободите порт, например: Stop-Process -Id {Pid} — и запустите снова.", owner.Value.Pid);
                }
                else
                {
                    Log.Warning("[Proxy] Порт {Port} уже занят другой программой.", listenPort);
                }
                return;
            }

            Log.Information("TokenMonitor.Proxy запущен на {Url}. Ожидание подключений... (Ctrl+C для остановки)", _listenUrl);

            _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; _cts.Cancel(); };

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(15));
                if (_hasConnection) return;
                var owner = FindPortOwner(listenPort);
                if (owner != null)
                {
                    if (owner.Value.Pid == Environment.ProcessId)
                        Log.Information("[Proxy] 15 сек без подключений. Порт {Port} удерживается текущим процессом (PID {Pid}) — приложение работает, ожидает подключений.", listenPort, owner.Value.Pid);
                    else
                        Log.Warning("[Proxy] 15 сек без подключений. Порт {Port} удерживается процессом '{Name}' (PID {Pid}). Освободите порт, например: Stop-Process -Id {Pid}", listenPort, owner.Value.Name, owner.Value.Pid, owner.Value.Pid);
                }
                else
                {
                    Log.Warning("[Proxy] 15 сек без подключений. Порт {Port} свободен — приложение, возможно, не слушает. Проверьте настройки ListenUrl.", listenPort);
                }
            });

            using var forwarderClient = _upstreamProxy?.WebProxyObject != null
                ? new HttpClient(new HttpClientHandler
                {
                    Proxy = _upstreamProxy.WebProxyObject,
                    UseProxy = true
                })
                : new HttpClient();

            if (_upstreamProxy?.WebProxyObject != null)
                _upstreamProxy.WebProxyObject.BypassProxyOnLocal = true;

            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    var remoteEp = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
                    Log.Debug("TCP CONNECT Remote={Remote}", remoteEp);
                    _ = Task.Run(() => HandleClientAsync(client, forwarderClient), _cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Error("[Proxy] Ошибка accept: {Message}", ex.Message);
            }
            finally
            {
                _listener.Stop();
                Log.Information("TokenMonitor.Proxy остановлен.");
            }
        }

        private async Task HandleClientAsync(TcpClient client, HttpClient forwarderClient)
        {
            using var tcpClient = client;
            using var stream = client.GetStream();
            _hasConnection = true;
            var firstBytes = new List<byte>();
            string logUrl = string.Empty;
            string operation = "REQUEST";
            try
            {
                var request = await ReadHttpRequestAsync(stream, _cts!.Token, firstBytes);
                if (request == null)
                {
                    var fb = firstBytes.Count > 0 ? BitConverter.ToString(firstBytes.ToArray()) : "?";
                    Log.Debug("CONN FirstBytes={Bytes} URL={Url}", fb, client.Client.RemoteEndPoint?.ToString() ?? "unknown");
                    return;
                }

                var (method, target, version, headers, bodyBytes, fbArr) = request.Value;
                logUrl = target;
                operation = method;

                if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = target.Split(':');
                    var host = parts[0];
                    var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 443;
                    logUrl = port == 443 ? $"https://{host}" : $"https://{host}:{port}";
                    operation = "CONNECT";
                    Log.Debug("CONN {Url} FirstBytes={Bytes}", logUrl, BitConverter.ToString(fbArr));

                    var (targetClient, targetStream) = await OpenTunnelAsync(host, port);
                    using (targetClient)
                    using (targetStream)
                    {
                        var ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
                        await stream.WriteAsync(ok, _cts.Token);
                        await stream.FlushAsync(_cts.Token);

                        var c2t = stream.CopyToAsync(targetStream, _cts.Token);
                        var t2c = targetStream.CopyToAsync(stream, _cts.Token);
                        await Task.WhenAny(c2t, t2c);
                        Log.Debug("{Host}:{Port} OK", host, port);
                    }
                    return;
                }

                // Обычный HTTP-запрос
                string targetUrl;
                if (Uri.TryCreate(target, UriKind.Absolute, out var absUri))
                    targetUrl = target;
                else
                {
                    var hostHeader = headers.TryGetValue("Host", out var hv) ? hv : string.Empty;
                    targetUrl = $"http://{hostHeader}{target}";
                }
                logUrl = targetUrl;
                operation = $"HTTP {method}";
                Log.Debug("CONN {Url} FirstBytes={Bytes}", logUrl, BitConverter.ToString(fbArr));

                // Дочитываем тело
                if (headers.TryGetValue("Content-Length", out var clStr) && int.TryParse(clStr, out var contentLength))
                {
                    if (bodyBytes.Length < contentLength)
                    {
                        var remaining = contentLength - bodyBytes.Length;
                        var extra = new byte[remaining];
                        var readTotal = 0;
                        while (readTotal < remaining)
                        {
                            var n = await stream.ReadAsync(extra.AsMemory(readTotal, remaining - readTotal), _cts.Token);
                            if (n == 0) break;
                            readTotal += n;
                        }
                        if (readTotal > 0)
                        {
                            var combined = new byte[bodyBytes.Length + readTotal];
                            Buffer.BlockCopy(bodyBytes, 0, combined, 0, bodyBytes.Length);
                            Buffer.BlockCopy(extra, 0, combined, bodyBytes.Length, readTotal);
                            bodyBytes = combined;
                        }
                    }
                }

                if (bodyBytes.Length > 0)
                {
                    Log.Information("[TokenMonitor] Перехвачен HTTP запрос! Длина: {Length} симв.", bodyBytes.Length);
                }

                var forwardMessage = new HttpRequestMessage(new HttpMethod(method), targetUrl);

                var connectionTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (headers.TryGetValue("Connection", out var connVal))
                {
                    foreach (var t in connVal.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        connectionTokens.Add(t);
                }

                foreach (var h in headers)
                {
                    if (IsHopByHopHeader(h.Key) || connectionTokens.Contains(h.Key))
                        continue;
                    if (!forwardMessage.Headers.TryAddWithoutValidation(h.Key, h.Value) && forwardMessage.Content != null)
                    {
                        forwardMessage.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
                    }
                }

                if (bodyBytes.Length > 0)
                {
                    forwardMessage.Content = new ByteArrayContent(bodyBytes);
                    if (headers.TryGetValue("Content-Type", out var ctVal))
                        forwardMessage.Content.Headers.TryAddWithoutValidation("Content-Type", ctVal);
                }

                var responseMessage = await forwarderClient.SendAsync(forwardMessage, HttpCompletionOption.ResponseHeadersRead, _cts.Token);

                var sb = new StringBuilder();
                sb.Append($"HTTP/1.1 {(int)responseMessage.StatusCode} {responseMessage.ReasonPhrase}\r\n");

                var respConnectionTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (responseMessage.Headers.Connection != null)
                {
                    foreach (var v in responseMessage.Headers.Connection)
                        foreach (var t in v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            respConnectionTokens.Add(t);
                }

                foreach (var h in responseMessage.Headers)
                {
                    if (IsHopByHopHeader(h.Key) || respConnectionTokens.Contains(h.Key))
                        continue;
                    foreach (var v in h.Value)
                        sb.Append($"{h.Key}: {v}\r\n");
                }
                foreach (var h in responseMessage.Content.Headers)
                {
                    if (IsHopByHopHeader(h.Key) || respConnectionTokens.Contains(h.Key))
                        continue;
                    foreach (var v in h.Value)
                        sb.Append($"{h.Key}: {v}\r\n");
                }
                sb.Append("Connection: close\r\n");
                sb.Append("\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), _cts.Token);
                await responseMessage.Content.CopyToAsync(stream, _cts.Token);
                await stream.FlushAsync(_cts.Token);
                Log.Debug("{Url} -> {StatusCode}", targetUrl, (int)responseMessage.StatusCode);
            }
            catch (Exception ex)
            {
                var url = string.IsNullOrWhiteSpace(logUrl) ? (client.Client.RemoteEndPoint?.ToString() ?? "unknown") : logUrl;
                var fb = firstBytes.Count > 0 ? BitConverter.ToString(firstBytes.ToArray()) : "?";
                bool isClosed = ex is IOException &&
                    (ex.Message.Contains("closed", StringComparison.OrdinalIgnoreCase) ||
                     ex.Message.Contains("reset", StringComparison.OrdinalIgnoreCase) ||
                     ex.Message.Contains("forcibly", StringComparison.OrdinalIgnoreCase));
                if (isClosed)
                    Log.Debug("{Operation}: {Message}. URL={Url} FirstBytes={Bytes}", operation, ex.Message, url, fb);
                else
                    Log.Error("{Operation}: {Message}. URL={Url} FirstBytes={Bytes}", operation, ex.Message, url, fb);
            }
        }

        private async Task<(TcpClient Client, Stream Stream)> OpenTunnelAsync(string host, int port)
        {
            var client = new TcpClient();
            try
            {
                using var setupCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

                bool isLocal = IsLocalOrLoopbackHost(host);

                if (_upstreamProxy == null || isLocal)
                {
                    if (isLocal && _upstreamProxy != null)
                        Log.Information("[Proxy] Локальный адрес {Host}:{Port} обнаружен. Подключение НАПРЯМУЮ в обход Chain Proxy.", host, port);

                    await client.ConnectAsync(host, port, setupCts.Token);
                    return (client, client.GetStream());
                }

                Log.Debug("[Proxy] Отправка запроса {Host}:{Port} через Chain Proxy...", host, port);
                await client.ConnectAsync(_upstreamProxy.Host, _upstreamProxy.Port, setupCts.Token);
                Stream stream = client.GetStream();

                if (_upstreamProxy.Scheme == "https")
                {
                    var sslStream = new SslStream(stream, leaveInnerStreamOpen: false);
                    await sslStream.AuthenticateAsClientAsync(
                        new SslClientAuthenticationOptions { TargetHost = _upstreamProxy.Host },
                        setupCts.Token);
                    stream = sslStream;
                }

                if (_upstreamProxy.Scheme == "socks5")
                    await Socks5ConnectAsync(stream, host, port, _upstreamProxy.Credentials, setupCts.Token);
                else
                    await HttpConnectAsync(stream, host, port, _upstreamProxy.Credentials, setupCts.Token);

                return (client, stream);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        private async Task HttpConnectAsync(Stream stream, string host, int port, NetworkCredential? credentials, CancellationToken ct)
        {
            var auth = credentials != null
                ? $"Proxy-Authorization: Basic {Convert.ToBase64String(Encoding.ASCII.GetBytes($"{credentials.UserName}:{credentials.Password}"))}\r\n"
                : string.Empty;

            var connectRequest = $"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n{auth}\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(connectRequest), ct);
            await stream.FlushAsync(ct);

            using var headerCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var responseHead = await ReadHeadersAsync(stream, headerCts.Token);

            var statusLine = responseHead.Split('\n')[0].Trim();
            if (!statusLine.Contains(" 200"))
                throw new IOException(statusLine);
        }

        private async Task Socks5ConnectAsync(Stream stream, string host, int port, NetworkCredential? credentials, CancellationToken ct)
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

            var hostBytes = Encoding.UTF8.GetBytes(host);
            var connectRequest = new byte[4 + 1 + hostBytes.Length + 2];
            connectRequest[0] = 0x05;
            connectRequest[1] = 0x01;
            connectRequest[2] = 0x00;
            connectRequest[3] = 0x03;
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

        private async Task<string> ReadHeadersAsync(Stream stream, CancellationToken ct)
        {
            var buffer = new MemoryStream();
            var chunk = new byte[256];
            while (true)
            {
                var data = buffer.ToArray();
                for (int i = 0; i + 3 < data.Length; i++)
                {
                    if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                    {
                        return Encoding.ASCII.GetString(data);
                    }
                }

                var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), ct);
                if (read == 0)
                    throw new IOException("Прокси закрыл соединение до завершения заголовков.");
                buffer.Write(chunk, 0, read);
                if (buffer.Length > 65536)
                    throw new IOException("Слишком большие заголовки.");
            }
        }

        private async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken ct)
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

        private async Task<(string Method, string Target, string Version, Dictionary<string, string> Headers, byte[] Body, byte[] FirstBytes)?> ReadHttpRequestAsync(Stream stream, CancellationToken ct, List<byte> firstBytes)
        {
            var pipe = new Pipe();
            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var fillTask = FillPipeAsync(stream, pipe.Writer, connectionCts.Token);

            try
            {
                var reader = pipe.Reader;
                ReadOnlySequence<byte> headerBuffer = default;
                SequencePosition advanceToPosition = default;
                bool headersFound = false;

                while (true)
                {
                    var result = await reader.ReadAsync(connectionCts.Token);
                    var buffer = result.Buffer;

                    var seqReader = new SequenceReader<byte>(buffer);
                    if (seqReader.TryReadTo(out ReadOnlySequence<byte> headerSeq, new byte[] { 0x0D, 0x0A, 0x0D, 0x0A }, advancePastDelimiter: true))
                    {
                        headerBuffer = headerSeq;
                        advanceToPosition = seqReader.Position;
                        headersFound = true;
                        break;
                    }

                    if (result.IsCompleted)
                    {
                        headerBuffer = buffer;
                        advanceToPosition = buffer.End;
                        headersFound = buffer.Length > 0;
                        break;
                    }

                    reader.AdvanceTo(buffer.Start, buffer.End);
                }

                if (!headersFound || headerBuffer.IsEmpty)
                {
                    connectionCts.Cancel();
                    try { await fillTask; } catch { }
                    reader.Complete();
                    return null;
                }

                var headerArray = headerBuffer.ToArray();
                reader.AdvanceTo(advanceToPosition);

                foreach (var b in headerArray)
                {
                    if (firstBytes.Count < 5) firstBytes.Add(b);
                    else break;
                }

                var headerText = Encoding.ASCII.GetString(headerArray);
                var headerLines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
                if (headerLines.Length == 0)
                {
                    connectionCts.Cancel();
                    try { await fillTask; } catch { }
                    reader.Complete();
                    return null;
                }

                var requestLine = headerLines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (requestLine.Length < 2)
                {
                    connectionCts.Cancel();
                    try { await fillTask; } catch { }
                    reader.Complete();
                    return null;
                }

                var method = requestLine[0];
                var target = requestLine[1];
                var version = requestLine.Length > 2 ? requestLine[2] : "HTTP/1.1";

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in headerLines.Skip(1))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var idx = line.IndexOf(':');
                    if (idx <= 0) continue;
                    var key = line.Substring(0, idx).Trim();
                    var val = line.Substring(idx + 1).Trim();
                    if (!headers.ContainsKey(key))
                        headers[key] = val;
                }

                byte[] body = Array.Empty<byte>();
                if (headers.TryGetValue("Content-Length", out var clStr) && int.TryParse(clStr, out var contentLength) && contentLength > 0)
                {
                    var bodyMs = new MemoryStream();
                    int remaining = contentLength;
                    while (remaining > 0)
                    {
                        var result = await reader.ReadAsync(connectionCts.Token);
                        var buffer = result.Buffer;
                        if (buffer.IsEmpty)
                        {
                            if (result.IsCompleted) break;
                            continue;
                        }
                        int toTake = (int)Math.Min(remaining, buffer.Length);
                        var slice = buffer.Slice(0, toTake);
                        foreach (var seg in slice)
                            bodyMs.Write(seg.Span);
                        remaining -= toTake;
                        reader.AdvanceTo(slice.End);
                    }
                    body = bodyMs.ToArray();
                }

                connectionCts.Cancel();
                try { await fillTask; } catch { }
                reader.Complete();

                return (method, target, version, headers, body, firstBytes.ToArray());
            }
            catch (Exception)
            {
                connectionCts.Cancel();
                try { await fillTask; } catch { }
                pipe.Reader.Complete();
                throw;
            }
        }

        private async Task FillPipeAsync(Stream stream, PipeWriter writer, CancellationToken ct)
        {
            try
            {
                while (true)
                {
                    var memory = writer.GetMemory(4096);
                    int bytesRead = await stream.ReadAsync(memory, ct);
                    if (bytesRead == 0)
                    {
                        await writer.CompleteAsync();
                        return;
                    }
                    writer.Advance(bytesRead);
                    var flushResult = await writer.FlushAsync(ct);
                    if (flushResult.IsCompleted)
                        return;
                }
            }
            catch (OperationCanceledException)
            {
                await writer.CompleteAsync();
            }
            catch (Exception ex)
            {
                await writer.CompleteAsync(ex);
            }
        }

        private bool IsHopByHopHeader(string name) => _hopByHopHeaders.Contains(name);

        private static bool IsLocalOrLoopbackHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
            if (IPAddress.TryParse(host, out var ip))
            {
                if (IPAddress.IsLoopback(ip)) return true;
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    var b = ip.GetAddressBytes();
                    if (b[0] == 10) return true;
                    if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
                    if (b[0] == 192 && b[1] == 168) return true;
                    if (b[0] == 169 && b[1] == 254) return true;
                }
                else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    var b = ip.GetAddressBytes();
                    if ((b[0] & 0xFE) == 0xFC) return true;
                    if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return true;
                }
            }
            if (!host.Contains('.')) return true;
            return false;
        }

        private static (int Pid, string Name)? FindPortOwner(int port)
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
    }
}