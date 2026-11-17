using System.Globalization;
using System.IO;

namespace TokenMonitor.DataProviders;

/// <summary>
/// Источник DeepSeek Harness: читает SQLite плагина token-logger через
/// представления v_* (PLAN §7.3). Дерево, KPI, живая лента и алерты
/// работают от того же DataSnapshot, что и у остальных источников.
/// <para>Бюджет в шапке — реальный usage аккаунта platform.deepseek.com
/// (TZ_ACCOUNT_USAGE), при недоступности — локальный подсчёт по tokens.db.</para>
/// </summary>
public sealed class DeepSeekDataProvider : IDataProvider
{
    const string DefaultTreeHint =
        "Кликните по сессии, чтобы увидеть детализацию. Правый клик — скопировать ID сессии "
        + "(запуск сессии из DSH Desktop не поддерживается).";

    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    readonly AppSettings settings;
    readonly DeepSeekStore store;
    readonly DeepSeekAccountClient accountClient;

    BudgetUiModel? cachedLocalBudget;
    DateTime lastLocalFetch = DateTime.MinValue;

    public DeepSeekDataProvider(AppSettings settings)
    {
        this.settings = settings;
        store = new DeepSeekStore(settings);
        accountClient = new DeepSeekAccountClient(settings);
    }

    public DataSource Source => DataSource.DeepSeek;
    public string DisplayName => "DeepSeek";
    public ProviderConfig Config => settings.DeepSeek;
    public string PathText => store.DbPath;
    public string TreeHint => DefaultTreeHint;
    public bool IsEnabled => settings.DeepSeek.IsEnabled;
    public bool SupportsSessionLaunch => false;

