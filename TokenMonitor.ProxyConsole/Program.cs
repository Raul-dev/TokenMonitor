using CommandLine;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using System;
using TokenMonitor.Proxy;

#if DEBUG
const LogEventLevel minimumLevel = LogEventLevel.Debug;
#else
const LogEventLevel minimumLevel = LogEventLevel.Information;
#endif

Log.Logger = Logging.CreateLoggerConfiguration("tokenmonitor-proxy", minimumLevel)
    .WriteTo.Console()
    .CreateLogger();

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Log.Fatal(e.ExceptionObject as Exception, "Необработанное исключение");

Log.Information("Каталог логов: {LogDirectory}", Logging.LogDirectory);

// === ПАРСИНГ АРГУМЕНТОВ КОМАНДНОЙ СТРОКИ ===
var cmdParser = new Parser(with => with.IgnoreUnknownArguments = true);
ProxyCommandLineOptions? cliOptions = null;
var parseResult = cmdParser.ParseArguments<ProxyCommandLineOptions>(args);
var parsedOk = true;
parseResult
    .WithParsed(o => cliOptions = o)
    .WithNotParsed(errs => parsedOk = false);

if (!parsedOk)
    return;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddUserSecrets(System.Reflection.Assembly.GetExecutingAssembly(), optional: true)
    .AddEnvironmentVariables()
    .Build();

var listenUrl = cliOptions?.ListenUrl
    ?? config["ListenUrl"]
    ?? "http://localhost:8181";

var proxyEnabled = cliOptions?.ProxyEnabled
    ?? config.GetValue<bool>("ProxyEnabled", false);

var proxyRawString = cliOptions?.ProxyUrl ?? config["ProxyUrl"];
if (!proxyEnabled)
    proxyRawString = null;

ProxySettings? upstreamProxy = null;
if (!string.IsNullOrWhiteSpace(proxyRawString))
{
    try
    {
        upstreamProxy = new ProxySettings(proxyRawString!);
        Log.Information("Chain Proxy: {Scheme}://{Host}:{Port} (настройки из: {Source})",
            upstreamProxy.Scheme, upstreamProxy.Host, upstreamProxy.Port, DescribeProxySource());
    }
    catch (Exception ex)
    {
        Log.Warning("[Proxy] Некорректный ProxyUrl, upstream-прокси отключён: {Message}", ex.Message);
        upstreamProxy = null;
    }
}
else
{
    Log.Information("Chain Proxy: не задан — прямое подключение без промежуточного прокси");
}

string DescribeProxySource()
{
    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ProxyUrl")))
        return "переменная окружения ProxyUrl";

    var secretsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "UserSecrets", "tokenmonitor-proxyconsole", "secrets.json");
    if (File.Exists(secretsPath) && File.ReadAllText(secretsPath).Contains("ProxyUrl"))
        return $"User Secrets: {secretsPath}";

    var appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    if (File.Exists(appSettingsPath) &&
        File.ReadAllText(appSettingsPath).Contains("\"ProxyUrl\""))
        return $"файл: {appSettingsPath}";

    return "конфигурация (источник не определён)";
}

try
{
    var server = new ProxyServer(listenUrl, upstreamProxy);
    await server.RunAsync();
}
finally
{
    await Log.CloseAndFlushAsync();
}

public class ProxyCommandLineOptions
{
    [Option("ListenUrl", Required = false, HelpText = "Адрес, на котором прокси слушает трафик (например http://localhost:8181)")]
    public string? ListenUrl { get; set; }

    [Option("ProxyEnabled", Required = false, HelpText = "Включить upstream-прокси (true/false)")]
    public bool? ProxyEnabled { get; set; }

    [Option("ProxyUrl", Required = false, HelpText = "URL upstream-прокси: http://user:pass@host:port")]
    public string? ProxyUrl { get; set; }
}