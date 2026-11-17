using System.IO;

namespace TokenMonitor.DataProviders;

/// <summary>
/// Источник DeepSeek Harness: читает SQLite плагина token-logger через
/// представления v_* (PLAN §7.3). Дерево, KPI, живая лента и алерты
/// работают от того же DataSnapshot, что и у остальных источников.
/// </summary>
public sealed class DeepSeekDataProvider : IDataProvider
{
    const string DefaultTreeHint =
        "Кликните по сессии, чтобы увидеть детализацию. Правый клик — скопировать ID сессии "
        + "(запуск сессии из DSH Desktop не поддерживается).";

    readonly AppSettings settings;
    readonly DeepSeekStore store;

    BudgetUiModel? cachedBudget;
    DateTime lastBudgetFetch = DateTime.MinValue;

    public DeepSeekDataProvider(AppSettings settings)
    {
        this.settings = settings;
        store = new DeepSeekStore(settings);
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
        if (!force
            && cachedBudget is not null
            && (DateTime.UtcNow - lastBudgetFetch).TotalSeconds < settings.BudgetRefreshSeconds)
        {
            return cachedBudget;
        }

        lastBudgetFetch = DateTime.UtcNow;

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

        cachedBudget = MapBudget(stats);
        return cachedBudget;
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
