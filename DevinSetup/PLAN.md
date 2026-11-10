# Devin: запись детализации сессий и живой ленты

> Статус: **исследование + план, код не менялся**
> Дата: 10.11.2026
> Цель: получить для Devin то же, что OpenCode-провайдер получает из `opencode.db` +
> `token-events.jsonl` (плагин `OpenCodeSetup/token-stats.ts`): дерево «сессия → запрос → шаг»
> и живую ленту событий.
> См. также проведённый рефакторинг: `../REFACTORING_DATAPROVIDERS.md`.

---

## 1. Вывод (TL;DR)

**Да, возможно, причём лучше, чем ожидалось** — тремя независимыми путями:

| # | Путь | Что даёт | Покрывает | Затраты |
|---|------|----------|-----------|---------|
| 1 | **Чтение локальной БД Devin** `%APPDATA%\devin\cli\sessions.db` | сессии, промпты, шаги, tool-calls, live-лента — **прямо из SQLite, без плагина и хуков** | локальные Devin CLI / Devin Desktop | только код, ничего ставить не нужно |
| 2 | **REST API v3** (существующий `DevinClient.cs` + новые эндпоинты) | облачные сессии, сообщения, по-дневный ACU-расход | cloud (все origin: webapp, Slack, CLI, API) | только код |
| 3 | **Devin SDK sidecar** (`@cognition-ai/sdk@beta`) | живые события + **детализация расхода за ход** (`TurnStatsPayload`: in/out tokens, acuCost, ttft, tokens/сек) | cloud и локальные сессии, запущенные через SDK | отдельный процесс Node ≥22, SDK в бете |

Хуки (`.devin/hooks.v1.json`) — рабочий, но **худший** вариант: payload хуков не содержит
расхода (ни токенов, ни ACU), в облаке работает best-effort и требует HTTP-эндпоинта,
доступного извне. Годится только как вспомогательный канал.

**Главный сюрприз:** Devin Desktop/CLI хранит всё в локальной SQLite — по структуре это
практически «opencode.db для Devin». В проекте уже используется `Microsoft.Data.Sqlite`
(`OpenCodeStore`), поэтому путь №1 ложится на готовый шаблон почти один-в-один.

---

## 2. Источник №1: локальная БД (`sessions.db`) — проверено на этой машине

### 2.1. Расположение

```
%APPDATA%\devin\cli\sessions.db        (~39 МБ, есть WAL: sessions.db-shm / sessions.db-wal)
```

= `C:\Users\Raul\AppData\Roaming\devin\cli\sessions.db`.
Открывалась в read-only режиме по URI — чтение работает при запущенном Desktop.

### 2.2. Схема (реальная, версия миграций 20)

```sql
CREATE TABLE sessions (
  id TEXT PRIMARY KEY,               -- короткий slug, напр. 'gray-astrodon'
  working_directory TEXT NOT NULL,   -- F:\Work\GitMy\TokenMonitor
  backend_type TEXT NOT NULL,        -- 'windsurf'
  model TEXT NOT NULL,               -- 'swe-1-6-slow'
  agent_mode TEXT NOT NULL,          -- 'accept-edits'
  created_at INTEGER NOT NULL,       -- unix-секунды
  last_activity_at INTEGER NOT NULL,
  title TEXT,
  cogs_json TEXT,                    -- состояние сессии (системные промпты, ~26 КБ) — игнорировать
  workspace_dirs TEXT,
  metadata TEXT);                    -- {"total_credit_cost":0,"total_acu_cost":0.0,"client_meta":{...}}

CREATE TABLE message_nodes (        -- 2762 строки: лес сообщений
  row_id INTEGER PRIMARY KEY AUTOINCREMENT,  -- порядок = хронология
  session_id TEXT NOT NULL,
  node_id INTEGER NOT NULL,          -- узел в лесу сессии
  parent_node_id INTEGER,            -- NULL у корней
  chat_message TEXT NOT NULL,        -- JSON (см. 2.3)
  created_at INTEGER NOT NULL,
  metadata TEXT,                     -- {"num_tokens_preceding":74334,"summarized_from":null,...}
  UNIQUE(session_id, node_id));

CREATE TABLE tool_call_state (      -- 356 строк: сериализованные acp::ToolCall
  session_id TEXT, tool_call_id TEXT,
  tool_call_json TEXT,               -- исходное событие ToolCall
  tool_call_update_json TEXT,        -- финальный ToolCallUpdate
  source_node_ids TEXT);

CREATE TABLE prompt_history (       -- промпты пользователя по сессиям
  id, content TEXT, timestamp INTEGER, session_id TEXT, is_shell INTEGER);
```

