using System.Globalization;
using System.IO;
using TokenMonitor;
using TokenMonitor.DataProviders;

// Приёмка реального usage аккаунта DeepSeek (TZ_ACCOUNT_USAGE §9–§10).
//   AccountCheck               — живой запрос к platform.deepseek.com (grant DSH), сводка без токена
//   AccountCheck --force       — обойти TTL снимка (не чаще 60 с)
//   AccountCheck --twice       — два вызова подряд: проверка кэша по счётчику HTTP
//   AccountCheck --self-test   — фикстуры + разбор credentials + маппинг шапки, без сети
//   AccountCheck --budget      — офлайн-предпросмотр шапки на фикстурах
//   AccountCheck --direct      — игнорировать прокси из settings.json
//   AccountCheck --credentials <путь> — другой файл credentials
//   AccountCheck --origin <url>       — другая база Platform API
//
// Exit code: 0 — успех/все проверки прошли, 1 — ошибка аккаунта или упавшая проверка.

var argsList = args.ToList();

string? ArgValue(string name)
{
    var index = argsList.IndexOf(name);
    return index >= 0 && index + 1 < argsList.Count ? argsList[index + 1] : null;
}

bool Has(string name) => argsList.Contains(name);

var ru = CultureInfo.GetCultureInfo("ru-RU");
string Group(long value) => value.ToString("N0", CultureInfo.InvariantCulture).Replace(',', ' ');

if (Has("--self-test")) return SelfTests.Run();
if (Has("--budget")) return BudgetPreview.Run();

var settings = SettingsStore.Load();
settings.DeepSeekAccount = DeepSeekAccountSettings.Normalize(settings.DeepSeekAccount);
settings.DeepSeekAccount.Enabled = true;
if (ArgValue("--credentials") is { Length: > 0 } credentials)
    settings.DeepSeekAccount.CredentialsPath = credentials;
if (ArgValue("--origin") is { Length: > 0 } origin)
    settings.DeepSeekAccount.PlatformOrigin = origin;
if (Has("--direct")) settings.ProxySettings.Enabled = false;
settings.DeepSeekAccount = DeepSeekAccountSettings.Normalize(settings.DeepSeekAccount);

GlobalRouteHttpClient.ProxySettings = settings.ProxySettings;

var client = new DeepSeekAccountClient(settings);
var snapshot = await client.GetAsync(Has("--force"));

if (Has("--twice"))
{
    var before = client.HttpRequests;
    await client.GetAsync(false);
    Console.WriteLine($"http-запросов: {before} → {client.HttpRequests} (второй вызов из кэша: {(before == client.HttpRequests ? "да" : "НЕТ")})");
    if (before != client.HttpRequests) return 1;
}

if (!snapshot.HasUsage)
{
    Console.WriteLine($"статус: ошибка ({snapshot.Error})");
    Console.WriteLine($"причина: {snapshot.ErrorMessage}");
    Console.WriteLine("подсказка: нужен вход в DeepSeek Harness (grant deepseek-account-platform/default).");
    return 1;
}

var usage = snapshot.Usage!;

static string Raw(decimal value) => value.ToString("0.#################", CultureInfo.InvariantCulture);

