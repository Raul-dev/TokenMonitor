# Cursor: детализация сессий и живая лента — статус и план завершения

> Статус: **исследование + план. Реализация начата; большая часть уже в коде.
> Единственный непроверенный фундамент — payload хука `stop` (§6).**
> Дата: 10.11.2026
> Метод тот же, что в `../DevinSetup/PLAN.md` и `../ClaudeSetup/PLAN.md`: документация,
> probe-скрипты и проверка этой машины (Cursor установлен, аккаунт вошёл, ~108 разговоров,
> есть агентные сессии с транскриптами).
>
> **Важно:** официальная документация cursor.com на момент исследования не была
> доступна из этой среды (ECONNREFUSED; websearch без результатов) — hook-часть
> подтверждена по встроенному в Cursor скилу `create-hook` (`~/.cursor/skills-cursor/create-hook/SKILL.md`)
> и по probe-выполнениям на живой машине. Перед релизом — контрольный прогон (§7).

---

## 1. Вывод (TL;DR)

**Да, для Cursor всё складывается — и самая сложная часть уже сделана.**
Есть три канала, покрывающие и историю, и бюджет, и живую ленту:

| # | Канал | Что даёт | Состояние |
|---|-------|----------|-----------|
| 1 | **Dashboard API** (`usage-summary`, `GetFilteredUsageEvents`) — уже реализовано | per-запрос события: `tokenUsage.inputTokens/outputTokens/cacheReadTokens/cacheWriteTokens`, `chargedCents`, модель, время, пагинация (40 дней); процент плана (`Auto/Api/Total`, границы цикла) | **готово** в `CursorClient.cs` + `CursorStore.cs` |
| 2 | **Хук Cursor** (`~/.cursor/hooks.json` → `token-stats.ps1`) — начато | живая лента без обращения к серверу: `beforeSubmitPrompt` + `stop`, запись в `cursor-events.jsonl` | **скрипт написан, hooks.json не установлен, payload не верифицирован** |
| 3 | **Локальные хранилища** (запасной канал) | `state.vscdb` (композеры, названия, модель, время, контекст), `agent-transcripts/*.jsonl` (полные диалоги), `ai-code-tracking.db` (AI-строки кода), `conversation-search.db` | **исследовано probe'ами, в код не включено** |

Чего не хватает (доделка):
1. **Установить hooks.json** (`%USERPROFILE%\.cursor\hooks.json` на машине отсутствует)
   и проверить, что хуки срабатывают;
2. **Верифицировать payload события `stop`** — полей токенов
   (`input_tokens`, `cache_read_tokens`, `cache_write_tokens`, `output_tokens`)
   в документации skill'а нет, они гипотеза автора скрипта; нужен одноразовый дампера-хук;
3. Dedup hook ↔ dashboard (ключ корреляции `generation_id` не проверен);
4. (Опционально) локальное дерево «сессия → запрос → шаг» из `agent-transcripts` как
   fallback при недоступном dashboard.

Устройство этих каналов — ниже.

---

## 2. Что уже реализовано в репозитории (на момент исследования)

`TokenMonitor/CursorClient.cs`:

- **Чтение auth**: `state.vscdb` → `ItemTable` (`cursorAuth/accessToken`,
  `cursorAuth/stripeMembershipAuthId`), JWT `sub` — read-only с fallback на
  `?mode=ro&immutable=1` (тоже уже как в Devin-плане);
- **Бюджет**: `GET https://cursor.com/api/usage-summary` →
  `CursorPeriodUsage { CycleStart, CycleEnd, Membership, AutoPercent, ApiPercent,
  TotalPercent, DisplayMessage }` — источник процентов плана;- **События расхода**: первая попытка
  `POST https://api2.cursor.sh/aiserver.v1.DashboardService/GetFilteredUsageEvents`,
  fallback `POST https://cursor.com/api/dashboard/get-filtered-usage-events`
  (cookie `WorkosCursorSessionToken=sub%3A%3A<jwt>`), разбор `usageEvents`/`usageEventsDisplay`,
  `tokenUsage.*`, `chargedCents`, пагинация по `totalUsageEventsCount`, окно 40 дней.

