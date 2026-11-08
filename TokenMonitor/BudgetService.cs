using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TokenMonitor;

public enum BudgetDisplay
{
    Money,
    Percent,
}

public sealed record BudgetMeter(string Label, string Title, double Percent, DateTime? ResetAt, bool Limited);

public sealed class BudgetInfo
{
    public double? Spend { get; init; }
    public double? MaxBudget { get; init; }
    public DateTime? ResetAt { get; init; }
    public string? Alias { get; init; }
    public string? Error { get; init; }
    public string? SourceUrl { get; init; }
    public string? Provider { get; init; }

    public BudgetDisplay Display { get; init; } = BudgetDisplay.Money;
    public double? Percent { get; init; }
    public string? Plan { get; init; }
    public IReadOnlyList<BudgetMeter> Meters { get; init; } = Array.Empty<BudgetMeter>();

    public double? Remaining => Display == BudgetDisplay.Money && MaxBudget.HasValue
        ? Math.Max(0, MaxBudget.Value - (Spend ?? 0))
        : null;

    public double Ratio => Display == BudgetDisplay.Percent
        ? Math.Clamp((Percent ?? 0) / 100, 0, 1)
        : MaxBudget is > 0 ? (Spend ?? 0) / MaxBudget.Value : 0;
}

public interface IBudgetProvider
{
    Uri Endpoint { get; }

    Task<BudgetInfo> GetAsync(CancellationToken token = default);
}

public sealed class BudgetService
{
    static readonly string[] ApiVersionSuffixes = { "/v1", "/v1beta", "/v1alpha", "/openai" };

    readonly AppSettings settings;

    public BudgetService(AppSettings settings) => this.settings = settings;

    public AppSettings Settings => settings;

    public IBudgetProvider CreateProvider() => settings.BudgetSource switch
    {
        BudgetSource.ExternalAPI => new OpenCodeBudgetProvider(settings),
        _ => new LiteLlmBudgetProvider(settings),
    };

    public string? ResolveApiKey() => new LiteLlmBudgetProvider(settings).ResolveApiKey();

    public async Task<BudgetInfo> GetAsync(CancellationToken token = default)
    {
        if (settings.BudgetSource == BudgetSource.Auto)
        {
            var openCode = new OpenCodeBudgetProvider(settings);
            if (openCode.IsAvailable)
            {
                var openCodeResult = await FetchAsync(openCode, token);
                if (openCodeResult.Error is null) return openCodeResult;

                var liteLlmResult = await FetchAsync(new LiteLlmBudgetProvider(settings), token);
                if (liteLlmResult.Error is null) return liteLlmResult;

                return new BudgetInfo
                {
                    Error = $"OpenCode: {openCodeResult.Error}; LiteLLM: {liteLlmResult.Error}",
                    SourceUrl = openCodeResult.SourceUrl ?? liteLlmResult.SourceUrl,
                };
            }
        }

        return await FetchAsync(CreateProvider(), token);
    }

    static async Task<BudgetInfo> FetchAsync(IBudgetProvider provider, CancellationToken token)
    {
        var endpoint = provider.Endpoint;

        if (!GlobalRouteHttpClient.CanAccess(endpoint))
        {
            return new BudgetInfo
            {
                Error = "внешние запросы запрещены (OnlyByProxy без прокси)",
                SourceUrl = endpoint.ToString(),
                Provider = provider is OpenCodeBudgetProvider ? "OpenCode" : "LiteLLM",
            };
        }

        return await provider.GetAsync(token);
    }

