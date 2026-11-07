using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TokenMonitor;

public enum DataSource
{
    OpenCode,
    Cursor,
    Claude,
    Devin
}

public readonly record struct ProviderConfig(
    string Name,
    string ConfigPath,
    bool IsEnabled = true,
    string? StateDbPath = null,
    string? EventsPath = null,
    string? ApiKey = null,
    string? OrgId = null
);

public sealed class ProviderConfigEx
{
    public ProviderConfigEx(ProviderConfig config, DataSource source, string pathText, string treeHint)
    {
        Config = config;
        Source = source;
        PathText = pathText;
        TreeHint = treeHint;
    }

    public ProviderConfig Config { get; }
    public DataSource Source { get; }
    public string PathText { get; }
    public string TreeHint { get; }

    public string Name => Config.Name;
    public string ConfigPath => Config.ConfigPath;
    public bool IsEnabled => Config.IsEnabled;
    public string? StateDbPath => Config.StateDbPath;
    public string? EventsPath => Config.EventsPath;
    public string? ApiKey => Config.ApiKey;
    public string? OrgId => Config.OrgId;
}

public sealed class AppSettings
{
    public const string DefaultLiteLLMBaseUrl = "https://mydomain.loc";

    // Общие глобальные настройки
    public string LiteLLMBaseUrl { get; set; } = DefaultLiteLLMBaseUrl;
    public string LiteLLMApiKey { get; set; } = string.Empty;
    public int BudgetRefreshSeconds { get; set; } = 60;
    public DataSource LastSource { get; set; } = DataSource.OpenCode;

    // Конфигурации провайдеров
    public ProviderConfig OpenCode { get; set; } = new(
        Name: "OpenCode",
        ConfigPath: SettingsStore.DefaultOpencodeConfigPath
    );

    public ProviderConfig Cursor { get; set; } = new(
        Name: "Cursor",
        ConfigPath: SettingsStore.DefaultCursorConfigPath,
        IsEnabled: false,
        StateDbPath: string.Empty,
        EventsPath: string.Empty
    );

    public ProviderConfig Claude { get; set; } = new(
        Name: "Claude",
        ConfigPath: SettingsStore.DefaultClaudeConfigPath,
        IsEnabled: false
    );

    public ProviderConfig Devin { get; set; } = new(
        Name: "Devin",
        ConfigPath: SettingsStore.DefaultDevinConfigPath,
        ApiKey: string.Empty,
        OrgId: string.Empty
    );

    public ProviderConfig For(DataSource source) => source switch
    {
        DataSource.Cursor => Cursor,
        DataSource.Claude => Claude,
        DataSource.Devin => Devin,
        _ => OpenCode,
    };
}