`TokenMonitor/CursorStore.cs`:

- `LoadAsync(refreshRemote)`: дашборд-события + локальный `cursor-events.jsonl`
  (`ReadShared`, FileShare.ReadWrite), merge: приоритет hook-записей по `messageId`,
  fallback-ключ `time+sessionId+tokens` (уже готов dedup-механизм; от хука нужен только
  стабильный `messageId` = `generation_id`);
- группировка в `SessionData` по `ConversationId` (и `day:YYYY-MM-DD` для пустых id),
  `Live`-лента, Signature по числу событий и max Timestamp;
- `GetPeriodAsync` → бюджет в UI (уже в `CursorDataProvider`).

`CursorSetup/token-stats.ps1` — хук-плагин (аналог `token-stats.ts` OpenCode):

- `beforeSubmitPrompt`: сохраняет pending-файл `%APPDATA%\TokenMonitor\cursor-pending\<generation_id>.json`
  (время, prompt, model, workspace_roots[0]);
- `stop`: восстанавливает pending, вычисляет токены
  (`input_tokens` (=контекст) минус кэш, `output_tokens`, `cacheRead/Write`),
  дописывает строку формата
  `{time, source:"hook", sessionId, messageId, model, prompt, workspace, status, steps:1,
    input, output, reasoning, cacheRead, cacheWrite, billed, context, durationMs}`
  в `%APPDATA%\TokenMonitor\cursor-events.jsonl`, пишет ошибки в `cursor-token-stats.log`;
  парсер `CursorStore` читает ровно эти ключи — контракт совпадает.

Проб-скрипты (`_probe_*.py`) — исследовали `state.vscdb`, bubbles, API-эндпоинты;
выводы свёрстаны в таблице ниже.

---

## 3. Хуки Cursor (официально, по встроенному скилу `create-hook`)

Cursor имеет собственную систему хуков — schema v1, JSON на stdin, JSON/exit-code на stdout.

Расположение конфига:

- проект: `.cursor/hooks.json`, скрипты `.cursor/hooks/*` (пути от корня проекта);
- пользователь: `~/.cursor/hooks.json`, скрипты relative от `~/.cursor/`.

Формат:

```json
{
  "version": 1,
  "hooks": {
    "beforeSubmitPrompt": [
      { "command": "powershell.exe -NoProfile -ExecutionPolicy Bypass -File
                    \"C:\\\\Tools\\\\TokenMonitor\\\\token-stats.ps1\"", "timeout": 10 }
    ],
    "stop": [
      { "command": "powershell.exe -NoProfile -ExecutionPolicy Bypass -File
                    \"C:\\\\Tools\\\\TokenMonitor\\\\token-stats.ps1\"", "timeout": 10 }
    ]
  }
}
```

Поля hook-определения: `command` (обязательно), `type` (`"command"`|`"prompt"`),
`timeout` (сек), `matcher` (regex, JavaScript-REGEXP — против tool/body в зависимости от события),
`failClosed` (блокирует или fails open при крахе хука), `loop_limit` (для `stop`/`subagentStop`).

События агента: `sessionStart`, `sessionEnd`, `preToolUse`, `postToolUse`,
`postToolUseFailure`, `subagentStart`, `subagentStop`, `beforeShellExecution`,
`afterShellExecution`, `beforeMCPExecution`, `afterMCPExecution`, `beforeReadFile`,
`afterFileEdit`, `beforeSubmitPrompt` (matcher = `UserPromptSubmit`), `preCompact`,
`stop`, `afterAgentResponse`, `afterAgentThought`; плюс события Tab
(`beforeTabFileRead`, `afterTabFileEdit`).

Взаимодействие: stdin → JSON; вывод JSON на stdout или exit-код (0 — ok,
2 — блокировать, иное — fail-open, если не задан `failClosed`). Вывод разрешённых полей
зависит от события (`preToolUse`: `permission`, `user_message`, `agent_message`,
`updated_input`; `postToolUse`: `additional_context`…). Cursor читает hooks.json на лету
(reload на save; при отсутствии — restart) и показывает в вкладке **Hooks** настроек
и **Hooks** output channel.