    public static string KeyInfoUrl(string? baseUrl)
    {
        var text = string.IsNullOrWhiteSpace(baseUrl) ? AppSettings.DefaultLiteLLMBaseUrl : baseUrl.Trim();
        text = text.TrimEnd('/');

        if (text.EndsWith("/key/info", StringComparison.OrdinalIgnoreCase)) return text;

        var trimmed = true;
        while (trimmed)
        {
            trimmed = false;

            foreach (var suffix in ApiVersionSuffixes)
            {
                if (!text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;

                text = text[..^suffix.Length];
                trimmed = true;
                break;
            }
        }

        return text + "/key/info";
    }
}

public sealed class LiteLlmBudgetProvider : IBudgetProvider
{
    static readonly HttpClient Http = GlobalRouteHttpClient.CreateClient(TimeSpan.FromSeconds(10));
    static readonly Regex ApiKeyRegex = new("\"apiKey\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
    static readonly Regex EnvReferenceRegex = new(@"^\{env:(?<name>[^}]+)\}$", RegexOptions.Compiled);
    static readonly string[] ApiKeyEnvironmentVariables = { "MYDOMAIN_API_KEY", "LITELLM_API_KEY" };

    readonly AppSettings settings;

    public LiteLlmBudgetProvider(AppSettings settings) => this.settings = settings;

    public Uri Endpoint => new(BudgetService.KeyInfoUrl(settings.LiteLLMBaseUrl));

    public async Task<BudgetInfo> GetAsync(CancellationToken token = default)
    {
        var endpoint = Endpoint;

        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new BudgetInfo
            {
                Error = $"не найден API-ключ: {settings.OpenCode.ConfigPath}",
                SourceUrl = endpoint.ToString(),
                Provider = "LiteLLM",
            };
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await Http.SendAsync(request, token);
            var body = await response.Content.ReadAsStringAsync(token);

            if (!response.IsSuccessStatusCode)
            {
                return new BudgetInfo
                {
                    Error = $"LiteLLM ответил {(int)response.StatusCode}",
                    SourceUrl = endpoint.ToString(),
                    Provider = "LiteLLM",
                };
            }

            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("info", out var info))
            {
                return new BudgetInfo
                {
                    Error = "LiteLLM вернул неожиданный ответ",
                    SourceUrl = endpoint.ToString(),
                    Provider = "LiteLLM",
                };
            }

            double? Number(string name)
            {
                if (!info.TryGetProperty(name, out var value)) return null;
                return value.ValueKind switch
                {
                    JsonValueKind.Number => value.GetDouble(),
                    JsonValueKind.String when double.TryParse(value.GetString(), out var parsed) => parsed,
                    _ => null,
                };
            }

            string? Text(string name) =>
                info.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

            DateTime? reset = null;
            var resetText = Text("budget_reset_at");
            if (resetText is not null && DateTimeOffset.TryParse(resetText, out var parsedReset))
            {
                reset = parsedReset.ToLocalTime().DateTime;
            }

            return new BudgetInfo
            {
                Spend = Number("spend"),
                MaxBudget = Number("max_budget"),
                ResetAt = reset,
                Alias = Text("key_alias"),
                SourceUrl = endpoint.ToString(),
                Provider = "LiteLLM",
            };
        }
        catch (Exception error)
        {
            return new BudgetInfo
            {
                Error = error.Message,
                SourceUrl = endpoint.ToString(),
                Provider = "LiteLLM",
            };
        }
    }

    public string? ResolveApiKey()
    {
        var configured = Expand(settings.LiteLLMApiKey);
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        foreach (var name in ApiKeyEnvironmentVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return ResolveApiKeyFromConfig();
    }

    string? ResolveApiKeyFromConfig()
    {
        var path = settings.OpenCode.ConfigPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch
        {
            return null;
        }

        var fromJson = ResolveApiKeyFromJson(text);
        if (!string.IsNullOrWhiteSpace(fromJson)) return Expand(fromJson);

        var match = ApiKeyRegex.Match(text);
        return match.Success ? Expand(match.Groups[1].Value) : null;
    }

    string? ResolveApiKeyFromJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (!document.RootElement.TryGetProperty("provider", out var providers)
                || providers.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var host = Endpoint.Host;
            string? fallback = null;

            foreach (var provider in providers.EnumerateObject())
            {
                if (provider.Value.ValueKind != JsonValueKind.Object) continue;

                if (!provider.Value.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!options.TryGetProperty("apiKey", out var keyElement) || keyElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var key = keyElement.GetString();
                if (string.IsNullOrWhiteSpace(key)) continue;

                fallback ??= key;

                if (options.TryGetProperty("baseURL", out var baseElement)
                    && baseElement.ValueKind == JsonValueKind.String
                    && Uri.TryCreate(baseElement.GetString(), UriKind.Absolute, out var uri)
                    && string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase))
                {
                    return key;
                }
            }

            return fallback;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string? Expand(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var match = EnvReferenceRegex.Match(value.Trim());
        if (!match.Success) return value.Trim();

        var expanded = Environment.GetEnvironmentVariable(match.Groups["name"].Value);
        return string.IsNullOrWhiteSpace(expanded) ? null : expanded.Trim();
    }
}