Console.WriteLine("статус: ok" + (snapshot.IsStale ? " (устаревший снимок)" : ""));
Console.WriteLine($"аккаунт: {DeepSeekAccountSettings.NormalizeOrigin(settings.DeepSeekAccount.PlatformOrigin)}");
Console.WriteLine($"месяц: {usage.Year}-{usage.Month:00}");
Console.WriteLine($"валюта: {usage.Currency}");
Console.WriteLine($"расход за месяц: {Fmt.MoneyExact(usage.MonthCost, usage.Currency)}  (raw {Raw(usage.MonthCost)})");
Console.WriteLine($"баланс: {Fmt.MoneyExact(usage.Balance, usage.Currency)} (оплачено {Raw(usage.BalancePaid)}, бонусы {Raw(usage.BalanceBonus)})");
Console.WriteLine($"всего было: {Fmt.MoneyExact(usage.TotalBudget, usage.Currency)}  (израсходовано всего {Raw(usage.TotalSpent)})");
Console.WriteLine($"осталось: {Fmt.MoneyExact(usage.Remaining, usage.Currency)}");
Console.WriteLine($"запросов за месяц: {Group(usage.Requests)}");
Console.WriteLine($"токены: cache hit {Group(usage.TokensCacheHit)} · cache miss {Group(usage.TokensCacheMiss)} · output {Group(usage.TokensOutput)}");
if (usage.TokensPrompt > 0) Console.WriteLine($"prompt: {Group(usage.TokensPrompt)}");

foreach (var model in usage.Models)
    Console.WriteLine($"модель {model.Model}: {Group(model.Tokens)} токенов · {Fmt.MoneyExact(model.Cost, usage.Currency)} · {Group(model.Requests)} запр.");

foreach (var wallet in usage.Wallets)
    Console.WriteLine($"кошелёк {wallet.Currency}: оплачено {Raw(wallet.Paid)} · бонусы {Raw(wallet.Bonus)}");

Console.WriteLine($"обновлено: {snapshot.FetchedAt:dd.MM.yyyy HH:mm:ss zzz}");
Console.WriteLine($"http-запросов сделано: {client.HttpRequests}");
return 0;

