using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TokenMonitor.DataProviders;
using TokenMonitor.Proxy;

namespace TokenMonitor;

public partial class MainWindow : Window
{
    static readonly Brush BudgetNormal = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27));
    static readonly Brush BudgetWarning = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));
    static readonly Brush BudgetCritical = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
    static readonly Brush BudgetMuted = new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0x98));

    readonly AppSettings settings;
    readonly Dictionary<DataSource, IDataProvider> dataProviders = new();
    readonly DispatcherTimer timer;
    readonly DispatcherTimer proxyTimer;

    ProxyServer? localProxy;
    CancellationTokenSource? localProxyCts;

    DataSnapshot? snapshot;
    List<Node> roots = new();
    Node? selected;
    long signature;
    string liveKey = string.Empty;
    bool busy;
    bool sourceReady;
    bool suppressSourceChange;
    DataSource currentSource = DataSource.OpenCode;

    IDataProvider CurrentProvider =>
        dataProviders.TryGetValue(currentSource, out var provider)
            ? provider
            : dataProviders[DataSource.OpenCode];

    // Свойство, к которому привяжется ComboBox
    public Dictionary<string, ProviderConfigEx> Providers { get; set; } = new();

    public MainWindow()
    {
        InitializeComponent();

        try
        {
            settings = SettingsStore.Load();
        }
        catch (JsonException error)
        {
            StatusText.Text =  $"Ошибка Settings: Строка: {error.LineNumber}, Позиция: {error.BytePositionInLine}";
        }
        if (settings == null)
            return;

        currentSource = settings.LastSource;
        dataProviders = DataProviderFactory.Create(settings);
        SettingsStore.EnsureExists(settings);

        Providers = dataProviders.Values
            .OrderBy(provider => provider.Source)
            .ToDictionary(
                provider => provider.DisplayName,
                provider => new ProviderConfigEx(
                    provider.Config,
                    provider.Source,
                    provider.PathText,
                    provider.TreeHint));

 
        DataContext = this;

        LivePathText.Text = "источник: token-events.jsonl (плагин token-stats.ts)";
        SourceConfigLink.ToolTip = SettingsStore.SettingsPath;
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += async (_, _) => await RefreshAsync();

        proxyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        proxyTimer.Tick += (_, _) => UpdateProxyTraffic();

        Loaded += async (_, _) =>
        {
            UpdateComboBoxSelection();
            ApplySourceChrome();
            sourceReady = true;
            StartLocalProxy();
            proxyTimer.Start();
            await RefreshAsync();
            timer.Start();
        };

        Closed += (_, _) =>
        {
            timer.Stop();
            proxyTimer.Stop();
            StopLocalProxy();
        };
    }

    void StartLocalProxy()
    {
        if (!settings.LocalProxyServerEnabled)
        {
            ProxyTrafficText.Visibility = Visibility.Collapsed;
            return;
        }

        if (localProxy is not null)
            return;

        var proxySettings = settings.ProxySettings;
        var listenUrl = string.IsNullOrWhiteSpace(proxySettings.ListenUrl)
            ? ProxySettings.DefaultListenUrl
            : proxySettings.ListenUrl;
        var upstream = proxySettings.CreateUpstream();

        var server = new ProxyServer(listenUrl, upstream);
        var cts = new CancellationTokenSource();
        localProxy = server;
        localProxyCts = cts;

        ProxyTrafficText.Visibility = Visibility.Visible;
        ProxyTrafficText.ToolTip = BuildProxyTooltip(listenUrl, upstream);

        _ = Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception error)
            {
                await Dispatcher.InvokeAsync(() => StatusText.Text = $"локальный прокси: {error.Message}");
            }
        });

        UpdateProxyTraffic();
    }

    void StopLocalProxy()
    {
        try
        {
            localProxyCts?.Cancel();
        }
        catch
        {
        }

        localProxyCts?.Dispose();
        localProxyCts = null;
        localProxy = null;
    }

    void UpdateProxyTraffic()
    {
        if (localProxy is not { } proxy)
            return;

        ProxyTrafficText.Text = $"↑ {FormatMb(proxy.BytesSent)} · ↓ {FormatMb(proxy.BytesReceived)} МБ";

        var upstream = settings.ProxySettings.CreateUpstream();
        ProxyTrafficText.ToolTip =
            BuildProxyTooltip(settings.ProxySettings.ListenUrl, upstream) +
            (proxy.IsRunning ? "\nстатус: работает" : "\nстатус: не запущен");
    }

    static string FormatMb(long bytes) => (bytes / 1024d / 1024d).ToString("0.00");

    static string BuildProxyTooltip(string listenUrl, ProxySettings? upstream) =>
        upstream is null
            ? $"локальный прокси: {listenUrl}\nupstream: напрямую"
            : $"локальный прокси: {listenUrl}\nupstream: {upstream.Scheme}://{upstream.Host}:{upstream.Port}";

    async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync(force: true);

    async Task RefreshAsync(bool force = false)
    {
        if (busy) return;
        busy = true;

        try
        {
            var provider = CurrentProvider;
            var result = await provider.LoadAsync(force);
            var loaded = result.Snapshot;

            snapshot = loaded;
            DbPathText.Text = loaded.DbPath;

            if (loaded.Signature != signature)
            {
                signature = loaded.Signature;
                RebuildTree(loaded);
            }

            UpdateLive(loaded);
            UpdateKpi();
            UpdateAlerts(loaded);
            ApplyStatus(loaded, result);
            ApplyTreeHint(provider, result);
            ApplyBudget(await provider.GetBudgetAsync(force));
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

    void ApplyStatus(DataSnapshot loaded, ProviderResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.StatusNote))
        {
            StatusText.Text = result.StatusNote;
            return;
        }

        var requests = loaded.Sessions.Sum(session => session.Requests.Count);
        StatusText.Text = !string.IsNullOrWhiteSpace(loaded.LoadError)
            ? $"загрузка: {loaded.LoadError} · сессий {loaded.Sessions.Count} · запросов {requests}"
            : $"сессий {loaded.Sessions.Count} · запросов {requests} · обновлено {loaded.LoadedAt:HH:mm:ss}";
    }

    void ApplyTreeHint(IDataProvider provider, ProviderResult result) =>
        TreeHint.Text = result.TreeHint ?? provider.TreeHint;

    void ApplyBudget(BudgetUiModel model)
    {
        BudgetText.Text = model.Text;
        BudgetText.Foreground = model.Severity switch
        {
            BudgetSeverity.Critical => BudgetCritical,
            BudgetSeverity.Warning => BudgetWarning,
            BudgetSeverity.Muted => BudgetMuted,
            _ => BudgetNormal,
        };

        BudgetDetail.Text = model.Detail;
        BudgetText.ToolTip = model.Tooltip;
        BudgetDetail.ToolTip = model.Tooltip;
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

    const long AlertContextLimit = 150_000;
    const int AlertStepLimit = 40;
    const long AlertCacheInputLimit = 100_000;
    const long AlertSessionLimit = 100_000_000;

    void UpdateAlerts(DataSnapshot loaded)
    {
        if (!settings.AlertWindowEnabled)
        {
            AlertsList.ItemsSource = null;
            AlertsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var alerts = new List<AlertInfo>();
        var cutoff = DateTime.Now.AddHours(-settings.AlertWindowHours);
        var sessions = loaded.Sessions
            .Where(session => session.Updated >= cutoff)
            .OrderByDescending(session => session.Updated)
            .ToList();

        var contextHits = sessions
            .Where(session => session.MaxContext > AlertContextLimit)
            .OrderByDescending(session => session.MaxContext)
            .ToList();

        if (contextHits.Count > 0)
        {
            var top = contextHits[0];
            alerts.Add(new AlertInfo
            {
                Text = $"Контекст > {Fmt.Tokens(AlertContextLimit)}: {contextHits.Count} сесс. · макс. {Fmt.Tokens(top.MaxContext)} · {SessionLabel(top)}",
                Detail = AlertDetails(contextHits.Select(session => $"{SessionLabel(session)}: {Fmt.Tokens(session.MaxContext)}")),
                Critical = true,
            });
        }

        var stepHits = sessions
            .Where(session => session.MaxSteps > AlertStepLimit)
            .OrderByDescending(session => session.MaxSteps)
            .ToList();

        if (stepHits.Count > 0)
        {
            var top = stepHits[0];
            alerts.Add(new AlertInfo
            {
                Text = $"Шагов > {AlertStepLimit}: {stepHits.Count} сесс. · макс. {top.MaxSteps} · {SessionLabel(top)}",
                Detail = AlertDetails(stepHits.Select(session => $"{SessionLabel(session)}: {session.MaxSteps} шагов")),
            });
        }

        var cacheHits = sessions
            .Where(session => session.Tokens.Input > AlertCacheInputLimit && session.Tokens.CacheRead == 0)
            .OrderByDescending(session => session.Tokens.Input)
            .ToList();

        if (cacheHits.Count > 0)
        {
            var top = cacheHits[0];
            alerts.Add(new AlertInfo
            {
                Text = $"cacheRead=0 при input > {Fmt.Tokens(AlertCacheInputLimit)}: {cacheHits.Count} сесс. · макс. {Fmt.Tokens(top.Tokens.Input)} · {SessionLabel(top)}",
                Detail = AlertDetails(cacheHits.Select(session => $"{SessionLabel(session)}: input {Fmt.Tokens(session.Tokens.Input)}")),
            });
        }

        var bigSessions = sessions
            .Where(session => session.Tokens.Total > AlertSessionLimit)
            .OrderByDescending(session => session.Tokens.Total)
            .ToList();

        if (bigSessions.Count > 0)
        {
            var top = bigSessions[0];
            alerts.Add(new AlertInfo
            {
                Text = $"Сессия > {Fmt.Tokens(AlertSessionLimit)}: {bigSessions.Count} · макс. {Fmt.Tokens(top.Tokens.Total)} · {SessionLabel(top)}",
                Detail = AlertDetails(bigSessions.Select(session => $"{SessionLabel(session)}: {Fmt.Tokens(session.Tokens.Total)}")),
                Critical = true,
            });
        }

        AlertsTitle.Text = $"АЛЕРТЫ · {settings.AlertWindowHours} ч";
        AlertsPanel.ToolTip = $"Сессии, обновлённые за последние {settings.AlertWindowHours} ч";
        AlertsList.ItemsSource = alerts;
        AlertsPanel.Visibility = alerts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    static string SessionLabel(SessionData session)
    {
        var title = string.IsNullOrWhiteSpace(session.Title) ? session.Id : session.Title;
        return Fmt.Preview(title, 55);
    }

    static string AlertDetails(IEnumerable<string> items)
    {
        var all = items.ToList();
        var lines = all.Take(8).ToList();
        if (all.Count > lines.Count) lines.Add($"… ещё {all.Count - lines.Count}");
        return string.Join("\n", lines);
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
        MonthDetail.Text = $"{monthRequests} запр · {monthSessions} сесс · in {Fmt.Tokens(month.Input)} / out {Fmt.Tokens(month.Output)} · кэш {month.CacheHitText}";

        DayTotal.Text = Fmt.Tokens(day.Total);
        DayDetail.Text = $"{dayRequests} запр · {daySessions} сесс · {now:dd.MM.yyyy} · кэш {day.CacheHitText}";

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
        SessionDetail.Text = $"{focusSession.Requests.Count} запр · {Fmt.Duration(focusSession.DurationMs)} · кэш {focusSession.Tokens.CacheHitText} · {Fmt.Folder(focusSession.Directory)}";

        var focusRequest = RequestOf(selected) ?? focusSession.Requests.LastOrDefault();

        if (focusRequest is null)
        {
            RequestTotal.Text = "—";
            RequestDetail.Text = "выберите запрос в дереве";
            return;
        }

        RequestTotal.Text = Fmt.Tokens(focusRequest.Tokens.Total);
        RequestDetail.Text = $"шагов {focusRequest.Steps.Count} · {Fmt.Duration(focusRequest.DurationMs)} · in {Fmt.Tokens(focusRequest.Tokens.Input)} / out {Fmt.Tokens(focusRequest.Tokens.Output)} · кэш {focusRequest.Tokens.CacheHitText} / W {focusRequest.Tokens.CacheWriteText}";
    }

    void ConfigLink_Click(object sender, RoutedEventArgs e)
    {
        SettingsStore.EnsureExists(settings);

        var configPath = CurrentProvider.Config.ConfigPath;

        try
        {
            Process.Start(new ProcessStartInfo(configPath) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            StatusText.Text = $"не удалось открыть конфиг: {error.Message}";
        }
    }

    void SourceConfigLink_Click(object sender, RoutedEventArgs e)
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
        var enabled = session is not null
            && Directory.Exists(session.Directory)
            && CurrentProvider.SupportsSessionLaunch;

        ResumeTuiItem.IsEnabled = enabled;
        ResumeForkItem.IsEnabled = enabled;
        CopySessionIdItem.IsEnabled = session is not null;

        var disabledHint = CurrentProvider.SupportsSessionLaunch
            ? "нужна сессия с существующим каталогом"
            : "источник не поддерживает запуск сессий";
        ResumeTuiItem.ToolTip = enabled ? $"opencode -s {session!.Id}" : disabledHint;
        ResumeForkItem.ToolTip = enabled ? $"opencode -s {session!.Id} --fork" : disabledHint;
    }

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

    async void SourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressSourceChange || !sourceReady) return;
        if (SelectedProvider() is not { } selected) return;
        if (!selected.IsEnabled)
        {
            UpdateComboBoxSelection();
            return;
        }
        if (selected.Source == currentSource) return;

        currentSource = selected.Source;
        settings.LastSource = currentSource;
        SettingsStore.TrySave(settings);

        // long.MinValue гарантирует перестройку дерева даже для пустого снапшота (Signature = 0).
        signature = long.MinValue;

        ApplySourceChrome();
        await RefreshAsync(force: true);
    }

    void UpdateComboBoxSelection()
    {
        var selected = Providers.Values.FirstOrDefault(p => p.Source == currentSource && p.IsEnabled)
            ?? Providers.Values.FirstOrDefault(p => p.IsEnabled);
        if (selected is null) return;

        if (selected.Source != currentSource)
        {
            currentSource = selected.Source;
            settings.LastSource = currentSource;
            SettingsStore.TrySave(settings);
        }

        if (ReferenceEquals(SourceComboBox.SelectedValue, selected)) return;

        suppressSourceChange = true;
        SourceComboBox.SelectedValue = selected;
        suppressSourceChange = false;
    }

    ProviderConfigEx? SelectedProvider() => SourceComboBox.SelectedValue as ProviderConfigEx
        ?? (SourceComboBox.SelectedItem is KeyValuePair<string, ProviderConfigEx> pair ? pair.Value : null);

    void ApplySourceChrome()
    {
        var provider = CurrentProvider;
        DbPathText.Text = provider.PathText;
        TreeHint.Text = provider.TreeHint;
        ConfigLink.ToolTip = provider.Config.ConfigPath;
    }
}
