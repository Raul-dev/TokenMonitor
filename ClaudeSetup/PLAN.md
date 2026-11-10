# Claude Code: запись детализации сессий и живой ленты

> Статус: **исследование + план, код не менялся**
> Дата: 10.11.2026
> Цель: получить для Claude то же, что OpenCode-провайдер получает из `opencode.db` +
> `token-events.jsonl` (плагин `OpenCodeSetup/token-stats.ts`): дерево «сессия → запрос → шаг»
> и живую ленту событий. Метод тот же, что в исследовании Devin (`../DevinSetup/PLAN.md`):
> официальная документация Claude Code 2.1.x + проверка этой машины.
>
> На машине установлен Claude Code **v2.1.316** (npm), но сессий пока нет — структуры
> `~/.claude` сверены с документацией (есть `settings.json`, `telemetry/`, `sessions/`).

---

## 1. Вывод (TL;DR)

**Да, возможно — и это самый «богатый» источник данных для TokenMonitor.**
В отличие от Devin, у Claude Code есть и локальная история, и токены за каждое сообщение,
и несколько готовых «плагин»-механизмов:

| # | Путь | Что даёт | Покрытие | Затраты |
|---|------|----------|----------|---------|
| 1 | **Транскрипты** `~/.claude/projects/<проект>/<session>.jsonl` | полная история: сессии, запросы, шаги, tool-вызовы, системный промпт, `usage` API за каждое сообщение | CLI, Desktop (Code-вкладка), IDE, `claude -p`, Agent SDK | только код |
| 2 | **Statusline-скрипт** — настоящий аналог `token-stats.ts` | после **каждого** ответа на stdin приходит JSON: `cost.total_cost_usd`, токены по компонентам (включая кэш), кэш-статистика, лимиты плана (5h/7d, `resets_at`, `spend_limit` USD) | все интерактивные сессии | маленький скрипт + одна строка в `settings.json` |
| 3 | **Hooks**, в т.ч. `type:"http"` → POST на localhost | точные границы: `session_id`, `prompt_id`, `transcript_path`, `last_assistant_message`, `StopFailure` с причиной | всё локальное | конфиг хуков + локальный приёмник |
| 4 | **OTEL / Admin API / SDK** | орган-уровень: метрики `claude_code.token.usage`, `claude_code.cost.usage`, per-user/per-модель; `claude -p --output-format json` с `total_cost_usd` | орг-аналитика, headless | collector / админ-ключи |

Отдельно существуют **mods** (с v2.1.287) — JS/TS-плагины, функции которых Claude Code
вызывает внутри своего процесса, с доступом к `$.session.usage()`. Это самый близкий аналог
OpenCode Plugin API, но для нашей задачи избыточен (§5.4).

**Ключевое отличие от Devin:** ответ API Claude содержит полную разбивку токенов
(`input_tokens`, `output_tokens`, `cache_creation_input_tokens`, `cache_read_input_tokens`),
поэтому TokenMonitor впервые сможет заполнить `TokenCounts` целиком, включая кэш,
и показать стоимость в USD.

---

## 2. Источник №1: локальные транскрипты `~/.claude/projects`

### 2.1. Расположение (подтверждено документацией `claude-directory`)

```
%USERPROFILE%\.claude\projects\<проект>\<session-id>.jsonl        — транскрипт сессии (JSONL)
…\<session>\subagents\<agent>.jsonl                              — сабагенты (тот же формат)
…\<session>\tool-results\                                        — большие выводы инструментов
%USERPROFILE%\.claude\history.jsonl                              — промпты (время + проект)
%USERPROFILE%\.claude\stats-cache.json                           — агрегаты токенов/стоимости для /usage
%USERPROFILE%\.claude\usage-data\report.html                     — отчёты /insights (до 200 сессий)
```

- `<проект>` — рабочий каталог, где все символы кроме букв и цифр заменены на `-`
  (например `-F--Work-GitMy-TokenMonitor`).