/// <summary>Офлайн-проверки: фикстуры ответов и выбор grant'а из credentials.</summary>
static class SelfTests
{
    public static int Run()
    {
        var failures = 0;
        var fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
        var checks = new List<string>();

        void Check(string name, bool condition, string? detail = null)
        {
            checks.Add($"[{(condition ? "ok" : "FAIL")}] {name}{(detail is null ? "" : $" — {detail}")}");
            if (!condition) failures++;
        }

        DeepSeekAccountUsage? usage = null;
        try
        {
            var summary = DeepSeekAccountParser.ParseSummary(Read(fixtures, "summary.json"));
            var amount = DeepSeekAccountParser.ParseAmount(Read(fixtures, "amount.json"));
            var cost = DeepSeekAccountParser.ParseCost(Read(fixtures, "cost.json"));
            usage = DeepSeekAccountParser.Create(summary, amount, cost, 2026, 11,
                new DateTimeOffset(2026, 11, 17, 16, 5, 12, TimeSpan.FromHours(3)));
        }
        catch (Exception error)
        {
            Check("разбор фикстур", false, error.Message);
        }

        if (usage is not null)
        {
            Check("валюта = USD", usage.Currency == "USD", usage.Currency);
            Check("расход за месяц = 0.9169755", usage.MonthCost == 0.9169755m, usage.MonthCost.ToString(CultureInfo.InvariantCulture));
            Check("баланс = 9.0830245", usage.Balance == 9.0830245m, usage.Balance.ToString(CultureInfo.InvariantCulture));
            Check("всего было = 10.0000000", usage.TotalBudget == 10m, usage.TotalBudget.ToString(CultureInfo.InvariantCulture));
            Check("запросов = 372", usage.Requests == 372, usage.Requests.ToString());
            Check("cache hit = 80044800", usage.TokensCacheHit == 80_044_800, usage.TokensCacheHit.ToString());
            Check("cache miss = 370905", usage.TokensCacheMiss == 370_905, usage.TokensCacheMiss.ToString());
            Check("output = 271196", usage.TokensOutput == 271_196, usage.TokensOutput.ToString());
            Check("модель одна", usage.Models.Count == 1, usage.Models.Count.ToString());
            Check("деньги модели = 0.9169755", usage.Models[0].Cost == 0.9169755m, usage.Models[0].Cost.ToString(CultureInfo.InvariantCulture));
            Check("формат шапки", Fmt.Money(usage.MonthCost, "USD") == "$0,92"
                && Fmt.Money(usage.TotalBudget, "USD") == "$10,00", Fmt.Money(usage.MonthCost, "USD"));
            Check("CNY-символ", Fmt.Money(12.3m, "CNY") == "¥12,30", Fmt.Money(12.3m, "CNY"));

            // Маппинг в шапку: крупно «потрачено / всего было», мелко — остаток.
            var config = new DeepSeekAccountSettings();
            var model = DeepSeekDataProvider.MapAccount(
                new DeepSeekAccountSnapshot(usage, DeepSeekAccountError.None, "", usage.FetchedAt, false), config);
            Check("шапка: текст", model.Text == "$0,92 / $10,00", model.Text);
            Check("шапка: деталь", model.Detail == "осталось $9,08 · 372 запр.", model.Detail);
            Check("шапка: severity Normal", model.Severity == BudgetSeverity.Normal, model.Severity.ToString());
            Check("шапка: остаток в tooltip", model.Tooltip?.Contains("осталось: $9,0830245") == true, "нет строки остатка");

            var stale = DeepSeekDataProvider.MapAccount(
                new DeepSeekAccountSnapshot(usage, DeepSeekAccountError.Network, "timeout", usage.FetchedAt, true), config);
            Check("шапка: stale-пометка", stale.Detail.EndsWith(" · устарело", StringComparison.Ordinal), stale.Detail);

            var low = usage with { BalancePaid = 0.05m, BalanceBonus = 0m };
            var lowModel = DeepSeekDataProvider.MapAccount(
                new DeepSeekAccountSnapshot(low, DeepSeekAccountError.None, "", usage.FetchedAt, false), config);
            Check("шапка: низкий баланс → Critical", lowModel.Severity == BudgetSeverity.Critical, lowModel.Severity.ToString());

            var manual = DeepSeekDataProvider.MapAccount(
                new DeepSeekAccountSnapshot(usage, DeepSeekAccountError.None, "", usage.FetchedAt, false),
                new DeepSeekAccountSettings { TotalBudget = 345m });
            Check("шапка: ручное «всего было»", manual.Text == "$0,92 / $345,00", manual.Text);
        }

        CheckError(fixtures, "error-40003.json", DeepSeekAccountError.TokenExpired, Check);
        CheckError(fixtures, "error-40001.json", DeepSeekAccountError.InvalidMonth, Check);
        CheckError(fixtures, "error-unknown-schema.json", DeepSeekAccountError.ApiChanged, Check);

        // credentials: приоритет default-записи
        var yaml = """
            version: 1
            records:
              client-connection/browser-session:
                kind: grant
                payload:
                  version: 1
                  secret: not-a-platform-token
              deepseek-account-platform/default:
                kind: grant
                payload:
                  version: 1
                  token: 1234567890abcdef
                  issuer: https://platform.deepseek.com
            """;
        var credential = DeepSeekCredentials.FromYaml(yaml, "https://platform.deepseek.com");
        Check("credentials: default найден", credential.HasToken && credential.Token == "1234567890abcdef", credential.Status.ToString());
        Check("credentials: issuer", credential.Issuer == "https://platform.deepseek.com", credential.Issuer);

        var onlyBrowser = DeepSeekCredentials.FromYaml(
            "records:\n  client-connection/browser-session:\n    kind: grant\n    payload:\n      secret: x\n",
            "https://platform.deepseek.com");
        Check("credentials: только browser-session → нет входа",
            onlyBrowser.Status == DeepSeekCredentialStatus.NoGrant, onlyBrowser.Status.ToString());

        var wrongIssuer = DeepSeekCredentials.FromYaml(
            "records:\n  deepseek-account-platform/default:\n    payload:\n      token: abc\n      issuer: https://example.com\n",
            "https://platform.deepseek.com");
        Check("credentials: чужой issuer → нет входа",
            wrongIssuer.Status == DeepSeekCredentialStatus.NoGrant, wrongIssuer.Status.ToString());

        var ambiguous = DeepSeekCredentials.FromYaml(
            "records:\n  deepseek-account-platform/a:\n    payload:\n      token: a\n      issuer: https://platform.deepseek.com\n"
            + "  deepseek-account-platform/b:\n    payload:\n      token: b\n      issuer: https://platform.deepseek.com\n",
            "https://platform.deepseek.com");
        Check("credentials: две записи без default → ambiguous",
            ambiguous.Status == DeepSeekCredentialStatus.Ambiguous, ambiguous.Status.ToString());

        var malformed = DeepSeekCredentials.FromYaml("version: 1\nrecords: {}\n", "https://platform.deepseek.com");
        Check("credentials: пустые records → нет grant'а",
            malformed.Status is DeepSeekCredentialStatus.NoGrant or DeepSeekCredentialStatus.Malformed,
            malformed.Status.ToString());

        Check("origin: http отклонён", DeepSeekAccountSettings.NormalizeOrigin("http://platform.deepseek.com") == DeepSeekAccountSettings.DefaultPlatformOrigin);
        Check("origin: trailing slash убран", DeepSeekAccountSettings.NormalizeOrigin("https://platform.deepseek.com/") == "https://platform.deepseek.com");
        Check("refresh: минимум 60",
            DeepSeekAccountSettings.Normalize(new DeepSeekAccountSettings { RefreshSeconds = 5 }).RefreshSeconds == 60);

        foreach (var line in checks) Console.WriteLine(line);
        Console.WriteLine(failures == 0 ? "self-test: все проверки прошли" : $"self-test: провалов {failures}");
        return failures == 0 ? 0 : 1;
    }

