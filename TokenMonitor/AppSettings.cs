using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using TokenMonitor.Proxy;

namespace TokenMonitor;

public enum DataSource
{
    OpenCode,
    Cursor,
    Claude,
    Devin,
    // Новые источники добавляются только в конец: иначе сохранённый
    // числовой LastSource в settings.json сдвинется.
    DeepSeek
}

public enum BudgetSource
{
    Auto,
    LiteLLM,
    ExternalAPI,
}

public sealed class ProviderConfig
{
    public string Name { get; set; } = string.Empty;
    public string ConfigPath { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public string? StateDbPath { get; set; }
    public string? EventsPath { get; set; }

    /// <summary>Путь к SQLite-базе источника (DeepSeek: tokens.db плагина).</summary>
    public string? DbPath { get; set; }

    public string? ApiKey { get; set; }
    public string? OrgId { get; set; }
}

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
    public const int DefaultAlertWindowHours = 8;

    // Общие глобальные настройки
    public string LiteLLMBaseUrl { get; set; } = DefaultLiteLLMBaseUrl;
    public string LiteLLMApiKey { get; set; } = string.Empty;
    public BudgetSource BudgetSource { get; set; } = BudgetSource.Auto;
    public int BudgetRefreshSeconds { get; set; } = 60;
    public bool AlertWindowEnabled { get; set; } = true;
    public int AlertWindowHours { get; set; } = DefaultAlertWindowHours;
    public DataSource LastSource { get; set; } = DataSource.OpenCode;
    public bool LocalProxyServerEnabled { get; set; }
    public ProxySettings ProxySettings { get; set; } = new();

    /// <summary>Реальный usage аккаунта platform.deepseek.com (TZ_ACCOUNT_USAGE).</summary>
    public DeepSeekAccountSettings DeepSeekAccount { get; set; } = new();

    // Конфигурации провайдеров
    public ProviderConfig OpenCode { get; set; } = new()
    {
        Name = "OpenCode",
        ConfigPath = SettingsStore.DefaultOpencodeConfigPath,
        ApiKey = string.Empty,
        OrgId = string.Empty
    };

    public ProviderConfig Cursor { get; set; } = new()
    {
        Name = "Cursor",
        ConfigPath = SettingsStore.DefaultCursorConfigPath,
        IsEnabled = false,
        StateDbPath = string.Empty,
        EventsPath = string.Empty
    };

    public ProviderConfig Claude { get; set; } = new()
    {
        Name = "Claude",
        ConfigPath = SettingsStore.DefaultClaudeConfigPath,
        IsEnabled = false
    };

    public ProviderConfig Devin { get; set; } = new()
    {
        Name = "Devin",
        ConfigPath = SettingsStore.DefaultDevinConfigPath,
        ApiKey = string.Empty,
        OrgId = string.Empty
    };

    public ProviderConfig DeepSeek { get; set; } = new()
    {
        Name = "DeepSeek",
        ConfigPath = SettingsStore.DefaultDeepSeekConfigPath,
        DbPath = SettingsStore.DefaultDeepSeekDbPath
    };

