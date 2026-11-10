namespace TokenMonitor.DataProviders;

public sealed class DevinDataProvider : IDataProvider
{
    const string DefaultTreeHint = "Статистика использования Devin API v3 по категориям задач.";

    readonly AppSettings settings;
    readonly DevinStore store;

    DevinSnapshot? snapshot;

    public DevinDataProvider(AppSettings settings)
    {
        this.settings = settings;
        store = new DevinStore(settings);
    }

    public DataSource Source => DataSource.Devin;
    public string DisplayName => "Devin";
    public ProviderConfig Config => settings.Devin;
    public bool IsEnabled => settings.Devin.IsEnabled;
    public bool SupportsSessionLaunch => false;

    public string PathText => snapshot?.DbPath is { Length: > 0 } path
        ? path
        : store.IsConfigured
            ? $"Devin API v3 (org: {settings.Devin.OrgId})"
            : "Devin API не настроен";

    public string TreeHint => store.IsConfigured
        ? DefaultTreeHint
        : "Статистика использования Devin API v3. Настройте API ключ и Org ID в конфиге.";

    public async Task<ProviderResult> LoadAsync(bool force, CancellationToken token = default)
    {
        snapshot = await store.LoadAsync(token);
        var loaded = DevinStore.ToDataSnapshot(snapshot);

        var errors = CollectErrors(snapshot);
        if (errors.Count == 0)
            return new ProviderResult { Snapshot = loaded };

        var message = $"Devin API: {string.Join("; ", errors)}";
        return new ProviderResult
        {
            Snapshot = loaded,
            TreeHint = message,
            StatusNote = $"{message} · сессий {loaded.Sessions.Count}",
        };
    }

    public Task<BudgetUiModel> GetBudgetAsync(bool force, CancellationToken token = default) =>
        Task.FromResult(MapBudget(snapshot));

    static BudgetUiModel MapBudget(DevinSnapshot? devinSnapshot)
    {
        if (devinSnapshot is null)
            return BudgetUiModel.Empty;

        if (devinSnapshot.UsageMetrics?.Error is not null)
            return new BudgetUiModel("—", devinSnapshot.UsageMetrics.Error, devinSnapshot.UsageMetrics.Error, BudgetSeverity.Muted);

        var usage = devinSnapshot.UsageMetrics;
        var sessions = usage?.SessionsCount ?? 0;

        var details = new List<string>();
        if (usage?.PrsCreatedCount > 0)
            details.Add($"PRs: {usage.PrsCreatedCount}");
        if (usage?.PrsMergedCount > 0)
            details.Add($"merged: {usage.PrsMergedCount}");
        if (usage?.SearchesCount > 0)
            details.Add($"searches: {usage.SearchesCount}");

        var tooltip = $"Сессий: {sessions}";
        if (devinSnapshot.SessionMetrics?.AvgAcusPerSession > 0)
            tooltip += $"\nСреднее ACU/сессия: {devinSnapshot.SessionMetrics.AvgAcusPerSession:0.0}";
        if (devinSnapshot.CategoryMetrics?.Categories.Count > 0)
            tooltip += $"\nКатегорий: {devinSnapshot.CategoryMetrics.Categories.Count}";
        tooltip += "\nисточник: Devin API v3";

        return new BudgetUiModel(
            sessions.ToString(),
            details.Count > 0 ? string.Join(" · ", details) : "сессий за месяц",
            tooltip,
            BudgetSeverity.Normal);
    }

    static List<string> CollectErrors(DevinSnapshot devinSnapshot)
    {
        var errors = new List<string>();
        if (devinSnapshot.UsageMetrics?.Error is not null)
            errors.Add($"usage: {devinSnapshot.UsageMetrics.Error}");
        if (devinSnapshot.SessionMetrics?.Error is not null)
            errors.Add($"session: {devinSnapshot.SessionMetrics.Error}");
        if (devinSnapshot.CategoryMetrics?.Error is not null)
            errors.Add($"category: {devinSnapshot.CategoryMetrics.Error}");
        return errors;
    }
}