    static void CheckError(string fixtures, string file, DeepSeekAccountError expected, Action<string, bool, string?> check)
    {
        try
        {
            var json = Read(fixtures, file);
            if (file.Contains("amount")) DeepSeekAccountParser.ParseAmount(json);
            else if (file.Contains("cost")) DeepSeekAccountParser.ParseCost(json);
            else DeepSeekAccountParser.ParseSummary(json);
            check($"ошибка {file}", false, "исключение не брошено");
        }
        catch (DeepSeekAccountException error)
        {
            check($"ошибка {file} → {expected}", error.Error == expected, error.Error.ToString());
        }
    }

    static string Read(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        if (!File.Exists(path)) throw new FileNotFoundException($"нет фикстуры {path}");
        return File.ReadAllText(path);
    }
}

/// <summary>Офлайн-предпросмотр шапки на фикстурах: «$0,92 / $10,00» + «осталось $9,08».</summary>
static class BudgetPreview
{
    public static int Run()
    {
        var fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");

        string Read(string name) => File.ReadAllText(Path.Combine(fixtures, name));

        var summary = DeepSeekAccountParser.ParseSummary(Read("summary.json"));
        var amount = DeepSeekAccountParser.ParseAmount(Read("amount.json"));
        var cost = DeepSeekAccountParser.ParseCost(Read("cost.json"));
        var usage = DeepSeekAccountParser.Create(summary, amount, cost, 2026, 11,
            new DateTimeOffset(2026, 11, 17, 16, 5, 12, TimeSpan.FromHours(3)));

        var model = DeepSeekDataProvider.MapAccount(
            new DeepSeekAccountSnapshot(usage, DeepSeekAccountError.None, "", usage.FetchedAt, false),
            new DeepSeekAccountSettings());

        Console.WriteLine($"крупно: {model.Text}");
        Console.WriteLine($"мелко:  {model.Detail}");
        Console.WriteLine($"цвет:   {model.Severity}");
        Console.WriteLine("tooltip:");
        Console.WriteLine(model.Tooltip);
        return 0;
    }
}
