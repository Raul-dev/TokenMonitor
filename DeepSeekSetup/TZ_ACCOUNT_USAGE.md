# ТЗ: реальный usage аккаунта DeepSeek в верхней полосе TokenMonitor

**Версия:** 1.0 · **Дата:** 2026-11-17
**Сборка DSH, на которой проверялось:** `0.2.2-rc.3`, профиль `desktop`
**Проект:** TokenMonitor (C#/WPF, `net10.0-windows`)
**Статус:** готово к реализации в новой сессии (P1).

---

## 1. Цель

В верхней полосе окна TokenMonitor (блок «бюджет»: `BudgetText` / `BudgetDetail` /
tooltip) для источника **DeepSeek** показывать **реальные данные аккаунта
platform.deepseek.com**, а не только локальный подсчёт по `tokens.db`:

- расход за текущий месяц в валюте аккаунта;
- баланс (оплаченный + бонусный) и признак доступности;
- количество запросов и токены (cache hit / cache miss / output);
- разбивку по моделям (в tooltip);
- время последнего успешного обновления и признак устаревания.

Данные забираются polling-ом приватных Platform-эндпоинтов через уже существующий
grant DeepSeek-аккаунта из DSH. Запись в аккаунт и в credentials DSH не выполняется.

## 2. Что не входит в P1

- Изменения в DSH и его плагинах; запись в `~/.dsh/.credentials.yaml`.
- Денежная оценка по прайсу и прогнозы; учёт лимитов/квот (их нет в API).
- История прошлых месяцев, графики, per-API-key и точный «сегодня» по локальной
  таймзон (это P2, см. §11).
- Переделка 4 KPI-карточек («за месяц/день/сессию/запрос») — они остаются
  локальными из `tokens.db`; в P1 меняется только блок бюджета в шапке.
- Другие источники (OpenCode/Cursor/Devin) — их бюджеты не трогаем.

## 3. Проверенные факты (не предположения)

Проверено 2026-11-17 запросами к реальному аккаунту этой машины; токен нигде не
печатался, ответы ниже — в сокращённом виде.

### 3.1 Где лежит доступ

Файл `%USERPROFILE%\.dsh\.credentials.yaml` (учитывать `DSH_HOME`; формат — YAML):

```yaml
version: 1
records:
  client-connection/browser-session:
    kind: grant
    payload:
      version: 1
      secret: <redacted>          # НЕ подходит для Platform API (см. 3.3)
  deepseek-account-platform/device:
    kind: grant
    payload:
      id: <redacted>
  deepseek-account-platform/default:
    kind: grant
    payload:
      version: 1
      token: <redacted>           # 64 символа — нужный grant
      issuer: https://platform.deepseek.com
```

Правила выбора:

- приоритет — запись `deepseek-account-platform/default`;
- иначе — единственная запись, у которой есть `payload.token` и
  `payload.issuer` совпадает с `PlatformOrigin` (нормализовать trailing `/`);
- `payload.token` пустой/нет записи → «нет входа в DeepSeek»;
- формат файла — внутренний для DSH. Парсить защитно: неизвестная `version`,
  отсутствие полей или несколько подходящих записей → понятная ошибка, не исключение.
- По документации DSH (`dsh-deepseek-account`) grant не имеет срока/refresh;
  отзыв проявляется кодом `40003` (см. 3.4).

Проверено: секрет `client-connection/browser-session` **не** является API-ключом —
`GET https://api.deepseek.com/user/balance` с ним вернул HTTP 401
(`Authentication Fails ... invalid`). Не использовать его для аккаунтных запросов.

### 3.2 Обязательный заголовок

Для grant из DSH работает **только** `x-dsh-auth-token`:

```http
GET /api/v0/users/get_user_summary HTTP/1.1
Host: platform.deepseek.com
Accept: application/json
x-dsh-auth-token: <token>
x-client-platform: web
Referer: https://platform.deepseek.com/usage
```

Проверено: тот же запрос с `Authorization: Bearer <token>` возвращает
`{"code":40003,"msg":"Authorization Failed (invalid token)"}`. `x-client-platform`,
`Referer`, `User-Agent` не обязательны, но допустимы; UA можно не подменять.

### 3.3 Эндпоинты и схемы (все — `https://platform.deepseek.com`)

Общий конверт у всех: HTTP 200 + `{ "code": 0, "msg": "", "data": { "biz_code": 0,
"biz_msg": "", "biz_data": ... } }`. Проверять нужно **и `code`, и `data.biz_code`**.

#### A. Баланс и суммарные расходы

`GET /api/v0/users/get_user_summary`

```json
{"code":0,"msg":"","data":{"biz_code":0,"biz_msg":"","biz_data":{
  "normal_wallets":[{"currency":"USD","balance":"9.0830245000000000","token_estimation":"0"}],
  "bonus_wallets":[{"currency":"USD","balance":"0","token_estimation":"0"}],
  "total_costs":[{"currency":"USD","amount":"0.9169755000000000"}]
}}}
```

- `normal_wallets` — оплаченный баланс, `bonus_wallets` — бонусный; суммы — **строки**,
  возможна экспонента (`0E-16`). Парсить `decimal` с `InvariantCulture`.
- `total_costs` — суммарные расходы по сведениям платформы. **Семантика периода
  не подтверждена** (на тестовом аккаунте история пуста, значение совпало с расходом
  за текущий месяц). Для «расхода за месяц» использовать не его, а `usage/cost` (§C).
- Балансы могут быть в нескольких валютах; **не суммировать разные валюты**.

#### B. Токены и запросы за месяц

`GET /api/v0/usage/amount?month=<1-12>&year=<YYYY>`

```json
{"code":0,"msg":"","data":{"biz_code":0,"biz_msg":"","biz_data":{
  "total":[{"model":"deepseek-flash","usage":[
    {"type":"PROMPT_TOKEN","amount":"0"},
    {"type":"PROMPT_CACHE_HIT_TOKEN","amount":"80044800"},
    {"type":"PROMPT_CACHE_MISS_TOKEN","amount":"370905"},
    {"type":"RESPONSE_TOKEN","amount":"271196"},
    {"type":"REQUEST","amount":"372"}]}],
  "days":[{"date":"2026-11-01","data":[{"model":"deepseek-flash","usage":[ ... ]}]}]
}}}
```

- `days[].date` — **UTC-дата** (`YYYY-MM-DD`); в месяце ровно число дней месяца,
  включая нулевые.
- `amount` — целые числа строками.
- `REQUEST` — число запросов, остальные типы — токены.

#### C. Расход за месяц (деньги)

`GET /api/v0/usage/cost?month=<1-12>&year=<YYYY>`

```json
{"code":0,"msg":"","data":{"biz_code":0,"biz_msg":"","biz_data":[{
  "total":[{"model":"deepseek-flash","usage":[
    {"type":"PROMPT_TOKEN","amount":"0"},
    {"type":"PROMPT_CACHE_HIT_TOKEN","amount":"0.4802688000000000"},
    {"type":"PROMPT_CACHE_MISS_TOKEN","amount":"0.1112715000000000"},
    {"type":"RESPONSE_TOKEN","amount":"0.3254352000000000"},
    {"type":"REQUEST","amount":"0"}]}],
  "days":[ ... ],
  "currency":"USD"
}]}}
```

- `biz_data` — **массив**, первый элемент содержит `total`, `days`, `currency`.
- Сумма всех `usage`, кроме `REQUEST`, = расход за месяц
  (проверено: `0.4802688 + 0.1112715 + 0.3254352 = 0.9169755` — совпало с
  `total_costs` из §A).
- За октябрь/сентябрь 2026 вернулись нули — данные за прошлые месяцы доступны.

#### D. Не использовать в P1

- `GET /api/v0/usage/export?start=<unix>&end=<unix>&tz=<sec>` — отдаёт **ZIP с CSV**
  (колонки `utc_date,type,amount,price,model,api_key_name`); нужен для точного
  «сегодня» в локальной таймзоне. Требует `tz` и выравнивания по часам; тяжёлый
  (лимиты: 64 МБ, распаковка). P2.
- `GET /api/v0/usage/by_api_key/amount|cost` — требует другие параметры
  (в проверке вернул `INVALID_PARAM`); per-key — P2.

#### E. Официальный документированный API (резерв)

`GET https://api.deepseek.com/user/balance` с `Authorization: Bearer <API key>` —
единственный публичный эндпоинт: `is_available`, `balance_infos[]`
(`currency`, `total_balance`, `granted_balance`, `topped_up_balance`), **без usage**.
Может быть опцией, если у пользователя есть валидный platform API key
(текущий сохранённый секрет не подходит, см. 3.1).

### 3.4 Ошибки и коды

| Ситуация | Признак |
|---|---|
| Успех | `code == 0` и `data.biz_code == 0` |
| Токен отозван/неверен | `code` или `data.biz_code` ∈ {40002, 40003}; также HTTP 401/403 у официального API |
| Неверный месяц/год | `data.biz_code == 40001` (`invalid month or year`) |
| Неверные параметры | HTTP 422 (FastAPI) — ошибка разработчика |
| `INVALID_PARAM` | `biz_code == 1` (например, неполные параметры export) |
| Запрос без параметров | HTTP 422 |

При `40002/40003` **не удалять** grant DSH и не менять `credentials.yaml` — только
показать «сессия DeepSeek истекла, войдите в DeepSeek Harness».

### 3.5 Прокси и сеть

- Использовать `GlobalRouteHttpClient.CreateClient(TimeSpan.FromSeconds(15))` —
  он применяет настройки `ProxySettings`/`UpstreamUrl` TokenMonitor и общий
  `BypassList`.
- Перед запросом — `GlobalRouteHttpClient.CanAccess(uri)`; если внешний доступ
  запрещён (OnlyByProxy без прокси), вернуть бюджет с причиной, без исключения.
- Проверено: текущий upstream-прокси в `settings.json` отдаёт HTTP 200 для
  `platform.deepseek.com`, прямой доступ тоже работает.
- Таймаут 15 с, отмена по `CancellationToken` при закрытии окна.

## 4. Архитектура

```
DeepSeekDataProvider.GetBudgetAsync(force)
   ├─ (P1) DeepSeekAccountClient.GetAsync(force)
   │       ├─ DeepSeekCredentials.ReadPlatformToken()   ~/.dsh/.credentials.yaml
   │       ├─ GET /api/v0/users/get_user_summary        (баланс)
   │       ├─ GET /api/v0/usage/amount?month&year       (токены/запросы)
   │       └─ GET /api/v0/usage/cost?month&year         (расход, per-model)
   │       → DeepSeekAccountUsage (кэш + stale-on-error)
   │       → BudgetUiModel (Spend/Balance)
   └─ fallback: DeepSeekStore.LoadBudgetStats() → прежний бюджет в токенах
```

Новые файлы (предлагаемые):

| Файл | Содержимое |
|---|---|
| `TokenMonitor/DeepSeekAccountSettings.cs` | настройки секции `DeepSeekAccount` + нормализация |
| `TokenMonitor/DeepSeekCredentials.cs` | чтение YAML, выбор grant, валидация issuer/token |
| `TokenMonitor/DeepSeekAccountClient.cs` | HTTP, DTO, разбор конверта, кэш, ошибки |
| `TokenMonitor/DeepSeekAccountUsage.cs` | доменная модель + `ParseSummary/ParseAmount/ParseCost` (чистые функции для тестов) |
| `DeepSeekSetup/tools/AccountCheck/` | консольная приёмка: печатает санитизированную сводку (без токена) |

Изменяемые файлы:

| Файл | Что |
|---|---|
| `TokenMonitor/AppSettings.cs` | свойство `DeepSeekAccount`, `SettingsStore.Normalize`, `ApplyLegacy` (`DeepSeekPlatformTokenEnv`, `DeepSeekAccountRefreshSeconds`) |
| `TokenMonitor/DataProviders/DeepSeekDataProvider.cs` | `GetBudgetAsync`: account → fallback; кэш обоих уровней |
| `TokenMonitor/Fmt.cs` | `Money(decimal, currency)` — `$`, `¥`, иначе код валюты |
| `TokenMonitor/TokenMonitor.csproj` | `PackageReference` на `YamlDotNet` (или минимальный парсер, см. §11) |
| `DeepSeekSetup/README.md` | раздел «Реальный usage аккаунта», как включить/выключить |
| `README.md` (корень) | короткая строка в разделе DeepSeek |

`DeepSeekStore`, `BudgetUiModel`, `IDataProvider` — без изменений.

## 5. Настройки

Добавить в `settings.json` (создаётся/нормализуется автоматически; старые файлы
не ломаются):

```json
"DeepSeekAccount": {
  "Enabled": true,
  "PlatformOrigin": "https://platform.deepseek.com",
  "CredentialsPath": "",
  "TokenEnvironmentVariable": "DEEPSEEK_PLATFORM_TOKEN",
  "RefreshSeconds": 300,
  "Show": "Spend",
  "LowBalanceWarning": 1.0,
  "LowBalanceCritical": 0.1
}
```

| Поле | Смысл | Default |
|---|---|---|
| `Enabled` | polling аккаунта; `false` → прежний бюджет в токенах | `true` |
| `PlatformOrigin` | база Platform API; только `https` (http — лишь loopback при отдельном флаге, как в DSH) | `https://platform.deepseek.com` |
| `CredentialsPath` | пусто → `%DSH_HOME%\.credentials.yaml` | `""` |
| `TokenEnvironmentVariable` | env с токеном имеет приоритет над файлом | `DEEPSEEK_PLATFORM_TOKEN` |
| `RefreshSeconds` | TTL успешного снимка; минимум 60, максимум 3600 | `300` |
| `Show` | `Spend` (расход за месяц) или `Balance` (баланс) — что в `BudgetText` | `Spend` |
| `LowBalanceWarning/Critical` | порог severity по остатку в валюте (для `Spend` — по балансу) | `1.0` / `0.1` |

Правила: в `settings.json` **никогда** не хранить токен; только путь/env-имя.
`RefreshSeconds < 60` поднимать до 60 (защита от rate-limit приватного API).

## 6. Доменная модель и маппинг в UI

```csharp
sealed record DeepSeekAccountUsage(
    string Currency,               // USD, CNY …
    decimal MonthCost,             // сумма usage/cost, кроме REQUEST
    decimal BalancePaid,           // normal_wallets
    decimal BalanceBonus,          // bonus_wallets
    bool IsAvailable,              // баланс достаточен (эвристика: total > 0)
    long Requests,                 // REQUEST за месяц
    long TokensCacheHit,
    long TokensCacheMiss,
    long TokensOutput,
    long TokensPrompt,             // PROMPT_TOKEN (обычно 0)
    IReadOnlyList<ModelUsage> Models, // model, tokens, cost
    DateTimeOffset FetchedAt,
    string Source);                // "platform"
```

Правила расчёта:

- `MonthCost` = сумма всех `usage.amount` из `usage/cost`, где `type != REQUEST`;
- токены = `CACHE_HIT + CACHE_MISS + RESPONSE`; если все три нулевые, а
  `PROMPT_TOKEN > 0` — использовать `PROMPT_TOKEN` (не складывать оба варианта);
- `Requests` = `REQUEST`;
- баланс = `normal + bonus` **одной** валюты; при нескольких валютах выбрать USD,
  остальные — в tooltip; кост-валюта из `biz_data[0].currency`;
- суммы — `decimal`; для отображения 2 знака (`$0,92`), для tooltip — точное
  значение строкой, как пришло (до 16 знаков).

### Маппинг в `BudgetUiModel`

**Режим `Show = "Spend"` (по умолчанию):**

- `Text` = `Fmt.Money(MonthCost, Currency)` → `$0,92`;
- `Detail` = `аккаунт · баланс $9,08 · 372 запр.` (обрезается существующим
  `Compact`/`MaxWidth`);
- `Severity`:
  - `Critical`, если баланс <= `LowBalanceCritical` или `IsAvailable == false`;
  - `Warning`, если баланс <= `LowBalanceWarning`;
  - иначе `Normal`;
  - ошибка аккаунта и нет stale-снимка → `Muted`.
- `Tooltip` (многострочный, перенос уже реализован):
  ```
  аккаунт DeepSeek · platform.deepseek.com
  расход за ноябрь 2026: $0,9169755
  баланс: $9,0830245 (оплачено $9,0830245, бонусы $0,00)
  запросов за месяц: 372
  токены: cache hit 80 044 800 · cache miss 370 905 · output 271 196
  deepseek-flash: 80 687 000 токенов · $0,9169755
  обновлено: 17.11.2026 16:05:12
  ```

**Режим `Show = "Balance"`:** `Text` = баланс, `Detail` = `расход $0,92 · 372 запр.`
(тот же tooltip).

**Fallback:** если `Enabled=false`, нет grant, `40003`, сеть/прокси, malformed-ответ
и нет stale-снимка — используется прежний локальный бюджет из `tokens.db`:
`Text` = токены месяца, `Detail` = `локально · аккаунт: <краткая причина>`, tooltip —
полная причина + локальные токены. Никогда не показывать `$0` как «нет расхода»,
если данные не получены.

## 7. Polling, кэш, ошибки

- UI-таймер вызывает `GetBudgetAsync` каждые 5 с (и по кнопке «Обновить»), но
  сетевые запросы — не чаще `RefreshSeconds` (300 с).
- `force: true` (кнопка) — обойти TTL, но не чаще 60 с и без параллельных запросов.
- In-flight dedup: один запрос на клиент; второй вызывающий ждёт тот же Task.
- Stale-on-error: при ошибке сети/5xx сохранять последний успешный снимок до 15 мин,
  показывать его с пометкой `устарело (N мин)` в tooltip; после — fallback.
- Ошибки `40002/40003` — не считать stale: показывать «сессия истекла» сразу.
- Все исключения ловятся в `GetBudgetAsync`; UI не падает (как сейчас).
- Логи: только endpoint path/HTTP status/код, **без токена, заголовков и тел**.
- При смене месяца (локальная дата 1-е) — принудительно обновить снимок.
- Отмена: `CancellationToken` из провайдера; запросы прерываются при закрытии окна.

## 8. Безопасность и приватность

- Токен читается из файла только на время запроса, хранится в памяти, не пишется
  в настройки/БД/логи/UI, не попадает в tooltip.
- Файл credentials открывается read-only, без блокировки на запись; ошибки доступа
  → «нет доступа к credentials DSH».
- Это тот же аккаунт и токен, которым уже пользуется DSH; новых прав не требуется.
- В README добавить предупреждение: включение polling отправляет запросы к
  `platform.deepseek.com` с grant DSH; отключается `DeepSeekAccount.Enabled=false`.
- Приватные эндпоинты не документированы и могут измениться — при неожиданной схеме
  показывать «API аккаунта изменился», не пытаться угадать поля.

## 9. Этапы

| Этап | Содержание | DoD |
|---|---|---|
| **P1.1** | `DeepSeekCredentials` + `AccountCheck` + тест на redacted YAML | находит token/issuer, не печатает токен |
| **P1.2** | `DeepSeekAccountUsage.Parse*` + golden fixtures (реальные ответы, санитизированы) | суммы/валюты/ошибки 40003/40001 покрыты |
| **P1.3** | `DeepSeekAccountClient` (HTTP, TTL, dedup, stale, proxy) | `AccountCheck` печатает расход/баланс/токены с реального аккаунта |
| **P1.4** | `DeepSeekAccountSettings` + `GetBudgetAsync` + `Fmt.Money` | шапка показывает `$0,92 · баланс $9,08 · 372 запр.` |
| **P1.5** | Fallback, ошибки, документация, приёмка | сценарии §10 зелёные, `dotnet build` 0 ошибок |

Оценка: ~1–1,5 рабочих дня.

## 10. Приёмка

1. **Успех:** при активном входе в DSH шапка показывает расход за месяц в валюте
   аккаунта; tooltip — баланс, запросы, токены, per-model, время.
   Ожидаемые значения на текущем аккаунте: `$0,9169755`, баланс `$9,0830245`,
   372 запроса, 80 044 800 cache hit / 370 905 cache miss / 271 196 output.
2. **Кэш:** за 5 минут обновлений UI счётчик сетевых вызовов — не больше 1 на
   эндпоинт (проверить логом/счётчиком в `AccountCheck`).
3. **Кнопка «Обновить»:** обходит TTL, но не создаёт параллельных запросов.
4. **Нет входа в DSH** (нет файла/записи): `Muted` + «нет входа в DeepSeek»,
   локальный fallback работает.
5. **Отозванный токен** (подменить на невалидный в тесте): «сессия истекла»,
   credentials DSH не изменены.
6. **Прокси недоступен** (`ProxySettings` сломан): ошибка/устаревший снимок,
   приложение живо.
7. **CNY-аккаунт:** отображается `¥`, доллары не подмешиваются.
8. **Месяц/год:** запросы идут с локальными `month/year`; 1-го числа снимок
   обновляется принудительно.
9. **Приватность:** `Select-String` по логам и `settings.json` не находит токен;
   в tooltip только суммы.
10. `dotnet build` — 0 ошибок; `DeepSeekSetup/tools/AccountCheck` — exit 0.

## 11. Открытые вопросы и риски

1. **Семантика `total_costs`** (месяц/всё время) не подтверждена — P1 не использует
   его для расхода, только `usage/cost`; при желании уточнить на аккаунте с историей.
2. **Границы месяца UTC vs локальные.** `days[].date` — UTC; для шапки это
   несущественно (месяц), для точного «сегодня» нужен `usage/export` (P2) либо
   явная пометка «UTC».
3. **Приватные API** (usage/*) не документированы, могут меняться. Митигация:
   defensive parsing, сообщение «API изменился», быстрый откат `Enabled=false`.
4. **Формат `credentials.yaml`** — внутренний для DSH. Митигация: отдельный парсер
   с валидацией, путь настраиваемый; при смене формата — понятная ошибка.
   Альтернатива на будущее: маленький DSH-плагин-bridge (как `dsh-deepseek-usage`),
   отдающий usage на loopback; тогда TokenMonitor не читает credentials.
5. **YAML-парсер:** рекомендуется `YamlDotNet`; допустим минимальный парсер под
   известную структуру, если не хочется новой зависимости (тогда — фикстуры и
   строгая валидация обязательны).
6. **Режим отображения:** `Spend` (расход) vs `Balance` (остаток) по умолчанию.
   В ТЗ принят `Spend` (запрос был про «расходы»), переключается настройкой.
7. **Мультивалютность:** правило «USD приоритетно» — подтвердить, если аккаунт
   в CNY (у большинства аккаунтов DeepSeek — CNY).
8. **Официальный API-ключ** как второй источник: можно добавить `Show=OfficialBalance`,
   но он даёт только баланс и требует валидный ключ; в P1 не нужен.

## 12. Источники (проверяемые)

- Официальный баланс: https://api-docs.deepseek.com/api/get-user-balance/ —
  `GET /user/balance`, Bearer API key (только баланс).
- Приватные эндпоинты и их назначение: CodexBar, `docs/deepseek.md` —
  `get_user_summary`, `usage/by_api_key/amount|cost`, `usage/amount|cost`,
  `usage/export`, заголовки `Authorization: Bearer <platform userToken>` (в нашем
  случае — `x-dsh-auth-token` с grant DSH).
- Реализация на плагине DSH с теми же эндпоинтами: `AzureHalcyon/dsh-deepseek-usage`
  (`usage/export` → ZIP CSV, `today` по локальной таймзоне).
- Установленная сборка DSH: `@deepseek-ai/dsh-deepseek-account/README.md`
  (grant, `get_user_summary`, `x-dsh-auth-token`, `403/40003`, отсутствие refresh).
- Проверки на этой машине: 2026-11-17, аккаунт `platform.deepseek.com`,
  расход `$0,9169755`, баланс `$9,0830245`, 372 запроса.

---

## Чек-лист исполнителю

- [ ] Прочитать §3 и не изобретать заголовки: только `x-dsh-auth-token`.
- [ ] `DeepSeekCredentials`: выбор `deepseek-account-platform/default`, проверка
      issuer, никакого логирования токена.
- [ ] `DeepSeekAccountUsage.Parse*`: `decimal`, строки, конверт `code`/`biz_code`,
      коды 40002/40003/40001.
- [ ] `DeepSeekAccountClient`: `GlobalRouteHttpClient`, `CanAccess`, таймаут 15 с,
      TTL 300 с, in-flight dedup, stale 15 мин, отмена.
- [ ] `GetBudgetAsync`: account → fallback `LoadBudgetStats`; `Fmt.Money`.
- [ ] `AccountCheck` + фикстуры; приёмка §10; `dotnet build` 0 ошибок.
- [ ] Документация: `DeepSeekSetup/README.md` и корневой `README.md`.