Также есть `live_row_times` (мгновенный старт «живых» строк), `rendered_commits`,
`subagent_heads`, `message_search` (FTS5) — пока не нужны.

### 2.3. `chat_message` JSON

Ключи: `message_id`, `role`, `content`, `tool_calls[]`, `tool_call_id`, `thinking`, `metadata`.
Роли (по факту в БД): `user` (145), `assistant` (1185), `tool` (1092), `system` (340).

```json
{"message_id":"fb60f07d-…","role":"assistant","content":"",
 "tool_calls":[{"id":"call_103195da…","name":"exec","arguments":{"command":"cd … && dotnet build"},"index":0,"kind":"function"}]}

{"message_id":"70a8796c-…","role":"tool","content":"Successfully edited file '…' - made 1 replacement(s).",
 "tool_call_id":"call_103195da…","metadata":{…}}
```

Имена `tool_calls[].name` нормализованы (`exec`, `read`, `edit`, …) — как `ToolCallDetail` в SDK.
В `tool_call_state.tool_call_json` лежит полный ACP-объект: `toolCallId`, `title`, `kind`
(`search|read|edit`), `locations[].path`, `rawInput`, `_meta["cognition.ai/inferenceToolName"]`.

### 2.4. Что есть / чего нет в БД

Есть (всё для дерева и ленты): сессии с каталогом/моделью/временами; один шаг = сообщение
`assistant` с `tool_calls` (или с `content`); результат шага = следующее сообщение `tool`
по `tool_call_id`; размер контекста `num_tokens_preceding`; итоги `metadata.total_credit_cost`
и `total_acu_cost` на сессию.

**Чего нет: per-message расхода.** `metadata.num_tokens`, `metrics`, `finish_reason` во всех
проверенных узлах — `null`. Локальный CLI расход за моделирование не сохраняет
(команда `/session-stats` берёт его с сервера). Повторить сценарий OpenCode-плагина
с токенами за запрос локально **нельзя** — только ACU на сессию из `sessions.metadata`
(на этой машине значения `0` — расход локальных сессий учитывается на сервере).

### 2.5. Гочевики чтения

- WAL: открывать как `Data Source=…;Mode=ReadOnly`; при ошибке «readonly database»
  (checkpoint требует записи) — копировать трио `.db + -wal + -shm` во временный файл
  и читать копию (аналог `ReadShared` в `OpenCodeStore` для JSONL).
- `row_id` — монотонный серийник: дёшево опрашивать «новое с row_id > X» (live-лента).
- Локальные id сессий — slug'и (`gray-astrodon`), облачные — `devin-…»; соединять источники
  по id безопасно, коллизии исключены.
- Формат не документирован: миграции добавляются (20 версий за месяц). Читать
  защитно: неизвестные столбцы игнорировать, сбой парсинга узла не должен ронять
  загрузку. Риски — §9.

---

## 3. Источник №2: REST API v3 (cloud)

База: `https://api.devin.ai/v3/organizations/{org_id}`, `Authorization: Bearer cog_…`.
OpenAPI (`https://docs.devin.ai/v3-openapi.yaml`, 170 эндпоинтов) подтверждает:

| Эндпоинт | Что даёт для нас |
|---|---|
| `GET /sessions?updated_after=…` | страницы сессий: `acus_consumed`, `status`, `status_detail`, `category`, `subcategory`, `title`, `created_at`, `updated_at`, `pull_requests`, `on_demand_usage`, `parent_session_id`, `child_session_ids`, `origin`, `devin_mode` |
| `GET /sessions/{devin_id}` | деталь сессии |
| `GET /sessions/{devin_id}/messages` | транскрипт по порядку: `SessionMessage {event_id, source: devin\|user, message, created_at, origin, user_id, username}`, курсорная пагинация `after`/`first` (макс. 200) |
| `GET /sessions/insights` | то же + `num_user_messages`, `num_devin_messages`, `session_size`, AI-анализ |
| `GET /consumption/daily/sessions/{session_id}` | **по-дневной ACU-расход конкретной сессии** — `consumption_by_date[].{date, acus, acus_by_product}`, `total_acus` (граница дня — полночь PST = 08:00 UTC) |
| `GET /metrics/usage` и др. | агрегаты org-уровня (уже используются `DevinClient`) |

Webhook'ов жизненного цикла сессий в v3 нет (только вебхуки автоматизаций и Git-провайдеров) —
облако даёт данные только поллингом или WebSocket (§4).

Ограничение: REST отдаёт **ACU, а не токены** (и не кэш). Per-request разбивки расхода
в REST нет; только сессионный `acus_consumed` и по-дневное распределение.

---

## 4. Источник №3: Devin SDK beta — живые события и TurnStats

`npm install @cognition-ai/sdk@beta` (сейчас `0.0.1-beta.7`, MIT, только beta dist-tag;
Node ≥22; браузеры не поддерживают авторизацию WebSocket). Проверено по `.d.ts` пакета:

```ts
process.env.DEVIN_API_KEY = "cog_…" // или apiKey в опциях
const cloud = await CloudDevin.connect({ orgId: "org_…" });

// seed-список сессий: статус, заголовок, url, PR, updatedAt уже в _meta
await cloud.listSessions({ updatedAfter: "…" });

// подписка на уже идущую сессию (запущенную кем угодно)
const session = await cloud.attach("devin-…");               // replay: "none" по умолчанию
for await (const ev of session.events()) { /* SessionEvent */ }
session.onExtension = (n) => { /* _cognition.ai/* уведомления */ };
```

### 4.1. `SessionEvent` (`dist/acp/events.d.ts`)

`message_delta | thought_delta | user_message | tool_call | plan | mode_change |
commands_change | status | activity | lifecycle | typing | pull_request | turn_state | raw`

Ключевое для ленты: `tool_call` (merged ToolCall, `settled`), `user_message`,
`status` (`working/blocked/finished` + `finishedOutcome`, `userActionRequired`),
`turn_state` (`running/idle` — только соединению, пославшему промпт).

### 4.2. `TurnStatsPayload` (подтверждено в `dist/generated/acp/types.gen.d.ts`)

Приходит в `_cognition.ai/agent_stopped` (поле `stats`, при успешном завершении) и
`_cognition.ai/turn_stats` (один раз за завершённый ход + по разу на каждый исторический
ход при `session/load`):

```ts
{ toolCalls, filesChanged, commandsRun,
  inputTokens, outputTokens, ttftMs, tokensPerSec, totalTimeMs,
  requestId, modelLabel, creditCost, acuCost,
  responseDimensions: [{ uid, groupTitle, label, kind }] }
```

`_cognition.ai/turn_stats` (`TurnStatsNotification`): `{sessionId,
turnClientMessageId, responseDimensions[], turnRequestId}` — ход идентифицируется
по `PromptRequest.messageId`, метрики уже просуммированы за ход.

Для промптов, отправленных самим клиентом, у `PromptResult.usage` есть полный разбор:
`inputTokens, outputTokens, cachedReadTokens, cachedWriteTokens, totalTokens`.
Кэш-токенов в `turn_stats` нет.

### 4.3. Ограничения живого канала