Подтверждение на этой машине: `create-hook` skill присутствует в
`~/.cursor/skills-cursor/`; `~/.cursor/hooks.json` пока **не создан** — это недоделка №1.

Чего в документации skill'а нет: полный набор **полей stdin-полезной нагрузки** каждого события
(в `create-hook` перечислены только разрешённые **ответные** поля). Токен-филды
в payload `stop`, на которых держится `token-stats.ps1`, — гипотеза.

---

## 4. Dashboard API (уже работает в коде)

Главный источник расхода. Разбор целей probe'ов и клиентского кода:

- `GET https://cursor.com/api/usage-summary` — проценты плана и границы цикла (реализовано);
- `POST .../GetFilteredUsageEvents` — «последние N событий расхода», артефакт веб-dashboard:
  каждое событие: время, модель, `tokenUsage.inputTokens|outputTokens|cacheReadTokens|cacheWriteTokens`,
  `chargedCents` (стоимость в центах), prompt-фрагменты, workspace; общее число из
  `totalUsageEventsCount` (реализовано в `TryEventsAsync` + `MapEvent`);
- ограничение: окно (в коде 40 дней) и страница, ограниченная планом; недоступность
  «в регионе» уже описана в статус-строке: «Dashboard API недоступен в этом регионе;
  живая лента идёт из хука Cursor».

Correlation с хуком: события dashboard не содержат явного `generation_id`, но
`messageId` из ленты есть в CursorUsageEvent (`MessageId`) — Merge уже пробует
смыкать по нему; следить (§7, Фаза B3).

---

## 5. Локальные хранилища (запасной и бонусный канал)

### 5.1. `%APPDATA%\Cursor\User\globalStorage\state.vscdb` (SQLite, 260k ключей)

- `ItemTable` (418 ключей): auth (уже используется), настройки;
- `composerHeaders` (108 разговоров): `composerId`, `workspaceId`, `createdAt`/`lastUpdatedAt`
  (мс), `isSubagent`, `name`/`title`/`subtitle`, `unifiedMode`, `workspaceIdentifier`,
  `totalLinesAdded/Removed`, `isArchived`, `isDraft` — готовый список сессий;
- `cursorDiskKV`:
  - `composerData:<id>` — новое поколение агентных разговоров: `name`, `createdAt`,
    `lastUpdatedAt` (мс), `status`, `modelConfig.modelName` (например `grok-4.6`),
    `workspaceIdentifier`, `unifiedMode`, `totalLinesAdded/Removed` **и контекст окна**:
    `contextTokensUsed`, `contextTokenLimit`, `contextUsagePercent`,
    `promptTokenBreakdown` (разбивка контекста по категориям: system_prompt / tools /
    rules / skills / mcp / subagents / summarized_conversation / conversation),
    `promptContextUsageTree` (v2, дерево по инструментам),
    `subComposerIds` (сабагенты), `conversationMap`;
  - `bubbleId:<composerId>:<bubbleId>` — сообщения IDE-чата: `type` (1=user, 2=assistant…),
    `createdAt`, `text`, `toolResults`, `tokenCount {inputTokens, outputTokens}`.
    **Внимание:** поле `tokenCount` существует, но во всех проверенных 4000 bubbles — `0`
    (Cursor его больше не заполняет). Не расчитывать на него.
- самого per-request расхода в state.vscdb **нет** — только на сервере (§4) или в хуке (§3).

### 5.2. `~/.cursor/projects/<проект>/agent-transcripts/<conversationId>.jsonl`

Полные транскрипты(agent) сессий — формат Anthropic-like:
`{"role":"user|assistant","message":{"content":[{"type":"text|tool_use", …}]}}` + строки
`{"type":"turn_ended","status":"ok|error",…}`. Пример на машине: сессия `c57ce659-…`
замечена, но расхожденного `message.usage` нет.
Полезно как **история/детализация** (сессия → промпт → tool-вызовы), и с `turn_ended`
— как «признак конца хода» (можно собирать длительность хода even offline).

