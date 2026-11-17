# DeepSeekSetup — мониторинг токенов DeepSeek-агента в TokenMonitor

Каталог с планом интеграции: DeepSeek Harness (DSH) как ещё один источник
данных для приложения TokenMonitor, с максимально детальным сбором информации
о токенах в SQLite.

## Состав

| Файл | Что внутри |
|---|---|
| [`PLAN.md`](PLAN.md) | **Главный документ.** Цель, разбор текущего приложения и назначения окна, архитектура, этапы P0–P4, приёмка, риски, открытые вопросы |
| [`FACTS.md`](FACTS.md) | Проверенные по установленной сборке DSH `0.2.2-rc.3` факты (события, `usage`, сервисы, пути, API персистентности) + что осталось непроверенным |
| [`schema.sql`](schema.sql) | Готовая схема БД (SQLite, `STRICT`, schema_version=1): таблицы, индексы, публичные представления `v_*`, примеры запросов |
| [`TZ_ACCOUNT_USAGE.md`](TZ_ACCOUNT_USAGE.md) | **ТЗ следующей сессии:** реальный usage аккаунта DeepSeek (расход, баланс, токены) в верхней полосе TokenMonitor — проверенные эндпоинты, схемы, настройки, приёмка |
| [`plugin/`](plugin/) | **Реализация:** host-only бандл `@local/dsh-token-logger` (live-индексация, backfill, SQLite), локаль/иконка/DETAIL и тесты |
| [`tools/`](tools/) | Диагностика: разбор журнала DSH (`inspect-journal.mjs`), сверка чисел (`verify-real.mjs`), запросы к `tokens.db` (`query-db.mjs`), приёмочный запуск C#-store (`StoreCheck`), поиск настроек прокси в установленных пакетах DSH (`asar-search.mjs`, `search-proxy.mjs`) |
| [`tools/AccountCheck/`](tools/AccountCheck/) | Приёмка бюджета аккаунта: живой запрос к `platform.deepseek.com`, офлайн-фикстуры и предпросмотр шапки |

## Коротко о решении

1. **Плагин DSH** (`@local/dsh-token-logger`, Host-only bundle) подписывается на
   `session/event`, `session/created`, `session/disposed`, `session/flush`, сворачивает
   события в таблицы и пишет их батчами в `%USERPROFILE%\.dsh\tokens\tokens.db`
   (`node:sqlite`, WAL). Историю догоняет через публичный `ctx.sessionPersistence`.
2. **TokenMonitor** получает нового провайдера `DataSource.DeepSeek`, который читает ту же
   БД только на чтение (`PRAGMA query_only=1`) через представления `v_session_summary`,
   `v_turn_summary`, `v_step_feed` и складывает в уже существующий `DataSnapshot`.
   Дерево, KPI, живая лента и алерты начинают работать без переделки UI.

Почему не читать журналы DSH напрямую: они в zstd, версионируются (сейчас v4) и не содержат
производных метрик (давление на контекст, тайминги, ретраи, разбивка контекста). Подробнее —
`PLAN.md` §3.

## Установка и настройка

1. Поставить бандл: из корня проекта `.\install.ps1 -DeepSeek` (или `-All`), либо вручную
   `plugin_manager → install_bundle` с путём `D:\Work\MyGit\TokenMonitor\DeepSeekSetup\plugin`
   (или **Plugins → Add plugin**). Скрипт использует `dsh plugin --profile desktop add`
   и сам регистрирует бандл в `dsh.profile.bundles`; профиль `desktop` управляется
   Electron, поэтому CLI-запуск профиля запрещён.
2. Плагин активируется сразу и создаёт `%USERPROFILE%\.dsh\tokens\tokens.db`;
   историю догоняет при старте (`backfillLimit` сессий, по умолчанию 50).
3. Настройки — строка `token-logger` в `%USERPROFILE%\.dsh\profiles\desktop\cordis.patch.yml`
   (`dbPath`, `capture`, `captureText`, `retentionDays`, …), образец — в
   [`plugin/cordis.patch.yml`](plugin/cordis.patch.yml) и `PLAN.md` §5.2.
4. В TokenMonitor источник выбирается в списке сверху; «конфиг» ведёт в патч профиля,
   кнопка обновления перечитывает базу.

> Плагин устанавливается **ссылкой** на каталог `DeepSeekSetup/plugin`, поэтому этот
> репозиторий нельзя перемещать/удалять, пока плагин включён. Удаление —
> `plugin_manager → remove_bundle`.