    public ProviderConfig For(DataSource source) => source switch
    {
        DataSource.Cursor => Cursor,
        DataSource.Claude => Claude,
        DataSource.Devin => Devin,
        DataSource.DeepSeek => DeepSeek,
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
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Devin", "config.json");

    /// <summary>Домашний каталог DSH: $DSH_HOME, иначе %USERPROFILE%\.dsh.</summary>
    public static string DshHome
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("DSH_HOME");
            if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        }
    }

    /// <summary>Патч профиля desktop — там живёт config плагина token-logger.</summary>
    public static string DefaultDeepSeekConfigPath =>
        Path.Combine(DshHome, "profiles", "desktop", "cordis.patch.yml");

    /// <summary>База, которую пишет плагин @local/dsh-token-logger.</summary>
    public static string DefaultDeepSeekDbPath => Path.Combine(DshHome, "tokens", "tokens.db");

    /// <summary>Credentials DSH с grant'ом DeepSeek-аккаунта.</summary>
    public static string DefaultDeepSeekCredentialsPath => Path.Combine(DshHome, ".credentials.yaml");

    public static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TokenMonitor");

    public static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

    public static AppSettings Load()
    {
        if (File.Exists(SettingsPath))
        {
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json);
            if (settings is not null)
            {
                var hadAccountSection = HasProperty(json, nameof(AppSettings.DeepSeekAccount));

                ApplyLegacy(settings, json);
                Normalize(settings);
                ApplyRuntime(settings);

                // Новая секция появляется в settings.json сразу, чтобы её можно было
                // править руками, не угадывая имена полей (TZ §5).
                if (!hadAccountSection) TrySave(settings);

                return settings;
            }


            var fallback = new AppSettings();
            Normalize(fallback);
            ApplyRuntime(fallback);
            return fallback;
        }

        var defaults = new AppSettings();
        Normalize(defaults);
        ApplyRuntime(defaults);
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

    public static void EnsureExists(AppSettings settings)
    {
        if (!File.Exists(SettingsPath)) TrySave(settings);
    }

    static void Normalize(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.LiteLLMBaseUrl)) settings.LiteLLMBaseUrl = AppSettings.DefaultLiteLLMBaseUrl;
        if (!Enum.IsDefined(settings.BudgetSource)) settings.BudgetSource = BudgetSource.Auto;
        if (settings.BudgetRefreshSeconds < 10) settings.BudgetRefreshSeconds = 10;
        if (settings.AlertWindowHours is < 1 or > 24 * 365) settings.AlertWindowHours = AppSettings.DefaultAlertWindowHours;

        settings.ProxySettings ??= new ProxySettings();
        settings.ProxySettings.BypassList ??= new();
        if (string.IsNullOrWhiteSpace(settings.ProxySettings.ListenUrl))
            settings.ProxySettings.ListenUrl = ProxySettings.DefaultListenUrl;
        else
            settings.ProxySettings.ListenUrl = settings.ProxySettings.ListenUrl.Trim();
        settings.ProxySettings.UpstreamUrl = (settings.ProxySettings.UpstreamUrl ?? string.Empty).Trim();

        if (settings.ProxySettings.Enabled && !settings.ProxySettings.TryInitialize())
        {
            settings.ProxySettings.Enabled = false;
        }

        settings.OpenCode = EnsureProvider(settings.OpenCode, "OpenCode", DefaultOpencodeConfigPath);
        settings.Cursor = EnsureProvider(settings.Cursor, "Cursor", DefaultCursorConfigPath);
        settings.Claude = EnsureProvider(settings.Claude, "Claude", DefaultClaudeConfigPath);
        settings.Devin = EnsureProvider(settings.Devin, "Devin", DefaultDevinConfigPath);
        settings.DeepSeek = EnsureProvider(settings.DeepSeek, "DeepSeek", DefaultDeepSeekConfigPath);
        if (string.IsNullOrWhiteSpace(settings.DeepSeek.DbPath))
            settings.DeepSeek.DbPath = DefaultDeepSeekDbPath;

        settings.DeepSeekAccount = DeepSeekAccountSettings.Normalize(settings.DeepSeekAccount);
    }

    static void ApplyRuntime(AppSettings settings) =>
        GlobalRouteHttpClient.ProxySettings = settings.ProxySettings;

    static ProviderConfig EnsureProvider(ProviderConfig config, string name, string defaultConfigPath)
    {
        if (config is null) config = new ProviderConfig();
        
        if (string.IsNullOrWhiteSpace(config.Name)) config.Name = name;
        if (string.IsNullOrWhiteSpace(config.ConfigPath)) config.ConfigPath = defaultConfigPath;
        config.ApiKey = config.ApiKey?.Trim();
        config.OrgId = config.OrgId?.Trim();
        
        return config;
    }

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

            if (string.IsNullOrWhiteSpace(settings.OpenCode.ApiKey)
                && TryGetString(root, "OpenCodeApiKey", out var openCodeApiKey))
            {
                settings.OpenCode.ApiKey = openCodeApiKey;
            }

            if (string.IsNullOrWhiteSpace(settings.OpenCode.OrgId)
                && TryGetString(root, "OpenCodeOrgId", out var openCodeOrgId))
            {
                settings.OpenCode.OrgId = openCodeOrgId;
            }

            if (!root.TryGetProperty(nameof(AppSettings.OpenCode), out _))
            {
                var path = FirstNonEmpty(root, "ConfigOpenCode", "OpencodeConfigPath");
                if (path is not null) settings.OpenCode.ConfigPath = path;
            }

            if (!root.TryGetProperty(nameof(AppSettings.Cursor), out _))
            {
                var path = FirstNonEmpty(root, "ConfigCursor");
                var stateDb = FirstNonEmpty(root, "CursorStateDbPath");
                var events = FirstNonEmpty(root, "CursorEventsPath");
                if (path is not null || stateDb is not null || events is not null)
                {
                    if (path is not null) settings.Cursor.ConfigPath = path;
                    if (stateDb is not null) settings.Cursor.StateDbPath = stateDb;
                    if (events is not null) settings.Cursor.EventsPath = events;
                }
            }

            if (!root.TryGetProperty(nameof(AppSettings.Claude), out _))
            {
                var path = FirstNonEmpty(root, "ConfigClaude");
                if (path is not null) settings.Claude.ConfigPath = path;
            }

            if (!root.TryGetProperty(nameof(AppSettings.Devin), out _))
            {
                var path = FirstNonEmpty(root, "ConfigDevin");
                var devinKey = FirstNonEmpty(root, "DevinApiKey");
                var orgId = FirstNonEmpty(root, "DevinOrgId");
                if (path is not null || devinKey is not null || orgId is not null)
                {
                    if (path is not null) settings.Devin.ConfigPath = path;
                    if (devinKey is not null) settings.Devin.ApiKey = devinKey;
                    if (orgId is not null) settings.Devin.OrgId = orgId;
                }
            }

            if (!root.TryGetProperty(nameof(AppSettings.DeepSeek), out _))
            {
                var path = FirstNonEmpty(root, "ConfigDeepSeek");
                var dbPath = FirstNonEmpty(root, "DeepSeekDbPath");
                if (path is not null) settings.DeepSeek.ConfigPath = path;
                if (dbPath is not null) settings.DeepSeek.DbPath = dbPath;
            }
        }
        catch
        {
        }
    }

    static bool HasProperty(string json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(name, out _);
        }
        catch
        {
            return false;
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