- `session-id` — UUID; при ветвлении/возобновлении появляются файлы
  `<session>.orphaned-<ts>-<суффикс>.jsonl` и `<session>.jsonl.superseded-<ts>`.
- `CLAUDE_CONFIG_DIR` переносит весь `~/.claude`.
- **Retention: `cleanupPeriodDays` = 30 дней по умолчанию** — старые транскрипты удаляются
  в начале следующего запуска (`claude purge <проект>` удаляет вручную, `--all` — всё).

### 2.2. Содержимое транскрипта

Официальная формулировка: «Conversation transcript: messages, tool calls, and tool results,
plus the system prompt, tool definitions, and parameters of the requests Claude Code sent
the model». По данным сообщества строка выглядит так (схема **не документирована**):

```json
{"type":"user|assistant|system|summary",
 "message":{… объект Messages API, включая "usage"},
 "timestamp":"2026-11-02T21:26:01.000Z","sessionId":"4b34d7b4-…",
 "uuid":"…","parentUuid":"…","cwd":"F:\\Work\\…","version":"2.1.316","requestId":"req_…"}
```

Правила чтения (защитный разбор, как для Devin `sessions.db`):
- парсер по `type`: `user` = начало запроса, `assistant` = шаг;
- `tool_use` / `tool_result` внутри `message.content` = детали шага;
- неизвестные поля и `type` игнорировать; сбой одной строки не должен ронять загрузку;
- перед поддержкой новой версии Claude Code — контрольный прогон на своей актуальной сессии.

### 2.3. Что есть / чего нет

Есть: все уровни (сессия → запрос → шаг → tool-вывод), модель, версия, вложенность через
`parentUuid`, timestamps, сабагенты отдельным каталогом, `usage` на каждое сообщение,
стоимость (ее считает сам Claude Code, см. §3).

Нет: готовой «живой ленты» — она собирается через statusline (§3) или поллинг новых
строк транскриптов.

---

## 3. Источник №2: statusline — «плагин» в духе token-stats.ts

Проверено по `code.claude.com/docs/en/statusline`. Statusline-скрипт запускается после
каждого ассистентского сообщения (не только при промптах) и получает на stdin полный JSON:

```json
{
  "session_id": "abc123…", "prompt_id": "550e8400-…", "transcript_path": "…/<sid>.jsonl",
  "model": {"id":"claude-opus-5-5","display_name":"Opus"},
  "workspace": {"current_dir":"…","project_dir":"…","repo":{"host":"github.com", …}},
  "cost": {"total_cost_usd":0.01234,"total_duration_ms":45000,"total_api_duration_ms":2300,
           "total_lines_added":156,"total_lines_removed":23},
  "context_window": {"total_input_tokens":15500,"total_output_tokens":1200,
           "current_usage":{"input_tokens":8500,"output_tokens":1200,
                            "cache_creation_input_tokens":5000,"cache_read_input_tokens":2000}},
  "prompt_cache": {"warm":true,"ttl":"1h","hit_ratio":0.91,"cache_write_tokens":352000, …},
  "rate_limits": {"five_hour":{"used_percentage":23.5,"resets_at":1738425600},
                  "seven_day":{"used_percentage":41.2,"resets_at":1738857600},
                  "spend_limit":{"used_usd":314.12,"limit_usd":500,"period":"monthly"}},
  "version":"2.1.90","session_name":"my-session"
}
```

Это в точности данные `token-stats.ts` — и даже больше: sessionId, модель, токены (включая
кэш по компонентам), длительность, стоимость, плюс лимиты подписки claude.ai (5ч/7д окна
с моментом сброса и spend-лимитом в долларах — прямая замена метрам Cursor).

Схема «плагина»:

1. скрипт читает JSON из stdin;
2. **дозаписывает** строку в `%USERPROFILE%\.claude\claude-events.jsonl`
   (PowerShell `Add-Content`, bash `printf >> file`);
3. печатает в stdout минимум (или ничего).

