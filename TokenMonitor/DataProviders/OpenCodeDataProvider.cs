using System.IO;

namespace TokenMonitor.DataProviders;

public sealed class OpenCodeDataProvider : IDataProvider
{
    const string DefaultTreeHint =
        "Кликните по сессии, чтобы увидеть детализацию. Правый клик — открыть сессию в OpenCode.";

    readonly AppSettings settings;
    readonly OpenCodeStore store = new();
    readonly BudgetService budgetService;

    BudgetUiModel? cachedBudget;
    DateTime lastBudgetFetch = DateTime.MinValue;

    public OpenCodeDataProvider(AppSettings settings)
    {
        this.settings = settings;
        budgetService = new BudgetService(settings);
    }

    public DataSource Source => DataSource.OpenCode;
    public string DisplayName => "OpenCode";
    public ProviderConfig Config => settings.OpenCode;
    public string PathText => store.DbPath;
    public string TreeHint => DefaultTreeHint;
    public bool IsEnabled => settings.OpenCode.IsEnabled;
    public bool SupportsSessionLaunch => true;

    public async Task<ProviderResult> LoadAsync(bool force, CancellationToken token = default)
    {
        try
        {
            var loaded = await Task.Run(store.Load, token);

            var note = loaded.Sessions.Count == 0
                && File.Exists(store.DbPath)
                && string.IsNullOrWhiteSpace(loaded.LoadError)
                    ? "База opencode пуста. Запустите opencode и выполните хотя бы один запрос."
                    : null;

            return new ProviderResult
            {
                Snapshot = loaded,
                StatusNote = note,
            };
        }
        catch (FileNotFoundException)
        {
            return new ProviderResult
            {
                Snapshot = DataSnapshot.Empty(store.DbPath),
                StatusNote = "База opencode не найдена. Запустите opencode и выполните хотя бы один запрос.",
            };
        }
    }

    public async Task<BudgetUiModel> GetBudgetAsync(bool force, CancellationToken token = default)
    {
        if (!force
            && cachedBudget is not null
            && (DateTime.UtcNow - lastBudgetFetch).TotalSeconds < settings.BudgetRefreshSeconds)
        {
            return cachedBudget;
        }

        lastBudgetFetch = DateTime.UtcNow;
        var budget = await budgetService.GetAsync(token);
        cachedBudget = MapBudget(budget);
        return cachedBudget;
    }

    BudgetUiModel MapBudget(BudgetInfo budget)
    {
        if (budget.Error is not null || (budget.Display == BudgetDisplay.Money && budget.Spend is null))
        {
            var error = budget.Error ?? "нет данных";
            return new BudgetUiModel("—", error, error, BudgetSeverity.Muted);
        }

        var severity = budget.Ratio switch
        {
            >= 0.9 => BudgetSeverity.Critical,
            >= 0.7 => BudgetSeverity.Warning,
            _ => BudgetSeverity.Normal,
        };

        var details = new List<string>();
        var tooltipLines = new List<string>();
        string text;

        if (budget.Display == BudgetDisplay.Percent)
        {
            var percent = budget.Percent ?? 0;
            text = $"{percent:0.0}%";

            if (budget.Plan is { } plan && !string.IsNullOrWhiteSpace(plan)) details.Add(plan);
            details.AddRange(budget.Meters.Select(meter => $"{meter.Label} {meter.Percent:0.0}%"));
            if (budget.Meters.Count == 0 && budget.ResetAt is { } reset)
                details.Add($"сброс {reset:dd.MM HH:mm}");

            tooltipLines.Add($"использовано {percent:0.0}%");
            AppendMeterTooltip(tooltipLines, budget.Meters);
            if (budget.Meters.Count == 0 && budget.ResetAt is { } tooltipReset)
                tooltipLines.Add($"сброс лимита: {tooltipReset:dd.MM.yyyy HH:mm}");
        }
        else
        {
            var spend = budget.Spend ?? 0;
            text = budget.MaxBudget is { } max
                ? $"${spend:0.00} / ${max:0.00}"
                : $"${spend:0.00}";

            if (budget.Remaining is { } remaining) details.Add($"осталось ${remaining:0.00}");
            if (budget.ResetAt is { } reset) details.Add($"сброс {reset:dd.MM HH:mm}");
            if (budget.Plan is { } plan && !string.IsNullOrWhiteSpace(plan)) details.Add(plan);

            tooltipLines.Add($"${spend:0.0000}" + (budget.MaxBudget is { } limit ? $" из ${limit:0.00}" : string.Empty));
            if (budget.ResetAt is { } tooltipReset)
                tooltipLines.Add($"сброс лимита: {tooltipReset:dd.MM.yyyy HH:mm}");
            if (budget.Meters.Count > 0)
            {
                tooltipLines.Add("лимиты OpenCode:");
                AppendMeterTooltip(tooltipLines, budget.Meters, "  ");
            }
        }

        if (budget.Alias is { } alias)
            tooltipLines.Add((budget.Provider == "OpenCode" ? "аккаунт: " : "ключ: ") + alias);
        if (!string.IsNullOrWhiteSpace(budget.Provider))
            tooltipLines.Add("источник: " + budget.Provider);
        tooltipLines.Add("адрес: " + (budget.SourceUrl ?? BudgetService.KeyInfoUrl(settings.LiteLLMBaseUrl)));

        var detail = details.Count > 0 ? string.Join(" · ", details) : "—";
        return new BudgetUiModel(text, detail, string.Join("\n", tooltipLines), severity);
    }

    static void AppendMeterTooltip(List<string> lines, IReadOnlyList<BudgetMeter> meters, string indent = "")
    {
        foreach (var meter in meters)
        {
            var line = $"{indent}{meter.Title}: {meter.Percent:0.0}%";
            if (meter.Limited) line += " · лимит исчерпан";
            if (meter.ResetAt is { } reset) line += $" · сброс {reset:dd.MM.yyyy HH:mm}";
            lines.Add(line);
        }
    }
}
