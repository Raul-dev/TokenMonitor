# Token Monitor

WPF-приложение для контроля расхода токенов opencode: за месяц, за день, за сессию, за запрос.
Дерево «сессия → запрос → шаг» раскрывается кликом, справа — живая лента последних запросов.

## Быстрая установка

1. Установите .NET 10 SDK (для готового exe достаточно .NET Desktop Runtime 10) и запустите
   `.\run.ps1` или `dotnet run`.
2. При первом запуске создаётся `%APPDATA%\TokenMonitor\settings.json`; откройте его по ссылке
   **конфиг** в верхней полосе.
3. Подключите бюджет LiteLLM и локальный домен:

```json
{
  "LiteLLMBaseUrl": "https://litellm.mydomain.loc",
  "LiteLLMApiKey": "",
  "ProxySettings": {
    "BypassList": ["litellm.mydomain.loc"]
  }
}
```

`LiteLLMBaseUrl` — адрес LiteLLM (можно с `/v1`); `BypassList` — домены, которые не должны
идти через внешний прокси. Замените `litellm.mydomain.loc` на ваш локальный домен LiteLLM.
Если внешний прокси не нужен, `ProxySettings` можно не указывать; для локального прокси
см. следующий раздел.

Ключ обычно подхватывается из конфига OpenCode; при необходимости задайте `LiteLLMApiKey`,
`MYDOMAIN_API_KEY` или `LITELLM_API_KEY`.

