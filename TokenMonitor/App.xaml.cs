using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Serilog;
using Serilog.Events;
using TokenMonitor.Proxy;

namespace TokenMonitor;

public partial class App : Application
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AttachConsole(int processId);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ConfigureLogging();
        RegisterGlobalExceptionHandlers();

        var dumpArgument = e.Args.FirstOrDefault(argument => argument.StartsWith("--dump", StringComparison.OrdinalIgnoreCase));

        if (dumpArgument is not null)
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;

            var path = dumpArgument.Contains('=')
                ? dumpArgument[(dumpArgument.IndexOf('=') + 1)..]
                : e.Args.SkipWhile(argument => !ReferenceEquals(argument, dumpArgument)).Skip(1).FirstOrDefault();

            try
            {
                WriteDump(path);
            }
            catch (Exception error)
            {
                WriteLine($"dump failed: {error}");
            }

            Shutdown();
            return;
        }

        Log.Information("TokenMonitor запущен. Каталог логов: {LogDirectory}", Logging.LogDirectory);
        _ = Task.Run(() => GlobalRouteHttpClient.InitializeAsync());
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("TokenMonitor остановлен.");
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    static void ConfigureLogging()
    {
#if DEBUG
        const LogEventLevel minimumLevel = LogEventLevel.Debug;
#else
        const LogEventLevel minimumLevel = LogEventLevel.Information;
#endif
        Log.Logger = Logging.CreateLoggerConfiguration("tokenmonitor", minimumLevel)
            .WriteTo.Debug()
            .CreateLogger();
    }

    void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal(args.Exception, "Необработанное исключение UI");
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "Необработанное исключение приложения");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Необработанное исключение фоновой задачи");
            args.SetObserved();
        };
    }

    static void WriteDump(string? path)
    {
        var store = new DataStore();
        var snapshot = store.Load();
        var settings = SettingsStore.Load();
        var budget = Task.Run(() => new BudgetService(settings).GetAsync()).GetAwaiter().GetResult();
        var text = BuildDump(snapshot, budget);

        if (!string.IsNullOrWhiteSpace(path))
        {
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return;
        }

        AttachConsole(-1);
        Console.Out.Write(text);
        Console.Out.Flush();
    }

    static void WriteLine(string text)
    {
        AttachConsole(-1);
        Console.Out.WriteLine(text);
        Console.Out.Flush();
    }

    static string BuildDump(DataSnapshot snapshot, BudgetInfo budget)
    {
        var builder = new StringBuilder();
        var now = DateTime.Now;
        var monthStart = new DateTime(now.Year, now.Month, 1);
        var dayStart = now.Date;

        var sessions = snapshot.Sessions;
        var requests = sessions.SelectMany(session => session.Requests).ToList();
        var steps = requests.SelectMany(request => request.Steps).ToList();

        var month = new TokenCounts(
            steps.Where(s => s.Time >= monthStart).Sum(s => s.Tokens.Input),
            steps.Where(s => s.Time >= monthStart).Sum(s => s.Tokens.Output),
            steps.Where(s => s.Time >= monthStart).Sum(s => s.Tokens.Reasoning),
            steps.Where(s => s.Time >= monthStart).Sum(s => s.Tokens.CacheRead),
            steps.Where(s => s.Time >= monthStart).Sum(s => s.Tokens.CacheWrite));

        var day = new TokenCounts(
            steps.Where(s => s.Time >= dayStart).Sum(s => s.Tokens.Input),
            steps.Where(s => s.Time >= dayStart).Sum(s => s.Tokens.Output),
            steps.Where(s => s.Time >= dayStart).Sum(s => s.Tokens.Reasoning),
            steps.Where(s => s.Time >= dayStart).Sum(s => s.Tokens.CacheRead),
            steps.Where(s => s.Time >= dayStart).Sum(s => s.Tokens.CacheWrite));

        builder.AppendLine($"Token Monitor dump · {now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"db:       {snapshot.DbPath}");
        builder.AppendLine($"live:     {snapshot.EventsPath} ({snapshot.Live.Count} events)");
        builder.AppendLine($"sessions: {sessions.Count} · requests: {requests.Count} · steps: {steps.Count}");
        builder.AppendLine();
        builder.AppendLine($"month    total={month.Total} ({Fmt.Tokens(month.Total)}) in={month.Input} out={month.Output} reasoning={month.Reasoning} cache={month.Cached} cacheRead={month.CacheRead} cacheWrite={month.CacheWrite} hit={month.CacheHitText}");
        builder.AppendLine($"day      total={day.Total} ({Fmt.Tokens(day.Total)}) in={day.Input} out={day.Output} reasoning={day.Reasoning} cache={day.Cached} cacheRead={day.CacheRead} cacheWrite={day.CacheWrite} hit={day.CacheHitText}");
        builder.AppendLine($"all time total={steps.Sum(s => s.Tokens.Total)} ({Fmt.Tokens(steps.Sum(s => s.Tokens.Total))})");
        builder.AppendLine(FormatBudget(budget));
        builder.AppendLine();

        builder.AppendLine("top sessions by tokens:");
        foreach (var session in sessions.OrderByDescending(s => s.Tokens.Total).Take(10))
        {
            builder.AppendLine($"  {Fmt.Tokens(session.Tokens.Total),8}  req={session.Requests.Count,4}  {session.Updated:yyyy-MM-dd HH:mm}  {Fmt.Folder(session.Directory)}  {Trim(session.Title, 70)}");
        }

        builder.AppendLine();
        builder.AppendLine("last requests (tree):");
        foreach (var request in requests.OrderByDescending(r => r.Time).Take(10))
        {
            builder.AppendLine($"  {Fmt.Tokens(request.Tokens.Total),8}  steps={request.Steps.Count,3}  {Fmt.Duration(request.DurationMs),8}  {request.Time:yyyy-MM-dd HH:mm:ss}  {Trim(Fmt.Preview(request.Prompt, 70), 70)}");
        }

        builder.AppendLine();
        builder.AppendLine("last live events:");
        foreach (var item in snapshot.Live.OrderByDescending(x => x.Time).Take(10))
        {
            builder.AppendLine($"  {item.BilledText,8}  steps={item.Steps,3}  {item.DurationText,8}  {item.DateText}  {item.SessionText}");
        }

        return builder.ToString();
    }

    static string FormatBudget(BudgetInfo budget)
    {
        var parts = new List<string>();

        if (budget.Display == BudgetDisplay.Percent)
        {
            parts.Add($"{(budget.Percent ?? 0):0.0}%");
            if (budget.Plan is { } plan && !string.IsNullOrWhiteSpace(plan)) parts.Add(plan);
            if (budget.ResetAt is { } reset) parts.Add($"сброс {reset:dd.MM HH:mm}");
        }
        else if (budget.Spend is { } spend)
        {
            parts.Add($"${spend:0.00}" + (budget.MaxBudget is { } max ? $" / ${max:0.00} · осталось ${budget.Remaining:0.00}" : string.Empty));
            if (budget.ResetAt is { } reset) parts.Add($"сброс {reset:dd.MM HH:mm}");
            if (budget.Plan is { } plan && !string.IsNullOrWhiteSpace(plan)) parts.Add(plan);
        }
        else
        {
            parts.Add($"n/a ({budget.Error})");
        }

        if (budget.Alias is { } alias) parts.Add(alias);
        if (budget.Meters.Count > 0)
        {
            parts.Add(string.Join(", ", budget.Meters.Select(meter =>
                $"{meter.Title} {meter.Percent:0.0}%" + (meter.Limited ? " (лимит)" : string.Empty))));
        }

        var text = "budget   " + string.Join(" · ", parts);
        if (budget.SourceUrl is { } source) text += $"\n         источник: {source}";
        return text;
    }

    static string Trim(string text, int max) => text.Length <= max ? text : text[..max];
}
