using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog;
using TokenMonitor.Proxy;

namespace TokenMonitor
{
    public static class GlobalRouteHttpClient
    {
        public static bool OnlyByProxy { get; private set; }

        public static ProxySettings ProxySettings { get; set; } = new();

        public static async Task InitializeAsync()
        {

            // 1. Настраиваем хендлер: отключаем прокси (чтобы исключить системные сбои) 
            // и принудительно выставляем IPv4, если виртуальный адаптер чудит
            var handler = new HttpClientHandler
            {
                UseProxy = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            using var geoClient = new HttpClient(handler);
            geoClient.Timeout = TimeSpan.FromSeconds(5);

            // 2. Ставим стандартный User-Agent обычного браузера (Chrome)
            geoClient.DefaultRequestHeaders.Clear();
            geoClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            geoClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/json");

            try
            {

                var countryCode = (await geoClient.GetStringAsync("https://ipinfo.io/country")).Trim();

                Log.Information("[GeoIP] Публичный IP определён. Код страны: {Code}", countryCode);

                var restricted = new[] { "RU", "IR", "KP", "CU", "VE", "BY" };
                OnlyByProxy = restricted.Contains(countryCode, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log.Warning("[GeoIP] Не удалось определить гео-положение: {Message}", ex.Message);
            }
        }

        public static bool CanMakeExternalRequests() => !OnlyByProxy || ProxySettings.HasProxy;

        public static bool RequiresExternalAccess(Uri uri) => !IsInternalUri(uri);

        public static bool CanAccess(Uri uri) => !RequiresExternalAccess(uri) || CanMakeExternalRequests();

        static bool IsInternalUri(Uri uri)
        {
            if (!uri.IsAbsoluteUri) return true;

            var host = uri.Host;
            if (string.IsNullOrWhiteSpace(host)) return true;

            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                || host.Equals("::1", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (ProxySettings.ShouldBypass(host))
            {
                return true;
            }

            if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".loc", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return IPAddress.TryParse(host, out var address) && IsPrivateAddress(address);
        }

        static bool IsPrivateAddress(IPAddress address)
        {
            if (IPAddress.IsLoopback(address)) return true;

            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var bytes = address.GetAddressBytes();
                return bytes[0] == 10
                    || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                    || (bytes[0] == 192 && bytes[1] == 168)
                    || (bytes[0] == 169 && bytes[1] == 254);
            }

            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;
            }

            return false;
        }

        public static HttpClient CreateClient(TimeSpan timeout)
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(5),
            };

            var httpProxy = ProxySettings.HttpProxy;
            if (httpProxy is not null)
            {
                handler.Proxy = httpProxy;
                handler.UseProxy = true;
            }

            return new HttpClient(handler)
            {
                Timeout = timeout,
            };
        }
    }
}