- `attach()` повторяет только последний статус: пропущенные офлайн-переходы не восстанавливаются → историю сидировать из REST (§3).
- Соединение должно жить, пока collector запущен; закрыли процесс — события за этот период потеряны (`turn_stats` задним числом через REST не отдаётся).
- SDK в бете (`0.0.1-beta.N`), API может меняться; нужно пинить точную версию.
- Наблюдатель не платит ACU, но каждый `attach` держит WS-сессию.

---

## 5. Хуки (`.devin/hooks.v1.json`) — вспомогательный вариант, не основной

Формат подтверждён (docs `cli/extensibility/hooks`): события `PreToolUse, PostToolUse,
PermissionRequest, UserPromptSubmit, Stop, PostCompaction, SessionStart, SessionEnd`;
в каждой полезной нагрузке stdin есть `session_id` и per-turn `prompt_id`; конфиги:
`.devin/hooks.v1.json` (весь файл = объект хуков), `.devin/config.json`,
`%APPDATA%\devin\config.json` (user-уровень), плюс форматы `.claude/settings.json`.

- Локально CLI/Desktop: работает полностью — можно писать `devin-events.jsonl` как OpenCode-плагин. Но при наличии §2 **не нужно**.
- Облачные сессии: hooks плагинов работают best-effort (fail-open), `SessionEnd` не срабатывает, скрипт выполняется **на VM** облака → для доставки в TokenMonitor нужен доступный извне эндпоинт (туннель/relay). Для настольного мониторинга практически неприемлемо.
- Полезная нагрузка **не содержит расхода**: у `Stop` только `stop_hook_active`.

Вывод: хуки = запасной канал на случай, если §2 перестанет работать (см. §9).

---

## 6. Целевая архитектура для TokenMonitor

### 6.1. Слои данных

```
DevinDataProvider (уже есть)
   ├── DevinLocalStore  — read-only SQLite %APPDATA%\devin\cli\sessions.db   [фаза 1]
   ├── DevinStore       — REST: sessions/messages/consumption (облако)       [фаза 2]
   └── DevinEventsFile  — опционально: JSONL от SDK-сборщика / хуков         [фаза 3]
```

Слияние источников: владелец id всегда один — локальный slug или `devin-…», поэтому
объединение — просто конкатенация списков с сортировкой по `Updated`; дублей нет по конструкции.

### 6.2. Соответствие моделям (`Models.cs`, сравнение с `OpenCodeStore.TryParseJson`)

`SessionData`:

| Поле | локальная БД | REST | SDK |
|---|---|---|---|
| Id | `sessions.id` | `session_id` | `sessionId` |
| Title | `sessions.title` | `title` | `title` |
| Directory | `working_directory` | (нет; `"cloud: {origin}"`) | `cwd?` |
| Model | `sessions.model` | `devin_mode` | `modelLabel` из TurnStats |
| Created / Updated | `created_at` (unix-сек) / `last_activity_at` | `created_at` / `updated_at` | `updatedAt` |
| Tokens | ACU → `TokenCounts.Input = (long)total_acu_cost` | `acus_consumed` | сумма за ходы |

`RequestData` = «ход» (user turn): начинается от узла/сообщения `role=user`;
`Prompt` = `content`; `Time` = `created_at`; в REST — каждое user-сообщение из `messages`.

`StepData` = «отклик модели»: узел `assistant` (или REST `source=devin`), `Completed` =
`created_at` следующего узла или конец хода; в SDK — `agent_stopped.stats`.

`LiveEvent` (та же схема JSON, что `token-events.jsonl`, чтобы при желании
переиспользовать логику `TryParseJson`):

```json
{"time":"2026-11-10T10:05:51Z","sessionId":"gray-astrodon","messageId":"fb60f07d-…",
 "model":"swe-1-6-slow","provider":"Devin","steps":3,
 "input":0,"output":0,"reasoning":0,"cacheRead":0,"cacheWrite":0,
 "billed":0,"context":74334,"durationMs":34000,"acuCost":0.0}