## Прокси для DeepSeek Harness

Отдельный плагин для прокси **не нужен**: DSH сам направляет исходящие запросы (вызовы
модели, web search, `web_fetch`, HTTP MCP) через стандартные переменные окружения и читает
их при старте. Для подсчёта токенов прокси не обязателен — он нужен, чтобы видеть трафик
DSH в TokenMonitor или направить его через внешний прокси.

1. В `%APPDATA%\TokenMonitor\settings.json` включите локальный сервер (то же, что для OpenCode):

```json
"LocalProxyServerEnabled": true,
"ProxySettings": {
  "ListenUrl": "http://127.0.0.1:8181",
  "UpstreamUrl": "",
  "BypassList": ["litellm.mydomain.loc"]
}
```

`ListenUrl` — адрес, который слушает TokenMonitor; `UpstreamUrl` — необязательный внешний
прокси (пусто = ходить напрямую); локальный домен LiteLLM оставлен в `BypassList`, чтобы
запросы бюджета и LiteLLM не уходили через внешний прокси.

2. Создайте/дополните файл `%USERPROFILE%\.dsh\.env`:

```ini
HTTPS_PROXY=http://127.0.0.1:8181
HTTP_PROXY=http://127.0.0.1:8181
```

Это штатный способ именно для DSH: он читает `$DSH_HOME/.env` (по умолчанию
`%USERPROFILE%\.dsh\.env`), экспортированная переменная окружения приоритетнее файла, а
`.env` рядом с репозиторием эти имена задавать не может — DSH откажется стартовать.
Системные настройки прокси Windows DSH не читает. Вместо пары `HTTP(S)_PROXY` можно задать
один `ALL_PROXY` — он работает для обеих схем.

3. Перезапустите TokenMonitor, затем **полностью** закройте и запустите DeepSeek Harness
(включая иконку в трее): прокси применяется только при старте, горячего переключения нет.

Схема: `DSH → http://127.0.0.1:8181 (TokenMonitor) → напрямую или UpstreamUrl`.
Если меняете порт/IP, обновите одновременно `ProxySettings.ListenUrl` и `.env`.

Проверка: счётчик `↑ … · ↓ … МБ` в шапке TokenMonitor растёт при запросах агента; ошибки
туннеля пишутся в `%APPDATA%\TokenMonitor\tokenmonitor-*.log`. Если в `UpstreamUrl` задан
внешний прокси, сначала убедитесь, что он доступен: `407 Proxy Authentication Required`
означает неверные креды/адрес, и запросы DSH через локальный прокси не пройдут.

Ограничения (встроенное поведение DSH):

- только `http://` и `https://`; SOCKS и PAC не поддерживаются — для такой схемы DSH
  пойдёт напрямую, а не через другой прокси;
- loopback (`localhost`, `127.0.0.0/8`, `::1`, `0.0.0.0`) всегда напрямую — для локальных
  сервисов прокси не применяется;
- `NO_PROXY` — хосты и суффиксы (`example.com` покрывает `api.example.com`), допускается
  `:port` и `*`; CIDR-диапазоны не работают;
- OTLP-телеметрия идёт напрямую, а код, который пишет модель (ptc-runtime, workflow-воркеры),
  прокси-переменные не получает;
- bash-инструмент и дочерние процессы наследуют переменные, поэтому пароль в URL прокси
  будет им виден;
- TLS-инспектирующий прокси требует `NODE_EXTRA_CA_CERTS` до запуска.

Отключить: удалите строки из `%USERPROFILE%\.dsh\.env` (или задайте `NO_PROXY=*`) и
перезапустите DeepSeek Harness.

## Реальный usage аккаунта DeepSeek

Бюджет в верхней полосе для источника DeepSeek показывает **деньги аккаунта**
`platform.deepseek.com`, а не локальный подсчёт токенов
([`TZ_ACCOUNT_USAGE.md`](TZ_ACCOUNT_USAGE.md)):

```
$1,04 / $10,00                      ← крупно: потрачено за месяц / всего было на счету
осталось $8,96 · 472 запр.  ·  конфиг  ← мелко: остаток и число запросов
```

- **потрачено** — сумма `usage/cost` за текущий месяц (без `REQUEST`);
- **всего было** — баланс (оплачено + бонусы) плюс израсходованное по данным платформы;
  можно зафиксировать вручную: `DeepSeekAccount.TotalBudget` (например, сумма пополнения);
