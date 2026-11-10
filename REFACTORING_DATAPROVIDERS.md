# Рефакторинг: DataProviders (Strategy + Factory)

> Статус: **реализовано** (см. `TokenMonitor/DataProviders/`)
> Затрагиваемые файлы: `TokenMonitor/MainWindow.xaml.cs`, новые файлы в `TokenMonitor/DataProviders/`
> Цель: убрать из `MainWindow` жёсткую привязку к OpenCode / Cursor / Devin / Claude и дублирование
> логики загрузки, статусов и бюджета.

---

## 1. Что не так сейчас

`MainWindow.xaml.cs` одновременно отвечает за UI, загрузку данных, выбор источника и отрисовку бюджета.

| Проблема | Где в коде |
| --- | --- |
| Поля под каждый источник | `store`, `cursorStore`, `devinStore`, `budgetService` (строки 20–24) |
| Состояние под каждый источник | `snapshot`, `budget`, `cursorPeriod`, `devinSnapshot`, `lastBudgetFetch`, `lastCursorPeriodFetch` (31–35) |
| Ветвление загрузки по источнику | `RefreshAsync`, `if/else if` на `currentSource` (215–273) |
| Ветвление отрисовки бюджета | `UpdateBudgetUi` + `UpdateCursorBudgetUi` + `UpdateDevinBudgetUi` (646–822) |
| Специфичные для Devin тексты и ошибки прямо в UI | 244–260, 294–317 |
| Специфичные для OpenCode тексты | 221–233, 327–333 |
| Ветвление сброса кэшей бюджета | 335–361, 1022–1023 |
| UI-хром источника | `ApplySourceChrome` (1056–1062) |

Итог: чтобы добавить новый источник, нужно править 5–6 мест в `MainWindow`, а не добавить один класс.

---

## 2. Целевая архитектура

```
MainWindow
   │  зависит только от
   ▼
IDataProvider  ◄── DataProviderFactory ── создаёт по DataSource
   ▲
   ├── OpenCodeDataProvider ── OpenCodeStore + BudgetService
   ├── CursorDataProvider   ── CursorStore
   ├── DevinDataProvider    ── DevinStore
   └── ClaudeDataProvider   ── заглушка

MainWindow получает от провайдера:
   • ProviderResult  — snapshot + подсказки/статус источника
   • BudgetUiModel   — готовые text/detail/tooltip/severity
```

Ключевое разделение ответственности:

- **Провайдер** знает, *откуда* брать данные и *как* их трактовать; он же решает,
  что показать в бюджете и насколько это критично.
- **MainWindow** знает только *как* отрисовать `BudgetUiModel` (какой `Brush` взять
  для `Normal / Warning / Critical / Muted`) и как обновить дерево/карточки.

`Brush` в модель не кладём: это WPF-тип, и провайдер не должен зависеть от UI.
Провайдер отдаёт состояние (`BudgetSeverity`), цвет выбирает View.

---

## 3. Новые контракты

### 3.1. `DataProviders/BudgetUiModel.cs`

```csharp
namespace TokenMonitor.DataProviders;

public enum BudgetSeverity
{
    Normal,
    Warning,
    Critical,
    Muted,   // нет данных / ошибка
}

public sealed record BudgetUiModel(
    string Text,
    string Detail,
    string? Tooltip = null,
    BudgetSeverity Severity = BudgetSeverity.Normal)
{
    public static readonly BudgetUiModel Empty = new("—", "—", null, BudgetSeverity.Muted);
}
```

Правила маппинга старых веток:

| Старый код | Новое значение |
| --- | --- |
| `budget.Ratio >= 0.9`, `totalPercent >= 90` | `Critical` |
| `budget.Ratio >= 0.7`, `totalPercent >= 70` | `Warning` |
| обычное значение | `Normal` |
| `Error != null`, `нет данных`, Claude | `Muted` |

### 3.2. `DataProviders/ProviderResult.cs`

```csharp
namespace TokenMonitor.DataProviders;

public sealed class ProviderResult
{
    public required DataSnapshot Snapshot { get; init; }

    /// Подсказка под деревом; null = оставить значение по умолчанию (TreeHint провайдера).
    public string? TreeHint { get; init; }

    /// Готовое сообщение в StatusText (ошибка API, пустая база и т.п.);
    /// null = стандартная строка «сессий N · запросов M · обновлено HH:mm:ss».
    public string? StatusNote { get; init; }
}
```

