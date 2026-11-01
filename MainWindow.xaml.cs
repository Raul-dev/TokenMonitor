using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TokenMonitor;

public partial class MainWindow : Window
{
    static readonly Brush BudgetNormal = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27));
    static readonly Brush BudgetWarning = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));
    static readonly Brush BudgetCritical = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
    static readonly Brush BudgetMuted = new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0x98));

    readonly DataStore store = new();
    readonly AppSettings settings;
    readonly BudgetService budgetService;
    readonly DispatcherTimer timer;

    DataSnapshot? snapshot;
    BudgetInfo? budget;
    DateTime lastBudgetFetch = DateTime.MinValue;
    List<Node> roots = new();
    Node? selected;
    long signature;
    string liveKey = string.Empty;
    bool busy;

    public MainWindow()
    {
        InitializeComponent();

        settings = SettingsStore.Load();
        budgetService = new BudgetService(settings);
        SettingsStore.EnsureExists(settings);

        LivePathText.Text = "источник: token-events.jsonl (плагин token-stats.ts)";
        ConfigLink.ToolTip = SettingsStore.SettingsPath;
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += async (_, _) => await RefreshAsync();

        Loaded += async (_, _) =>
        {
            DbPathText.Text = store.DbPath;
            await RefreshAsync();
            timer.Start();
        };

        Closed += (_, _) => timer.Stop();
    }

    async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        lastBudgetFetch = DateTime.MinValue;
        await RefreshAsync();
    }

    async Task RefreshAsync()
    {
        if (busy) return;
        busy = true;

        try
        {
            var loaded = await Task.Run(store.Load);
            snapshot = loaded;

            DbPathText.Text = loaded.DbPath;

            if (loaded.Signature != signature)
            {
                signature = loaded.Signature;
                RebuildTree(loaded);
            }

            UpdateLive(loaded);
            UpdateKpi();

            var requests = loaded.Sessions.Sum(s => s.Requests.Count);
            StatusText.Text = $"сессий {loaded.Sessions.Count} · запросов {requests} · обновлено {loaded.LoadedAt:HH:mm:ss}";

            if (budget is null || (DateTime.UtcNow - lastBudgetFetch).TotalSeconds >= settings.BudgetRefreshSeconds)
            {
                lastBudgetFetch = DateTime.UtcNow;
                budget = await budgetService.GetAsync();
                UpdateBudgetUi();
            }
        }
        catch (Exception error)
        {
            StatusText.Text = $"ошибка: {error.Message}";
        }
        finally
        {
            busy = false;
        }
    }

    void RebuildTree(DataSnapshot loaded)
    {
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        var selectedKey = selected?.Key;

        foreach (var node in Walk(roots))
        {
            if (node.IsExpanded) expanded.Add(node.Key);
            if (node.IsSelected) selectedKey = node.Key;
        }

        var list = new List<Node>();

        foreach (var session in loaded.Sessions.OrderByDescending(s => s.Updated))
        {
            var sessionNode = new SessionNode
            {
                Key = "s:" + session.Id,
                Session = session,
                Title = string.IsNullOrWhiteSpace(session.Title) ? session.Id : session.Title,
                Subtitle = $"{Fmt.Folder(session.Directory)} · {session.Model} · {Fmt.Duration(session.DurationMs)}"
                    + (session.IsChild ? " · subagent" : string.Empty),
                Tokens = session.Tokens,
                CountText = session.Requests.Count.ToString(),
                TimeText = session.Updated.ToString("dd.MM HH:mm"),
            };

            var index = 0;
            foreach (var request in session.Requests)
            {
                index++;

                var requestNode = new RequestNode
                {
                    Key = "r:" + request.SessionId + ":" + request.Id,
                    Request = request,
                    Session = session,
                    Title = string.IsNullOrWhiteSpace(request.Prompt) ? $"запрос {index}" : Fmt.Preview(request.Prompt),
                    Subtitle = $"{Fmt.Duration(request.DurationMs)} · шагов {request.Steps.Count}"
                        + (request.Steps.Count > 0 ? " · " + request.Steps[^1].Model : string.Empty),
                    Tokens = request.Tokens,
                    CountText = request.Steps.Count.ToString(),
                    TimeText = request.Time.ToString("HH:mm:ss"),
                };

                var stepIndex = 0;
                foreach (var step in request.Steps)
                {
                    stepIndex++;
                    requestNode.Children.Add(new StepNode
                    {
                        Key = "t:" + step.Id,
                        Step = step,
                        Request = request,
                        Title = $"шаг {stepIndex} · {step.Model}",
                        Tokens = step.Tokens,
                        TimeText = step.Time.ToString("HH:mm:ss"),
                    });
                }

                sessionNode.Children.Add(requestNode);
            }

            list.Add(sessionNode);
        }

        roots = list;
        SessionsTree.ItemsSource = roots;

        selected = null;
        foreach (var node in Walk(roots))
        {
            if (expanded.Contains(node.Key)) node.IsExpanded = true;
            if (node.Key == selectedKey)
            {
                node.IsSelected = true;
                selected = node;
            }
        }
    }

    void UpdateLive(DataSnapshot loaded)
    {
        LivePathText.Text = loaded.EventsPath;

        var key = loaded.Live.Count + "|" + (loaded.Live.Count == 0 ? string.Empty : loaded.Live[^1].Time.Ticks.ToString());
        if (key == liveKey) return;

        liveKey = key;
        LiveList.ItemsSource = loaded.Live
            .OrderByDescending(item => item.Time)
            .Take(200)
            .ToList();
    }

    void UpdateKpi()
    {
        if (snapshot is null) return;

        var now = DateTime.Now;
        var monthStart = new DateTime(now.Year, now.Month, 1);
        var dayStart = now.Date;

        var month = TokenCounts.Zero;
        var day = TokenCounts.Zero;
        int monthRequests = 0, monthSessions = 0, dayRequests = 0, daySessions = 0;

        foreach (var session in snapshot.Sessions)
        {
            var inMonth = false;
            var inDay = false;

            foreach (var request in session.Requests)
            {
                foreach (var step in request.Steps)
                {
                    if (step.Time >= monthStart)
                    {
                        month += step.Tokens;
                        inMonth = true;
                    }

                    if (step.Time >= dayStart)
                    {
                        day += step.Tokens;
                        inDay = true;
                    }
                }

                if (request.Time >= monthStart) monthRequests++;
                if (request.Time >= dayStart) dayRequests++;
            }

            if (inMonth) monthSessions++;
            if (inDay) daySessions++;
        }

        MonthTotal.Text = Fmt.Tokens(month.Total);
        MonthDetail.Text = $"{monthRequests} запр · {monthSessions} сесс · in {Fmt.Tokens(month.Input)} / out {Fmt.Tokens(month.Output)}";

        DayTotal.Text = Fmt.Tokens(day.Total);
        DayDetail.Text = $"{dayRequests} запр · {daySessions} сесс · {now:dd.MM.yyyy}";

        var focusSession = SessionOf(selected) ?? snapshot.Sessions.OrderByDescending(s => s.Updated).FirstOrDefault();

        if (focusSession is null)
        {
            SessionTotal.Text = "—";
            SessionDetail.Text = "нет данных";
            RequestTotal.Text = "—";
            RequestDetail.Text = "нет данных";
            return;
        }

        SessionTotal.Text = Fmt.Tokens(focusSession.Tokens.Total);
        SessionDetail.Text = $"{focusSession.Requests.Count} запр · {Fmt.Duration(focusSession.DurationMs)} · {Fmt.Folder(focusSession.Directory)}";

        var focusRequest = RequestOf(selected) ?? focusSession.Requests.LastOrDefault();

        if (focusRequest is null)
        {
            RequestTotal.Text = "—";
            RequestDetail.Text = "выберите запрос в дереве";
            return;
        }

        RequestTotal.Text = Fmt.Tokens(focusRequest.Tokens.Total);
        RequestDetail.Text = $"шагов {focusRequest.Steps.Count} · {Fmt.Duration(focusRequest.DurationMs)} · in {Fmt.Tokens(focusRequest.Tokens.Input)} / out {Fmt.Tokens(focusRequest.Tokens.Output)}";
    }

    void UpdateBudgetUi()
    {
        if (budget is null) return;

        if (budget.Spend is not { } spend)
        {
            BudgetText.Text = "—";
            BudgetText.Foreground = BudgetMuted;
            BudgetDetail.Text = budget.Error ?? "нет данных";
            BudgetText.ToolTip = budget.Error;
            BudgetDetail.ToolTip = budget.Error;
            return;
        }

        BudgetText.Text = budget.MaxBudget is { } max
            ? $"${spend:0.00} / ${max:0.00}"
            : $"${spend:0.00}";

        BudgetText.Foreground = budget.Ratio switch
        {
            >= 0.9 => BudgetCritical,
            >= 0.7 => BudgetWarning,
            _ => BudgetNormal,
        };

        var details = new List<string>();
        if (budget.Remaining is { } remaining) details.Add($"осталось ${remaining:0.00}");
        if (budget.ResetAt is { } reset) details.Add($"сброс {reset:dd.MM HH:mm}");
        BudgetDetail.Text = string.Join(" · ", details);

        var tooltip = $"${spend:0.0000}" + (budget.MaxBudget is { } limit ? $" из ${limit:0.00}" : string.Empty);
        if (budget.ResetAt is { } tooltipReset) tooltip += $"\nсброс лимита: {tooltipReset:dd.MM.yyyy HH:mm}";
        if (budget.Alias is { } alias) tooltip += $"\nключ: {alias}";
        tooltip += "\nисточник: " + settings.LitellmBaseUrl.TrimEnd('/') + "/key/info";

        BudgetText.ToolTip = tooltip;
        BudgetDetail.ToolTip = tooltip;
    }

    void ConfigLink_Click(object sender, RoutedEventArgs e)
    {
        SettingsStore.EnsureExists(settings);

        try
        {
            Process.Start(new ProcessStartInfo(SettingsStore.SettingsPath) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            StatusText.Text = $"не удалось открыть конфиг: {error.Message}";
        }
    }

    void SessionsTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject) is not { } item)
        {
            e.Handled = true;
            return;
        }

        item.IsSelected = true;
        item.Focus();
        selected = item.DataContext as Node;
        UpdateKpi();
    }

    void SessionMenu_Opened(object sender, RoutedEventArgs e)
    {
        var session = SessionOf(selected);
        var enabled = session is not null && Directory.Exists(session.Directory);

        OpenInAppItem.IsEnabled = enabled;
        ResumeTuiItem.IsEnabled = enabled;
        ResumeForkItem.IsEnabled = enabled;
        CopySessionIdItem.IsEnabled = session is not null;

        ResumeTuiItem.ToolTip = enabled ? $"opencode -s {session!.Id}" : "нужна сессия с существующим каталогом";
        ResumeForkItem.ToolTip = enabled ? $"opencode -s {session!.Id} --fork" : "нужна сессия с существующим каталогом";
        OpenInAppItem.ToolTip = enabled
            ? $"{OpenProjectUrl(session!)}\nприложение откроет проект; сессию выберет само (deep link на сессию не поддерживается)"
            : "нужна сессия с существующим каталогом";
    }

    void OpenInApp_Click(object sender, RoutedEventArgs e)
    {
        if (SessionOf(selected) is not { } session) return;

        var url = OpenProjectUrl(session);

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            StatusText.Text = $"OpenCode открыт на проекте {session.Directory} (сессию выбирает приложение)";
        }
        catch (Exception error)
        {
            StatusText.Text = $"не удалось открыть проект: {error.Message}";
        }
    }

    static string OpenProjectUrl(SessionData session) =>
        "opencode://open-project?directory=" + Uri.EscapeDataString(session.Directory.Replace('\\', '/'));

    void ResumeTui_Click(object sender, RoutedEventArgs e) => LaunchTui(fork: false);


    void ResumeFork_Click(object sender, RoutedEventArgs e) => LaunchTui(fork: true);

    void LaunchTui(bool fork)
    {
        if (SessionOf(selected) is not { } session) return;

        if (!Directory.Exists(session.Directory))
        {
            StatusText.Text = $"нет каталога: {session.Directory}";
            return;
        }

        var arguments = $"/k opencode -s {session.Id}" + (fork ? " --fork" : string.Empty);

        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = true,
                WorkingDirectory = session.Directory,
                Arguments = arguments,
            });

            StatusText.Text = fork
                ? $"открыта копия сессии {session.Id} в терминале"
                : $"сессия {session.Id} открыта в терминале";
        }
        catch (Exception error)
        {
            StatusText.Text = $"не удалось запустить opencode: {error.Message}";
        }
    }

    void CopySessionId_Click(object sender, RoutedEventArgs e)
    {
        if (SessionOf(selected) is not { } session) return;

        try
        {
            Clipboard.SetText(session.Id);
            StatusText.Text = $"ID скопирован: {session.Id}";
        }
        catch (Exception error)
        {
            StatusText.Text = $"не удалось скопировать ID: {error.Message}";
        }
    }

    static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = source is Visual
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return null;
    }

    static SessionData? SessionOf(Node? node) => node switch
    {
        SessionNode session => session.Session,
        RequestNode request => request.Session,
        StepNode step => step.Request.Session,
        _ => null,
    };

    static RequestData? RequestOf(Node? node) => node switch
    {
        RequestNode request => request.Request,
        StepNode step => step.Request,
        SessionNode session => session.Session.Requests.LastOrDefault(),
        _ => null,
    };

    static IEnumerable<Node> Walk(IEnumerable<Node> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;

            IEnumerable<Node>? children = node switch
            {
                SessionNode session => session.Children,
                RequestNode request => request.Children,
                _ => null,
            };

            if (children is null) continue;

            foreach (var child in Walk(children))
            {
                yield return child;
            }
        }
    }

    void SessionsTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        selected = e.NewValue as Node;
        UpdateKpi();
    }

    void LiveList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LiveList.SelectedItem is not LiveEvent item) return;

        var node = roots.FirstOrDefault(candidate => candidate is SessionNode session && session.Session.Id == item.SessionId);
        if (node is null) return;

        node.IsExpanded = true;
        node.IsSelected = true;
        selected = node;
        UpdateKpi();

        if (SessionsTree.ItemContainerGenerator.ContainerFromItem(node) is TreeViewItem container)
        {
            container.BringIntoView();
        }
    }
}
