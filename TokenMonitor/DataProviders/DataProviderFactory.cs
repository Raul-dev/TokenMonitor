namespace TokenMonitor.DataProviders;

public static class DataProviderFactory
{
    public static Dictionary<DataSource, IDataProvider> Create(AppSettings settings) => new()
    {
        [DataSource.OpenCode] = new OpenCodeDataProvider(settings),
        [DataSource.Cursor] = new CursorDataProvider(settings),
        [DataSource.Claude] = new ClaudeDataProvider(settings),
        [DataSource.Devin] = new DevinDataProvider(settings),
    };
}