`StatusNote` заменяет собой всю ветку «оставить прошлую ошибку»: провайдер формирует
актуальное сообщение на каждой загрузке, поэтому запоминать предыдущий текст в UI не нужно.

### 3.3. `DataProviders/IDataProvider.cs`

```csharp
namespace TokenMonitor.DataProviders;

public interface IDataProvider
{
    DataSource Source { get; }
    string DisplayName { get; }

    /// Конфиг из settings.json — для ProviderConfigEx и ссылки «конфиг».
    ProviderConfig Config { get; }

    /// Текст под именем источника (DbPathText).
    string PathText { get; }

    /// Подсказка под деревом по умолчанию.
    string TreeHint { get; }

    bool IsEnabled { get; }

    /// Умеет ли источник открывать сессию в терминале (меню OpenCode).
    bool SupportsSessionLaunch { get; }

    Task<ProviderResult> LoadAsync(bool force, CancellationToken token = default);

    /// Провайдер сам кэширует бюджет и сам соблюдает BudgetRefreshSeconds.
    Task<BudgetUiModel> GetBudgetAsync(bool force, CancellationToken token = default);
}
```

`force` появляется вместо ручного сброса `lastBudgetFetch` в `MainWindow`:
кнопка «Обновить» и смена источника вызывают `RefreshAsync(force: true)`, и провайдер
перезапрашивает бюджет, а не ждёт истечения интервала.

### 3.4. `DataProviders/DataProviderFactory.cs`

```csharp
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
```

Фабрика — единственное место, где перечислены конкретные типы. При желании позже
заменяется на `IDataProviderFactory` для тестов; на первом этапе статический метод проще.

---

## 4. Конкретные провайдеры

### 4.1. `OpenCodeDataProvider`

Переносит:

- `new OpenCodeStore()` и `new BudgetService(settings)`;
- ветку `DataSource.OpenCode` из `RefreshAsync` (215–233, 327–343);
- весь код `UpdateBudgetUi` для `BudgetDisplay.Percent` / `Money` и `AppendMeterTooltip`.

Поведение:

```csharp
public async Task<ProviderResult> LoadAsync(bool force, CancellationToken token = default)
{
    try
    {
        var snapshot = await Task.Run(_store.Load, token);
        var note = snapshot.Sessions.Count == 0 && File.Exists(_store.DbPath)
            && string.IsNullOrWhiteSpace(snapshot.LoadError)
            ? "База opencode пуста. Запустите opencode и выполните хотя бы один запрос."
            : null;
        return new ProviderResult { Snapshot = snapshot, StatusNote = note };
    }
    catch (FileNotFoundException)
    {
        return new ProviderResult
        {
            Snapshot = DataSnapshot.Empty(_store.DbPath),
            StatusNote = "База opencode не найдена. Запустите opencode и выполните хотя бы один запрос.",
        };
    }
}
```

`DataSnapshot.Empty(...)` — небольшой статический helper рядом с `DataSnapshot`
(в `OpenCodeStore.cs`), чтобы не дублировать инициализацию пустого снапшота в двух местах.

`GetBudgetAsync`:

- кэш `BudgetUiModel` + `DateTime _lastFetch`;
- `force || _cached is null || elapsed >= settings.BudgetRefreshSeconds` → `_budgetService.GetAsync()`;
- маппинг `BudgetInfo` → `BudgetUiModel` 1:1 с текущим `UpdateBudgetUi`
  (текст, детали, тултип, `Tooltip`-строки, адрес `BudgetService.KeyInfoUrl`);
- ошибки и `Display == Money && Spend is null` → `Muted`.

### 4.2. `CursorDataProvider`

Переносит `CursorStore`, ветку `RefreshAsync` (235–238), `UpdateCursorBudgetUi` (743–782).

- `LoadAsync`: `_store.LoadAsync(refreshRemote: true, token)`.
- `GetBudgetAsync`: кэш `CursorPeriodUsage` + `GetPeriodAsync()`, маппинг как в
  `UpdateCursorBudgetUi` (`TotalPercent`, диапазон цикла, membership, Auto/API, source).
