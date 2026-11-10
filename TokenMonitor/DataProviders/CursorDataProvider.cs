namespace TokenMonitor.DataProviders;

public sealed class CursorDataProvider : IDataProvider
{
    const string DefaultTreeHint =
        "Кликните по сессии, чтобы увидеть детализацию. Правый клик — открыть сессию в Cursor.";

    readonly AppSettings settings;
    readonly CursorStore store;

    BudgetUiModel? cachedBudget;
    DateTime lastBudgetFetch = DateTime.MinValue;

    public CursorDataProvider(AppSettings settings)
    {
        this.settings = settings;
        store = new CursorStore(settings);
    }

    public DataSource Source => DataSource.Cursor;
    public string DisplayName => "Cursor";
    public ProviderConfig Config => settings.Cursor;
    public string PathText => store.EventsPath;
    public string TreeHint => DefaultTreeHint;
    public bool IsEnabled => settings.Cursor.IsEnabled;
    public bool SupportsSessionLaunch => false;

    public async Task<ProviderResult> LoadAsync(bool force, CancellationToken token = default)
    {
        var loaded = await store.LoadAsync(refreshRemote: true, token);
        return new ProviderResult { Snapshot = loaded };
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
        var period = await store.GetPeriodAsync(token);
        cachedBudget = MapBudget(period);
        return cachedBudget;
    }

    static BudgetUiModel MapBudget(CursorPeriodUsage period)
    {
        if (period.Error is not null)
            return new BudgetUiModel("—", period.Error, period.Error, BudgetSeverity.Muted);

        var totalPercent = period.TotalPercent;
        var severity = totalPercent switch
        {
            >= 90 => BudgetSeverity.Critical,
            >= 70 => BudgetSeverity.Warning,
            _ => BudgetSeverity.Normal,
        };

        var details = new List<string>();
        if (period.CycleStart is { } start && period.CycleEnd is { } end)
            details.Add($"{start:dd.MM} - {end:dd.MM}");
        if (!string.IsNullOrWhiteSpace(period.Membership))
            details.Add(period.Membership);

        var tooltip = $"Auto: {period.AutoPercent:0.0}% · API: {period.ApiPercent:0.0}%";
        if (!string.IsNullOrWhiteSpace(period.DisplayMessage))
            tooltip += $"\n{period.DisplayMessage}";
        if (!string.IsNullOrWhiteSpace(period.Source))
            tooltip += $"\nисточник: {period.Source}";

        return new BudgetUiModel($"{totalPercent:0.0}%", string.Join(" · ", details), tooltip, severity);
    }
}