    public async Task<ProviderResult> LoadAsync(bool force, CancellationToken token = default)
    {
        try
        {
            var loaded = await Task.Run(store.Load, token);

            var note = loaded.Sessions.Count == 0 && File.Exists(store.DbPath) && string.IsNullOrWhiteSpace(loaded.LoadError)
                ? "База плагина пуста. Выполните хотя бы один запрос в DeepSeek Harness."
                : null;

            return new ProviderResult
            {
                Snapshot = loaded,
                StatusNote = note,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            return new ProviderResult
            {
                Snapshot = DataSnapshot.Empty(store.DbPath, $"ошибка: {error.Message}"),
                StatusNote = $"DeepSeek: {error.Message}",
            };
        }
    }

    public async Task<BudgetUiModel> GetBudgetAsync(bool force, CancellationToken token = default)
    {
        if (!settings.DeepSeekAccount.Enabled)
            return await LocalBudgetAsync(force, token);

        DeepSeekAccountSnapshot snapshot;
        try
        {
            snapshot = await accountClient.GetAsync(force, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            // Клиент ловит всё сам; здесь — страховка, чтобы UI не падал.
            snapshot = new DeepSeekAccountSnapshot(null, DeepSeekAccountError.Unknown,
                error.Message, DateTimeOffset.Now, false);
        }

        if (snapshot.Usage is not null)
            return MapAccount(snapshot, settings.DeepSeekAccount);

        return WithAccountFallback(await LocalBudgetAsync(force, token), snapshot);
    }

    // ----- аккаунт -------------------------------------------------------------

    /// <summary>
    /// Маппинг снимка аккаунта в шапку: крупно «потрачено / всего было», мелко — остаток.
    /// Публичный для приёмки (AccountCheck) без запуска окна.
    /// </summary>
    public static BudgetUiModel MapAccount(DeepSeekAccountSnapshot snapshot, DeepSeekAccountSettings config)
    {
        var usage = snapshot.Usage!;

        var total = config.TotalBudget > 0 ? config.TotalBudget : usage.TotalBudget;
        var remaining = usage.Remaining;

        var severity = remaining <= config.LowBalanceCritical || !usage.IsAvailable
            ? BudgetSeverity.Critical
            : remaining <= config.LowBalanceWarning
                ? BudgetSeverity.Warning
                : BudgetSeverity.Normal;

        // Крупно: «$0,92 / $10,00» — потрачено / всего было.
        var text = $"{Fmt.Money(usage.MonthCost, usage.Currency)} / {Fmt.Money(total, usage.Currency)}";

        var detail = $"осталось {Fmt.Money(remaining, usage.Currency)} · {Spaced(usage.Requests)} запр.";
        if (snapshot.IsStale) detail += " · устарело";

        var tooltip = new List<string>
        {
            $"аккаунт DeepSeek · {OriginLabel(config)}",
            $"расход за {MonthLabel(usage.Month)} {usage.Year}: {Fmt.MoneyExact(usage.MonthCost, usage.Currency)}",
            $"баланс: {Fmt.MoneyExact(usage.Balance, usage.Currency)} (оплачено {Fmt.MoneyExact(usage.BalancePaid, usage.Currency)}, бонусы {Fmt.MoneyExact(usage.BalanceBonus, usage.Currency)})",
            $"всего было: {Fmt.MoneyExact(total, usage.Currency)} · осталось: {Fmt.MoneyExact(remaining, usage.Currency)}",
        };

        if (config.TotalBudget > 0)
            tooltip.Add($"«всего было» задано вручную: DeepSeekAccount.TotalBudget = {Fmt.MoneyExact(config.TotalBudget, usage.Currency)}");
        else if (usage.TotalSpent > usage.MonthCost)
            tooltip.Add($"израсходовано по данным платформы: {Fmt.MoneyExact(usage.TotalSpent, usage.Currency)}");

        tooltip.Add($"запросов за месяц: {Spaced(usage.Requests)}");
        tooltip.Add($"токены: cache hit {Spaced(usage.TokensCacheHit)} · cache miss {Spaced(usage.TokensCacheMiss)} · output {Spaced(usage.TokensOutput)}");

        if (usage.TokensPrompt > 0 && usage.TokensCacheHit + usage.TokensCacheMiss + usage.TokensOutput == 0)
            tooltip.Add($"prompt: {Spaced(usage.TokensPrompt)}");

        foreach (var model in usage.Models.Take(6))
            tooltip.Add($"{model.Model}: {Spaced(model.Tokens)} токенов · {Fmt.MoneyExact(model.Cost, usage.Currency)}");

        foreach (var wallet in usage.Wallets.Where(w => !Same(w.Currency, usage.Currency)))
            tooltip.Add($"другая валюта: {wallet.Currency} · {Fmt.MoneyExact(wallet.Total, wallet.Currency)}");

        var updated = snapshot.IsStale ? usage.FetchedAt : snapshot.FetchedAt;
        tooltip.Add($"обновлено: {updated:dd.MM.yyyy HH:mm:ss}");

        if (snapshot.IsStale)
        {
            var minutes = Math.Max(0, (int)(DateTimeOffset.Now - usage.FetchedAt).TotalMinutes);
            tooltip.Add($"данные устарели на {minutes} мин: {Short(snapshot.ErrorMessage, 120)}");
        }
        if (!usage.IsAvailable) tooltip.Add("баланс исчерпан: пополните счёт platform.deepseek.com");

        return new BudgetUiModel(text, detail, string.Join("\n", tooltip), severity);
    }

    static BudgetUiModel WithAccountFallback(BudgetUiModel local, DeepSeekAccountSnapshot snapshot)
    {
        var reason = Reason(snapshot);

        return new BudgetUiModel(
            local.Text,
            $"локально · {reason}",
            $"{local.Tooltip}\n\nаккаунт platform.deepseek.com недоступен: {reason}",
            local.Severity == BudgetSeverity.Normal ? BudgetSeverity.Muted : local.Severity);
    }

    static string Reason(DeepSeekAccountSnapshot snapshot) => snapshot.Error switch
    {
        DeepSeekAccountError.NoCredentials => "нет входа в DeepSeek",
        DeepSeekAccountError.CredentialsFormat => "credentials DSH не распознаны",
        DeepSeekAccountError.TokenExpired => "сессия DeepSeek истекла — войдите в DeepSeek Harness",
        DeepSeekAccountError.Network => "нет связи с platform.deepseek.com",
        DeepSeekAccountError.ProxyBlocked => "нужен прокси для внешнего доступа",
        DeepSeekAccountError.ApiChanged => "API аккаунта изменился",
        DeepSeekAccountError.InvalidMonth => "неверный месяц",
        _ => Short(snapshot.ErrorMessage, 80),
    };

    static string OriginLabel(DeepSeekAccountSettings config) =>
        Uri.TryCreate(DeepSeekAccountSettings.NormalizeOrigin(config.PlatformOrigin), UriKind.Absolute, out var uri)
            ? uri.Host
            : DeepSeekAccountSettings.DefaultPlatformOrigin;

    static string MonthLabel(int month) =>
        month is >= 1 and <= 12 ? Ru.DateTimeFormat.GetMonthName(month) : month.ToString(CultureInfo.InvariantCulture);

    static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>«80 044 800» — разряды через неразрывный пробел, как в русском счёте.</summary>
    static string Spaced(long value) => value.ToString("N0", CultureInfo.InvariantCulture).Replace(',', '\u00A0');

    static string Short(string value, int max)
    {
        var flat = string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= max ? flat : flat[..max].TrimEnd() + "…";
    }

    // ----- локальный fallback ---------------------------------------------------

    async Task<BudgetUiModel> LocalBudgetAsync(bool force, CancellationToken token)
    {
        if (!force
            && cachedLocalBudget is not null
            && (DateTime.UtcNow - lastLocalFetch).TotalSeconds < settings.BudgetRefreshSeconds)
        {
            return cachedLocalBudget;
        }

        lastLocalFetch = DateTime.UtcNow;

        var now = DateTime.Now;
        var monthStart = new DateTime(now.Year, now.Month, 1);
        BudgetStats stats;
        try
        {
            stats = await Task.Run(() => store.LoadBudgetStats(monthStart), token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            stats = BudgetStats.Empty;
        }

        cachedLocalBudget = MapBudget(stats);
        return cachedLocalBudget;
    }

    static BudgetUiModel MapBudget(BudgetStats stats)
    {
        if (!stats.HasData)
        {
            return new BudgetUiModel("—", "нет данных плагина", "БД плагина token-logger пуста или недоступна.", BudgetSeverity.Muted);
        }

        var severity = stats.ContextPercent switch
        {
            >= 90 => BudgetSeverity.Critical,
            >= 70 => BudgetSeverity.Warning,
            _ => BudgetSeverity.Normal,
        };

        var contextText = stats.ContextWindow > 0
            ? $"контекст {stats.ContextPercent:0.0}% окна"
            : "контекст —";
        var detail = $"кэш {stats.CacheHitPercent:0.0}% · {contextText}";

        var tooltip = new List<string>
        {
            $"токены за текущий месяц: {Fmt.Exact(stats.MonthTokens)}",
            $"оплачиваемых шагов: {stats.Requests}",
            $"кэш-хит: {stats.CacheHitPercent:0.0}% ({Fmt.Exact(stats.CacheRead)} из {Fmt.Exact(stats.Input + stats.CacheRead)})",
        };

        if (stats.ContextWindow > 0)
        {
            tooltip.Add($"давление на контекст: {Fmt.Exact(stats.LastContext)} из {Fmt.Exact(stats.ContextWindow)} ({stats.ContextPercent:0.0}%)");
        }

        if (stats.LastSessionTokens > 0)
        {
            var title = string.IsNullOrWhiteSpace(stats.LastSessionTitle) ? "последняя сессия" : Fmt.Preview(stats.LastSessionTitle, 60);
            tooltip.Add($"последняя сессия «{title}»: {Fmt.Exact(stats.LastSessionTokens)} токенов");
        }

        tooltip.Add("источник: tokens.db (плагин @local/dsh-token-logger)");

        return new BudgetUiModel(Fmt.Tokens(stats.MonthTokens), detail, string.Join("\n", tooltip), severity);
    }
}
