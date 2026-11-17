using System.Globalization;
using System.Text.Json;

namespace TokenMonitor;

public enum DeepSeekAccountError
{
    None,
    NoCredentials,
    CredentialsFormat,
    TokenExpired,
    Network,
    ProxyBlocked,
    ApiChanged,
    InvalidMonth,
    Unknown,
}

/// <summary>Ошибка аккаунтного запроса с машиночитаемой причиной (TZ §3.4).</summary>
public sealed class DeepSeekAccountException : Exception
{
    public DeepSeekAccountException(DeepSeekAccountError error, string message, int code = 0)
        : base(message)
    {
        Error = error;
        Code = code;
    }

    public DeepSeekAccountError Error { get; }
    public int Code { get; }
}

/// <summary>Кошелёк аккаунта: оплаченный и бонусный баланс одной валюты.</summary>
public sealed record DeepSeekWallet(string Currency, decimal Paid, decimal Bonus)
{
    public decimal Total => Paid + Bonus;
}

/// <summary>Расход и токены по одной модели за месяц.</summary>
public sealed record DeepSeekModelUsage(string Model, decimal Cost, long Tokens, long Requests);

/// <summary>
/// Снимок реального usage аккаунта platform.deepseek.com (TZ §6).
/// <para>«Всего было» = баланс + расход по данным платформы (или ручной
/// <see cref="DeepSeekAccountSettings.TotalBudget"/>): 123$ потрачено / 345$ было,
/// внизу мелко — остаток.</para>
/// </summary>
public sealed record DeepSeekAccountUsage(
    string Currency,
    decimal MonthCost,
    decimal BalancePaid,
    decimal BalanceBonus,
    decimal TotalSpent,
    bool IsAvailable,
    long Requests,
    long TokensCacheHit,
    long TokensCacheMiss,
    long TokensOutput,
    long TokensPrompt,
    IReadOnlyList<DeepSeekModelUsage> Models,
    IReadOnlyList<DeepSeekWallet> Wallets,
    int Year,
    int Month,
    DateTimeOffset FetchedAt)
{
    /// <summary>Баланс в валюте аккаунта (оплачено + бонусы).</summary>
    public decimal Balance => BalancePaid + BalanceBonus;

    /// <summary>Сумма, которая была на счету: баланс + израсходовано.</summary>
    public decimal TotalBudget => Balance + TotalSpent;

    /// <summary>Сколько осталось.</summary>
    public decimal Remaining => Balance;

    public long TokensTotal => TokensCacheHit + TokensCacheMiss + TokensOutput + TokensPrompt;
}

/// <summary>Разбор ответов Platform API. Чистые функции — покрываются фикстурами.</summary>
public static class DeepSeekAccountParser
{
    const NumberStyles Money = NumberStyles.Float | NumberStyles.AllowLeadingSign;