- **осталось** — текущий баланс аккаунта, в его валюте (`$`, `¥`, иначе код валюты);
- цвет: серый — данных нет, жёлтый — остаток ≤ `LowBalanceWarning`, красный — ≤
  `LowBalanceCritical` или баланс исчерпан;
- tooltip: валюта, расход и баланс с точными суммами (оплачено/бонусы), запросы, токены
  cache hit / cache miss / output, разбивка по моделям и время обновления.

Отдельный API key **не нужен**: используется тот же grant DSH
(`deepseek-account-platform/default` из `%DSH_HOME%\.credentials.yaml`), запросы идут с
заголовком `x-dsh-auth-token`. Токен читается только в память на время запроса, не
попадает в `settings.json`, логи и tooltip; файл credentials открывается только на чтение
и никогда не изменяется.

### Настройки

```json
"DeepSeekAccount": {
  "Enabled": true,
  "PlatformOrigin": "https://platform.deepseek.com",
  "CredentialsPath": "",
  "TokenEnvironmentVariable": "DEEPSEEK_PLATFORM_TOKEN",
  "RefreshSeconds": 300,
  "LowBalanceWarning": 1.0,
  "LowBalanceCritical": 0.1,
  "TotalBudget": 0
}
```

| Поле | Смысл |
|---|---|
| `Enabled` | `false` — бюджет считается локально по `tokens.db` (как раньше), сеть не используется |
| `PlatformOrigin` | база Platform API; только `https` (http допустим лишь для loopback) |
| `CredentialsPath` | путь к `.credentials.yaml`; пусто = `%DSH_HOME%\.credentials.yaml` |
| `TokenEnvironmentVariable` | env-переменная с токеном — приоритетнее файла |
| `RefreshSeconds` | TTL снимка, 60…3600 (значения меньше поднимаются до 60) |
| `LowBalanceWarning` / `LowBalanceCritical` | пороги «заканчивается» / «критично» по остатку |
| `TotalBudget` | «всего было» вручную; `0` — баланс + расход по данным платформы |

UI обновляется каждые 5 с, но сетевые запросы — не чаще `RefreshSeconds`; кнопка
«Обновить» обходит TTL, но не чаще раза в 60 с. Параллельных запросов нет: второй
вызывающий ждёт тот же запрос. При сетевой ошибке до 15 минут показывается последний
успешный снимок с пометкой «устарело»; при `40002`/`40003` — сразу «сессия DeepSeek
истекла — войдите в DeepSeek Harness» (grant не трогается).

### Приёмка

```powershell
dotnet run --project DeepSeekSetup\tools\AccountCheck            # живой запрос: расход/баланс/токены
dotnet run --project DeepSeekSetup\tools\AccountCheck -- --twice  # кэш: 3 HTTP-запроса, повтор из кэша
dotnet run --project DeepSeekSetup\tools\AccountCheck -- --self-test   # фикстуры, credentials, шапка
dotnet run --project DeepSeekSetup\tools\AccountCheck -- --budget      # офлайн-предпросмотр шапки
```

Exit code `0` — успех/все проверки прошли. `--direct` игнорирует прокси из `settings.json`,
`--origin`/`--credentials` подменяют базу API и файл grant'а.

Если живой запрос падает с `Network`/`The SSL connection could not be established`:

- проверьте `ProxySettings` в `%APPDATA%\TokenMonitor\settings.json` — при `407` прокси
  требует верные креды (для проверки запустите `AccountCheck --direct`);
- в песочнице/ограниченной учётной записи Windows без доступа к хранилищу сертификатов
  TLS не работает вообще (даже `https://example.com`) — запускайте `AccountCheck` из
  обычного PowerShell.

### Отличия от ТЗ

Пункт `Show` (`Spend`/`Balance`) из §5 ТЗ не вводился: по решению для шапки выводятся
оба числа сразу — потрачено и «всего было», — а остаток показывается мелкой строкой.
Остальное соответствует ТЗ; `DeepSeekStore`, `BudgetUiModel` и `IDataProvider` не менялись.

## С чего начать

