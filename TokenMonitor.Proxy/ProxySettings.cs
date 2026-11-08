using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Serialization;

namespace TokenMonitor.Proxy
{
    public class ProxySettings
    {
        public const string DefaultListenUrl = "http://127.0.0.1:8181";

        public bool Enabled { get; set; }
        public string Url { get; set; } = string.Empty;
        public string ListenUrl { get; set; } = DefaultListenUrl;
        public string UpstreamUrl { get; set; } = string.Empty;
        public List<string> BypassList { get; set; } = new();

        [JsonIgnore]
        public string Host { get; private set; } = string.Empty;

        [JsonIgnore]
        public int Port { get; private set; }

        [JsonIgnore]
        public string Scheme { get; private set; } = string.Empty;

        [JsonIgnore]
        public NetworkCredential? Credentials { get; private set; }

        [JsonIgnore]
        public WebProxy? WebProxyObject { get; private set; }

        [JsonIgnore]
        public bool HasProxy => Enabled && WebProxyObject is not null;

        [JsonIgnore]
        public IWebProxy? HttpProxy =>
            HasProxy ? new BypassAwareWebProxy(WebProxyObject!, ShouldBypass) : null;

        public ProxySettings()
        {
        }

        public ProxySettings(string proxyUrl) : this(proxyUrl, null)
        {
        }

        public ProxySettings(string proxyUrl, IEnumerable<string>? bypassList)
        {
            if (string.IsNullOrWhiteSpace(proxyUrl))
                throw new ArgumentNullException(nameof(proxyUrl));

            Enabled = true;
            Url = proxyUrl;

            if (bypassList is not null)
                BypassList = bypassList.ToList();

            Initialize();
        }

        public void Initialize() => ParseProxyUrl();

        public bool TryInitialize()
        {
            try
            {
                ParseProxyUrl();
                return true;
            }
            catch
            {
                Host = string.Empty;
                Port = 0;
                Scheme = string.Empty;
                Credentials = null;
                WebProxyObject = null;
                return false;
            }
        }

        public bool ShouldBypass(string? host) => ProxyBypassList.IsMatch(BypassList, host);

        public bool ShouldBypass(Uri? uri) => uri is not null && ShouldBypass(uri.Host);

        [JsonIgnore]
        public string EffectiveUpstreamUrl =>
            string.IsNullOrWhiteSpace(UpstreamUrl) ? Url : UpstreamUrl;

        public ProxySettings? CreateUpstream()
        {
            var upstreamUrl = EffectiveUpstreamUrl;
            if (string.IsNullOrWhiteSpace(upstreamUrl))
                return null;

            try
            {
                return new ProxySettings(upstreamUrl.Trim(), BypassList);
            }
            catch
            {
                return null;
            }
        }

        void ParseProxyUrl()
        {
            var proxyUrl = NormalizeProxyUrl(Url);
            var uri = new Uri(proxyUrl);

            Host = uri.Host;
            Port = uri.Port;
            Scheme = uri.Scheme.Equals("socks5h", StringComparison.OrdinalIgnoreCase) ? "socks5" : uri.Scheme;

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var userInfoParts = uri.UserInfo.Split(':');
                var username = Uri.UnescapeDataString(userInfoParts[0]);
                var password = userInfoParts.Length > 1 ? Uri.UnescapeDataString(userInfoParts[1]) : string.Empty;
                Credentials = new NetworkCredential(username, password);
            }
            else
            {
                Credentials = null;
            }

            if (Scheme is "http" or "https")
            {
                WebProxyObject = new WebProxy(uri)
                {
                    Credentials = Credentials,
                    BypassProxyOnLocal = true,
                };
            }
            else
            {
                WebProxyObject = null;
            }
        }

