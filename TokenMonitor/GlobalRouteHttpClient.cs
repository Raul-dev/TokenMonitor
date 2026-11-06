using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog;

namespace TokenMonitor
{
    public static class GlobalRouteHttpClient
    {
        public static bool OnlyByProxy { get; private set; }
        public static bool ProxyEnabled { get; set; }
        public static string? ProxyUrl { get; set; }

        public static async Task InitializeAsync()
        {
            try
            {
                using var geoClient = new HttpClient();
                geoClient.Timeout = TimeSpan.FromSeconds(5);
                geoClient.DefaultRequestHeaders.Add("User-Agent", "TokenMonitor/1.0");

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

        public static bool CanMakeExternalRequests() =>
            !OnlyByProxy || (ProxyEnabled && !string.IsNullOrWhiteSpace(ProxyUrl));

        public static HttpClient CreateClient(TimeSpan timeout)
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(5),
            };

            if (ProxyEnabled && !string.IsNullOrWhiteSpace(ProxyUrl))
            {
                handler.Proxy = new WebProxy(ProxyUrl);
                handler.UseProxy = true;
            }

            return new HttpClient(handler)
            {
                Timeout = timeout,
            };
        }
    }
}