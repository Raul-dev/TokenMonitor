# Логгер токенов DeepSeek Harness

Host-only плагин DSH, который записывает детальную телеметрию токенов каждой сессии
в локальную базу SQLite. Приложение **TokenMonitor** читает эту базу только на чтение
и показывает дерево «сессия → запрос → шаг», KPI, живую ленту и алерты.

## Что собирается

| Уровень | Данные |
|---|---|
| Сессия | `cwd`, создатель/субагент (`parentSession`, `origin`), preset, seed/fork, заголовок, провайдер и модель |
| Ход (turn) | границы, причина завершения (`completed`, `error` с кодом, `max-tokens`, `interrupted`), превью промпта и его длина, число шагов |
| Шаг (step) | провайдерский `usage` (input — **некэшированный** вход, output, cache read/write, total), промпт целиком (`input + cacheRead`), размер контекста и ёмкость окна, TTFT, длительность, число записей стрима, объём текста и thinking |
| Попытки | `assistant/attempt` — оплачиваемые попытки без ответа (объём стрима) |
| Ретраи | `llm/retry` / `llm/retry-started`: id, провайдер, режим, политика, код ошибки, задержка, факт старта |
| Инструменты | имя, аргументы (длина + превью), результат (длина + превью), `is_error`, код/причина ошибки, длительность, `meta` |
| Сырые события | только при `capture: raw`: полный JSON каждого события |

Агрегаты (`v_session_summary`, `v_turn_summary`, `v_step_feed`, `v_daily_by_model`,
`v_cache_efficiency`, `v_tool_timing`, `v_ingest_health`) — публичный контракт для
TokenMonitor; внутренние таблицы могут расширяться.

## Установка и настройка

Плагин ставится как локальный бандл (`DeepSeekSetup/plugin`) через Plugin Manager
или `plugin_manager → install_bundle`. Настройки живут в строке `token-logger`
профильного `cordis.patch.yml`:

```yaml
- id: token-logger
  name: '@local/dsh-token-logger'
  config:
    dbPath: 'C:\Users\<вы>\.dsh\tokens\tokens.db'  # по умолчанию $DSH_HOME/tokens/tokens.db
    capture: full           # usage | full | raw
    captureText: preview    # off | preview | full
    previewChars: 200
    backfillOnStart: true
    backfillLimit: 50
    flushIntervalMs: 250
    flushMaxEvents: 200
    retentionDays: 0        # 0 = хранить бессрочно
    journalMode: wal
    verbose: false
```

## Приватность

Тексты промптов и ответов — самый чувствительный материал. По умолчанию
`captureText: preview` пишет только первые 200 символов и полную длину;
`off` оставляет только длины и хеши-эквиваленты (длины), полный текст — только
явный opt-in `full`. Аргументы и результаты инструментов подчиняются тому же режиму.
При `capture: raw` в БД попадает полный JSON событий, включая тексты — включайте
осознанно.

## Надёжность

* Плагин **никогда** не пишет в журнал сессии и не добавляет события: только читает.
* Любая ошибка обработчика логируется и проглатывается; сессия агента не ломается.
* Запись батчами по `flushIntervalMs`/`flushMaxEvents`, одна транзакция на батч, WAL.
* История догоняется через публичный `ctx.sessionPersistence` (без разбора `.zstd`),
  идемпотентно: повторный запуск не создаёт дублей (`INSERT ... ON CONFLICT` по `(session_id, seq)`).
* Переполнение очереди (> `maxQueue`) отбрасывает метрики, но не память сессии.

## Ограничения

* События компакции (`compaction/*`) в этой версии в таблицу `compactions` не
  раскладываются — только в `raw_events` при `capture: raw`.
* `reasoningTokens` записывается, только если провайдер его присылает; объём
  thinking всегда доступен как `steps.reasoning_chars`.
* Проекции `projected_tokens`/`surface_tokens`/разбивка контекста
  (`ctx_system_tokens` и др.) пока не заполняются: их источник — `ctx.tokenMeter`,
  который считает эвристику на лету; см. PLAN §6.3.
* Плагин — единственный писатель БД; TokenMonitor обязан открывать её только на чтение.