```

Пока расхода нет: `input/output/… = 0`, `context` = `num_tokens_preceding` последнего
узла, `billed` = `total_acu_cost`; фаза 3 заполняет токены из `TurnStatsPayload`.

Дисплей: колонки токенов для Devin локально будут пустыми — корректно показывать
«событий N · ACU X» (шаблон уже есть: `DevinStore.ToDataSnapshot` кладёт ACU в `Input`).

### 6.3. Сигнатура и live-обновление

- Локальный store: сигнатура = (максимум `message_nodes.row_id`, количество сессий). Дерево перестраивается только при новых узлах.
- Live-лента локально: `row_id > последнего известного` каждые `BudgetRefreshSeconds` (таймер уже есть в `MainWindow`). WAL-чтение дешёвое.
- REST: инкремент `updated_after` + `messages?after=<event_id>`.
- SDK: события приходят сами; collector дописывает `devin-events.jsonl`, TokenMonitor перечитывает файл (как OpenCode `token-events.jsonl`, FileShare.ReadWrite).

---

## 7. План реализации (фазы)

### Фаза 1. Локальная БД (первый шаг, максимальный эффект)

Новый файл `TokenMonitor/DevinLocalStore.cs` + точечные правки `DevinStore` / `DevinDataProvider`.

1. `DevinLocalStore` (каркас по `OpenCodeStore`):
   - `DbPath` по умолчанию `%APPDATA%\devin\cli\sessions.db`; переопределяется настройкой `Devin.LocalDbPath`;
   - read-only соединение, fallback-копия при блокировке/readonly-WAL;
   - `Load()` собирает `DataSnapshot` (§6.2) и Signature по максимуму `row_id`;
   - «ход» = узел user; в `Requests` — `Steps` из последующих assistant/tool узлов до следующего user (по `row_id`, при ветвлениях — с учётом `parent_node_id`);
   - `tool_calls[].name` → `StepData.Model = name`; таблицу `tool_call_state` на первой итерации можно не подключать — JSON узлов достаточно.
2. `DevinDataProvider.LoadAsync` объединяет локальные сессии и (только для `devin-…» id) текущий REST-путь. В `PathText` показываем `%APPDATA%\devin\cli\sessions.db`.
3. Подсказки:
   - БД отсутствует → «Devin CLI / Desktop не установлен»;
   - БД есть, сессий 0 → «Начните работать с Devin CLI/Desktop»;
   - новая сессия появляется по таймеру без перезапуска.

Критерии готовности фазы 1: при запущенном Devin Desktop в дереве TokenMonitor на вкладке
Devin видны реальные сессии этой машины с шагами; live-лента пополняется по таймеру;
`dotnet build` проходит.

### Фаза 2. REST-облачные сессии

В `DevinClient.cs` добавляются вызовы:

- `GET /sessions?updated_after=<ms>` (пагинация `first`/`after`);
- `GET /sessions/{id}/messages` (курсор `after`);
- `GET /consumption/daily/sessions/{id}?time_after&time_before`.

