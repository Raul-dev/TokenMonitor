using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TokenMonitor;

public sealed class AppSettings
{
    public string LitellmBaseUrl { get; set; } = "https://litellm.mydomen";
    public string ApiKey { get; set; } = string.Empty;
    public string OpencodeConfigPath { get; set; } = SettingsStore.DefaultOpencodeConfigPath;
    public int BudgetRefreshSeconds { get; set; } = 60;
}

public static class SettingsStore
{
    public static string DefaultOpencodeConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "opencode", "opencode.jsonc");

    public static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TokenMonitor");

    public static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                if (settings is not null)
                {
                    if (string.IsNullOrWhiteSpace(settings.LitellmBaseUrl)) settings.LitellmBaseUrl = "https://litellm.mydomen";
                    if (string.IsNullOrWhiteSpace(settings.OpencodeConfigPath)) settings.OpencodeConfigPath = DefaultOpencodeConfigPath;
                    if (settings.BudgetRefreshSeconds < 10) settings.BudgetRefreshSeconds = 10;
                    return settings;
                }
            }
        }
        catch
        {
        }

        var defaults = new AppSettings();
        TrySave(defaults);
        return defaults;
    }

    public static void TrySave(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }

    public static void EnsureExists(AppSettings settings) => TrySave(settings);
}

public sealed class BudgetInfo
{
    public double? Spend { get; init; }
    public double? MaxBudget { get; init; }
    public DateTime? ResetAt { get; init; }
    public string? Alias { get; init; }
    public string? Error { get; init; }

    public double? Remaining => MaxBudget.HasValue ? Math.Max(0, MaxBudget.Value - (Spend ?? 0)) : null;
    public double Ratio => MaxBudget is > 0 ? (Spend ?? 0) / MaxBudget.Value : 0;
}

public sealed class BudgetService
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    static readonly Regex ApiKeyRegex = new("\"apiKey\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);

    readonly AppSettings settings;

    public BudgetService(AppSettings settings) => this.settings = settings;

    public AppSettings Settings => settings;

    public string? ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(settings.ApiKey)) return settings.ApiKey.Trim();

        var fromEnvironment = Environment.GetEnvironmentVariable("MYDOMEN_API_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment.Trim();

        try
        {
            if (File.Exists(settings.OpencodeConfigPath))
            {
                var match = ApiKeyRegex.Match(File.ReadAllText(settings.OpencodeConfigPath));
                if (match.Success) return match.Groups[1].Value;
            }
        }
        catch
        {
        }

        return null;
    }

    public async Task<BudgetInfo> GetAsync(CancellationToken token = default)
    {
        var apiKey = ResolveApiKey();
        if (apiKey is null)
        {
            return new BudgetInfo { Error = $"не найден API-ключ: {settings.OpencodeConfigPath}" };
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, settings.LitellmBaseUrl.TrimEnd('/') + "/key/info");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await Http.SendAsync(request, token);
            var body = await response.Content.ReadAsStringAsync(token);

            if (!response.IsSuccessStatusCode)
            {
                return new BudgetInfo { Error = $"LiteLLM ответил {(int)response.StatusCode}" };
            }

            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("info", out var info))
            {
                return new BudgetInfo { Error = "LiteLLM вернул неожиданный ответ" };
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
            };
        }
        catch (Exception error)
        {
            return new BudgetInfo { Error = error.Message };
        }
    }
}