Если в OpenCode подключён OpenCode Go (ключ `oc_sk_...`) или аккаунт OpenCode Console,
бюджет подхватится автоматически (режим `Auto`), а LiteLLM останется запасным источником.
Подробнее — в разделе [«Бюджет и настройки»](#бюджет-и-настройки).

4. Установите плагины OpenCode (OpenCode должен хотя бы раз запускаться) и перезапустите его:

```powershell
.\install.ps1
```

Бюджет появится в верхней полосе, история и живая лента — из данных OpenCode.

## Прокси для OpenCode

Локальный прокси нужен, если требуется видеть HTTP-трафик OpenCode или направить его через
внешний прокси. Для подсчёта токенов и бюджета он не обязателен.

1. В `%APPDATA%\TokenMonitor\settings.json` включите локальный сервер:

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

2. Настройте плагин OpenCode `%USERPROFILE%\.config\opencode\token-monitor-proxy.json`
(создаётся `install.ps1`, если файла нет):

```json
{
  "enabled": true,
  "proxyUrl": "http://127.0.0.1:8181",
  "disableDirectFallback": false
}
```

`providers` — необязательный список id провайдеров OpenCode, для которых плагин
подставляет свой `fetch`. Если параметр не задан или пуст, `fetch` подставляется всем
провайдерам из `opencode.jsonc`. Это нужно для desktop-клиента: Electron/NodeService
игнорирует `HTTP_PROXY`/`HTTPS_PROXY`, поэтому плагин передаёт провайдеру `fetch`
с CONNECT-туннелем через `proxyUrl`. В CLI дополнительно работают переменные окружения.

`disableDirectFallback` — если `true`, при ошибке подключения к прокси или TLS-туннелю
плагин не делает прямое соединение, а завершает запрос ошибкой. По умолчанию `false`:
при недоступном TokenMonitor запросы идут напрямую, но не учитываются в статистике.

3. Перезапустите TokenMonitor, затем OpenCode.

Схема: `OpenCode → http://127.0.0.1:8181 (TokenMonitor) → напрямую или UpstreamUrl`.
Если меняете порт/IP, обновите одновременно `ProxySettings.ListenUrl` и `proxyUrl` плагина.

- `enabled: false` — плагин не подменяет `HTTP_PROXY`/`HTTPS_PROXY`;
- `TOKEN_MONITOR_PROXY_DISABLED=1` — принудительно отключить плагин;
- `TOKEN_MONITOR_PROXY_URL` — временно переопределить `proxyUrl`;
- `TOKEN_MONITOR_PROXY_PROVIDERS` — временно переопределить список `providers`;
- `TOKEN_MONITOR_PROXY_CONFIG` — путь к другому конфигу плагина;
- диагностика: `%USERPROFILE%\.local\share\opencode\token-proxy.log`.

Плагин направляет запросы провайдеров из `providers` и дополнительно выставляет
`HTTP_PROXY`/`HTTPS_PROXY` для Bun CLI. Служебные HTTP-клиенты OpenCode он не
перехватывает: например, `opencode models --refresh` может ходить напрямую.
`$schema` в `opencode.jsonc` — подсказка редактору, `baseURL` провайдера менять не нужно.
После старта в логе появляется строка `proxy fetch injected providers=...` — это признак,
что desktop-запросы будут идти через `proxyUrl`. Если TokenMonitor не запущен и
`disableDirectFallback` равен `false`, плагин откатывается на прямое соединение, но
счётчик трафика в этом случае не увеличится. При `disableDirectFallback: true` запрос
завершится ошибкой.

## Установка плагинов OpenCode

Плагины лежат в проекте:

- `TokenMonitor\OpenCodeSetup\token-stats.ts` — статистика токенов и живая лента;
- `TokenMonitor\OpenCodeSetup\proxy.ts` — направляет HTTP-трафик opencode через локальный прокси TokenMonitor.

Устанавливаются одной командой:

```powershell
.\install.ps1              # установить или обновить оба плагина
.\install.ps1 -Check       # только проверить состояние
.\install.ps1 -Uninstall -Force  # удалить оба плагина
```

Что делает скрипт:

1. Сравнивает SHA-256 файлов из `TokenMonitor\OpenCodeSetup` с установленными копиями
   `%USERPROFILE%\.config\opencode\plugins\token-stats.ts` и `proxy.ts`.
2. Если версии совпадают — ничего не меняет и сообщает «уже установлен».
3. Если плагин отсутствует — создаёт каталог `plugins` и копирует файл.
4. Если установлена другая версия — сохраняет резервную копию `<имя>.bak-<дата-время>` и перезаписывает.
5. После копирования проверяет хэш каждого скопированного файла.

Коды возврата `-Check`: `0` — оба установлены и совпадают, `2` — хотя бы один установлен,
но отличается по версии, `3` — хотя бы один не установлен. Каталог конфигурации можно
переопределить переменной `OPENCODE_CONFIG_DIR`.

После установки **перезапустите opencode** — плагины загружаются только при старте.
Признак работы: после первого запроса появляются
`%USERPROFILE%\.local\share\opencode\token-stats.log`, `token-events.jsonl` и `token-proxy.log`,
в приложении обновляется живая лента.

## Требования

- Windows
- .NET SDK 10 (проверено на 10.0.402); для запуска уже собранной версии достаточно .NET Desktop Runtime 10

## Запуск из PowerShell

```powershell
# самый простой способ — из исходников
dotnet run

# сборка Release и запуск exe
dotnet build -c Release
& ".\bin\Release\net10.0-windows\TokenMonitor.exe"

# либо через приложенный скрипт (сам соберёт, если нужно)
.\run.ps1
```

Текстовый отчёт без окна (удобно для консоли и проверки цифр):

```powershell
$exe = ".\bin\Debug\net10.0-windows\TokenMonitor.exe"
& $exe --dump                        # печатает отчёт в консоль (включая бюджет)
& $exe --dump=C:\Temp\tokens.txt     # пишет отчёт в файл
```

Сборка одним файлом:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

## Откуда берутся данные

Приложение только читает файлы, ничего не изменяет (SQLite открывается в режиме `ReadOnly`).

| Источник | Путь по умолчанию | Назначение |
| --- | --- | --- |
| База opencode | `%USERPROFILE%\.local\share\opencode\opencode.db` | история: сессии, сообщения, шаги, токены |
| Живая лента | `%USERPROFILE%\.local\share\opencode\token-events.jsonl` | по одной строке JSON на завершённый запрос (пишет плагин) |
| Резервный лог | `%USERPROFILE%\.local\share\opencode\token-stats.log` | человекочитаемый лог плагина; используется, если `token-events.jsonl` ещё нет |

Другой каталог можно указать переменной окружения:

```powershell
$env:OPENCODE_DATA_DIR = "D:\other\opencode-data"
```

Обновление — автоматически каждые 5 секунд (кнопка «Обновить» — принудительно).

### Схема базы opencode

- **opencode 1.x** (через LiteLLM): история в таблицах `session`, `message`, `part`.
  Запрос = сообщение пользователя + шаги ассистента с его `parentID`.
- **opencode 2.x** (официальный opencode.ai): история в `session_v2` и `session_message`
  (поле `type` = `user`/`assistant`/..., данные в JSON-колонке `data`).
  Запрос = user-сообщение + все assistant-сообщения до следующего user.

Приложение определяет схему автоматически. Для 2.x живая лента синтезируется из самой базы
(плагин `token-stats.ts` собран под SDK 1.x и с opencode 2.x не работает — `token-events.jsonl`
может отсутствовать или устареть, на счётчики это не влияет).

## Плагин статистики OpenCode

`token-stats.ts` читает историю из базы opencode, а «живую ленту» — из своего лога.
Эталонная копия лежит в проекте: `TokenMonitor\OpenCodeSetup\token-stats.ts`
(устанавливается скриптом `install.ps1`, см. раздел «Установка плагинов OpenCode»).
Рабочая копия: `%USERPROFILE%\.config\opencode\plugins\token-stats.ts` —
opencode подключает её автоматически из каталога `plugins` при старте.
Прокси-плагин описан в разделе «Прокси для OpenCode».

На событие `session.idle` плагин берёт последнее сообщение пользователя, суммирует все шаги
ассистента с его `parentID` и пишет:

- `token-events.jsonl` — машиночитаемая строка на запрос (источник для этого приложения):

```json
{"time":"2026-10-30T11:30:40.460Z","sessionId":"ses_ebbcae762ffe...","messageId":"msg_18c9...","model":"developer","provider":"mydomain.loc","steps":61,"input":85679038,"output":368717,"reasoning":1028494,"cacheRead":0,"cacheWrite":0,"billed":87066249,"context":169000,"durationMs":694000}
```

| Поле | Значение |
| --- | --- |
| `time` | момент завершения запроса (UTC, ISO 8601) |
| `sessionId` | id сессии opencode |
| `messageId` | id последнего шага ассистента |
| `model`, `provider` | модель и провайдер |
| `steps` | сколько шагов ассистента было в запросе |
| `input`, `output`, `reasoning` | токены по категориям |
| `cacheRead`, `cacheWrite` | токены из кэша / в кэш |
| `billed` | сумма всех категорий выше |
| `context` | контекст последнего шага (`input + cacheRead`) |
| `durationMs` | длительность запроса, мс |

- `token-stats.log` — то же в виде строки `... report <session> 8.41m tokens in 11m34s | ...`
  (и диагностика `plugin loaded` / `error`).

Плагин также отправляет тост, но десктопное приложение opencode их не отображает —
поэтому и сделано отдельное окно.

> После правки плагина нужен перезапуск opencode: набор плагинов читается при старте.
> До первого перезапуска приложение работает на резервном источнике `token-stats.log`.

## Что считается в окне

- **За месяц** — токены всех шагов ассистента с начала текущего календарного месяца (локальное время).
- **За день** — то же с начала текущих суток.
- **За сессию** — сумма по выбранной сессии (если ничего не выбрано — по последней активной).
- **За запрос** — выбранный запрос; запрос = сообщение пользователя + все шаги ассистента с его `parentID`.
- **Токены** = `input + output + reasoning + cacheRead + cacheWrite`; в колонках показаны те же категории
  (`Вход`, `Выход`, `Reasoning`, `Кэш`). Полные значения — в подсказке при наведении на строку.
- **Бюджет** (в центре верхней полосы) — расход и лимит из LiteLLM (`$47.76 / $60.00`)
  или из аккаунта OpenCode (проценты лимитов OpenCode Go и бюджеты OpenCode Console);
  ниже — остаток и время сброса лимита (UTC+3).

## Бюджет и настройки

Источник бюджета задаётся полем `BudgetSource`:

- `0` (`Auto`, по умолчанию) — сначала внешний API провайдера (сейчас OpenCode), при ошибке — LiteLLM;
- `1` (`LiteLLM`) — только LiteLLM;
- `2` (`ExternalAPI`) — только внешний API провайдера (сейчас OpenCode).

### LiteLLM

Цифры в верхней полосе берутся из LiteLLM: `GET <LiteLLMBaseUrl>/key/info`
(заголовок `Authorization: Bearer <ключ>`). Если в `LiteLLMBaseUrl` указан `/v1`, суффикс
отбрасывается автоматически. Ответ содержит `spend`, `max_budget`, `budget_reset_at`;
при лимите в сутки (`budget_duration: 1d`) остаток обнуляется в 00:00 UTC.

Ключ берётся по порядку:

1. поле `LiteLLMApiKey` в конфиге приложения;
2. переменные окружения `MYDOMAIN_API_KEY`, `LITELLM_API_KEY`;
3. поле `"apiKey"` провайдера из `OpenCode.ConfigPath`, у которого host в `baseURL` совпадает
   с host из `LiteLLMBaseUrl` (если совпадения нет — первый найденный ключ).

### Внешний API провайдера (OpenCode)

`ExternalAPI` — общий режим для запроса бюджета напрямую у API AI-провайдера. Сейчас реализован
OpenCode; для других провайдеров добавляется свой формат ответа и свой маппинг в верхнюю полосу.

Есть два независимых вида входа, и TokenMonitor использует оба:

1. **Ключ OpenCode Go** (подписка на модели). Хранится не в `account.json`, а в базе
   `%USERPROFILE%\.local\share\opencode\opencode.db`, таблица `credential`
   (`integration_id = 'opencode-go'`, поле `value` — JSON с `key`; активная запись `active = 1`).
   Проверить можно командой `opencode auth list` — там будет строка `OpenCode Go`.
   TokenMonitor читает ключ из базы и запрашивает
   `GET https://opencode.ai/zen/go/v1/usage` с `Authorization: Bearer <key>`.
   Ответ содержит проценты `rolling` (5 часов), `weekly` (неделя), `monthly` (месяц) —
   этого достаточно, чтобы показывать бюджет, даже если OpenCode Console не подключён.
2. **Аккаунт OpenCode Console** (организация и денежные бюджеты). Основной путь — локальная
   служба OpenCode: TokenMonitor читает `%USERPROFILE%\.local\state\opencode\service.json`
   (или `%XDG_STATE_HOME%\opencode\service.json`), берёт `url` и `password` и запрашивает
   `GET <url>/api/experimental/usage/limits` с Basic-авторизацией `opencode:<password>`.
   Ответ содержит те же лимиты Go (`rolling`, `weekly`, `monthly`) и бюджеты Console
   (`budgets`) в долларах. Если служба не запущена, используется прямой запрос по данным
   `account.json` / `auth.json` (`access`, `metadata.orgID`, `metadata.server`):
   `GET https://opencode.ai/zen/go/v1/usage` (заголовок `x-opencode-org-id`)
   и `GET https://opencode.ai/console/api/policies/current-budgets` (`x-org-id`).

Порядок опроса: локальная служба → прямой аккаунт Console → ключ OpenCode Go из базы.
Если `usage/limits` возвращает `data: null`, это значит, что Console-аккаунт не подключён;
TokenMonitor в этом случае не сдаётся, а берёт лимиты по ключу OpenCode Go.

Если есть бюджеты Console, в верхней полосе показываются доллары (самый близкий к исчерпанию
бюджет); иначе — процент месячного лимита OpenCode Go. Проценты всех трёх окон и время сброса
видны в подсказке.

TokenMonitor не обновляет OAuth-токен: если он истёк, в верхней полосе будет
`токен OpenCode истёк: запустите OpenCode, чтобы обновить вход`. Запустите OpenCode
(или войдите заново) и нажмите «Обновить». Файлы OpenCode читаются только на чтение.
Для ручного переопределения используйте `OpenCode.ApiKey` в секции провайдера
(можно вписать и ключ `oc_sk_...`) и `OpenCode.OrgId`.

Конфиг приложения (создаётся при первом запуске, открывается ссылкой **конфиг** в верхней полосе):

`%APPDATA%\TokenMonitor\settings.json`

```json
{
  "LiteLLMBaseUrl": "https://litellm.mydomain.loc/v1",
  "LiteLLMApiKey": "",
  "BudgetSource": 0,
  "BudgetRefreshSeconds": 60,
  "LastSource": 0,
  "LocalProxyServerEnabled": false,
  "ProxySettings": {
    "Enabled": false,
    "Url": "",
    "ListenUrl": "http://127.0.0.1:8181",
    "UpstreamUrl": "",
    "BypassList": ["litellm.mydomain.loc"]
  },
  "OpenCode": {
    "Name": "OpenCode",
    "ConfigPath": "C:\\Users\\<user>\\.config\\opencode\\opencode.jsonc",
    "IsEnabled": true,
    "ApiKey": "",
    "OrgId": ""
  },
  "Cursor": {
    "Name": "Cursor",
    "ConfigPath": "C:\\Users\\<user>\\.config\\cursor\\cursor.jsonc",
    "IsEnabled": true,
    "StateDbPath": "",
    "EventsPath": ""
  },
  "Devin": {
    "Name": "Devin",
    "ConfigPath": "C:\\Users\\<user>\\.config\\devin\\devin.jsonc",
    "IsEnabled": true,
    "ApiKey": "",
    "OrgId": ""
  }
}
```

- `LiteLLMBaseUrl` — адрес LiteLLM (можно заменить на другой инстанс).
- `LiteLLMApiKey` — пусто = брать из конфига OpenCode (обычный случай); можно вписать отдельный ключ.
- `BudgetSource` — `0` Auto, `1` LiteLLM, `2` ExternalAPI (см. выше).
- `OpenCode.ApiKey` / `OpenCode.OrgId` — необязательные переопределения: ключ OpenCode Go
  (`oc_sk_...`) или токен Console и организация; пусто = читать из базы `opencode.db`
  и `account.json` / `auth.json`.
- `OpenCode.ConfigPath` — откуда читать ключ, если `LiteLLMApiKey` пуст.
- `Cursor.StateDbPath` / `Cursor.EventsPath` — пути Cursor; пусто = значения по умолчанию.
- `Devin.ApiKey` / `Devin.OrgId` — доступ к Devin API v3.
- `BudgetRefreshSeconds` — как часто перезапрашивать бюджет (кнопка «Обновить» запрашивает сразу).
- `LocalProxyServerEnabled` — запускать локальный прокси TokenMonitor.
- `ProxySettings.ListenUrl` — адрес локального прокси; `UpstreamUrl` — внешний прокси для него
  (пусто = напрямую).
- `ProxySettings.Enabled` / `Url` — общий внешний прокси для запросов самого TokenMonitor;
  `BypassList` — домены, которые не должны идти через него. Добавьте туда локальный домен LiteLLM,
  чтобы запрос бюджета не уходил через прокси.

Старый плоский `settings.json` (`LitellmBaseUrl`, `ApiKey`, `ConfigOpenCode`, `DevinApiKey` и т.д.) подхватывается при загрузке; `OpenCodeApiKey` / `OpenCodeOrgId` переносятся в `OpenCode.ApiKey` / `OpenCode.OrgId`.

Если источник бюджета недоступен (LiteLLM или внешний API провайдера), в верхней полосе будет `—` с причиной,
остальные показатели работают. В режиме `Auto` ошибка внешнего API не мешает: TokenMonitor сразу
пробует LiteLLM и показывает его данные.

## Открытие сессии в opencode

Правый клик по сессии в дереве (или по её запросу/шагу):

- **Показать проект в окне OpenCode** — deep link `opencode://open-project?directory=<каталог>`.
  Приложение OpenCode одноэкземплярное, повторный запуск лишь фокусирует окно, поэтому извне
  можно открыть только проект (и его последнюю сессию) — deep link на конкретную сессию
  в 1.19.5 не поддерживается.
- **Открыть в терминале (эта сессия)** — новый консольный процесс `opencode -s <id>` с рабочим
  каталогом сессии: TUI загрузит историю этой сессии, даже если в приложении она давно закрыта.
- **Открыть в терминале копией (--fork)** — то же с `--fork`: создаётся копия сессии,
  оригинал не меняется.
- **Скопировать ID сессии** — в буфер обмена.

Сессии в opencode не «закрываются»: они остаются в базе, пока их не удалят через
`opencode session delete` (в нашей базе архивных нет). Пункты меню недоступны, если у сессии
нет существующего каталога на диске.

Нюансы: TUI и приложение OpenCode — два независимых процесса над одной базой. Если сессия
открыта в приложении, берите вариант `--fork`, иначе два клиента будут менять одну сессию;
сообщения, отправленные из TUI, могут появиться в окне приложения не сразу.

## Ограничения

- `cost` в базе opencode равен 0: расход в долларах ведёт LiteLLM или OpenCode Console,
  поэтому бюджет берётся из их API.
- `cache.read` / `cache.write` тоже приходят нулевыми — провайдер не сообщает метаданные кэша;
  приложение корректно покажет их, если они появятся.
- Шаги, у которых нет токенов (например, служебные сообщения), попадают в дерево с нулями.

## Структура проекта

| Файл | Назначение |
| --- | --- |
| `App.xaml`, `App.xaml.cs` | точка входа, режим `--dump` |
| `MainWindow.xaml`, `MainWindow.xaml.cs` | интерфейс, автообновление, карточки, дерево |
| `DataStore.cs` | чтение `opencode.db` и логов, парсинг JSONL/лога |
| `Models.cs` | модели сессий/запросов/шагов и элементов дерева |
| `Fmt.cs` | форматирование чисел и длительности |
| `AppSettings.cs` | конфиг приложения (settings.json) |
| `BudgetService.cs` | выбор источника бюджета, LiteLLM `/key/info` |
| `OpenCodeBudgetProvider.cs` | бюджет OpenCode Go/Console (служба, SQLite-ключ, прямой API) |
| `install.ps1` | установка/обновление/проверка/удаление плагинов в opencode |
| `TokenMonitor\OpenCodeSetup\token-stats.ts` | эталонная копия плагина статистики |
| `TokenMonitor\OpenCodeSetup\proxy.ts` | эталонная копия прокси-плагина |
| `run.ps1` | сборка при необходимости и запуск |