Ставка в `~/.claude/settings.json`:

```json
{
  "statusLine": {
    "type": "command",
    "command": "powershell -NoProfile -File ~/.claude/claude-events.ps1"
  }
}
```

Моменты обновления: новый ответ ассистента, завершение `/compact`, смена permission-mode,
смена модели, `refreshInterval`-таймер. Debounce 300 мс. Скрипт не расходует API-токены.

**Это рекомендуемый живой канал:** нулевые зависимости, все данные уже в JSON,
нет лишних процессов. Аналог SDK-sidecar Девина не нужен.

---

## 4. Источник №3: hooks (в т.ч. HTTP на localhost)

Проверено по `code.claude.com/docs/en/hooks`. События: `SessionStart`, `SessionEnd`,
`UserPromptSubmit`, `Stop`, `StopFailure`, `PreToolUse` / `PostToolUse` / `PostToolUseFailure`,
`PermissionRequest` / `PermissionDenied`, `SubagentStart` / `SubagentStop`,
`PreCompact` / `PostCompact`, `Notification`, `MessageDisplay`, `CwdChanged`, `FileChanged`,
`PreModelSwitch` / `PostModelSwitch` и другие.

Общие поля stdin или POST-тела: `session_id`, `prompt_id` (совпадает с атрибутом `prompt.id`
OTEL-событий), **`transcript_path`**, `cwd`, `permission_mode`, `hook_event_name`.
Событийно-специфичные: `tool_name` / `tool_input` / `tool_response` / `tool_use_id`;
на `Stop` и `SubagentStop` — `last_assistant_message` (не надо читать файл);
на `SubagentStop` — `agent_transcript_path` (сабагент-файл в `subagents/`);
на `SessionEnd` — источник завершения (`clear`, `resume`, `logout`, `prompt_input_exit`, `other`);
на `StopFailure` — тип ошибки (`rate_limit`, `overloaded`, `authentication_failed`,
`account_on_hold`, `billing_error`, `max_output_tokens`, …).

Типы обработчиков: `command`, **`http`** (POST JSON на URL), `mcp_tool`, `prompt`, `agent`.
Для локального сбора идеально `"type": "http"` на `http://127.0.0.1:<порт>` приёмника,
который поднимет TokenMonitor (или отдельный слушатель). Для безопасности есть allowlist
`allowedHttpHookUrls` в настройках — itch только локальные адреса подписываем.

Конфиги: `~/.claude/settings.json`, `.claude/settings.json`,
`.claude/settings.local.json`, managed settings, plugin `hooks/hooks.json`.

Когда hooks дают больше statusline: точные границы хода (`prompt_id`), события завершения
сессии и `StopFailure` (ошибки API), полный tool_trace, если понадобится.

---

## 5. Прочие пути (для сравнения)

### 5.1. Agent SDK / headless

`claude -p "…" --output-format json` → `result`, `session_id`, **`total_cost_usd`**
и разбор стоимости по моделям; `stream-json` выдает события по мере генерации
(требует `--verbose`). Пригодно, когда TokenMonitor сам запускает фоновой задачи
(по аналогии с Cursor), но не для постоянного мониторинга интерактивных сессий.

### 5.2. OTEL (организационный уровень)

`CLAUDE_CODE_ENABLE_TELEMETRY=1` + OTLP-экспорт (по умолчанию на `localhost:4317`).
Метрики: `claude_code.token.usage`, `claude_code.cost.usage` (USD), `claude_code.session.count`,
`claude_code.lines_of_code.count`; события: `claude_code.user_prompt`, `api_request`,
`tool_decision`, `tool_result`, `subagent_completed` — с атрибутами `session.id`,
`user.account_uuid`, `model`, `agent.name`. Работает на всех поверхностях.
Для корпоративного мониторинга это «родной» канал; для одного пользователя дублирует
statusline, но требует collector (OpenTelemetry Collector — отдельный процесс).

### 5.3. Admin APIs