1. Прочитать `PLAN.md` §1–4 (цель и архитектура) и §13 (что уже реализовано).
2. Установить плагин и открыть источник DeepSeek в TokenMonitor — см. «Установка и настройка».
3. При необходимости направить трафик DSH через локальный прокси — см. выше.
4. Реальный usage аккаунта в шапке — реализовано, см. «Реальный usage аккаунта DeepSeek»;
   спецификация и остаток работ (P2) — [`TZ_ACCOUNT_USAGE.md`](TZ_ACCOUNT_USAGE.md).
5. Открытые вопросы и опциональный этап P4 — `PLAN.md` §11 и §13.

## Что уже проверено

- `schema.sql` выполнен на реальном SQLite **3.53.1**: все 14 таблиц и 7 представлений
  создаются, `PRAGMA integrity_check = ok`.
- Представления `v_*` прогнаны на тестовых строках (сессия с `subagent`, ход, 2 шага,
  2 сэмпла usage, вызов инструмента): суммы, кэш-процент (99.5%), контекст (96 709 =
  input + cacheRead), дневная агрегация и тайминги инструментов считаются корректно.

## Статус

- [x] Изучены проект TokenMonitor и назначение окна
- [x] Изучены API DSH установленной сборки `0.2.2-rc.3`
- [x] Схема БД (проверена исполнением) и план
- [x] **P0.** Проверен `node:sqlite` в рантайме DSH (Node 24.21 / SQLite 3.53.4), сняты
      фактические payload'ы событий из живого журнала, форматы `assistant/message`, `tool/*`,
      `llm/retry`, причины `turn/end`.
- [x] **P1.** Плагин собирается и устанавливается как бандл, live-индексация пишет
      `sessions/turns/steps/attempts/retries/usage_samples/context_samples/tool_calls`.
- [x] **P2.** Backfill через `ctx.sessionPersistence` (3 сессии импортированы при старте),
      курсоры, `ingest_runs`, идемпотентность (тест + повторный прогон), retention-настройка.
- [x] **P3.** TokenMonitor: `DataSource.DeepSeek`, `DeepSeekStore`, `DeepSeekDataProvider`,
      фабрика, бюджет-токены, алерты по % окна; `dotnet build` зелёный.
- [x] **P4.** Бюджет аккаунта (`TZ_ACCOUNT_USAGE` P1.1–P1.5): `DeepSeekCredentials`,
      `DeepSeekAccountUsage`/парсеры, `DeepSeekAccountClient`, `DeepSeekAccountSettings`,
      `Fmt.Money`, `AccountCheck` + фикстуры. Живой прогон: 17.11.2026 —
      `$1,04 / $10,00`, осталось `$8,96`, 472 запроса, cache hit 97 541 248;
      `AccountCheck --self-test` — все проверки прошли.
- [ ] **P5 (по желанию).** `TryLaunch`/«Открыть папку сессии», раскладка `compaction/*`
      в таблицу `compactions`, `projected_tokens` из `ctx.tokenMeter`; точный «сегодня» по
      локальной таймзоне через `usage/export` (P2 в ТЗ).

## Как проверено

| Проверка | Результат |
|---|---|
| `node --test plugin/test/fold.test.mjs` | 4/4: usage и агрегаты, идемпотентность, `capture=usage`, `raw_events` |
| `node tools/verify-real.mjs <журнал> <проекция>` | Все суммы шага сошлись с `tokenUsage.totals` проекции DSH до токена (54 шага, 5 476 201 токен), `usage_samples` — 53 |
| Живая установка бандла | `ingest_runs`: live + startup-backfill, 0 ошибок; новая сессия и её шаги появляются в БД без ручных действий |
| `dotnet run --project tools/StoreCheck` | 3 сессии, 182 шага, живая лента, бюджет `22.35M · кэш 98,8% · контекст 20,2% окна`; отсутствующая БД → «нет данных» без исключения |
| Запуск `TokenMonitor.exe` с `LastSource=DeepSeek` | 10 с без ошибок в `tokenmonitor-*.log` |
| `AccountCheck --self-test` | 30 проверок: разбор фикстур, коды 40003/40001/неизвестная схема, выбор grant'а (default/чужой issuer/ambiguous), формат шапки `$0,92 / $10,00`, stale и низкий баланс |
| `AccountCheck` на живом аккаунте (17.11.2026) | `$1,0364574` за ноябрь, баланс `$8,9590426`, всего было `$10,00`, 472 запроса, 97 541 248 cache hit / 470 170 miss / 358 034 output |
| `AccountCheck --twice` | 3 HTTP-запроса на первый вызов, второй — из кэша (0 новых) |
