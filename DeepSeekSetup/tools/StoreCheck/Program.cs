// Приёмочная проверка DeepSeekStore на живой базе плагина (PLAN §9.3, п.1/9).
// dotnet run --project DeepSeekSetup/tools/StoreCheck [dbPath]
using System.IO;
using TokenMonitor;
using TokenMonitor.DataProviders;

var dbPath = args.Length > 0 ? args[0] : SettingsStore.DefaultDeepSeekDbPath;
Console.WriteLine($"db: {dbPath}");
Console.WriteLine($"exists: {File.Exists(dbPath)}");

var settings = new AppSettings();
settings.DeepSeek.DbPath = dbPath;
var store = new DeepSeekStore(settings);
var snapshot = store.Load();

Console.WriteLine($"loadError: {(string.IsNullOrWhiteSpace(snapshot.LoadError) ? "—" : snapshot.LoadError)}");
Console.WriteLine($"sessions: {snapshot.Sessions.Count}, live: {snapshot.Live.Count}, signature: {snapshot.Signature}, window: {snapshot.ContextWindow}");

foreach (var session in snapshot.Sessions)
{
    var requests = session.Requests.Count;
    var steps = session.Requests.Sum(request => request.Steps.Count);
    Console.WriteLine($"  [{session.Id}] «{session.Title}» {Fmt.Folder(session.Directory)} · {session.Model} · requests={requests} steps={steps} tokens={session.Tokens.Total} (in {session.Tokens.Input}, out {session.Tokens.Output}, cache {session.Tokens.CacheRead}, hit {session.Tokens.CacheHitText}) maxCtx={session.MaxContext}");
    var lastRequest = session.Requests.LastOrDefault();
    if (lastRequest is not null)
    {
        var lastStep = lastRequest.Steps.LastOrDefault();
        Console.WriteLine($"    last turn: {lastRequest.Id} steps={lastRequest.Steps.Count} prompt={Fmt.Preview(lastRequest.Prompt, 50)} time={lastRequest.Time:HH:mm:ss}");
        if (lastStep is not null)
            Console.WriteLine($"    last step: {lastStep.Id} time={lastStep.Time:HH:mm:ss} tokens={lastStep.Tokens.Total} model={lastStep.Model}");
    }
}

var live = snapshot.Live.FirstOrDefault();
if (live is not null)
    Console.WriteLine($"live[0]: {live.Time:HH:mm:ss} {live.SessionId} step={live.MessageId} billed={live.BilledText} duration={live.DurationText} ctx={live.Context}");

var stats = store.LoadBudgetStats(new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1));
Console.WriteLine($"budget: month={stats.MonthTokens} requests={stats.Requests} cache={stats.CacheHitPercent:0.0}% ctx={stats.LastContext}/{stats.ContextWindow} ({stats.ContextPercent:0.0}%) session={stats.LastSessionTokens} «{stats.LastSessionTitle}»");

var provider = new DeepSeekDataProvider(settings);
var budget = await provider.GetBudgetAsync(force: true);
Console.WriteLine($"budget UI: {budget.Text} | {budget.Detail} | {budget.Severity}");
Console.WriteLine(budget.Tooltip);

var result = await provider.LoadAsync(force: true);
Console.WriteLine($"provider: sessions={result.Snapshot.Sessions.Count} note={result.StatusNote ?? "—"} path={provider.PathText}");
Console.WriteLine($"supportsLaunch={provider.SupportsSessionLaunch} hint={provider.TreeHint}");

// Отсутствующая БД должна давать «нет данных», а не исключение.
var missing = new DeepSeekStore(Path.Combine(Path.GetTempPath(), "token-monitor-no-such-dir", "none.db"));
var empty = missing.Load();
Console.WriteLine($"missing db -> sessions={empty.Sessions.Count} error={Fmt.Preview(empty.LoadError, 120)}");

// Сверка (PLAN §9.2): сумма шагов == сумма сессий.
foreach (var session in snapshot.Sessions)
{
    var stepSum = session.Requests.SelectMany(request => request.Steps).Sum(step => step.Tokens.Total);
    if (stepSum != session.Tokens.Total)
        Console.WriteLine($"MISMATCH {session.Id}: steps={stepSum} session={session.Tokens.Total}");
}
Console.WriteLine("done");