- `PathText`: `CursorStore.EventsPath`.
- `TreeHint`: текущая строка про Cursor.

### 4.3. `DevinDataProvider`

Переносит `DevinStore`, ветку `RefreshAsync` (239–260, 294–317), `UpdateDevinBudgetUi` (784–822).

- Внутри хранит `DevinSnapshot? _snapshot` (вместо поля `MainWindow`).
- `LoadAsync`:
  - `_snapshot = await _store.LoadAsync(token)`;
  - `ToDataSnapshot`;
  - собирает ошибки `UsageMetrics / SessionMetrics / CategoryMetrics` в `StatusNote`
    и в `TreeHint` (сейчас это делается дважды — в 244–260 и 294–317; останется один раз).
- `GetBudgetAsync`:
  - ошибка `UsageMetrics` → `Muted` с текстом ошибки;
  - иначе сессии за месяц, PRs/merged/searches, ACU/сессия, категории, источник
    — как в `UpdateDevinBudgetUi`.
- `PathText`: `DevinStore` отдаёт `DbPath` после первой загрузки; до неё — строка
  `"Devin API v3 (org: …)"` / `"Devin API не настроен"` (текущая логика из конструктора).
- `IsEnabled`: как в `settings.Devin`.

### 4.4. `ClaudeDataProvider`

Заглушка без хранилища:

- `LoadAsync` → пустой `DataSnapshot` с `DbPath = "Claude - не реализовано"`;
- `GetBudgetAsync` → `new BudgetUiModel("—", "Claude не реализован", "Claude не реализован", Muted)`;
- `TreeHint` = `"Claude - не реализовано"`.

### 4.5. Общие требования к провайдерам

- Не трогают WPF-контролы и `Dispatcher`.
- Не бросают исключения наружу: `LoadAsync`/`GetBudgetAsync` возвращают ошибку в данных
  (как это уже делает `CursorStore`/`DevinStore`). `MainWindow` оставляет общий `try/catch`
  как последнюю линию защиты.
- Потокобезопасность: вызовы идут только из UI-потока `RefreshAsync`; `Task.Run` внутри
  `OpenCodeDataProvider` — единственное место, где работа уходит в фон.
- `BudgetRefreshSeconds` соблюдается внутри провайдера, а не в окне.

---

## 5. Перевод `MainWindow` на полиморфизм

### 5.1. Поля: до и после

Удаляем:

```csharp
readonly OpenCodeStore store = new();
readonly CursorStore cursorStore;
readonly DevinStore devinStore;
readonly BudgetService budgetService;

BudgetInfo? budget;
CursorPeriodUsage? cursorPeriod;
DevinSnapshot? devinSnapshot;
DateTime lastBudgetFetch = DateTime.MinValue;
DateTime lastCursorPeriodFetch = DateTime.MinValue;
```

Оставляем/добавляем:

```csharp
readonly Dictionary<DataSource, IDataProvider> dataProviders;
IDataProvider CurrentProvider => dataProviders[currentSource];

// для ComboBox, как и раньше
public Dictionary<string, ProviderConfigEx> Providers { get; set; } = new();
```

`snapshot`, `signature`, `roots`, `selected`, `currentSource` остаются: это состояние
представления, а не источника.

### 5.2. Конструктор

```csharp
dataProviders = DataProviderFactory.Create(settings);

Providers = dataProviders.Values
    .OrderBy(provider => provider.Source)
    .ToDictionary(
        provider => provider.DisplayName,
        provider => new ProviderConfigEx(
            provider.Config,
            provider.Source,
            provider.PathText,
            provider.TreeHint));
```

Порядок `OpenCode, Cursor, Claude, Devin` сохраняется, потому что он совпадает с порядком
`DataSource`. Диагностика Devin (запись `tokenmonitor_debug.txt` и стартовый `StatusText`)
остаётся в `MainWindow` до отдельного шага — либо переносится в `DevinDataProvider`
как `StartupNote`.

### 5.3. `RefreshAsync` после рефакторинга

```csharp
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
```

Шаблонные шаги больше не знают, чей snapshot пришёл. Веток по `currentSource` в методе нет.

