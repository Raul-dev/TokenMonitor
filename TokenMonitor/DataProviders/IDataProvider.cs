namespace TokenMonitor.DataProviders;

public interface IDataProvider
{
    DataSource Source { get; }

    string DisplayName { get; }

    ProviderConfig Config { get; }

    /// Текст под именем источника (DbPathText).
    string PathText { get; }

    /// Подсказка под деревом по умолчанию.
    string TreeHint { get; }

    bool IsEnabled { get; }

    /// Умеет ли источник открывать сессию в терминале.
    bool SupportsSessionLaunch { get; }

    Task<ProviderResult> LoadAsync(bool force, CancellationToken token = default);

    /// Провайдер сам кэширует бюджет и соблюдает BudgetRefreshSeconds.
    Task<BudgetUiModel> GetBudgetAsync(bool force, CancellationToken token = default);
}
