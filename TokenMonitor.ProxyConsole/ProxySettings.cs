using System;
using System.Net;
namespace TokenMonitor.ProxyConsole
{
    public class ProxySettings
    {
        public string Host { get; private set; }
        public int Port { get; private set; }
        public string Scheme { get; private set; }
        public NetworkCredential? Credentials { get; private set; }
        public WebProxy? WebProxyObject { get; private set; }

        public ProxySettings(string proxyUrl)
        {
            if (string.IsNullOrEmpty(proxyUrl))
                throw new ArgumentNullException(nameof(proxyUrl));

            // Добавляем схему http://, если её забыли указать
            if (!proxyUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !proxyUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                !proxyUrl.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase) &&
                !proxyUrl.StartsWith("socks5h://", StringComparison.OrdinalIgnoreCase))
            {
                proxyUrl = "http://" + proxyUrl;
            }

            var uri = new Uri(proxyUrl);

            Host = uri.Host;
            Port = uri.Port;
            Scheme = uri.Scheme.Equals("socks5h", StringComparison.OrdinalIgnoreCase) ? "socks5" : uri.Scheme;

            // Извлекаем логин и пароль, если они есть
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var userInfoParts = uri.UserInfo.Split(':');
                var username = Uri.UnescapeDataString(userInfoParts[0]);
                var password = userInfoParts.Length > 1 ? Uri.UnescapeDataString(userInfoParts[1]) : string.Empty;

                Credentials = new NetworkCredential(username, password);
            }

            // Сразу создаем готовый объект WebProxy для HttpClient
            // (WebProxy поддерживает только http/https; socks5 используется только для CONNECT-туннелей)
            if (Scheme is "http" or "https")
            {
                WebProxyObject = new WebProxy(uri)
                {
                    Credentials = Credentials
                };
            }
        }
    }
}
