# Проверенные факты о DeepSeek Harness (для планирования)

Всё ниже прочитано в **установленной** сборке, а не по памяти:

- DSH Desktop, профиль `desktop`, установка:
  `C:\Users\raul\AppData\Local\Programs\DeepSeek Harness\resources\app.asar\dsh\`
- Версия CLI/пакетов: **`0.2.2-rc.3`** (`@deepseek-ai/dsh-desktop-runtime`, `@deepseek-ai/dsh`)
- Домашний каталог DSH: `C:\Users\raul\.dsh` (podтверждено файлами профиля, сессий и настроек)
- Активный профиль: `C:\Users\raul\.dsh\profiles\desktop` (патч: `cordis.patch.yml`)
- Текущая сессия (пример реальных данных):
  `C:\Users\raul\.dsh\sessions\--D-Work-MyGit-TokenMonitor-TokenMonitor--\session-a0cd90c6-9a77-44e3-9817-85bc8b7bd413\session.v4.jsonl.zstd`

Пути пакетов ниже указаны относительно
`…\dsh\node_modules\@deepseek-ai\`. Документация самого DSH —
`…\dsh\node_modules\@deepseek-ai\dsh\docs\`.

---

## 1. Журнал сессии — единственный источник истины

| Факт | Где проверено |
|---|---|
| Сессия — append-only журнал типизированных событий; всё остальное (история модели, телеметрия, заголовки) выводится из него | `dsh-session/README.md` |
| `Session.append(type, data, opts)` принимает только известные типы; неизвестный тип без `ignorable` ломает переоткрытие сессии | `dsh-session/README.md`, `dsh-session/src/types.ts` |
| `SessionEvent` = `{ type, seq, time, data, ignorable?, surfaceOp?, sourceEventSeqs? }`, `seq` монотонный, `time` — Unix ms | `dsh-session/src/types.ts` (строки 506–529) |
| Актуальная версия формата: `SESSION_FORMAT_VERSION = 4` | `dsh-session/src/types.ts:90` |
| Заголовок сессии: `version, id, createdAt, cwd?, parentSession?, isSeeded, origin?, delegationDepth?, agentPreset?` | `dsh-session/src/types.ts` (SessionHeader) |

### События, из которых берутся токены и тайминги

`SessionEventMap` (`dsh-session/src/types.ts`):

| Событие | Полезная нагрузка | Что даёт мониторингу |
|---|---|---|
| `turn/start`, `turn/end` | `turn`, `reason` (`completed/aborted/blocked/error/max-tokens/interrupted/forked`) | границы «запроса», причины сбоев |
| `step/start`, `step/end` | `turn`, `step` | границы шага, длительность |
| `user/message` | `UserMessage` (+ `source.kind`) | начало хода, превью промпта |
| `assistant/message` | `message`, `stream`, **`usage?`**, `interrupted?` | главный источник токенов: `usage` едет вместе с ответом, отдельной записи usage нет |
| `assistant/attempt` | `stream` | биллингуемая попытка без ответа (ретраи, ошибки стрима) |
| `tool/call`, `tool/result` | `name`, `arguments`, `message`, `error?`, `meta?` | время инструментов, ошибки |
| `request/header` | `header.config` (`LlmCallConfig`: provider, model, reasoningEffort, sampling), `adapterDefaults?`, `tools?`, `reason` | маршрут запроса и параметры генерации |
| `request/context` | `provider`, `model`, `contextWindow?`, `systemPromptUpdate?` | ёмкость окна (для давления на контекст) |
| `session/end-seed` | `inherited?` | граница fork-наследования |

Дополнительно композиция `dsh-base` монтирует `dsh-llm-retry`, который пишет **несёрфейсные** события
`llm/retry` (retry id, provider, mode, policy key, failure, scheduled delay) и `llm/retry-started`
(`dsh-llm-retry/README.md`). Каждый ретрай — ещё один оплачиваемый запрос.

---

## 2. Что реально лежит в `usage` (проверено на живой сессии)

`assistant/message.usage` попадает в состояние проекции `tokenMeasurement`:

```json
{
  "inputTokens": 453,
  "outputTokens": 637,
  "cacheReadTokens": 96256,
  "cacheWriteTokens": 0,
  "totalTokens": 97346
}
```

Источник: `C:\Users\raul\.dsh\storages\session_projcache\sessions\session-a0cd90c6-….json`
(строка ~50). `totalTokens` = сумма четырёх слагаемых.

Сводка `tokenUsage` по всей сессии (та же проекция):

```json
{ "totals": { "uncachedInputTokens": 92303, "outputTokens": 8757,
              "cacheReadTokens": 1153408, "cacheWriteTokens": 0 },
  "last":   { "turn": 2, "step": 23, "buckets": { … } } }