### 5.3. `~/.cursor/ai-tracking/ai-code-tracking.db` (50 МБ)

- `ai_code_hashes`: per-фрагмент кода, написанный AI — `requestId`, `conversationId`,
  `model`, `timestamp`, `fileExtension` (можно посчитать долю AI-кода);
- `scored_commits`: по коммиту — `linesAdded/Deleted`, `tabLines…`, `composerLines…`,
  `v1AiPercentage`, `v2AiPercentage` — фактически бесплатный KPI «доля AI-строк»;
- `conversation_summaries` (пусто здесь), `tracking_state`.

### 5.4. Прочее

- `globalStorage\conversation-search.db` — FTS-индекс диалогов;
- `globalStorage\anysphere.cursor-agent-host\data.sqlite` — внутренности agent-host;
- `%APPDATA%\Cursor\agent-harness\` — рантайм CLI-агента (`agent-harness.cjs`),
  `~/.cursor/cli-config.json` — настройки CLI-агента (к ним не обращаемся).

---

## 6. Недоделки — конкретный список «что осталось»

1. **~/.cursor/hooks.json не установлен.** Решение: installer'ная функция (или run. script)
   пишет файл с двумя хуками на `token-stats.ps1` (абsolut path + `powershell.exe -NoProfile`),
   при этом не трогая пользовательские существующие hooks (merge-политика: если файл есть —
   добавить/обновить только записи с ключом `command = ours path`).
2. **Пayload `stop`/`beforeSubmitPrompt` не проверен.** Основной дологон:
   - временный CursorSetup `_dump_hook.ps1`: stdin → `%APPDATA%\TokenMonitor\hook-dump\<event>-<ts>.json`
     с выключенной логикой;
   - конфиг hooks.json на оба события;
   - прогнать одну генерацию в агентном режиме Cursor;
   - сверить: есть ли `generation_id`, `input_tokens`, `cache_read_tokens`,
     `cache_write_tokens`, `output_tokens`, `model`, `status`, `workspace_roots`,
     как именно передаётся `conversation_id`/`session_id` и что такое `hook_event_name`;
   - привести `token-stats.ps1` (и `CursorStore.LoadLocal`) в соответствие с реальными
     полями; учесть что tokenizer'ы могут отсутствовать (запись это прямо умеет: 0.
     Но в этом случае billing остаётся только в dashboard-канале).
3. **Dedup hook ↔ dashboard**. Ключ должен совпадать: просто покрывает `Merge` по
   `MessageId`; надо убедиться, что hook пишет в `messageId` тот же `generation_id`,
   что встречается в dashboard-событии (поля `requestId`?). Если не совпадает — оставить
   hook-канал как «основной» (priority уже реализована) и ясно это прямо в UI:
   dashboard продолжает играть роль бюджета и истории, лента не задублирована.
4. **Timeout хуков**: put `timeout: 5-10` (`beforeSubmitPrompt` блокирует промпт —
   нельзя медленно; `stop` не блокирует). `failClosed: false` — обязательно, чтобы хук
   не оправил работу Cursor при ошибках записи.

После этих пунктов провайдер Cursor считается «доделанным»: история, бюджет, детализация,
лента живут (dashboard уже работает с текущего релиза).

---

## 7. Фазы завершения

### Фаза A. Верификация хука (главная)

1. `CursorSetup/_dump_hook.ps1` — dumps (stdin) в hook-dump-каталог; открыл hooks.json на
   user-уровне, времен, элементы: `beforeSubmitPrompt`, `stop`, `afterAgentResponse`.
2. Прогон: одна агентная генерация, одна onError-генерация (лимит), одна IDE-чат-генерация.
3. Сверить номинал полей → правки `token-stats.ps1`/`CursorStore.LoadLocal`
   (и, возможно, порядок вывода `continue:true` для `beforeSubmitPrompt`).
4. Документировать фактическую схему в README программы (Cursor официальный формат手掌
   payload не описывает).

Критерии: `cursor-events.jsonl` получает строки с корректными токенами при реальных
генерациях; cursor их отображает в ленте без переустановки.

### Фаза B. Установка/постройка installer'а хука

1. Экранный признак настроек: разговор пользователя с Cursor требует ручной установки
   hooks.json (см. Фаза A) — ограничиться teacher'ом (в TokenMonitor settings UI:
   кнопка «Install Cursor hook» → пишет `%USERPROFILE%\.cursor\hooks.json`, отогнав
   чужие уровни, и просит перезапустить Cursor);
2. Кнопка «Uninstall» — снять только свои записи.

Критерии: повторная установка не роняет пользовательские Cursor hooks; после перезапуска
Cursor во вкладке Hooks видны два события.

### Фаза C. Локальная история как fallback (необязательно, по желанию)

1. `CursorStore`: если dashboard недоступен и hook-ленты мало, строить «историю» из
   `composerHeaders/composerData` (сессии с названиями/моделью/временами/линиями) и
   `agent-transcripts/*.jsonl` (шаги-пормы и tool-вызовы, длительность хода по `turn_ended`);
   токены не подставлять (пустые) — только N факторов.
2. Опционально — KPI «доля AI-строк» из `ai-tracking.db` (`scored_commits`).

Критерии: при «регионе недоступен»/ошибках — пользователь видит реальную историю сессий,
а не пустоту.

### Фаза D. Полировка

1. Заголовок `PathText` = `state.vscdb` уже есть; расписать в tooltip источник (dashboard
   + hook + local);
2. `BudgetDetail` в показе Cursor: Auto% / API% / сброс (уже — от `usage-summary`);
   протестировать с Edge-cases (нулее использование, новый цикл);
3. README: инструкция по установке хука Cursor и пояснение, откуда берутся каждые цифры.

---

## 8. Риски и рейтинги

| Риск | Оценка | Митигация |
|---|---|---|
| Токен-филды отсутствуют в payload `stop` (гипотеза не подтвердится) | средняя | остаём direction: KPI берутся из dashboard; лента в hook-режиме показывает ход+длительность без токенов; доп. источник — `afterAgentResponse` |
| Cursor меняет события/форматы хуков (System сейчасaget schema v1, версия Cursor меняется часто) | средняя | verify-прогон при обновлении Cursor; installer осторожно перезаписывает только свои записи |
| Дашборд-эндо по регионам недоступен (уже есть message в коде) | средняя | hook-канал покрывает ленту; локальные хранилища покрывают историю |
| Merge-дубляры hook+dashboard (разные ключи) | низкая | Фаза A: проверить correlation key; dashboard priority уже реализована |
| Bloom `tokenCount = {0,0}` — норма | подтверждено | не использовать bubble-тokeнCount |
| Несанкционированный захват cursor-events.jsonl из dashboard (права на запись) | низкая | Write-проверка и лог ошибки в `cursor-token-stats.log` |
| Просроченный JWT-токен Cursor | низкая | уже обрабатывается в UI (ошибка auth из CursorClient) |

## 9. Открытые вопросы

1. Полная схема stdin payload всех событий Cursor (модификация/стёновки)
   — получается только ручно верифицированным дампа-хуком (Фаза A);
2. Совместимость hook'ов с `agent-harness` CLI-агентом (`cursor-agent`) и с фоновыми
   (облачными) агентами — проверить: события нажимаются ли в CLI-режиме;
3. Понятие `generation_id` vs `requestId` из dashboard — какой ключ для dedup;
4. Не попадает ли в hook payload чат-Tab (вкладка Tab-автору) — если да, есть отдельная
   неизумdriven величина расхода.

## 10. Ссылки

- Встроенный скил Cursor (главный источник по hooks): `%USERPROFILE%\.cursor\skills-cursor\create-hook\SKILL.md`
- Заготовки и эксперименты: `CursorSetup/_probe_*.py`, `CursorSetup/token-stats.ps1`
- Реализация: `TokenMonitor/CursorClient.cs`, `TokenMonitor/CursorStore.cs`,
  `TokenMonitor/DataProviders/CursorDataProvider.cs`
- Соседние исследования: `../DevinSetup/PLAN.md`, `../ClaudeSetup/PLAN.md`,
  `OpenCodeSetup/token-stats.ts` (эталон формата ленты)