- **Claude Code Analytics API** (Console, API-организации): ежедневные per-user метрики
  по моделям; нужен Admin API-ключ (`platform.claude.com/settings/admin-keys`).
- **Enterprise Analytics API**: per-user расход по всем поверхностям Claude, ключ со scope
  `read:analytics`.

Годится как внешний бюджетный источник, если пользователь — админ организации.

### 5.4. Mods (JS-плагин внутри процесса, с v2.1.287)

Plugin может содержать **mod**: JS/TS-модуль, чьи функции Claude Code вызывает в собственном
процессе. События: `turn.start`, `turn.step` (по каждому request к модели, async generator),
`turn.complete`, `session.start` / `end` / `compact` / `append` / `measure`, `tool.call`,
`prompt.submit`, `telemetry.log`. Доступно: `$.session.usage()` →
`{startedAt, context:{tokens, window, percent}, rateLimits, cost}`, `$.fs.read/write`
(**не append** — перезапись, лимит 4 МиБ на файл), `$.store` (KV 4 МиБ, общий для сессий),
`$.process.run`, `$.http.fetch`, панельки/тосты.

Для TokenMonitor избыточно (statusline даёт то же проще), но упомянуто как опция: если
когда-нибудь понадобится UI внутри Claude Code,mod зрелый путь.

---

## 6. Целевая архитектура для TokenMonitor

```
ClaudeDataProvider (новый, по образцу остальных провайдеров)
   ├── ClaudeStore      — read-only разбор ~/.claude/projects/**/*.jsonl + history.jsonl
   │                      + stats-cache.json (агрегаты /usage)                    [фаза 1]
   └── ClaudeEventsFile — живая лента из statusline-скрипта (claude-events.jsonl) [фаза 2]
```

Каждая поддиректория `projects/<проект>` — «рабочая директория» группы сессий;
сессии внутри не пересекаются по `session_id`.

### 6.1. Соответствие моделям (`Models.cs`, `LiveEvent` ↔ `OpenCodeStore.TryParseJson`)

| Модель | Транскрипт JSONL | Statusline JSON |
|---|---|---|
| `SessionData.Id` | `sessionId` (UUID) | `session_id` |
| `SessionData.Title` | первый user-промпт или `session_name` | `session_name` |
| `SessionData.Directory` | `cwd` строк транскрипта | `workspace.project_dir` |
| `SessionData.Model` | модель сообщений | `model.display_name` |
| `SessionData.Created/Updated` | timestamp строк | — (старт от `total_duration_ms`) |
| `SessionData.Tokens` | сумма `message.usage` всех шагов | `context_window.current_usage` (текущий контекст) |
| `RequestData` | строка `type:"user"` (не tool_result); `Prompt` = текст | `prompt_id` |
| `StepData` | строка `type:"assistant"`; `Tokens` = `message.usage` | — |

Заполнение `TokenCounts` (первый «полный» провайдер):

- `input_tokens` → `Input` (без кэша);
- `output_tokens` → `Output` (thinking входит сюда; `Reasoning = 0` — отдельного поля в API нет);
- `cache_read_input_tokens` → `CacheRead`;
- `cache_creation_input_tokens` → `CacheWrite`.

`LiveEvent` (та же схема JSON, что `token-events.jsonl`):

```json
{"time":"2026-11-10T10:05:51Z","sessionId":"4b34d7b4-…","messageId":"uuid-…",
 "model":"claude-opus-5-5","provider":"Claude","steps":12,
 "input":8500,"output":1200,"reasoning":0,"cacheRead":2000,"cacheWrite":5000,
 "billed":16700,"context":15500,"durationMs":45000,"costUsd":0.01234}
```

`LiveEvent.Billed` — `long`, а стоимость USD — новое поле `CostUsd (double?)`.
Предлагается: (а) добавить `CostUsd` в `LiveEvent` (старые источники пишут `null`),
и (б) USD-сумму выводить в бюджет.