    /// <summary>Баланс и суммарные расходы: GET /api/v0/users/get_user_summary.</summary>
    public static DeepSeekSummary ParseSummary(string json)
    {
        var biz = RequireBizData(json, "get_user_summary");
        try
        {
            // Без кошельков ответ бессмысленен: не угадываем поля, а честно сообщаем,
            // что схема изменилась (TZ §8).
            if (!biz.TryGetProperty("normal_wallets", out _) && !biz.TryGetProperty("bonus_wallets", out _))
                throw new InvalidOperationException("нет normal_wallets/bonus_wallets");

            var wallets = new List<DeepSeekWallet>();
            ReadWallets(biz, "normal_wallets", bonus: false, wallets);
            ReadWallets(biz, "bonus_wallets", bonus: true, wallets);

            var merged = new List<DeepSeekWallet>();
            foreach (var group in wallets.GroupBy(w => w.Currency, StringComparer.OrdinalIgnoreCase))
            {
                merged.Add(new DeepSeekWallet(
                    group.Key,
                    group.Sum(w => w.Paid),
                    group.Sum(w => w.Bonus)));
            }

            var lifetime = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            if (biz.TryGetProperty("total_costs", out var costs) && costs.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in costs.EnumerateArray())
                {
                    var currency = String(item, "currency") ?? "USD";
                    lifetime[currency] = lifetime.TryGetValue(currency, out var existing)
                        ? existing + Decimal(item, "amount")
                        : Decimal(item, "amount");
                }
            }

            return new DeepSeekSummary(merged, lifetime);
        }
        catch (InvalidOperationException error)
        {
            throw new DeepSeekAccountException(DeepSeekAccountError.ApiChanged,
                $"неожиданная схема get_user_summary: {error.Message}");
        }
    }

    /// <summary>Токены и запросы за месяц: GET /api/v0/usage/amount?month&amp;year.</summary>
    public static DeepSeekAmountSplit ParseAmount(string json)
    {
        var biz = RequireBizData(json, "usage/amount");
        var buckets = new Dictionary<string, AmountBucket>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var item in Array(biz, "total"))
            {
                var model = String(item, "model") ?? "unknown";
                if (!buckets.TryGetValue(model, out var bucket))
                {
                    bucket = new AmountBucket();
                    buckets[model] = bucket;
                }

                foreach (var usage in Array(item, "usage"))
                    bucket.Add(String(usage, "type"), Long(usage, "amount"));
            }
        }
        catch (InvalidOperationException error)
        {
            throw new DeepSeekAccountException(DeepSeekAccountError.ApiChanged,
                $"неожиданная схема usage/amount: {error.Message}");
        }

        var totals = new AmountBucket();
        foreach (var bucket in buckets.Values) totals.Merge(bucket);

        var models = buckets.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.AsTuple(),
            StringComparer.OrdinalIgnoreCase);

        return new DeepSeekAmountSplit(models, totals.AsTuple());
    }

    /// <summary>Расход за месяц в деньгах: GET /api/v0/usage/cost?month&amp;year.</summary>
    public static DeepSeekCostSplit ParseCost(string json)
    {
        var biz = RequireBizData(json, "usage/cost");

        JsonElement first;
        try
        {
            first = biz.ValueKind == JsonValueKind.Array
                ? (biz.GetArrayLength() > 0 ? biz[0] : throw new InvalidOperationException("пустой biz_data"))
                : biz;
        }
        catch (InvalidOperationException error)
        {
            throw new DeepSeekAccountException(DeepSeekAccountError.ApiChanged,
                $"неожиданная схема usage/cost: {error.Message}");
        }

        var models = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var item in Array(first, "total"))
            {
                var model = String(item, "model") ?? "unknown";
                decimal sum = 0;
                foreach (var usage in Array(item, "usage"))
                {
                    if (string.Equals(String(usage, "type"), "REQUEST", StringComparison.OrdinalIgnoreCase)) continue;
                    sum += Decimal(usage, "amount");
                }

                models[model] = models.TryGetValue(model, out var existing) ? existing + sum : sum;
            }

            var currency = String(first, "currency");
            return new DeepSeekCostSplit(currency ?? "USD", models);
        }
        catch (InvalidOperationException error)
        {
            throw new DeepSeekAccountException(DeepSeekAccountError.ApiChanged,
                $"неожиданная схема usage/cost: {error.Message}");
        }
    }

    /// <summary>Собирает доменный снимок из трёх ответов. Валюта — из usage/cost.</summary>
    public static DeepSeekAccountUsage Create(
        DeepSeekSummary summary,
        DeepSeekAmountSplit amount,
        DeepSeekCostSplit cost,
        int year,
        int month,
        DateTimeOffset fetchedAt)
    {
        var currency = PickCurrency(summary, cost.Currency);

        var paid = summary.Wallets.Where(w => Same(w.Currency, currency)).Sum(w => w.Paid);
        var bonus = summary.Wallets.Where(w => Same(w.Currency, currency)).Sum(w => w.Bonus);

        var monthCost = cost.Models.Values.Sum();
        var lifetime = summary.LifetimeCost.TryGetValue(currency, out var known) ? known : 0m;
        if (lifetime <= 0) lifetime = monthCost;

        // Токены по моделям: cache hit + miss + output; PROMPT_TOKEN — только если
        // первых трёх нет (TZ §6, чтобы не складывать два варианта).
        var tokens = amount.Models.ToDictionary(p => p.Key, p => TokenTotal(p.Value), StringComparer.OrdinalIgnoreCase);

        var names = new List<string>(cost.Models.Keys);
        foreach (var name in tokens.Keys)
        {
            if (!cost.Models.ContainsKey(name)) names.Add(name);
        }

        var models = names
            .Select(name => new DeepSeekModelUsage(
                name,
                cost.Models.TryGetValue(name, out var modelCost) ? modelCost : 0m,
                tokens.TryGetValue(name, out var modelTokens) ? modelTokens : 0,
                amount.Models.TryGetValue(name, out var bucket) ? bucket.Request : 0))
            .OrderByDescending(m => m.Cost)
            .ThenByDescending(m => m.Tokens)
            .ToList();

        var balance = paid + bonus;

        return new DeepSeekAccountUsage(
            currency,
            monthCost,
            paid,
            bonus,
            lifetime,
            balance > 0,
            amount.Totals.Request,
            amount.Totals.CacheHit,
            amount.Totals.CacheMiss,
            amount.Totals.Response,
            amount.Totals.Prompt,
            models,
            summary.Wallets,
            year,
            month,
            fetchedAt);
    }

    static string PickCurrency(DeepSeekSummary summary, string? costCurrency)
    {
        if (!string.IsNullOrWhiteSpace(costCurrency)) return costCurrency.Trim().ToUpperInvariant();

        foreach (var preferred in new[] { "USD", "CNY" })
        {
            var wallet = summary.Wallets.FirstOrDefault(w => Same(w.Currency, preferred));
            if (wallet is not null) return wallet.Currency;
        }

        return summary.Wallets.Count > 0 ? summary.Wallets[0].Currency : "USD";
    }

    static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    static long TokenTotal((long Prompt, long CacheHit, long CacheMiss, long Response, long Request) bucket)
    {
        var cached = bucket.CacheHit + bucket.CacheMiss + bucket.Response;
        return cached > 0 ? cached : bucket.Prompt;
    }

    // ----- конверт и примитивы -------------------------------------------------

    /// <summary>
    /// Проверяет общий конверт <c>{code, data:{biz_code, biz_data}}</c> и возвращает
    /// <c>biz_data</c> (TZ §3.3). Коды 40002/40003 → истёкшая сессия.
    /// </summary>
    static JsonElement RequireBizData(string json, string endpoint)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException error)
        {
            throw new DeepSeekAccountException(DeepSeekAccountError.ApiChanged,
                $"ответ {endpoint} — не JSON: {error.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new DeepSeekAccountException(DeepSeekAccountError.ApiChanged,
                    $"ответ {endpoint}: ожидался объект");

            if (root.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.Number
                && code.TryGetInt32(out var codeValue)
                && codeValue != 0)
            {
                throw MapCode(codeValue, endpoint);
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                throw new DeepSeekAccountException(DeepSeekAccountError.ApiChanged,
                    $"ответ {endpoint}: нет поля data");

            if (data.TryGetProperty("biz_code", out var bizCode)
                && bizCode.ValueKind == JsonValueKind.Number
                && bizCode.TryGetInt32(out var bizCodeValue)
                && bizCodeValue != 0)
            {
                throw MapCode(bizCodeValue, endpoint);
            }

            if (!data.TryGetProperty("biz_data", out var bizData))
                throw new DeepSeekAccountException(DeepSeekAccountError.ApiChanged,
                    $"ответ {endpoint}: нет поля biz_data");

            return bizData.Clone();
        }
    }

    static DeepSeekAccountException MapCode(int code, string endpoint) => code switch
    {
        40002 or 40003 => new DeepSeekAccountException(DeepSeekAccountError.TokenExpired,
            $"{endpoint}: сессия DeepSeek истекла (код {code})", code),
        40001 => new DeepSeekAccountException(DeepSeekAccountError.InvalidMonth,
            $"{endpoint}: неверный месяц или год (код {code})", code),
        _ => new DeepSeekAccountException(DeepSeekAccountError.ApiChanged,
            $"{endpoint}: ошибка платформы (код {code})", code),
    };

    static IEnumerable<JsonElement> Array(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"нет массива {name}");

        return value.EnumerateArray();
    }

    static string? String(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    static long Long(JsonElement parent, string name) => (long)Decimal(parent, name);

    static decimal Decimal(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)) return 0m;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;

        if (value.ValueKind == JsonValueKind.String)
        {
            var raw = (value.GetString() ?? string.Empty).Trim();
            if (raw.Length == 0) return 0m;
            if (decimal.TryParse(raw, Money, CultureInfo.InvariantCulture, out var parsed)) return parsed;

            throw new DeepSeekAccountException(DeepSeekAccountError.ApiChanged,
                $"не число в поле {name}: {raw[..Math.Min(raw.Length, 24)]}");
        }

        throw new DeepSeekAccountException(DeepSeekAccountError.ApiChanged,
            $"поле {name} не строка и не число");
    }

    static void ReadWallets(JsonElement biz, string name, bool bonus, List<DeepSeekWallet> wallets)
    {
        if (!biz.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return;

        foreach (var item in array.EnumerateArray())
        {
            var currency = String(item, "currency") ?? "USD";
            var balance = Decimal(item, "balance");
            wallets.Add(bonus
                ? new DeepSeekWallet(currency.Trim().ToUpperInvariant(), 0m, balance)
                : new DeepSeekWallet(currency.Trim().ToUpperInvariant(), balance, 0m));
        }
    }

    sealed class AmountBucket
    {
        public long Prompt;
        public long CacheHit;
        public long CacheMiss;
        public long Response;
        public long Request;

        public void Add(string? type, long amount)
        {
            switch ((type ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "PROMPT_TOKEN": Prompt += amount; break;
                case "PROMPT_CACHE_HIT_TOKEN": CacheHit += amount; break;
                case "PROMPT_CACHE_MISS_TOKEN": CacheMiss += amount; break;
                case "RESPONSE_TOKEN": Response += amount; break;
                case "REQUEST": Request += amount; break;
            }
        }

        public void Merge(AmountBucket other)
        {
            Prompt += other.Prompt;
            CacheHit += other.CacheHit;
            CacheMiss += other.CacheMiss;
            Response += other.Response;
            Request += other.Request;
        }

        public (long Prompt, long CacheHit, long CacheMiss, long Response, long Request) AsTuple() =>
            (Prompt, CacheHit, CacheMiss, Response, Request);
    }
}

/// <summary>Баланс + суммарные расходы по валютам (get_user_summary).</summary>
public sealed record DeepSeekSummary(
    IReadOnlyList<DeepSeekWallet> Wallets,
    IReadOnlyDictionary<string, decimal> LifetimeCost);

/// <summary>Токены/запросы за месяц: итог и разбивка по моделям.</summary>
public sealed record DeepSeekAmountSplit(
    IReadOnlyDictionary<string, (long Prompt, long CacheHit, long CacheMiss, long Response, long Request)> Models,
    (long Prompt, long CacheHit, long CacheMiss, long Response, long Request) Totals);

/// <summary>Расход за месяц в деньгах: валюта и суммы по моделям.</summary>
public sealed record DeepSeekCostSplit(string Currency, IReadOnlyDictionary<string, decimal> Models);
