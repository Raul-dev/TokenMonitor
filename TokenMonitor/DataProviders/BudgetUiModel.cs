namespace TokenMonitor.DataProviders;

public enum BudgetSeverity
{
    Normal,
    Warning,
    Critical,
    Muted,
}

public sealed record BudgetUiModel(
    string Text,
    string Detail,
    string? Tooltip = null,
    BudgetSeverity Severity = BudgetSeverity.Normal)
{
    public static readonly BudgetUiModel Empty = new("—", "—", null, BudgetSeverity.Muted);
}