Бюджет:
- подписка claude.ai → `rate_limits.five_hour/seven_day` (`used_percentage`, `resets_at`
  как epoch-сек) + `spend_limit` (`used_usd`, `limit_usd`, `period`) — почти 1-в-1
  модель метров Cursor (`BudgetMeter`, `ResetAt` в `BudgetService`);
- API-ключ → сумма `costUsd` за месяц по локальным данным; при наличии админ-ключа —
  Analytics API (фаза 4).

### 6.2. Сигнатура и live-обновление

- Signature = (максимальный `timestamp` + число строк) по всем `projects/**/*.jsonl`;
  дерево перестраивается только при изменении.
- Live-лента: `claude-events.jsonl` читается как `token-events.jsonl`
  (`FileShare.ReadWrite` — как `OpenCodeStore.ReadShared`), хвост 500 строк.
- Поллинг транскриптов каждые `BudgetRefreshSeconds` (таймер уже есть в `MainWindow`);
  отдельный daemon не нужен.
- JSONL пишется атомарно (append-дозапись, как в `token-stats.ts` за образца).

---

## 7. План реализации (фазы)

### Фага 1. Чтение транскриптов — все уровни деталей, без хуков и внешних скриптов

Новые файлы `TokenMonitor/ClaudeStore.cs` + `TokenMonitor/DataProviders/ClaudeDataProvider.cs`
(по шаблону `OpenCodeStore` / `DevinDataProvider`).

1. Конфиг `Claude.ClaudeDir` (пусто = `%USERPROFILE%\.claude`; учитывать `CLAUDE_CONFIG_DIR`).
2. `Load()`:
   - сканировать `projects/<проект>/*.jsonl` (пропускать `.orphaned-` / `.superseded-`),
     читать через `ReadShared` (FileShare.ReadWrite);
   - каждую сессию: `sessionId` из строк, первый/последний user-промпт, цепочка
     `assistant`-шагов, модель, суммарные `usage` по компонентам;
   - задержка разбора или sboй формата → `DataSnapshot.LoadError` (как в `DevinStore`).
3. Дерево «сессия → запрос → шаг»; KPI месяц/день, кэш-проценты — работают как у OpenCode.
4. Live-лента фазы 1 — из новых строк транскриптов по timestamp (чуть медленнее realtime,
   зато без dependencies).

Критерии: после одного Claude Code сеанса в TokenMonitor на вкладке Claude видны сессии с
шагами и токенами; лента пополняется по таймеру; `dotnet build` зелёный.

### Фаза 2. Statusline-плагин (живая лента и бюджеты подписки)

`ClaudeSetup/statusline/claude-events.ps1` (Windows) и `claude-events.sh` (macOS/Linux):

1. читать stdin JSON;
2. если значение изменилось (сравнение с temp-файлом по `session_id`, как советует
   документация в разделе «Cache expensive operations»), дозаписать строку формата §6.1
   в `~/.claude/claude-events.jsonl`;
3. stdout — короткий заголовок или пусто;
4. установка: TokenMonitor дописывает `statusLine` в `~/.claude/settings.json` при флаге
   `Claude.StatuslineInstall` (и снимает его же).
5. `ClaudeDataProvider` читает `Claude.EventsPath` (формат §6.1), мерджит `LiveEvent`
   по `sessionId` + `messageId`, а `rate_limits` / `prompt_cache` / `cost.total_cost_usd`
   кладёт в `BudgetUiModel` (проценты 5ч/7д, даты сброса, spend USD).

Критерии: после каждого Claude-ответа в ленте появляется свежая строка; бюджет показывает
лимиты подписки и последние окна сброса (как метры Cursor).

### Фаза 3 (необязательно). HTTP-хук + OTEL

1. HTTP-hook на `Stop` / `SessionEnd` / `StopFailure` на локальный listener
   (мини-HTTP-сервер как в `TokenMonitor.Proxy`) — для прерываний и ошибок API.
2. OTEL-слой для org-обзора (collector + метрики `claude_code.token.usage`) — по желанию.

