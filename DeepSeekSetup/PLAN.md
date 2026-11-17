# План: мониторинг токенов DeepSeek-агента (DSH) в TokenMonitor

**Версия плана:** 1.0 · **Дата:** 2026-11-17
**Объект мониторинга:** DeepSeek Harness Desktop, профиль `desktop`, сборка `0.2.2-rc.3`
**Проект-потребитель:** `TokenMonitor` (C#/WPF, `net10.0-windows`)

> Факты об API DSH, на которых держится план, собраны с указанием файлов в
> [`FACTS.md`](FACTS.md). Схема БД — [`schema.sql`](schema.sql). Ни один пункт плана не
> основан на предположении «как обычно бывает»: всё, что не проверено, вынесено в
> [`FACTS.md` §9](FACTS.md) и в раздел 10 настоящего плана.

---

## 1. Цель и критерии успеха

**Цель:** TokenMonitor показывает расход токенов DeepSeek-агента (DSH) так же, как сейчас
показывает OpenCode: дерево «сессия → запрос(ход) → шаг», четыре KPI-карточки, живую ленту,
алерты — но с **максимально детальными** данными о токенах, которых нет ни в одном другом
источнике: провайдерский `usage` на каждый шаг, попытки и ретраи, давление на контекст,
кэш-хит по каждому шагу, тайминги TTFT/декодирования, стоимость инструментов по времени и
размерам, разбивка контекста на system/tools/messages.

**Критерии успеха (проверяемые):**

1. После установки плагина новая сессия агента появляется в TokenMonitor без ручных действий
   и без правки журналов DSH.
2. Числа в дереве совпадают с провайдерским учётом: сумма `total_tokens` по шагам сессии
   равна сумме `usage.totalTokens` из событий `assistant/message` этой сессии (проверяется
   SQL-запросом, см. §9.2).
3. История за прошлые сессии (backfill) попадает в БД при первом запуске, повторный запуск
   не создаёт дублей (`INSERT OR REPLACE` по `(session_id, seq)`).
4. Плагин не может сломать сессию: любая его ошибка логируется и проглатывается; append
   в журнал DSH не выполняется ни при каких условиях.
5. TokenMonitor не пишет в БД плагина (только `PRAGMA query_only = 1`) и корректно показывает
   «нет данных», если БД отсутствует или плагин не установлен.
6. `dotnet build` проходит, приложение запускается, переключение источника
   OpenCode ↔ DeepSeek не теряет состояние UI.

**Не входит в цель:** правки самого DSH, отображение цены в деньгах по прайсу DeepSeek
(см. §7.6 и §11), замена штатной аналитики DSH.

---

## 2. Что представляет собой проект сегодня

### 2.1 Одно окно и его зоны

Приложение — WPF-приложение с **одним окном** `MainWindow`
(`MainWindow.xaml`, `MainWindow.xaml.cs`), заголовок «Token Monitor — расход токенов».
Второго окна нет: диалоги не используются, «конфиг» открывается системным редактором по
ссылке (`ConfigLink_Click`, `SourceConfigLink_Click`). Назначение окна — один экран, на
котором виден расход токенов выбранного источника за разные горизонты и его детализация.

| Зона (XAML) | Назначение | Что показывает |
|---|---|---|
| Шапка: `SourceComboBox` + `ProxyTrafficText` + `DbPathText` | выбор источника (`OpenCode/Cursor/Claude/Devin`), путь к данным, трафик локального прокси | `Providers` (словарь `ProviderConfigEx`) |
| Шапка по центру: `BudgetText`, `BudgetDetail`, `ConfigLink` | «бюджет» источника (проценты лимита или деньги) и ссылка на конфиг провайдера | `BudgetUiModel` |
| Шапка справа: `StatusText`, `RefreshButton`, `SourceConfigLink` | статус загрузки, ручное обновление, `settings.json` | `IDataProvider.LoadAsync` |
| 4 карточки: ЗА МЕСЯЦ / ЗА ДЕНЬ / ЗА СЕССИЮ / ЗА ЗАПРОС | KPI по `TokenCounts` | считаются из `DataSnapshot` в `UpdateKpi()` |
| `AlertsPanel` | предупреждения за `AlertWindowHours` | `UpdateAlerts()`: контекст >150k, шаги >40, кэш-вход >100k, сессия >100M |
| `SessionsTree` (слева) | дерево «сессия → запрос → шаг» с колонками ТОКЕНЫ/ВХОД/ВЫХОД/REASONING/КЭШ %/CACHE W/ЗАПР·ШАГИ/ВРЕМЯ | `SessionNode`/`RequestNode`/`StepNode` |
| Контекстное меню дерева | «Открыть в терминале», «копией (--fork)», «Скопировать ID» | `SupportsSessionLaunch` + `LaunchTui()` |
| `LiveList` (справа) | «живая лента запросов», последние 200 записей | `DataSnapshot.Live` (`LiveEvent`) |

### 2.2 Модель данных

`Models.cs`:

- `TokenCounts(Input, Output, Reasoning, CacheRead, CacheWrite)`; `Total` — сумма пяти,
  `CacheHitPercent = CacheRead / (Input + CacheRead)`, т.е. **`Input` — это НЕкэшированный вход**;
- `SessionData` (Id, ParentId, Title, Directory, Model, Created, Updated, Tokens, Requests);
  `IsChild`, `DurationMs`, `MaxContext`, `MaxSteps`;
- `RequestData` (Id, SessionId, Prompt, Time, Tokens, Steps); `DurationMs` — до конца последнего шага,
  `Context` — input+cacheRead последнего шага;
- `StepData` (Id, Time, Completed, Model, Tokens);
- `LiveEvent` (Time, SessionId, MessageId, Model, Steps, Input/Output/Reasoning/CacheRead/CacheWrite,
  Billed, Context, DurationMs).

### 2.3 Конвейер данных

```
IDataProvider.LoadAsync(force)
   → ProviderResult { DataSnapshot Snapshot, TreeHint?, StatusNote? }
   → MainWindow.RefreshAsync(): RebuildTree / UpdateLive / UpdateAlerts / UpdateKpi / ApplyBudget
```

`DataSnapshot` (`OpenCodeStore.cs`) = `Sessions`, `Live`, `Signature` (для дешёвого сравнения),
`DbPath`, `EventsPath`, `LogPath`, `LoadedAt`, `LoadError`.

### 2.4 Провайдеры сегодня

| Источник | Файлы | Как получает данные | Состояние |
|---|---|---|---|
| OpenCode | `OpenCodeStore.cs`, `DataProviders/OpenCodeDataProvider.cs` | SQLite `~/.local/share/opencode/opencode.db` (v1/v2 таблицы) + `token-events.jsonl` | полноценный, эталон |
| Cursor | `CursorClient.cs`, `CursorStore.cs` | локальный `state.vscdb` + `token-events.jsonl` (хук) + Dashboard API | полноценный |
| Claude | `DataProviders/ClaudeDataProvider.cs` | — | заглушка «не реализовано» |
| Devin | `DevinClient.cs`, `DevinStore.cs` | REST API v3 | полноценный (без дерева шагов) |

Точки расширения, которые обязана пройти новая интеграция:
`AppSettings.cs` (`DataSource`, `ProviderConfig`, `SettingsStore`), `DataProviders/DataProviderFactory.cs`,
`DataProviders/IDataProvider.cs`, `MainWindow.xaml.cs` (создание `Providers`, `LaunchTui`).

---

## 3. Почему для DeepSeek нужен свой сборщик

DSH пишет журнал сессий (`~/.dsh/sessions/--<cwd>--/<id>/session.v4.jsonl.zstd`) — там уже есть
провайдерский `usage` в `assistant/message`. Прямое чтение журнала из C# выглядит дешёвым, но:

| Вариант | Плюсы | Минусы | Решение |
|---|---|---|---|
| **A. TokenMonitor читает JSONL напрямую** | нет плагина | zstd-фреймы, смена версии формата (v4 → v5), миграции, «рваный хвост», нужен zstd-декодер в C#; usage есть, но нет давления на контекст, таймингов, ретраев, разбивки | отклонён |
| **B. OTel-экспорт DSH** (`dsh-session-telemetry-otel`) | «официальный» канал | требует коллекционера, шлёт префиксы сессии только по фидбеку пользователя, не даёт пошаговой детализации токенов | отклонён |
| **C. Host-плагин DSH → SQLite; TokenMonitor читает SQLite** | пишем ровно те метрики, что нужны; плагин видит все события live и умеет backfill через публичный API; C# уже умеет SQLite (`Microsoft.Data.Sqlite`); БД — derived-артефакт, её можно пересоздать | нужен код плагина + установка бандла | **выбран** |

Ключевой принцип: **журнал DSH остаётся источником истины, наша БД — производная**.
Любую derived-таблицу можно пересобрать из `raw_events`/журнала, поэтому потеря БД не критична.

---

## 4. Архитектура

```
┌────────────────────────── DSH host process ──────────────────────────┐
│  dsh-session (журнал сессий)                                         │
│      │ session/event, session/created, session/disposed, session/flush│
│      ▼                                                               │
│  @local/dsh-token-logger (Host-only bundle)                          │
│      • fold событий → строки таблиц                                  │
│      • backfill через ctx.sessionPersistence (list/open/handle.read)  │
│      • обогащение через ctx.tokenMeter / ctx.sessionProjections       │
│      • буфер + батч-запись node:sqlite (DatabaseSync, WAL)           │
└───────────────────────────────┬──────────────────────────────────────┘
                                │  %USERPROFILE%\.dsh\tokens\tokens.db
                                ▼
┌────────────────────────── TokenMonitor (WPF) ────────────────────────┐
│  DataProviders/DeepSeekDataProvider  →  DeepSeekStore (read-only)     │
│      SELECT ... FROM v_session_summary / v_turn_summary / v_step_feed │
│      → DataSnapshot → дерево, KPI, живая лента, алерты, бюджет       │
└──────────────────────────────────────────────────────────────────────┘
```

**Границы ответственности:**

- плагин — единственный писатель БД (SQLite single-writer, WAL);
- TokenMonitor — второй процесс, только `PRAGMA query_only = 1`, ретраи на `SQLITE_BUSY`;
- контракт между ними — **представления `v_*`** (см. `schema.sql`), а не внутренние таблицы;
- схема версионируется в `meta.schema_version`; C# проверяет версию и деградирует мягко.

---

## 5. Плагин `@local/dsh-token-logger`

### 5.1 Пакет и файлы

Целевое дерево (создаётся на этапе P1 в `DeepSeekSetup/plugin/`):

```
DeepSeekSetup/plugin/
  package.json          # "type":"module", exports ".", dsh.bundle.patch, files[]
  cordis.patch.yml      # вставляет запись token-logger с config
  index.js              # apply(ctx, config): подписки, батчинг, backfill, миграции
  src/fold.js           # чистые функции: событие → строки (тестируемо без DSH)
  src/schema.js         # DDL + PRAGMA + PRAGMA user_version/meta
  src/db.js             # DatabaseSync-обёртка: батчи, транзакции, ретраи, WAL
  src/backfill.js       # импорт истории через ctx.sessionPersistence
  src/redact.js         # превью/хеши текста (приватность)
  locale/en.json        # meta.title / meta.description / meta.detail
  locale/ru.json
  icon.svg
  docs/DETAIL.md        # страница плагина в Plugin Manager
  test/fold.test.mjs    # node:test, без DSH
```

Почему Host-only и без UI: цель — сбор данных; UI-панель DSH дублировала бы TokenMonitor.
`inject: ['sessions']`, опционально `sessionPersistence`, `tokenMeter`, `sessionProjections`
(через `ctx.inject([...])`, чтобы плагин оставался работоспособным в композициях без них).

### 5.2 Config (всё изменяемое — здесь, а не в коде)

| Поле | Тип | По умолчанию | Смысл |
|---|---|---|---|
| `dbPath` | string | `dshHomePath('tokens','tokens.db')` | путь БД; родительский каталог создаётся |
| `capture` | `'usage' \| 'full' \| 'raw'` | `'full'` | `usage` — только токены/тайминги; `full` — + тулы, контекст, ретраи, попытки; `raw` — + `raw_events` |
| `captureText` | `'off' \| 'preview' \| 'full'` | `'preview'` | тексты промптов/ответов: не писать / превью 200 симв. / целиком |
| `previewChars` | number | `200` | длина превью |
| `backfillOnStart` | boolean | `true` | импорт истории при активации |
| `backfillLimit` | number | `50` | сколько сессий догонять за один старт (остальные — по LRU) |
| `flushIntervalMs` | number | `250` | окно батча записи |
| `flushMaxEvents` | number | `200` | порог немедленного сброса |
| `retentionDays` | number | `0` (не удалять) | удаление старых сессий и сырых событий |
| `journalMode` | `'wal' \| 'delete'` | `'wal'` | WAL для параллельного чтения |
| `verbose` | boolean | `false` | подробный лог в `ctx.logger` |

### 5.3 Жизненный цикл

```js
export const inject = ['sessions']
export const Config = { /* schemastery/zod-схема полей выше */ }

export function apply(ctx, config) {
  const db = openDb(config)                     // миграции + PRAGMA
  ctx.effect(() => () => db.close())            // закрытие при выгрузке плагина
  ctx.on('session/created',   s => onSessionCreated(s))
  ctx.on('session/event',     (s, e) => onEvent(s, e))
  ctx.on('session/disposed',  s => onSessionDisposed(s))
  ctx.on('session/flush',     s => flushSession(s))   // awaited барьер
  ctx.effect(() => { const t = setInterval(drain, config.flushIntervalMs); return () => clearInterval(t) })
  if (config.backfillOnStart) queueMicrotask(() => backfill(ctx, db, config))
}
```

Правила, которые нельзя нарушать:

- **никогда** не вызывать `session.append(...)` и не добавлять новые типы событий — это ломает
  переоткрытие сессии (`dsh-session/README.md`, `references/practices.md`);
- любой обработчик целиком в `try/catch`; ошибка → `ctx.logger.warn` + строка в `ingest_runs`;
- никакой синхронной работы в `session/event` дольше пары микросекунд: только нормализация
  события в плоский объект и push в очередь; запись в SQLite — в `drain()` (батч, транзакция);
- при `session/disposed` — принудительный `drain()` для этой сессии, чтобы не потерять хвост.

### 5.4 Индексация: событие → таблицы

| Событие DSH | Что пишем |
|---|---|
| `session/created` | `sessions` (id, header: cwd/createdAt/parent/origin/depth/preset/isSeeded), `session_cursors` |
| `request/header` | `sessions.provider/model/reasoning_effort`, `steps.request_header_seq` (и полный конфиг в `raw_events`) |
| `request/context` | `steps.request_context_seq`, `context_window` |
| `turn/start` / `turn/end` | `turns` (границы, end_kind, end_error_*) |
| `user/message` | `turns.prompt_preview` / `prompt_chars` (если своего промпта ещё нет) |
| `step/start` | `steps.started_ms` |
| `assistant/message` | `steps` (usage, timed, interrupted, message id, chars), `usage_samples`, `context_samples`, `attempts`-счётчики |
| `assistant/attempt` | `attempts` (seq, stream_records/chars) |
| `llm/retry` | `retries` (event_kind='scheduled', retry_id, provider, mode, policy_key, failure, delay) |
| `llm/retry-started` | `retries` (event_kind='started') |
| `tool/call` | `tool_calls` (name, arguments_chars/preview, called_ms) |
| `tool/result` | `tool_calls` (finished_ms, duration, is_error, error_*, result_chars/preview, meta_json) |
| `step/end` | `steps.ended_ms`, `duration_ms`, пересчёт агрегатов шага |
| `compaction/*` | `compactions` (если payload подтверждён на P1, иначе — только `raw_events`) |
| любое | `raw_events` при `capture='raw'`, `sessions.last_seq/event_count/updated_ms` |

Агрегаты (`turns.steps_count`, `steps.tools_count/tool_errors`, суммы токенов сессии) не хранятся
вручную, а пересчитываются представлениями — кроме тех полей, что нужны дереву (денормализация
осознанная, см. §6).

### 5.5 Backfill истории

`ctx.sessionPersistence` (публичный контракт):

```
const snaps = await ctx.sessionPersistence.list()          // все сохранённые сессии
for (const snap of snaps) {
  const cur = SELECT last_seq FROM session_cursors WHERE session_id = ?
  if (snap.header && cur >= (snap.eventCount - 1)) continue  // уже импортировано
  const h = await ctx.sessionPersistence.open(snap.header.id, 'read')
  const { events } = await h.read()                        // offset/length — для дочитывания
  importEvents(snap.header, events)                        // тот же fold, что и live
  await h.close()
}
```

Свойства: идемпотентность (`INSERT OR REPLACE` по PK `(session_id, seq)`), устойчивость к
смене формата (backend сам мигрирует), отсутствие чтения `.zstd` своими руками.
Импорт идёт порциями (`batchSize = 500` событий = одна транзакция), с паузой между порциями,
чтобы не блокировать loop; прогресс — в `session_cursors` и `ingest_runs`.

### 5.6 Пересборка и восстановление

- `meta.schema_version` не совпал → `DROP`/`CREATE` derived-таблиц и полный backfill
  (журналы сессий не трогаются);
- повреждение БД → переименовать файл (`tokens.db.broken-<ts>`) и создать заново;
- «дырка» в данных (`last_seq` < конца журнала) → повторный backfill этой сессии;
- аварийный признак в UI: `v_ingest_health.last_run_finished_ms` старше 5 минут при живых сессиях.

### 5.7 Ретеншен и размер

Объём: ~1–3 КБ на шаг без `raw_events`; `raw_events` с `stream` в `assistant/message` — до
десятков КБ на шаг. Поэтому:

- по умолчанию `capture='full'` (сырые события не пишутся, но всё «производное» — пишется);
- `captureText='preview'` по умолчанию;
- `retentionDays>0` → ночная задача: `DELETE` сессий старше N дней (каскад по FK),
  затем `PRAGMA incremental_vacuum`;
- `raw_events` хранится с отдельным лимитом (`retentionDays` для raw = `retentionDays/3`).

### 5.8 Приватность

Промпты — самый чувствительный материал (ключи, токены доступа, персональные данные).
Поэтому: `captureText='off'` оставляет только длины и хеши; `preview` — 200 символов;
полный текст — только явным opt-in. В `docs/DETAIL.md` и в описании плагина это должно быть
написано прямо. Пароли/ключи никогда не попадают в `meta.json` инструментов — только `meta`
из события.

### 5.9 Производительность и отказоустойчивость

- `DatabaseSync` — синхронный: батч по `flushIntervalMs`/`flushMaxEvents`, одна транзакция
  на батч, `PRAGMA synchronous=NORMAL`, `wal_autocheckpoint=2000`;
- оценка бюджета: 20–30 событий на шаг, 2–4 КБ JSON → на 1000 шагов ≈ 1–2 МБ; запись батчами
  по 200 событий — единицы миллисекунд;
- очередь ограничена (`maxQueue=10000`); при переполнении — drop с инкрементом счётчика
  ошибок в `ingest_runs` (лучше потерять метрику, чем память сессии);
- ошибка записи (диск/лок) не пробрасывается: одна повторная попытка и лог;
- закрытие/выгрузка плагина: финальный `drain()` в disposer'е.

### 5.10 Совместимость версий

- `engines.dsh: ">=0.2.2-rc.3 <0.3.0"` (проверка при установке);
- в `meta` пишется `dsh_version`, `plugin_version`, `schema_version`;
- «неизвестный тип события» никогда не роняет плагин: пишется в `raw_events` (если включено)
  и в диагностику; это осознанно, потому что `SessionEventMap` merge-extensible;
- при первом запуске на новой версии DSH — проверка, что ожидаемые поля `usage` на месте;
  если набор полей изменился, `raw_json` всё равно сохранит правду.

### 5.11 Установка и проверка

1. `plugin_manager` → `install_bundle` с абсолютным путём `DeepSeekSetup/plugin`
   (или **Plugins → Add plugin** → каталог). Профиль `desktop` управляется Electron,
   CLI-запуск профиля `desktop` запрещён (`FACTS.md` §8).
2. Проверить строку плагина в **Plugins**, заголовок/описание/иконку.
3. Новая сессия → один ход → в БД появились `sessions/turns/steps/usage_samples`.
4. `sqlite3 tokens.db "select * from v_ingest_health;"` — сессии/шаги/последний usage свежие.

---

## 6. Схема БД и маппинг

Полный DDL — [`schema.sql`](schema.sql) (schema_version = 1, SQLite ≥ 3.45, `STRICT`).
Таблицы: `meta`, `sessions`, `turns`, `steps`, `attempts`, `retries`, `usage_samples`,
`context_samples`, `tool_calls`, `compactions`, `raw_events`, `session_cursors`, `ingest_runs`.
Публичный контракт для C#: `v_session_summary`, `v_turn_summary`, `v_step_feed`,
`v_daily_by_model`, `v_cache_efficiency`, `v_tool_timing`, `v_ingest_health`.

### 6.1 Семантика токенов (важно для корректности UI)

Проверено на живой сессии (см. `FACTS.md` §2):

```
usage.totalTokens = inputTokens + outputTokens + cacheReadTokens + cacheWriteTokens
```

где `inputTokens` — **некэшированный** вход (в примере 453 при `cacheReadTokens=96256`).
Это ровно семантика `TokenCounts` TokenMonitor (`Input` + `CacheRead` = весь промпт), поэтому
маппинг прямой:

| DSH usage | `TokenCounts` | Комментарий |
|---|---|---|
| `inputTokens` | `Input` | некэшированный вход |
| `outputTokens` | `Output` | видимый ответ |
| `reasoningTokens` (если есть) | `Reasoning` | иначе 0; объём thinking хранится в `steps.reasoning_chars` |
| `cacheReadTokens` | `CacheRead` | |
| `cacheWriteTokens` | `CacheWrite` | |
| `totalTokens` | `Total` (сумма) | сходится с `TokenCounts.Total` |

### 6.2 Маппинг на узлы дерева

| Узел TokenMonitor | Источник | Поля |
|---|---|---|
| `SessionNode` | `v_session_summary` | Id=`session_id`, ParentId=`parent_session_id`, Title=`title`, Directory=`cwd`, Model=`model`, Created/Updated, Tokens=суммы, Subtitle: `Fmt.Folder(cwd) · model · duration · subagent` |
| `RequestNode` (ход) | `v_turn_summary` | Id=`turn-<turn>`, Prompt=`prompt_preview`, Time=`started_ms`, Tokens=суммы хода, Steps=шаги |
| `StepNode` | `v_step_feed` | Id=`<session>:<turn>:<step>`, Time=`started_ms`, Completed=`ended_ms`, Model, Tokens |
| `LiveEvent` | `v_step_feed` | Time=`ended_ms`, SessionId, MessageId=`<turn>:<step>`, Model, Steps=1, Billed=`total_tokens`, Context=`input+cache_read`, DurationMs |

`SessionData.ParentId` уже используется для пометки «subagent» (`IsChild`) — у DSH есть
`header.parentSession`/`origin`, маппинг бесплатный.

### 6.3 «Максимально детально»: полный перечень собираемых метрик

Токены: input (uncached), output, reasoning (если провайдер даёт), cache read, cache write,
total, prompt_total, на шаг / ход / сессию / модель / день.
Контекст: pressure, projected, surface, context_window, system/tools/messages (эвристика),
максимум по сессии.
Тайминги: длительность шага, TTFT, decode, llm_ms, tool_ms, длительность хода, сессии.
Попытки: `assistant/attempt` (кол-во/объём стрима), ретраи (id, provider, mode, policy,
код ошибки, задержка, факт старта).
Инструменты: имя, аргументы (превью+длина), результат (превью+длина), is_error, код/причина,
длительность, `meta_json`.
Сбойность: `turn/end.reason` (включая `error.code`, `max-tokens`, `interrupted`, `forked`),
прерывания шага, ошибки инструментов.
Структура: `agentPreset`, `cwd`, parent/origin/depth, `isSeeded`, версия формата,
`request/header.config` (provider, model, reasoningEffort, sampling) — в `raw_events`,
`request/context` (capacity), компакции.

---

## 7. Интеграция в TokenMonitor (C#)

### 7.1 `AppSettings.cs`

- `enum DataSource { OpenCode, Cursor, Claude, Devin, DeepSeek }` — **в конец**, чтобы
  сохранённый числовой `LastSource` не сдвинулся;
- `ProviderConfig DeepSeek`:
  `Name="DeepSeek"`, `ConfigPath = ~/.dsh/profiles/desktop/cordis.patch.yml`,
  `EventsPath = ~/.dsh/tokens/tokens.db` (переиспользуем существующее поле как путь к БД,
  чтобы не менять модель настроек; альтернатива — новое `DbPath`, см. §11);
- `For(DataSource.DeepSeek)`, `Normalize(...)` (EnsureProvider), `ApplyLegacy` (ключи
  `ConfigDeepSeek` / `DeepSeekDbPath`);
- `SettingsStore.DefaultDeepSeekConfigPath` / `DefaultDeepSeekDbPath`.

### 7.2 Новый `DeepSeekStore.cs`

```csharp
public sealed class DeepSeekStore
{
    public string DbPath { get; }
    public DeepSeekStore(AppSettings settings);
    public DataSnapshot Load();      // синхронно, как OpenCodeStore, вызывается через Task.Run
}
```

Детали реализации:

- если `File.Exists(DbPath) == false` → `DataSnapshot.Empty(db, "БД плагина не найдена. Установите …")`;
- connection string: `Data Source=…;Mode=ReadWrite;Cache=Shared;Pooling=False`;
- сразу после `Open()`: `PRAGMA query_only = 1; PRAGMA busy_timeout = 3000;` — гарантия,
  что TokenMonitor не пишет, и устойчивость к WAL-блоку;
- проверка `meta.schema_version`; если > поддерживаемой — `LoadError = "обновите TokenMonitor"`,
  но старые `v_*` читать всё равно пытаемся;
- запросы: `v_session_summary` (LIMIT 200, `ORDER BY updated_ms DESC`), `v_turn_summary`
  по нужным сессиям, `v_step_feed` (LIMIT 2000 на сессию), `v_ingest_health`;
- `Signature` = `last_session_update_ms` + счётчики шагов (чтобы не перестраивать дерево зря);
- все исключения `SqliteException` → `LoadError`, не падение окна.

### 7.3 Новый `DataProviders/DeepSeekDataProvider.cs`

По образцу `OpenCodeDataProvider`:

- `Source=DataSource.DeepSeek`, `DisplayName="DeepSeek"`, `IsEnabled => settings.DeepSeek.IsEnabled`;
- `PathText => store.DbPath`;
- `TreeHint = "Кликните по сессии, чтобы увидеть детализацию. Правый клик — скопировать ID сессии (запуск из DSH Desktop не поддерживается)."`;
- `SupportsSessionLaunch => false` (на P4 — опционально);
- `LoadAsync`: `Task.Run(store.Load)`, при пустой БД — `StatusNote` с инструкцией установки плагина;
- `GetBudgetAsync`: `BudgetUiModel` в режиме «токены» (см. §7.6), кэш на `BudgetRefreshSeconds`.

### 7.4 Фабрика и окно

- `DataProviderFactory.Create`: добавить `[DataSource.DeepSeek] = new DeepSeekDataProvider(settings)`;
- `MainWindow`: `Providers` строится из фабрики — новых правок не требует;
- `MainWindow.LivePathText`: сейчас жёстко «источник: token-events.jsonl (плагин token-stats.ts)»;
  заменить на `loaded.EventsPath` (уже есть в `UpdateLive`, но начальная строка вводит в заблуждение);
- `DbPathText`/`StatusText` — берутся из провайдера, изменений нет.

### 7.5 Что менять не нужно

`UpdateKpi`, `UpdateAlerts`, `RebuildTree`, `UpdateLive`, `SessionNode/RequestNode/StepNode`
работают от `DataSnapshot` и `TokenCounts` — DeepSeek подключается без правок этих методов.
Это главный аргумент в пользу выбранной архитектуры: новые данные не требуют новой UI-модели.

### 7.6 «Бюджет» для DeepSeek

У маршрута `deepseek-account` нет API стоимости, поэтому денежный бюджет недостоверен.
Предлагается (P3):

`BudgetText` = суммарные токены за текущий месяц (`Fmt.Tokens`), `BudgetDetail` =
`«кэш 92% · контекст 78% окна»`, severity по «заполненности контекста последней сессии»
(>70% Warning, >90% Critical), tooltip — из `v_session_summary`/`v_step_feed`.
Это полезнее и честнее, чем «$0.00».

### 7.7 Алерты

Существующие пороги (`AlertContextLimit=150_000`, `AlertStepLimit=40`,
`AlertCacheInputLimit=100_000`, `AlertSessionLimit=100_000_000`) для DSH работают, но
`context_window = 1_048_576`, поэтому имеет смысл добавить в `UpdateAlerts` процентный порог
от `context_window` (P3): «контекст 82% окна» вместо абсолютных 150k. Формула уже есть в
`v_cache_efficiency` (кэш) и `v_step_feed` (контекст).

### 7.8 Запуск сессии (P4, опционально)

`LaunchTui` сейчас хардкодит `opencode -s <id>`. Для DSH Desktop аналога CLI-resume нет
(профиль `desktop` — Electron, CLI его не запускает). Варианты: (а) оставить
`SupportsSessionLaunch=false`; (б) добавить в `IDataProvider` метод
`bool TryLaunch(SessionData session, out string message)` и реализовать «открыть папку
сессии/скопировать id». Рекомендация — (а) на первый релиз, (б) — по запросу.

### 7.9 Конфиг-ссылки

`ConfigLink` открывает `CurrentProvider.Config.ConfigPath` → для DeepSeek это
`~/.dsh/profiles/desktop/cordis.patch.yml` (там живёт `config` плагина: путь БД, capture,
retention). Так пользователь попадает ровно туда, где настраивается сборщик.

---

## 8. Этапы работ

| Этап | Содержание | DoD | Оценка |
|---|---|---|---|
| **P0. Подготовка** | проверить `node:sqlite` в host-процессе DSH, права на запись в `~/.dsh/tokens`; зафиксировать фактический payload `compaction/*` и `AssistantStreamRecord`; сверить `usage` на 2–3 сессиях | короткая записка-уточнение к `FACTS.md` §9; тестовая БД создаётся и читается | 0.5 дн |
| **P1. Плагин: live-индексация** | пакет/патч/локаль/иконка; `index.js`, `fold.js`, `db.js`; `sessions/turns/steps/usage_samples/context_samples/tool_calls/attempts/retries`; батчинг; миграции; установка бандла | новая сессия появляется в БД; `v_step_feed` содержит все шаги; ошибок в логе нет; выгрузка плагина закрывает БД | 1.5 дн |
| **P2. Backfill + устойчивость** | `backfill.js` через `sessionPersistence.list/open/read`; курсоры; повторный запуск без дублей; retention; `ingest_runs`; `raw_events` (off по умолчанию) | история N сессий импортирована; повторный старт не пишет дублей; `v_ingest_health` показывает свежесть | 1.5 дн |
| **P3. TokenMonitor: провайдер** | `AppSettings`, `DeepSeekStore`, `DeepSeekDataProvider`, фабрика, подписи, бюджет-токены, алерт по % окна | переключение источника показывает дерево/KPI/ленту DeepSeek; `loadError` при отсутствии БД; `dotnet build` зелёный | 1.5 дн |
| **P4. Полировка (опционально)** | `meta.detail`, иконка, retention-задача, `--rebuild` диагностика, `TryLaunch`, документация пользователю | README/DETAIL без расхождений с поведением; проверка на 3 сценариях | 1 дн |

Итого: **≈ 6 рабочих дней** (P0–P3), P4 — по желанию.

---

## 9. Тестирование и приёмка

### 9.0 Проверка схемы (уже выполнена)

`schema.sql` исполнен на SQLite 3.53.1: 14 таблиц + 7 представлений создаются,
`PRAGMA integrity_check = ok`. Представления прогнаны на тестовых данных
(сессия-субагент, ход, два шага, два сэмпла usage, вызов инструмента):
`v_session_summary` даёт верные суммы (`198 046` = 97 346 + 100 700),
`v_cache_efficiency` — 99.5% при `input=453`, `cacheRead=96 256`,
`prompt_tokens_total` = 96 709, `v_daily_by_model`, `v_tool_timing`, `v_ingest_health`
возвращают ожидаемые строки. Это снимает риск «схема не компилируется» до начала P1.

### 9.1 Юнит-тесты (без DSH)

`node --test test/fold.test.mjs` на «золотых» событиях, снятых из реальной сессии:
`assistant/message` с usage → строка `steps`; `tool/call`+`tool/result` → одна строка
`tool_calls`; `llm/retry`+`llm/retry-started` → две строки `retries`; повторная подача того же
батча → те же строки (идемпотентность).

### 9.2 Сверка чисел (обязательная приёмка)

```sql
-- сумма по шагам сессии
SELECT SUM(total_tokens) FROM steps WHERE session_id = :id;
-- независимая сумма из сырых сэмплов
SELECT SUM(total_tokens) FROM usage_samples WHERE session_id = :id;
-- число оплачиваемых попыток
SELECT COUNT(*) FROM usage_samples WHERE session_id = :id;
```

Три числа должны сходиться между собой и с `tokenUsage.totals` из
`session_projcache/sessions/<id>.json` (если сессия ещё в кэше).

### 9.3 Сценарии (ручные)

1. Новая сессия, один ход → запись появилась, KPI вырос, живая лента обновилась.
2. Сессия с ретраем (искусственный сбой/лимит) → `retries` заполнена, `steps.retries>0`.
3. `subagent` (spawn) → две сессии, у дочерней `parent_session_id`.
4. `fork` → `is_seeded=1`, история импортирована без дублей.
5. Компакция → `compactions` (если payload подтверждён) и уменьшение `context_pressure_tokens`.
6. Аварийное завершение DSH посреди шага → после рестарта `turn/end interrupted` учтён.
7. Backfill: удалить БД, перезапустить DSH → история восстановлена.
8. БД заблокирована/повреждена → плагин не ломает сессию, TokenMonitor показывает `LoadError`.
9. TokenMonitor открыт при пустой БД → «БД плагина не найдена», без исключений.
10. Одновременная работа: агент пишет, TokenMonitor обновляется каждые 5 с без `SQLITE_BUSY`.

### 9.4 Критерий «не навредил»

`git status` DSH-журналов не меняется от работы плагина (кроме естественного роста самих
журналов); ни одного `session/event` типа, добавленного плагином; в логах DSH нет ошибок
плагина после 1 часа работы.

---

## 10. Риски и ограничения

| Риск | Вероятность | Смягчение |
|---|---|---|
| API DSH изменится в следующем релизе (0.2.2 → 0.3) | средняя | `engines.dsh`, schema_version, `raw_json`, устойчивость к неизвестным событиям |
| `reasoningTokens` отсутствует у маршрута | факт | `Reasoning=0`, объём thinking — в `reasoning_chars`; в UI не выдавать ноль за «нет thinking» |
| Синхронный `node:sqlite` подтормаживает loop на больших бэкфиллах | средняя | порции по 500 событий + пауза, батчи, лимит `backfillLimit` |
| WAL + два процесса на Windows (антивирус/сетевой диск `D:`) | средняя | `busy_timeout`, `PRAGMA query_only` у читателя, ретраи |
| Приватность: промпты в БД | высокая | `captureText='preview'` по умолчанию, `off` — опция, явное предупреждение в DETAIL |
| Рост БД при `capture='raw'` | средняя | retention, `incremental_vacuum`, отдельный лимит raw |
| Плагин установлен, но профиль собран иначе (нет `sessionPersistence`) | низкая | `inject`/`ctx.inject` и мягкая деградация: live работает, backfill отключается |
| Эта сессия агента не может собрать проект (ACL sandbox, см. `FACTS.md` §10) | факт | исполнителю нужен `danger-full-access` или локальная сессия |

---

## 11. Открытые вопросы (решения нужны до P1)

1. **Поле для пути БД.** Переиспользовать `ProviderConfig.EventsPath` (быстро, ноль правок
   модели настроек) или добавить `ProviderConfig.DbPath` (чище, но трогает десериализацию
   старых `settings.json`)? *Рекомендация: добавить `DbPath`, а `EventsPath` оставить пустым —
   старые настройки не ломаются, т.к. поле nullable.*
2. **Глубина текстов.** `preview` (200 симв.) достаточно? Нужен ли `captureText='full'`
   для отладки собственных сессий?
3. **Retention.** Сколько дней хранить? *Рекомендация: 180 дней, raw — 30.*
4. **Бюджет.** Показывать токены (рекомендация) или завести таблицу цен моделей и считать
   псевдо-стоимость?
5. **UI.** Оставить один провайдер в существующем окне (рекомендация) или добавить вкладку
   «Детали» с графиками контекста (потребует нового окна/панели — это уже отдельный проект)?
6. **Запуск сессии.** Нужен ли пункт «Открыть папку сессии» в контекстном меню?

---

## 12. Чек-лист исполнителя

- [x] `FACTS.md` §9 закрыт: подтверждены payload'ы `assistant/message`/`tool/*`/`llm/retry` и
      права записи в `~/.dsh/tokens`; `compaction/*` и `AssistantStreamRecord` остались
      невыясненными — план от них не зависит (см. §13.7).
- [x] Плагин собирается как локальный бандл, ставится через `plugin_manager` и активируется;
      локаль/иконка/DETAIL объявлены в пакете (рендер в UI визуально не проверялся).
- [x] `schema.sql` применён (schema_version=1), `PRAGMA integrity_check` — ok.
- [x] Live: новая сессия → строки во всех нужных таблицах.
- [x] Backfill: история есть, дублей нет, повторный старт идемпотентен.
- [x] Сверка §9.2 сходится до токена.
- [x] TokenMonitor: `dotnet build` — 0 ошибок (6 предупреждений CS8618 — существовавшие в
      `MainWindow`), источник DeepSeek работает, пустая/битая БД обрабатываются.
- [x] Алерты и бюджет осмысленны для `context_window=1_048_576`.
- [x] Приватность: в БД нет полного текста при `captureText='preview'`.
- [x] Документация: `DeepSeekSetup/README.md` и `plugin/docs/DETAIL.md` соответствуют поведению.

---

## 13. Статус реализации (2026-11-17)

Реализованы P0–P3. Принятые решения по открытым вопросам §11 и отклонения от плана:

1. **Путь БД** — добавлено `ProviderConfig.DbPath` (вопрос §11.1), `EventsPath` не
   переиспользован.
2. **Тексты** — `captureText='preview'`, 200 символов; `full`/`off` доступны через config.
3. **Retention** — по умолчанию `0` (не удалять), реализовано удаление при `retentionDays > 0`.
4. **Бюджет** — токены, а не деньги (§7.6); тултип показывает точные числа и давление на окно.
5. **UI** — один провайдер в существующем окне, без новой вкладки.
6. **Запуск сессии** — `SupportsSessionLaunch = false`, доступно «Скопировать ID».
7. **`compaction/*`** — таблица создана, но не заполняется: в проверенных журналах этих
   событий нет, payload остался неподтверждённым; при `capture: raw` они сохраняются.
8. **`projected_tokens`/`surface_tokens`/разбивка контекста** — колонки есть, значения
   `NULL`: `ctx.tokenMeter` считает их на лету и не публикует события, которые можно
   свернуть в журнал без риска разойтись с проекциями DSH.
9. **Тайминги** — `duration_ms`/`ttft_ms` считаются из `step/start`, `step/end` и первой
   записи `assistant/message.stream`; `llm_ms`/`decode_ms` не заполняются (в событии их нет).
10. **`reasoning_tokens`** — 0 при отсутствии поля у провайдера (факт §2), объём thinking
    хранится в `steps.reasoning_chars`.
11. **`v_step_feed.started_ms`** — добавленная (совместимо) колонка: дереву нужен шаг,
    который ещё выполняется. Плагин пересоздаёт представления при каждом открытии БД,
    поэтому существующие базы получают новую колонку без смены `schema_version`.

Проверочные инструменты: `DeepSeekSetup/tools/verify-real.mjs` (сверка §9.2 на реальном
журнале), `tools/query-db.mjs` (диагностика БД), `tools/StoreCheck` (приёмочный прогон
C#-store). Результаты — в `DeepSeekSetup/README.md`, раздел «Как проверено».