Вспомогательные методы:

```csharp
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
```

### 5.4. Отрисовка бюджета

```csharp
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
```

`UpdateBudgetUi`, `UpdateCursorBudgetUi`, `UpdateDevinBudgetUi`, `AppendMeterTooltip`
удаляются из `MainWindow` (переезжают в провайдеры).

### 5.5. Смена источника и кнопка «Обновить»

```csharp
async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync(force: true);

// SourceComboBox_SelectionChanged
signature = 0;              // дерево перестроить
ApplySourceChrome();
await RefreshAsync(force: true);
```

Ручной сброс `lastBudgetFetch`/`lastCursorPeriodFetch` больше не нужен.

`UpdateBudgetUi()` как отдельный публичный вход тоже больше не нужен: бюджет приходит
из `GetBudgetAsync` в каждом цикле (из кэша провайдера — дёшево).

### 5.6. `ApplySourceChrome`

```csharp
void ApplySourceChrome()
{
    var provider = CurrentProvider;
    DbPathText.Text = provider.PathText;
    TreeHint.Text = provider.TreeHint;
    ConfigLink.ToolTip = provider.Config.ConfigPath;
}
```

`CurrentProvider()`/`SelectedProvider()`:

- `CurrentProvider` становится свойством (см. 5.1);
- `SelectedProvider()` остаётся для ComboBox, но возвращает выбранный `ProviderConfigEx`.

### 5.7. Меню сессии

Сейчас пункты «Открыть в терминале» показываются для любой сессии. Добавляем проверку
`CurrentProvider.SupportsSessionLaunch` (true только у OpenCode) в `SessionMenu_Opened`:

```csharp
var canLaunch = CurrentProvider.SupportsSessionLaunch;
ResumeTuiItem.IsEnabled = enabled && canLaunch;
ResumeForkItem.IsEnabled = enabled && canLaunch;
```

Это не обязательный шаг для компиляции, но логичное завершение развязки UI и источника.

---

## 6. Матрица переноса кода

| Куда | Откуда (`MainWindow.xaml.cs`) |
| --- | --- |
| `OpenCodeDataProvider.LoadAsync` | 215–233, 327–333 |
| `OpenCodeDataProvider.GetBudgetAsync` | 335–343, 646–741 |
| `CursorDataProvider.LoadAsync` | 235–238 |
| `CursorDataProvider.GetBudgetAsync` | 344–352, 743–782 |
| `DevinDataProvider.LoadAsync` | 239–260, 294–317 |
| `DevinDataProvider.GetBudgetAsync` | 353–356, 784–822 |
| `ClaudeDataProvider` | 262–272, 357–361 |
| `MainWindow.RefreshAsync` | шаблонная часть 207–214, 275–293, 318–325 |
| `MainWindow.ApplyBudget` | 673–678, 760–765, 802 (только выбор цвета) |
| `MainWindow.ApplyStatus` | 290–325 |
| `MainWindow.ApplyTreeHint` | 244–260 |

---

## 7. Пошаговый порядок работ

Каждый шаг заканчивается сборкой (`dotnet build`) — так проще локализовать ошибки.

1. **Контракты.** Добавить `DataProviders/IDataProvider.cs`, `BudgetUiModel.cs`,
   `ProviderResult.cs`, `DataProviderFactory.cs` (фабрика пока может быть пустой или
   с заглушками). Сборка должна проходить.
2. **OpenCode.** Перенести `OpenCodeStore` + `BudgetService` в `OpenCodeDataProvider`.
   Сравнить бюджет в UI до/после (проценты, деньги, ошибки, тултип).
3. **Cursor.** Перенести `CursorStore` и `UpdateCursorBudgetUi`. Проверить `EventsPath`,
   процент, Auto/API, цикл.
4. **Devin.** Перенести `DevinStore`, ошибки API, бюджет. Проверить оба состояния:
   настроен / не настроен.
5. **Claude.** Заглушка, проверка пустого дерева и `—` в бюджете.
6. **MainWindow.** Удалить старые поля/методы, перевести `RefreshAsync`,
   `ApplySourceChrome`, `ConfigLink_Click`, ComboBox на провайдеров.
