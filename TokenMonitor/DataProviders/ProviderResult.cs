namespace TokenMonitor.DataProviders;

public sealed class ProviderResult
{
    public required DataSnapshot Snapshot { get; init; }

    /// Подсказка под деревом; null = значение по умолчанию из IDataProvider.TreeHint.
    public string? TreeHint { get; init; }

    /// Готовое сообщение в StatusText; null = стандартная строка со счётчиками.
    public string? StatusNote { get; init; }
}