1. Только сессии, отсутствующие в локальной базе (id `devin-…»), за окно `CloudHistoryDays`.
2. Облачные сессии разворачиваются в дерево по `messages`: user-сообщение = ход,
   цепочка devin-сообщений между двумя user = шаги.
3. В `LiveEvent` для облака — переходы статуса (`status`, `finishedOutcome`) как события ленты.

Критерии: облако не мешает локальным сессиям; при недоступной сети статус не падает
(образец — `DevinUsageMetrics.Error`).

### Фаза 3 (необязательно). SDK-сборщик для живых токенов

`TokenMonitor/DevinSetup/collector/` (Node 22+):

1. `package.json` + lock по точной версии `@cognition-ai/sdk@0.0.1-beta.N`;
2. `collector.ts`: `CloudDevin.connect` → `listSessions({updatedAfter})`; для активных
   сессий `attach()` → `session.events()` + `session.onExtension`:
   - `agent_stopped` с `stats` → живое событие с токенами/ACU/toolCalls/totalTimeMs/modelLabel;
   - `turn_stats` → корректировка по `turnClientMessageId` (начало хода);
   - `status` (`working/blocked/finished`) → живое событие состояния.
3. Запись в `%APPDATA%\TokenMonitor\devin-events.jsonl` (альтернатива — HTTP-POST на
   локальный listener; файл проще и устойчивее к перезапускам).
4. Запуск: вручную или `Process.Start` из TokenMonitor при `Devin.EventsPath` непустом.
5. `DevinDataProvider` читает файл (как `CursorStore` `EventsPath`) и дополняет
   `LiveEvent` токенами по ссылке sessionId/turnClientMessageId.

Ограничения: бета-SDK, Node 22+, соединение живёт всё окно.

### Фаза 4 (необязательно). Хуки как fallback

`DevinSetup/hooks/`:

- `hooks.v1.json` — образец на `UserPromptSubmit` / `PostToolUse` / `Stop` (matcher `""`);
- `log-hook.ps1` / `log-hook.js` — append в `devin-events.jsonl` (по образцу `token-stats.ts`);
- данные хуков: prompt / tool_name / tool_input / tool_response → «шаги без расхода».

Включать только если фазы 1–3 не работают.

---

## 8. Конфигурация (новые ключи секции `Devin` в `settings.json`)

```jsonc
{
  "Devin": {
    "ApiKey": "apk_user_…",        // уже есть
    "OrgId": "org-…",              // уже есть
    "IsEnabled": true,             // уже есть
    "LocalDbPath": "",             // новое; пусто = %APPDATA%\devin\cli\sessions.db
    "CloudHistoryDays": 30,        // новое; глубина REST-выгрузки облака
    "EventsPath": ""               // новое; JSONL фазы 3, пусто = отключено
  }
}
```

(`ProviderConfig` уже содержит нужные поля; добавление — несущее совместимость изменение `AppSettings.cs`.)

---

## 9. Риски

| Риск | Оценка | Фазы | Митигация |
|---|---|---|---|
| Схема `sessions.db` изменится (20 миграций за месяц) | средняя | 1 | защитный разбор: только известные столбцы; флаг в конфиге; `LoadError` — сниженная загрузка |
| `sessions.db` заблокирована / WAL требует записи | подтверждено на практике | 1 | fallback-копия файлов на refresh (подход `ReadShared`) |
| Токенов per-message нет нигде локально | подтверждено | все | показываем ACU + контекст; фаза 3 добирает токены в облаке |
| SDK бета, API нестабилен | средняя | 3 | пин точной версии, изоляция в collector, не в main app |
| REST-лимиты (429) при опросе | низкая | 2 | окно = `BudgetRefreshSeconds`, инкремент `updated_after`, только delta |
| Облачные события не все доходят `attach` (офлайн-ходы не восстанавливаются) | подтверждено SDK | 3 | сидирование из REST `messages` + `consumption` |
| Чувствительные промпты в UI | низкая | все | TokenMonitor уже показывает промпты OpenCode — одинаковое поведение |

## 10. Открытые вопросы

1. `cognition.ai/acuUsed` из `usage_update` (ACP v2) — приходит ли в локальные сессии
   и можно ли привязать к конкретному ходу (эксперимент с SDK).
2. Появятся ли токены в `message_nodes.metadata.num_tokens` в будущих версиях CLI —
   следить за миграциями 21+.
3. Нужна ли одна лента для облака + локально (origin уже различает `webapp|cli|desktop|slack|api`).

## 11. Ссылки

- OpenAPI v3: <https://docs.devin.ai/v3-openapi.yaml>
- SDK reference (TurnStats, events, attach): <https://docs.devin.ai/sdk/reference/>
- Hooks: <https://docs.devin.ai/cli/extensibility/hooks/>
- Plugins: <https://docs.devin.ai/product-guides/plugins/>
- Шаблоны кода в репозитории: `TokenMonitor/OpenCodeStore.cs`, `TokenMonitor/OpenCodeSetup/token-stats.ts`
