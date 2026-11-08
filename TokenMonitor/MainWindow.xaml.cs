using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TokenMonitor.Proxy;

namespace TokenMonitor;

public partial class MainWindow : Window
{
    static readonly Brush BudgetNormal = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27));
    static readonly Brush BudgetWarning = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));
    static readonly Brush BudgetCritical = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
    static readonly Brush BudgetMuted = new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0x98));

    readonly DataStore store = new();
    readonly CursorStore cursorStore;
    readonly DevinStore devinStore;
    readonly AppSettings settings;
    readonly BudgetService budgetService;
    readonly DispatcherTimer timer;
    readonly DispatcherTimer proxyTimer;

    ProxyServer? localProxy;
    CancellationTokenSource? localProxyCts;

    DataSnapshot? snapshot;
    BudgetInfo? budget;
    CursorPeriodUsage? cursorPeriod;
    DevinSnapshot? devinSnapshot;
    DateTime lastBudgetFetch = DateTime.MinValue;
    DateTime lastCursorPeriodFetch = DateTime.MinValue;
    List<Node> roots = new();
    Node? selected;
    long signature;
    string liveKey = string.Empty;
    bool busy;
    bool sourceReady;
    bool suppressSourceChange;
    DataSource currentSource = DataSource.OpenCode;

    // Свойство, к которому привяжется ComboBox
    public Dictionary<string, ProviderConfigEx> Providers { get; set; } = new();

    public MainWindow()
    {
        InitializeComponent();

        settings = SettingsStore.Load();
        currentSource = settings.LastSource;
        budgetService = new BudgetService(settings);
        cursorStore = new CursorStore(settings);
        devinStore = new DevinStore(settings);
        SettingsStore.EnsureExists(settings);

        Providers = new Dictionary<string, ProviderConfigEx>
        {
            { "OpenCode", new ProviderConfigEx(settings.OpenCode, DataSource.OpenCode, store.DbPath, "Кликните по сессии, чтобы увидеть детализацию. Правый клик — открыть сессию в OpenCode.") },
            { "Cursor", new ProviderConfigEx(settings.Cursor, DataSource.Cursor, cursorStore.EventsPath, "Кликните по сессии, чтобы увидеть детализацию. Правый клик — открыть сессию в Cursor.") },
            { "Claude", new ProviderConfigEx(settings.Claude, DataSource.Claude, "Claude - не реализовано", "Claude - не реализовано") },
            { "Devin", new ProviderConfigEx(settings.Devin, DataSource.Devin, devinStore.IsConfigured ? $"Devin API v3 (org: {settings.Devin.OrgId})" : "Devin API не настроен", "Статистика использования Devin API v3. Настройте API ключ и Org ID в конфиге.") },
        };
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
            DataSnapshot loaded;
            if (currentSource == DataSource.OpenCode)
            {
                try
                {
                    loaded = await Task.Run(store.Load);
                }
                catch (FileNotFoundException)
                {
                    loaded = new DataSnapshot
                    {
                        Sessions = new List<SessionData>(),
                        Live = new List<LiveEvent>(),
                        Signature = 0,
                        DbPath = store.DbPath,
                        EventsPath = string.Empty,
                        LoadedAt = DateTime.Now,
                    };
                    StatusText.Text = "База opencode не найдена. Запустите opencode и выполните хотя бы один запрос.";
                }
            }
            else if (currentSource == DataSource.Cursor)
            {
                loaded = await cursorStore.LoadAsync(refreshRemote: true);
            }
            else if (currentSource == DataSource.Devin)
            {
                devinSnapshot = await devinStore.LoadAsync();
                loaded = DevinStore.ToDataSnapshot(devinSnapshot);
            }
            else
            {
                loaded = new DataSnapshot
                {
                    Sessions = new List<SessionData>(),
                    Live = new List<LiveEvent>(),
                    Signature = 0,
                    DbPath = "Claude - не реализовано",
                    EventsPath = string.Empty,
                    LoadedAt = DateTime.Now,
                };
            }

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

            var requests = loaded.Sessions.Sum(s => s.Requests.Count);
            if (StatusText.Text.StartsWith("ошибка") || StatusText.Text.StartsWith("База opencode"))
            {
                // Keep the error message
            }
            else
            {
                StatusText.Text = $"сессий {loaded.Sessions.Count} · запросов {requests} · обновлено {loaded.LoadedAt:HH:mm:ss}";
            }

            if (currentSource == DataSource.OpenCode && loaded.Sessions.Count == 0 && File.Exists(store.DbPath))
            {
                StatusText.Text = "База opencode пуста. Запустите opencode и выполните хотя бы один запрос.";
            }

            if (currentSource == DataSource.OpenCode)
            {
                if (budget is null || (DateTime.UtcNow - lastBudgetFetch).TotalSeconds >= settings.BudgetRefreshSeconds)
                {
                    lastBudgetFetch = DateTime.UtcNow;
                    budget = await budgetService.GetAsync();
                    UpdateBudgetUi();
                }
            }
            else if (currentSource == DataSource.Cursor)
            {
                if (cursorPeriod is null || (DateTime.UtcNow - lastCursorPeriodFetch).TotalSeconds >= settings.BudgetRefreshSeconds)
                {
                    lastCursorPeriodFetch = DateTime.UtcNow;
                    cursorPeriod = await cursorStore.GetPeriodAsync();
                    UpdateCursorBudgetUi();
                }
            }
            else if (currentSource == DataSource.Devin)
            {
                UpdateDevinBudgetUi();
            }
            else
            {
                BudgetText.Text = "—";
                BudgetDetail.Text = "Claude не реализован";
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

    void UpdateBudgetUi()
    {
        if (currentSource == DataSource.Cursor)
        {
            UpdateCursorBudgetUi();
            return;
        }

        if (currentSource == DataSource.Devin)
        {
            UpdateDevinBudgetUi();
            return;
        }

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
        tooltip += "\nисточник: " + (budget.SourceUrl ?? BudgetService.KeyInfoUrl(settings.LiteLLMBaseUrl));

        BudgetText.ToolTip = tooltip;
        BudgetDetail.ToolTip = tooltip;
    }

    void UpdateCursorBudgetUi()
    {
        if (cursorPeriod is null) return;

        if (cursorPeriod.Error is not null)
        {
            BudgetText.Text = "—";
            BudgetText.Foreground = BudgetMuted;
            BudgetDetail.Text = cursorPeriod.Error;
            BudgetText.ToolTip = cursorPeriod.Error;
            BudgetDetail.ToolTip = cursorPeriod.Error;
            return;
        }

        var totalPercent = cursorPeriod.TotalPercent;
        BudgetText.Text = $"{totalPercent:0.0}%";

        BudgetText.Foreground = totalPercent switch
        {
            >= 90 => BudgetCritical,
            >= 70 => BudgetWarning,
            _ => BudgetNormal,
        };

        var details = new List<string>();
        if (cursorPeriod.CycleStart is { } start && cursorPeriod.CycleEnd is { } end)
            details.Add($"{start:dd.MM} - {end:dd.MM}");
        if (!string.IsNullOrWhiteSpace(cursorPeriod.Membership))
            details.Add(cursorPeriod.Membership);
        BudgetDetail.Text = string.Join(" · ", details);

        var tooltip = $"Auto: {cursorPeriod.AutoPercent:0.0}% · API: {cursorPeriod.ApiPercent:0.0}%";
        if (!string.IsNullOrWhiteSpace(cursorPeriod.DisplayMessage))
            tooltip += $"\n{cursorPeriod.DisplayMessage}";
        if (!string.IsNullOrWhiteSpace(cursorPeriod.Source))
            tooltip += $"\nисточник: {cursorPeriod.Source}";

        BudgetText.ToolTip = tooltip;
        BudgetDetail.ToolTip = tooltip;
    }

    void UpdateDevinBudgetUi()
    {
        if (devinSnapshot is null) return;

        if (devinSnapshot.UsageMetrics?.Error is not null)
        {
            BudgetText.Text = "—";
            BudgetText.Foreground = BudgetMuted;
            BudgetDetail.Text = devinSnapshot.UsageMetrics.Error;
            BudgetText.ToolTip = devinSnapshot.UsageMetrics.Error;
            BudgetDetail.ToolTip = devinSnapshot.UsageMetrics.Error;
            return;
        }

        var usage = devinSnapshot.UsageMetrics;
        var sessions = usage?.SessionsCount ?? 0;
        BudgetText.Text = sessions.ToString();

        BudgetText.Foreground = BudgetNormal;

        var details = new List<string>();
        if (usage?.PrsCreatedCount > 0)
            details.Add($"PRs: {usage.PrsCreatedCount}");
        if (usage?.PrsMergedCount > 0)
            details.Add($"merged: {usage.PrsMergedCount}");
        if (usage?.SearchesCount > 0)
            details.Add($"searches: {usage.SearchesCount}");
        BudgetDetail.Text = details.Count > 0 ? string.Join(" · ", details) : "сессий за месяц";

        var tooltip = $"Сессий: {sessions}";
        if (devinSnapshot.SessionMetrics?.AvgAcusPerSession > 0)
            tooltip += $"\nСреднее ACU/сессия: {devinSnapshot.SessionMetrics.AvgAcusPerSession:0.0}";
        if (devinSnapshot.CategoryMetrics?.Categories.Count > 0)
            tooltip += $"\nКатегорий: {devinSnapshot.CategoryMetrics.Categories.Count}";
        tooltip += "\nисточник: Devin API v3";

        BudgetText.ToolTip = tooltip;
        BudgetDetail.ToolTip = tooltip;
    }

    void ConfigLink_Click(object sender, RoutedEventArgs e)
    {
        SettingsStore.EnsureExists(settings);

        var configPath = CurrentProvider()?.ConfigPath ?? settings.For(currentSource).ConfigPath;

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
        var enabled = session is not null && Directory.Exists(session.Directory);

        ResumeTuiItem.IsEnabled = enabled;
        ResumeForkItem.IsEnabled = enabled;
        CopySessionIdItem.IsEnabled = session is not null;

        ResumeTuiItem.ToolTip = enabled ? $"opencode -s {session!.Id}" : "нужна сессия с существующим каталогом";
        ResumeForkItem.ToolTip = enabled ? $"opencode -s {session!.Id} --fork" : "нужна сессия с существующим каталогом";
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

        lastBudgetFetch = DateTime.MinValue;
        lastCursorPeriodFetch = DateTime.MinValue;
        signature = 0;

        ApplySourceChrome();
        await RefreshAsync();
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

    ProviderConfigEx? CurrentProvider() =>
        Providers.Values.FirstOrDefault(p => p.Source == currentSource);

    ProviderConfigEx? SelectedProvider() => SourceComboBox.SelectedValue as ProviderConfigEx
        ?? (SourceComboBox.SelectedItem is KeyValuePair<string, ProviderConfigEx> pair ? pair.Value : null);

    void ApplySourceChrome()
    {
        if (CurrentProvider() is not { } provider) return;
        DbPathText.Text = provider.PathText;
        TreeHint.Text = provider.TreeHint;
        ConfigLink.ToolTip = provider.ConfigPath;
    }
}