public static class SettingsStore
{
    public static string DefaultOpencodeConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "opencode", "opencode.jsonc");

    public static string DefaultCursorConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "cursor", "cursor.jsonc");

    public static string DefaultClaudeConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "claude", "claude.jsonc");

    public static string DefaultDevinConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "devin", "devin.jsonc");

    public static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TokenMonitor");

    public static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings is not null)
                {
                    ApplyLegacy(settings, json);
                    Normalize(settings);
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

    static void Normalize(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.LiteLLMBaseUrl)) settings.LiteLLMBaseUrl = AppSettings.DefaultLiteLLMBaseUrl;
        if (settings.BudgetRefreshSeconds < 10) settings.BudgetRefreshSeconds = 10;

        settings.OpenCode = EnsureProvider(settings.OpenCode, "OpenCode", DefaultOpencodeConfigPath);
        settings.Cursor = EnsureProvider(settings.Cursor, "Cursor", DefaultCursorConfigPath);
        settings.Claude = EnsureProvider(settings.Claude, "Claude", DefaultClaudeConfigPath);
        settings.Devin = EnsureProvider(settings.Devin, "Devin", DefaultDevinConfigPath);
    }

    static ProviderConfig EnsureProvider(ProviderConfig config, string name, string defaultConfigPath) =>
        config with
        {
            Name = string.IsNullOrWhiteSpace(config.Name) ? name : config.Name,
            ConfigPath = string.IsNullOrWhiteSpace(config.ConfigPath) ? defaultConfigPath : config.ConfigPath,
        };

    static void ApplyLegacy(AppSettings settings, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            if (!root.TryGetProperty(nameof(AppSettings.LiteLLMBaseUrl), out _)
                && TryGetString(root, "LitellmBaseUrl", out var baseUrl))
            {
                settings.LiteLLMBaseUrl = baseUrl;
            }

            if (!root.TryGetProperty(nameof(AppSettings.LiteLLMApiKey), out _)
                && TryGetString(root, "ApiKey", out var apiKey))
            {
                settings.LiteLLMApiKey = apiKey;
            }

            if (!root.TryGetProperty(nameof(AppSettings.OpenCode), out _))
            {
                var path = FirstNonEmpty(root, "ConfigOpenCode", "OpencodeConfigPath");
                if (path is not null) settings.OpenCode = settings.OpenCode with { ConfigPath = path };
            }

            if (!root.TryGetProperty(nameof(AppSettings.Cursor), out _))
            {
                var path = FirstNonEmpty(root, "ConfigCursor");
                var stateDb = FirstNonEmpty(root, "CursorStateDbPath");
                var events = FirstNonEmpty(root, "CursorEventsPath");
                if (path is not null || stateDb is not null || events is not null)
                {
                    settings.Cursor = settings.Cursor with
                    {
                        ConfigPath = path ?? settings.Cursor.ConfigPath,
                        StateDbPath = stateDb ?? settings.Cursor.StateDbPath,
                        EventsPath = events ?? settings.Cursor.EventsPath,
                    };
                }
            }

            if (!root.TryGetProperty(nameof(AppSettings.Claude), out _))
            {
                var path = FirstNonEmpty(root, "ConfigClaude");
                if (path is not null) settings.Claude = settings.Claude with { ConfigPath = path };
            }

            if (!root.TryGetProperty(nameof(AppSettings.Devin), out _))
            {
                var path = FirstNonEmpty(root, "ConfigDevin");
                var devinKey = FirstNonEmpty(root, "DevinApiKey");
                var orgId = FirstNonEmpty(root, "DevinOrgId");
                if (path is not null || devinKey is not null || orgId is not null)
                {
                    settings.Devin = settings.Devin with
                    {
                        ConfigPath = path ?? settings.Devin.ConfigPath,
                        ApiKey = devinKey ?? settings.Devin.ApiKey,
                        OrgId = orgId ?? settings.Devin.OrgId,
                    };
                }
            }
        }
        catch
        {
        }
    }

    static string? FirstNonEmpty(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetString(root, name, out var value) && !string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }

    static bool TryGetString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? string.Empty;
        return true;
    }
}

public sealed class BudgetInfo
{
    public double? Spend { get; init; }
    public double? MaxBudget { get; init; }
    public DateTime? ResetAt { get; init; }
    public string? Alias { get; init; }
    public string? Error { get; init; }
    public string? DisplayText { get; init; }
    public string? DetailText { get; init; }

    public double? Remaining => MaxBudget.HasValue ? Math.Max(0, MaxBudget.Value - (Spend ?? 0)) : null;
    public double Ratio => MaxBudget is > 0 ? (Spend ?? 0) / MaxBudget.Value : 0;
}

public sealed class BudgetService
{
    static readonly HttpClient Http = GlobalRouteHttpClient.CreateClient(TimeSpan.FromSeconds(10));
    static readonly Regex ApiKeyRegex = new("\"apiKey\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);

    readonly AppSettings settings;

    public BudgetService(AppSettings settings) => this.settings = settings;

    public AppSettings Settings => settings;

    public string? ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(settings.LiteLLMApiKey)) return settings.LiteLLMApiKey.Trim();

        var fromEnvironment = Environment.GetEnvironmentVariable("MYDOMAIN_API_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment.Trim();

        var configPath = settings.OpenCode.ConfigPath;
        try
        {
            if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
            {
                var match = ApiKeyRegex.Match(File.ReadAllText(configPath));
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
        if (!GlobalRouteHttpClient.CanMakeExternalRequests())
        {
            return new BudgetInfo { Error = "внешние запросы запрещены (OnlyByProxy без прокси)" };
        }

        var apiKey = ResolveApiKey();
        if (apiKey is null)
        {
            return new BudgetInfo { Error = $"не найден API-ключ: {settings.OpenCode.ConfigPath}" };
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, settings.LiteLLMBaseUrl.TrimEnd('/') + "/key/info");
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
