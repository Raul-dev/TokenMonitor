using System;
using System.IO;
using Serilog;
using Serilog.Events;

namespace TokenMonitor.Proxy
{
    /// <summary>
    /// Единая конфигурация Serilog: логи пишутся в ту же папку, что и настройки приложения
    /// (%APPDATA%\TokenMonitor). Размер файла ограничен 200 КБ, хранение — не больше 3 дней.
    /// </summary>
    public static class Logging
    {
        public const long MaxLogFileSizeBytes = 200 * 1024;

        public static readonly TimeSpan LogRetention = TimeSpan.FromDays(3);

        public static string LogDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TokenMonitor");

        public static LoggerConfiguration CreateLoggerConfiguration(
            string filePrefix = "tokenmonitor",
            LogEventLevel minimumLevel = LogEventLevel.Information,
            LogEventLevel fileMinimumLevel = LogEventLevel.Information)
        {
            Directory.CreateDirectory(LogDirectory);

            return new LoggerConfiguration()
                .MinimumLevel.Is(minimumLevel)
                .Enrich.FromLogContext()
                .WriteTo.File(
                    Path.Combine(LogDirectory, $"{filePrefix}-.log"),
                    restrictedToMinimumLevel: fileMinimumLevel,
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: MaxLogFileSizeBytes,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: null,
                    retainedFileTimeLimit: LogRetention,
                    flushToDiskInterval: TimeSpan.FromSeconds(2),
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
        }
    }
}
