namespace TokenMonitor;

/// <summary>
/// Настройки секции <c>DeepSeekAccount</c> (TZ_ACCOUNT_USAGE §5): polling приватного
/// Platform API тем же grant'ом, которым пользуется DeepSeek Harness.
/// Токен здесь не хранится — только путь к credentials и имя env-переменной.
/// </summary>
public sealed class DeepSeekAccountSettings
{
    public const string DefaultPlatformOrigin = "https://platform.deepseek.com";
    public const int DefaultRefreshSeconds = 300;
    public const int MinRefreshSeconds = 60;
    public const int MaxRefreshSeconds = 3600;

    /// <summary>Polling аккаунта; false — прежний бюджет в токенах из tokens.db.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>База Platform API. Только https; http допустим лишь для loopback.</summary>
    public string PlatformOrigin { get; set; } = DefaultPlatformOrigin;

    /// <summary>Путь к <c>.credentials.yaml</c>; пусто — %DSH_HOME%\.credentials.yaml.</summary>
    public string CredentialsPath { get; set; } = string.Empty;

    /// <summary>Имя env-переменной с platform-токеном (приоритет над файлом).</summary>
    public string TokenEnvironmentVariable { get; set; } = "DEEPSEEK_PLATFORM_TOKEN";

    /// <summary>TTL успешного снимка, 60…3600 с.</summary>
    public int RefreshSeconds { get; set; } = DefaultRefreshSeconds;

    /// <summary>Порог «заканчивается» по остатку в валюте аккаунта.</summary>
    public decimal LowBalanceWarning { get; set; } = 1.0m;

    /// <summary>Порог «критично» по остатку в валюте аккаунта.</summary>
    public decimal LowBalanceCritical { get; set; } = 0.1m;

    /// <summary>
    /// «Всего было» вручную (сумма пополнений). 0 — считать автоматически:
    /// баланс + расход по данным платформы.
    /// </summary>
    public decimal TotalBudget { get; set; }

    public static DeepSeekAccountSettings Normalize(DeepSeekAccountSettings? settings)
    {
        settings ??= new DeepSeekAccountSettings();

        settings.PlatformOrigin = NormalizeOrigin(settings.PlatformOrigin);
        settings.CredentialsPath = (settings.CredentialsPath ?? string.Empty).Trim();
        settings.TokenEnvironmentVariable = (settings.TokenEnvironmentVariable ?? string.Empty).Trim();
        if (settings.TokenEnvironmentVariable.Length == 0)
            settings.TokenEnvironmentVariable = "DEEPSEEK_PLATFORM_TOKEN";

        if (settings.RefreshSeconds < MinRefreshSeconds) settings.RefreshSeconds = MinRefreshSeconds;
        if (settings.RefreshSeconds > MaxRefreshSeconds) settings.RefreshSeconds = MaxRefreshSeconds;

        if (settings.LowBalanceWarning < 0) settings.LowBalanceWarning = 0;
        if (settings.LowBalanceCritical < 0) settings.LowBalanceCritical = 0;
        if (settings.LowBalanceCritical > settings.LowBalanceWarning)
            settings.LowBalanceCritical = settings.LowBalanceWarning;
        if (settings.TotalBudget < 0) settings.TotalBudget = 0;

        return settings;
    }

    /// <summary>Нормализует origin: https обязателен, кроме loopback (dev-сценарии).</summary>
    public static string NormalizeOrigin(string? origin)
    {
        var raw = (origin ?? string.Empty).Trim();
        if (raw.Length == 0) return DefaultPlatformOrigin;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return DefaultPlatformOrigin;

        var isLoopback = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase);

        if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) && !isLoopback)
            return DefaultPlatformOrigin;

        var builder = new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty };
        return builder.Uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>Файл credentials: настройка, иначе %DSH_HOME%\.credentials.yaml.</summary>
    public string ResolveCredentialsPath() =>
        CredentialsPath.Length > 0 ? CredentialsPath : SettingsStore.DefaultDeepSeekCredentialsPath;
}