7. **Уборка.** Удалить неиспользуемые `using`, мёртвые методы, обновить
   `README.md` (раздел «Структура проекта»: добавить `DataProviders/`) и при желании
   `SessionMenu_Opened` (capability-флаг).

---

## 8. Изменения поведения (осознанные)

1. **Статусная строка.** Старое правило «если StatusText начинается с `ошибка` или
   `База opencode`, не перезаписывать» исчезает. Провайдер сам возвращает `StatusNote`
   на каждом цикле, поэтому сообщение всегда актуально. Это исправляет ситуацию, когда
   старая ошибка «залипала» после успешного обновления.
2. **Бюджет обновляется на каждом цикле, но запрашивается по расписанию.** Провайдер
   возвращает кэш `BudgetUiModel`; сетевой запрос — только по `BudgetRefreshSeconds`
   или по `force`. Визуально ничего не меняется.
3. **Devin TreeHint.** Ошибки API больше не пишутся дважды; подсказка обновляется
   из одного места (`ProviderResult.TreeHint`).
4. **Ошибки провайдера не пробрасываются в общий `catch`.** Они попадают в
   `StatusNote`/`BudgetUiModel`, как сейчас у Cursor/Devin. Общий `catch` остаётся
   для непредвиденных исключений.

Если какое-то из изменений нежелательно — фиксируем его отдельно и сохраняем
поведение 1:1 (например, оставляем старый приоритет сообщений в `ApplyStatus`).

---

## 9. Риски

| Риск | Митигация |
| --- | --- |
| `BudgetInfo`/`CursorPeriodUsage`/`DevinSnapshot` имеют разную семантику | Не унифицировать модели данных, унифицировать только **UI-модель бюджета** |
| `ProviderConfigEx` нужен XAML (`DisplayMemberPath="Key"`, `SelectedValuePath="Value"`) | Оставляем `Providers` как есть, наполняем из провайдеров |
| OpenCode `store` создаётся без `settings`, а `BudgetService` — с ними | `OpenCodeDataProvider(settings)` создаёт оба внутри |
| Devin `PathText` зависит от факта загрузки | До первой загрузки — строка из настроек, после — `DevinSnapshot.DbPath` |
| `RefreshAsync` могут вызвать повторно (таймер + кнопка) | Сохраняем `busy`-флаг; `force` не ломает защиту от параллельных запусков |
| Потеря `FileNotFoundException`-спецобработки в `--dump` | `--dump` (`App.xaml.cs`) не трогаем: он использует `OpenCodeStore`/`BudgetService` напрямую |

---

## 10. Критерии готовности

- В `MainWindow.xaml.cs` нет упоминаний `OpenCodeStore`, `CursorStore`, `DevinStore`,
  `BudgetService`, `BudgetInfo`, `CursorPeriodUsage`, `DevinSnapshot`.
- В `RefreshAsync` нет `if (currentSource == ...)`.
- В `UpdateBudgetUi`/`UpdateCursorBudgetUi`/`UpdateDevinBudgetUi` нет необходимости —
  методы удалены.
- Добавление нового источника = новый класс + одна строка в `DataProviderFactory`.
- `dotnet build` без предупреждений, `--dump` работает как раньше.
- Ручной чек-лист:
  - переключение OpenCode → Cursor → Claude → Devin и обратно;
  - OpenCode: деньги/проценты, ошибка LiteLLM, пустая/отсутствующая база;
  - Cursor: процент, период, membership, ошибка API;
  - Devin: настроен / не настроен, ошибки usage/session/category;
  - Claude: `—` и «Claude не реализован»;
  - кнопка «Обновить» форсирует и данные, и бюджет;
  - дерево, KPI, алерты, живая лента не регрессировали.

## 11. Вне рамок этого рефакторинга

- Асинхронная отмена загрузки (`CancellationTokenSource` на цикл обновления).
- Юнит-тесты WPF-окна; тестируемыми делаем провайдеры (при необходимости —
  через `IDataProviderFactory` и подмену HTTP-клиентов).
- Пересмотр `App.xaml.cs` (`--dump`) и `BudgetService`/`OpenCodeBudgetProvider`.
- Изменение XAML, кроме, возможно, привязки `IsEnabled` меню к capability-флагу.
