namespace TokenMonitor.DataProviders;

public sealed class ClaudeDataProvider : IDataProvider
{
    const string NotImplemented = "Claude - не реализовано";

    readonly AppSettings settings;

    public ClaudeDataProvider(AppSettings settings) => this.settings = settings;

    public DataSource Source => DataSource.Claude;
    public string DisplayName => "Claude";
    public ProviderConfig Config => settings.Claude;
    public string PathText => NotImplemented;
    public string TreeHint => NotImplemented;
    public bool IsEnabled => settings.Claude.IsEnabled;
    public bool SupportsSessionLaunch => false;

    public Task<ProviderResult> LoadAsync(bool force, CancellationToken token = default) =>
        Task.FromResult(new ProviderResult
        {
            Snapshot = DataSnapshot.Empty(NotImplemented),
        });

    public Task<BudgetUiModel> GetBudgetAsync(bool force, CancellationToken token = default) =>
        Task.FromResult(new BudgetUiModel("—", "Claude не реализован", "Claude не реализован", BudgetSeverity.Muted));
}