### Фаза 4 (необязательно). Admin Analytics API

`Claude.ApiKey` (Admin) → per-user/per-модель сводки в бюджетную панель (аналог DevinStore),
если пользователь админ организации.

---

## 8. Конфигурация (новая секция `Claude` в `settings.json`)

```jsonc
{
  "Claude": {
    "IsEnabled": true,
    "ClaudeDir": "",            // пусто = %USERPROFILE%\.claude (учитывает CLAUDE_CONFIG_DIR)
    "StatuslineInstall": true,  // фаза 2: прописать statusLine в ~/.claude/settings.json
    "EventsPath": ""            // фаза 2: пусто = ~/.claude/claude-events.jsonl
    // фаза 4: "ApiKey": "…", "Endpoint": "https://api.anthropic.com/…"
  }
}
```

(Добавление секции — совместимое изменение `AppSettings.cs`; по образцу секций `Devin`/`Cursor`.)

---

## 9. Риски

| Риск | Оценка | Фазы | Митигация |
|---|---|---|---|
| Схема JSONL не документирована и меняется с версиями Claude Code | высокая | 1 | защитный разбор: игнорировать неизвестное, `LoadError`, контрольный прогон на новой версии |
| Транскрипты удаляются через 30 дней (`cleanupPeriodDays`) | высокая | 1 | предложить в README поднять `cleanupPeriodDays`; или агрегировать в собственный long-lived store (кумулятивно, по образцу CursorStore) |
| Транскрипты содержат системный промпт и содержимое файлов — чувствительные данные | средняя | все | TokenMonitor уже показывает промпты OpenCode; отметить это в README |
| `cost.total_cost_usd` — клиентская оценка по списочным ценам (не счёт; `modelPricing` меняет цифру) | средняя | 2 | подписывать «оценка» в UI |
| Statusline вызывается очень часто — дозапись должна быть дешёвой | низкая | 2 | `Add-Content` / `>>`; debounce 300 мс уже встроен в Claude Code |
| `$.fs.write` — не append (ограничение mods) | — | §5.4 | mods не используем; если всё же — `$.store` или append через `$.process.run` |
| Много проектов и сессий одновременно; сабагенты пишут свои файлы | средняя | 1 | сканировать всё `projects/**`; сабагенты при необходимости — доп. узел дерева |
| `/clear` сбрасывает счётчики сессии, а `session_id` меняется | низкая | 2 | сверить поведение в контрольном прогоне; старые события не дублировать (dedup по `messageId`) |

## 10. Открытые вопросы

1. Регистрирует ли statusline-значение `cost.total_cost_usd` по сессии (сбрасывается при
   `/clear`) — как аккуратно накопить месяц; скорее всего — суммировать `costUsd` ленты (фаза 1).
2. Насколько полны `usage` у сообщений сабагентов (`projects/<пр>/<сессия>/subagents/*.jsonl`),
   и как их правильно отнести к родительскому запросу — проверить в контрольном прогоне.
3. Достаточно ли `history.jsonl` для восстановления истории, если транскрипты скошены
   (retention уже удалил) — только агрегаты `stats-cache.json`; точно ли там помесячные цены.

## 11. Ссылки

- Документация (fetched 10.11.2026, Claude Code 2.1.316):
  <https://code.claude.com/docs/en/hooks>, `statusline`, `costs`, `monitoring-usage`
  (OTEL), `claude-directory` (структура `~/.claude`), `headless` (Agent SDK, `-p`),
  `plugins/mods/overview` + `plugins/mods/reference`.
- Локальная машина: `%USERPROFILE%\.claude` (`settings.json`, `telemetry/`, `sessions/`),
  `claude.ps1` в `%APPDATA%\npm`.
- Шаблоны кода в репозитории: `TokenMonitor/OpenCodeStore.cs`,
  `TokenMonitor/OpenCodeSetup/token-stats.ts`, `TokenMonitor/DevinSetup/PLAN.md`.