```

Давление на контекст (`contextPressure`): `surfaceTokens`, `contextWindow`,
`pressureTokens`, `sampledSurfaceTokens`; для `deepseek-flash` в этой установке
`contextWindow = 1048576`. Разбивка (`contextBreakdown`): `systemTokens`, `toolsTokens`,
`messageTokens`.

Статистика хода/шага (`sessionStats`): `turns`, `steps`, `llmMs`, `toolMs`,
`ttftMs`/`ttftSteps`, `decodeMs`/`decodeTokens`.

**Важно:** отдельного `reasoningTokens` в провайдерском usage этой установки нет —
thinking приходит блоками `reasoning` в самом сообщении. Поэтому в плане:
`reasoning_tokens` заполняется, если провайдер его пришлёт, иначе 0, а «reasoning-объём»
хранится как `reasoning_chars`; полный `usage` всё равно дублируется в `usage_samples.raw_json`.

---

## 3. Сервис `ctx.tokenMeter`

`dsh-token-meter/README.md`:

- `ctx.tokenMeter.measure(session, requestHeader?)` → `{ totalTokens, surfaceTokens, nodes[] }`
  (детерминированный пересчёт по журналу, без вызовов модели);
- `ctx.tokenMeter.contextPressure(session)` → последний провайдерский размер промпта + ёмкость;
- `ctx.tokenMeter.estimateMessage(message)` → эвристическая цена одного сообщения;
- проекции: `tokenUsage`, `contextPressure`, `contextBreakdown`, host-only `tokenMeasurement`;
- `deriveTurnTokenUsage(events)` — точный fold по попыткам одного хода.

Ограничение (пригодится в плане): эвристика — «4 символа на токен», приблизительная;
провайдерский usage переиспользуется только при совпадении конверта запроса.

---

## 4. События жизненного цикла, на которые подписывается плагин

`dsh-session/src/index.ts:43-88` — точные имена и режимы:

| Событие | Режим | Когда |
|---|---|---|
| `session/created` | `emit` | после входа сессии в store; слушатель видит всю уже существующую историю, но она не переигрывается как `session/event` |
| `session/disposed` | `emit` | сессия покидает store |
| `session/event` | `emit` | post-commit, fire-and-forget; `(session, event)`; ошибки слушателя логируются и не ломают append |
| `session/flush` | `parallel` (awaited) | барьер долговечности |

---

## 5. Чтение истории вне live-потока (backfill)

`dsh-session-persistence/README.md` — backend-независимый сервис `ctx.sessionPersistence`:

```text
create(header) / open(id,'write') / open(id,'read')
stat(id)  → header + revision (+ eventCount, sizeBytes)
list()    → снимки всех видимых сохранённых сессий
flush()
handle.read(offset?, length?) → { eventState, events }
```

Это правильный путь для импорта истории: он сам разбирается с историческими
форматами, сжатием и «рваным хвостом». Прямое чтение файлов — только как аварийный вариант.

---

## 6. Где DSH хранит данные

`dsh-base/cordis.patch.yml` (строки 138–200):

| Что | Конфигурация | Фактический путь |
|---|---|---|
| Журналы сессий (JSONL) | `dsh-session-persistence-jsonl`, `root: dshHomePath('sessions')` | `C:\Users\raul\.dsh\sessions\` |
| KV-хранилища плагинов | `dsh-storage-json`, `root: dshHomePath('storages')` | `C:\Users\raul\.dsh\storages\` |
| Кэш проекций сессий | `dsh-session-projection-cache` | `…\.dsh\storages\session_projcache\` |
| Профиль | `dsh.profile.bundles` | `C:\Users\raul\.dsh\profiles\desktop\` |
| SQLite-поиск | `dsh-session-query-sqlite`, `openAt: never` (по умолчанию выключен) | не открывается |

`dsh-home-paths/README.md`: `resolveDshHome()` = явный конфиг → `$DSH_HOME` → `~/.dsh`;
есть `dshHomePath(...)`/`dshCachePath(...)`.

Раскладка журнала: `<root>/--<normalized-cwd>--/<encoded-id>/session.vN.jsonl[.zstd]`,
по умолчанию **zstd** (`compression: 'zstd'`), одна физическая строка на событие
(`dsh-session-persistence-jsonl/README.md`).

---

## 7. SQLite в процессе DSH уже используется

`dsh-session-query-sqlite/README.md` прямо описывает `DatabaseSync` (`node:sqlite`),
`journalMode: wal|delete|truncate|persist`, `application_id`, версию схемы, `openAt`.
Значит в host-процессе DSH доступен встроенный SQLite без нативных аддонов и сборки —
это и берём для БД мониторинга.

---

## 8. Как устроен плагин/бандл

`references/host-plugin.md` (skill `dsh-doc`):

- Host-only бандл — обычный npm-пакет: `"type": "module"`, `exports: { ".": "./index.js" }`,
  `dsh.bundle.patch: "./cordis.patch.yml"`, патч вставляет запись плагина;
- `index.js` экспортирует `apply(ctx, config)` (+ опционально `inject`, `Config`);
- все ресурсы регистрируются внутри `apply` через `ctx.effect` / `ctx.on`;
- для видимости в Plugin Manager нужны `locale/*.json` (`meta.title`, `meta.description`,
  опционально `meta.detail`), `icon`, и всё это в `files`;
- установка — `plugin_manager` → `install_bundle` (или **Plugins → Add plugin** в UI),
  вручную `package.json`/`cordis.patch.yml` профиля не правим.

Для профиля `desktop` CLI-запуск запрещён (`dsh/README.md`: «The `desktop` name is reserved
for the Electron-owned profile»), поэтому установка — только через Plugin Manager / UI.

---

## 9. Что осталось непроверенным (важно для честного плана)

1. **Точный набор опциональных полей `TokenUsage`** (есть ли `reasoningTokens` в других
   адаптерах/маршрутах). В этой установке — пять полей, см. §2. План не зависит от ответа:
   `usage` пишется целиком в `raw_json`.
2. **События компакции** (`compaction/start` … `compaction/end`) упомянуты в
   `dsh-token-meter/README.md`, но их точные payload'ы не вычитаны — таблица `compactions`
   в схеме помечена как «опционально/уточнить при реализации».
3. **Формат `AssistantStreamRecord`** (структура timed-стрима) — известен только факт
   существования; в плане из него берутся лишь счётчики/длины.
4. **Поведение файлового sandbox для host-плагинов**: инструменты ограничены sandbox-политикой,
   а запись плагина через `node:sqlite` в `~/.dsh` формально идёт мимо инструментов.
   Проверяется первым же прогоном на живой установке (этап P1).
5. **Фактические цифры цены/стоимости** маршрута `deepseek-account` недоступны из DSH
   (нет API стоимости) — бюджет в UI останется «токены/оценка», см. план, раздел 7.6.

---

## 10. Ограничения текущей сессии агента (для исполнителя плана)

`pwsh` в этой сессии падает до запуска команды:
`windows-acl workspace grant failed: … grantWrite(D:\Work\MyGit\TokenMonitor\TokenMonitor)`.
То есть **сборку/тесты/CLI из этой сессии запустить нельзя** без `danger-full-access`.
Файловые инструменты записи при этом работают, поэтому документы созданы.
Исполнителю плана понадобится сессия с полноценным доступом к `dotnet build`
и к каталогу `C:\Users\raul\.dsh`.