        static string NormalizeProxyUrl(string? proxyUrl)
        {
            var value = (proxyUrl ?? string.Empty).Trim();
            if (value.Length == 0)
                throw new ArgumentNullException(nameof(proxyUrl));

            if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                !value.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase) &&
                !value.StartsWith("socks5h://", StringComparison.OrdinalIgnoreCase))
            {
                value = "http://" + value;
            }

            return value;
        }
    }

    public sealed class BypassAwareWebProxy : IWebProxy
    {
        readonly IWebProxy inner;
        readonly Func<string?, bool> shouldBypass;

        public BypassAwareWebProxy(IWebProxy inner, Func<string?, bool> shouldBypass)
        {
            this.inner = inner;
            this.shouldBypass = shouldBypass;
        }

        public ICredentials? Credentials
        {
            get => inner.Credentials;
            set => inner.Credentials = value;
        }

        public Uri? GetProxy(Uri destination) =>
            destination is null || shouldBypass(destination.Host) || inner.IsBypassed(destination)
                ? null
                : inner.GetProxy(destination);

        public bool IsBypassed(Uri host) =>
            host is null || shouldBypass(host.Host) || inner.IsBypassed(host);
    }

    public static class ProxyBypassList
    {
        public static bool IsMatch(IEnumerable<string>? patterns, string? host)
        {
            if (patterns is null || string.IsNullOrWhiteSpace(host))
                return false;

            var normalizedHost = NormalizeHost(host);
            if (normalizedHost.Length == 0)
                return false;

            IPAddress? address = null;

            foreach (var raw in patterns)
            {
                var pattern = NormalizePattern(raw);
                if (pattern.Length == 0)
                    continue;

                if (TryParseCidr(pattern, out var network, out var prefix))
                {
                    address ??= ParseAddress(normalizedHost);
                    if (address is not null && IsInSubnet(address, network, prefix))
                        return true;

                    continue;
                }

                if (IPAddress.TryParse(pattern, out var single))
                {
                    address ??= ParseAddress(normalizedHost);
                    if (address is not null && address.Equals(single))
                        return true;

                    continue;
                }

                if (DomainMatches(normalizedHost, pattern))
                    return true;
            }

            return false;
        }

        internal static string NormalizePattern(string? raw)
        {
            var value = (raw ?? string.Empty).Trim();
            if (value.Length == 0 || value.StartsWith("#", StringComparison.Ordinal))
                return string.Empty;

            if (value.Contains("://", StringComparison.Ordinal)
                && Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                value = uri.Host;
            }

            value = value.Trim();
            if (value.StartsWith("*.", StringComparison.Ordinal))
                value = value[2..];

            return value.TrimStart('.').TrimEnd('.');
        }

        static string NormalizeHost(string host) =>
            host.Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();

        static IPAddress? ParseAddress(string host) =>
            IPAddress.TryParse(host, out var address) ? address : null;

        static bool TryParseCidr(string pattern, out IPAddress network, out int prefix)
        {
            network = IPAddress.None;
            prefix = 0;

            var slash = pattern.IndexOf('/');
            if (slash <= 0)
                return false;

            if (!IPAddress.TryParse(pattern[..slash], out var parsed))
                return false;

            if (!int.TryParse(pattern[(slash + 1)..], out var parsedPrefix))
                return false;

            var maxPrefix = parsed.GetAddressBytes().Length * 8;
            if (parsedPrefix < 0 || parsedPrefix > maxPrefix)
                return false;

            network = parsed;
            prefix = parsedPrefix;
            return true;
        }

        static bool IsInSubnet(IPAddress address, IPAddress network, int prefix)
        {
            var addressBytes = address.GetAddressBytes();
            var networkBytes = network.GetAddressBytes();
            if (addressBytes.Length != networkBytes.Length)
                return false;

            var fullBytes = prefix / 8;
            var remainingBits = prefix % 8;

            for (var i = 0; i < fullBytes; i++)
            {
                if (addressBytes[i] != networkBytes[i])
                    return false;
            }

            if (remainingBits == 0)
                return true;

            var mask = (byte)(0xFF << (8 - remainingBits));
            return (addressBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
        }

        static bool DomainMatches(string host, string domain)
        {
            var normalizedDomain = domain.ToLowerInvariant();
            return host.Equals(normalizedDomain, StringComparison.Ordinal)
                || host.EndsWith("." + normalizedDomain, StringComparison.Ordinal);
        }
    }
}
