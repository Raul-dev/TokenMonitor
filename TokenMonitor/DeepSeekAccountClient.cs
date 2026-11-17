using System.Net.Http;
using Serilog;

namespace TokenMonitor;

/// <summary>
/// Снимок аккаунта: успешный (Usage != null) либо причина ошибки (TZ §6, §7).
/// <para><see cref="IsStale"/> — показан последний успешный снимок после сетевой ошибки.</para>
/// </summary>
public sealed record DeepSeekAccountSnapshot(
    DeepSeekAccountUsage? Usage,
    DeepSeekAccountError Error,
    string ErrorMessage,
    DateTimeOffset FetchedAt,
    bool IsStale)
{
    public bool HasUsage => Usage is not null;
}

/// <summary>
/// Клиент приватных Platform-эндпоинтов (TZ §3): баланс, токены и расход за месяц
/// тем же grant'ом, которым пользуется DeepSeek Harness. Запросы — только чтение.
/// <para>TTL успешного снимка, in-flight dedup, stale-on-error и отмена — TZ §7.</para>
/// </summary>
public sealed class DeepSeekAccountClient
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan StaleWindow = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ForceInterval = TimeSpan.FromSeconds(60);

    readonly AppSettings settings;
    readonly SemaphoreSlim gate = new(1, 1);

    DeepSeekAccountSnapshot? last;
    DateTimeOffset lastAttempt = DateTimeOffset.MinValue;
    int httpRequests;

    public DeepSeekAccountClient(AppSettings settings) => this.settings = settings;

    /// <summary>Сколько HTTP-запросов сделано этим клиентом (для приёмки §10.2).</summary>
    public int HttpRequests => Volatile.Read(ref httpRequests);

    DeepSeekAccountSettings Account => settings.DeepSeekAccount;

    public async Task<DeepSeekAccountSnapshot> GetAsync(bool force, CancellationToken token = default)
    {
        if (TryUseCache(force, out var cached)) return cached!;

        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Пока ждали семафор, другой вызывающий мог обновить снимок.
            if (TryUseCache(force, out cached)) return cached!;

            lastAttempt = DateTimeOffset.UtcNow;
            var snapshot = await FetchAsync(token).ConfigureAwait(false);
            last = snapshot;
            return snapshot;
        }
        finally
        {
            gate.Release();
        }
    }

    bool TryUseCache(bool force, out DeepSeekAccountSnapshot? snapshot)
    {
        snapshot = last;
        if (snapshot is null) return false;

        var age = DateTimeOffset.UtcNow - lastAttempt;
        var ttl = TimeSpan.FromSeconds(Account.RefreshSeconds);

        if (force)
        {
            // «Обновить» обходит TTL, но не чаще 60 с: защита от rate-limit.
            return age < ForceInterval;
        }

        if (age >= ttl) return false;

        // Смена месяца (локальная дата) — принудительно обновляем.
        if (snapshot.Usage is not null)
        {
            var now = DateTime.Now;
            if (snapshot.Usage.Year != now.Year || snapshot.Usage.Month != now.Month) return false;
        }

        return true;
    }

    async Task<DeepSeekAccountSnapshot> FetchAsync(CancellationToken token)
    {
        var now = DateTimeOffset.Now;
        var origin = DeepSeekAccountSettings.NormalizeOrigin(Account.PlatformOrigin);

        try
        {
            var usage = await FetchUsageAsync(origin, now, token).ConfigureAwait(false);
            return new DeepSeekAccountSnapshot(usage, DeepSeekAccountError.None, string.Empty, now, false);
        }
        catch (DeepSeekAccountException error)
        {
            Log.Warning("[DeepSeekAccount] {Endpoint}: {Error} ({Code}) — {Message}",
                "account", error.Error, error.Code, error.Message);
            return Failure(error.Error, error.Message, now);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            Log.Warning("[DeepSeekAccount] таймаут запроса к {Origin}", origin);
            return Failure(DeepSeekAccountError.Network, "таймаут 15 с", now);
        }
        catch (HttpRequestException error)
        {
            Log.Warning("[DeepSeekAccount] сеть: {Message}", error.Message);
            return Failure(DeepSeekAccountError.Network, error.Message, now);
        }
        catch (Exception error)
        {
            Log.Warning("[DeepSeekAccount] неожиданная ошибка: {Message}", error.Message);
            return Failure(DeepSeekAccountError.Unknown, error.Message, now);
        }
    }

    /// <summary>
    /// Ошибка: если прошлый успешный снимок моложе 15 минут и причина не в токене,
    /// показываем его как устаревший (TZ §7).
    /// </summary>
    DeepSeekAccountSnapshot Failure(DeepSeekAccountError error, string message, DateTimeOffset now)
    {
        var previous = last;
        var keepStale = previous?.Usage is not null
            && error is not (DeepSeekAccountError.TokenExpired or DeepSeekAccountError.NoCredentials)
            && now - previous.Usage!.FetchedAt < StaleWindow;

        if (keepStale) return new DeepSeekAccountSnapshot(previous!.Usage, error, message, now, true);

        return new DeepSeekAccountSnapshot(null, error, message, now, false);
    }

    async Task<DeepSeekAccountUsage> FetchUsageAsync(string origin, DateTimeOffset now, CancellationToken token)
    {
        var credential = DeepSeekCredentials.Read(Account);
        if (!credential.HasToken)
        {
            var kind = credential.Status == DeepSeekCredentialStatus.Malformed
                ? DeepSeekAccountError.CredentialsFormat
                : DeepSeekAccountError.NoCredentials;

            if (credential.Status == DeepSeekCredentialStatus.Ambiguous)
                kind = DeepSeekAccountError.CredentialsFormat;

            throw new DeepSeekAccountException(kind, credential.Message);
        }

        var baseUri = new Uri(origin + "/");
        if (!GlobalRouteHttpClient.CanAccess(baseUri))
            throw new DeepSeekAccountException(DeepSeekAccountError.ProxyBlocked,
                "внешний доступ запрещён: нужен прокси (ProxySettings.UpstreamUrl)");

        using var http = GlobalRouteHttpClient.CreateClient(RequestTimeout);

        var local = DateTime.Now;
        var summaryJson = await GetAsync(http, origin, "/api/v0/users/get_user_summary", credential.Token!, token)
            .ConfigureAwait(false);
        var amountJson = await GetAsync(http, origin,
            $"/api/v0/usage/amount?month={local.Month}&year={local.Year}", credential.Token!, token)
            .ConfigureAwait(false);
        var costJson = await GetAsync(http, origin,
            $"/api/v0/usage/cost?month={local.Month}&year={local.Year}", credential.Token!, token)
            .ConfigureAwait(false);

        var summary = DeepSeekAccountParser.ParseSummary(summaryJson);
        var amount = DeepSeekAccountParser.ParseAmount(amountJson);
        var cost = DeepSeekAccountParser.ParseCost(costJson);

        return DeepSeekAccountParser.Create(summary, amount, cost, local.Year, local.Month, now);
    }

    async Task<string> GetAsync(HttpClient http, string origin, string pathAndQuery, string accessToken, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, origin + pathAndQuery);
        request.Headers.TryAddWithoutValidation("x-dsh-auth-token", accessToken);
        request.Headers.TryAddWithoutValidation("x-client-platform", "web");
        request.Headers.TryAddWithoutValidation("Referer", origin + "/usage");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);

        var status = (int)response.StatusCode;
        var path = pathAndQuery.Split('?')[0];
        Interlocked.Increment(ref httpRequests);

        if (status is 401 or 403)
        {
            throw new DeepSeekAccountException(DeepSeekAccountError.TokenExpired,
                $"{path}: HTTP {status}", status);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new DeepSeekAccountException(DeepSeekAccountError.Network,
                $"{path}: HTTP {status}", status);
        }

        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        Log.Debug("[DeepSeekAccount] {Path} -> HTTP {Status}, {Length} байт", path, status, body.Length);
        return body;
    }
}
