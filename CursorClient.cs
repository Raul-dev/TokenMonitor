using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TokenMonitor;

public sealed class CursorPeriodUsage
{
    public DateTime? CycleStart { get; init; }
    public DateTime? CycleEnd { get; init; }
    public string Membership { get; init; } = string.Empty;
    public double AutoPercent { get; init; }
    public double ApiPercent { get; init; }
    public double TotalPercent { get; init; }
    public string? DisplayMessage { get; init; }
    public string? Error { get; init; }
    public string Source { get; init; } = string.Empty;
}

public sealed class CursorUsageEvent
{
    public DateTime Time { get; init; }
    public string ConversationId { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Prompt { get; init; } = string.Empty;
    public string Workspace { get; init; } = string.Empty;
    public string MessageId { get; init; } = string.Empty;
    public TokenCounts Tokens { get; init; }
    public double ChargedCents { get; init; }
    public long DurationMs { get; init; }
    public string Source { get; init; } = string.Empty;
}

public sealed class CursorClient
{
    static readonly HttpClient Http = CreateClient();

    readonly AppSettings settings;

    public CursorClient(AppSettings settings) => this.settings = settings;

    public static string DefaultStateDbPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor", "User", "globalStorage", "state.vscdb");

    public static string DefaultEventsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TokenMonitor", "cursor-events.jsonl");

    static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(12),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }

    public bool TryLoadSession(out string accessToken, out string cookie, out string source, out string? error)
    {
        accessToken = string.Empty;
        cookie = string.Empty;
        source = string.Empty;
        error = null;

        var dbPath = string.IsNullOrWhiteSpace(settings.CursorStateDbPath)
            ? DefaultStateDbPath
            : settings.CursorStateDbPath;

        if (!File.Exists(dbPath))
        {
            error = $"не найден Cursor state.vscdb: {dbPath}";
            return false;
        }

        try
        {
            using var connection = OpenReadonly(dbPath);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT key, value FROM ItemTable WHERE key IN ('cursorAuth/accessToken','cursorAuth/stripeMembershipAuthId')";

            string? token = null;
            string? authId = null;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetString(0);
                var value = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
                if (value.Length >= 2 && value[0] == '"')
                {
                    try { value = JsonSerializer.Deserialize<string>(value) ?? value; }
                    catch { }
                }

                if (key.EndsWith("accessToken", StringComparison.Ordinal)) token = value;
                else authId = value;
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                error = "в Cursor нет accessToken — войдите в аккаунт в IDE";
                return false;
            }

            var sub = JwtClaim(token, "sub") ?? authId;
            if (string.IsNullOrWhiteSpace(sub))
            {
                error = "не удалось взять user id из сессии Cursor";
                return false;
            }

            accessToken = token;
            cookie = sub + "%3A%3A" + token;
            source = dbPath;
            return true;
        }
        catch (Exception ex)
        {
            error = "не удалось прочитать сессию Cursor: " + ex.Message;
            return false;
        }
    }

    static Microsoft.Data.Sqlite.SqliteConnection OpenReadonly(string path)
    {
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 3,
        };

        try
        {
            var connection = new Microsoft.Data.Sqlite.SqliteConnection(builder.ToString());
            connection.Open();
            return connection;
        }
        catch
        {
            var uri = new Uri(path).AbsoluteUri + "?mode=ro&immutable=1";
            var fallback = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = uri,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                Pooling = false,
            };
            var connection = new Microsoft.Data.Sqlite.SqliteConnection(fallback.ToString());
            connection.Open();
            return connection;
        }
    }

    static string? JwtClaim(string jwt, string name)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }

            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<CursorPeriodUsage> GetPeriodAsync(CancellationToken token = default)
    {
        if (!TryLoadSession(out var accessToken, out var cookie, out var source, out var error))
        {
            return new CursorPeriodUsage { Error = error };
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://cursor.com/api/usage-summary");
            ApplyBrowser(request, cookie);
            using var response = await Http.SendAsync(request, token);
            var body = await response.Content.ReadAsStringAsync(token);

            if (!response.IsSuccessStatusCode)
            {
                return new CursorPeriodUsage { Error = $"usage-summary {(int)response.StatusCode}", Source = source };
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var plan = root.TryGetProperty("individualUsage", out var individual) && individual.TryGetProperty("plan", out var planEl)
                ? planEl
                : default;

            return new CursorPeriodUsage
            {
                CycleStart = ReadTime(root, "billingCycleStart"),
                CycleEnd = ReadTime(root, "billingCycleEnd"),
                Membership = Text(root, "membershipType") ?? string.Empty,
                AutoPercent = Number(plan, "autoPercentUsed") ?? 0,
                ApiPercent = Number(plan, "apiPercentUsed") ?? 0,
                TotalPercent = Number(plan, "totalPercentUsed") ?? 0,
                DisplayMessage = Text(root, "autoModelSelectedDisplayMessage"),
                Source = "https://cursor.com/api/usage-summary",
            };
        }
        catch (Exception ex)
        {
            return new CursorPeriodUsage { Error = ex.Message };
        }
        finally
        {
            _ = accessToken;
        }
    }

    public async Task<(List<CursorUsageEvent> Events, string? Error)> GetDashboardEventsAsync(DateTime start, DateTime end, CancellationToken token = default)
    {
        if (!TryLoadSession(out var accessToken, out var cookie, out _, out var error))
        {
            return (new List<CursorUsageEvent>(), error);
        }

        var startMs = new DateTimeOffset(start.ToUniversalTime()).ToUnixTimeMilliseconds().ToString();
        var endMs = new DateTimeOffset(end.ToUniversalTime()).ToUnixTimeMilliseconds().ToString();
        var collected = new List<CursorUsageEvent>();

        var api2 = await TryEventsAsync(
            "https://api2.cursor.sh/aiserver.v1.DashboardService/GetFilteredUsageEvents",
            accessToken, cookie, bearer: true, startMs, endMs, collected, token);
        if (api2 is null) return (collected, null);

        var dashboard = await TryEventsAsync(
            "https://cursor.com/api/dashboard/get-filtered-usage-events",
            accessToken, cookie, bearer: false, startMs, endMs, collected, token);
        if (dashboard is null) return (collected, null);

        return (collected, dashboard);
    }

    async Task<string?> TryEventsAsync(
        string url,
        string accessToken,
        string cookie,
        bool bearer,
        string startMs,
        string endMs,
        List<CursorUsageEvent> collected,
        CancellationToken token)
    {
        string? lastError = null;

        for (var page = 1; page <= 15; page++)
        {
            var payload = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["page"] = page,
                ["pageSize"] = 100,
                ["startDate"] = startMs,
                ["endDate"] = endMs,
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };

            if (bearer)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
            }
            else
            {
                ApplyBrowser(request, cookie);
            }

            try
            {
                using var response = await Http.SendAsync(request, token);
                var body = await response.Content.ReadAsStringAsync(token);
                if (!response.IsSuccessStatusCode)
                {
                    lastError = RegionMessage(body) ?? $"{url} {(int)response.StatusCode}";
                    return lastError;
                }

                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                var items = root.TryGetProperty("usageEventsDisplay", out var display) ? display
                    : root.TryGetProperty("usageEvents", out var events) ? events
                    : default;

                var batch = 0;
                if (items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        collected.Add(ParseEvent(item));
                        batch++;
                    }
                }

                var total = (int)(Number(root, "totalUsageEventsCount") ?? collected.Count);
                if (batch == 0 || collected.Count >= total) return null;
            }
            catch (TaskCanceledException)
            {
                return lastError ?? "таймаут запроса событий Cursor";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        return lastError;
    }

    static CursorUsageEvent ParseEvent(JsonElement item)
    {
        var usage = item.TryGetProperty("tokenUsage", out var tokenUsage) ? tokenUsage : default;
        var input = Long(usage, "inputTokens");
        var output = Long(usage, "outputTokens");
        var cacheRead = Long(usage, "cacheReadTokens");
        var cacheWrite = Long(usage, "cacheWriteTokens");

        return new CursorUsageEvent
        {
            Time = FromMs(Text(item, "timestamp")),
            ConversationId = Text(item, "conversationId") ?? string.Empty,
            Model = Text(item, "model") ?? string.Empty,
            Kind = Text(item, "kind") ?? string.Empty,
            MessageId = Text(item, "timestamp") ?? Guid.NewGuid().ToString("N"),
            Tokens = new TokenCounts(input, output, 0, cacheRead, cacheWrite),
            ChargedCents = Number(item, "chargedCents") ?? Number(usage, "totalCents") ?? 0,
            Source = "dashboard",
        };
    }

    static void ApplyBrowser(HttpRequestMessage request, string cookie)
    {
        request.Headers.TryAddWithoutValidation("Cookie", "WorkosCursorSessionToken=" + cookie);
        request.Headers.TryAddWithoutValidation("Origin", "https://cursor.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://cursor.com/dashboard");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", "TokenMonitor/1.0");
    }

    static string? RegionMessage(string body)
    {
        if (body.Contains("not available in your region", StringComparison.OrdinalIgnoreCase))
            return "Dashboard API недоступен в этом регионе; живая лента идёт из хука Cursor";
        return null;
    }

    static DateTime? ReadTime(JsonElement root, string name)
    {
        var text = Text(root, name);
        return DateTimeOffset.TryParse(text, out var parsed) ? parsed.ToLocalTime().DateTime : null;
    }

    static DateTime FromMs(string? text)
    {
        if (long.TryParse(text, out var ms) && ms > 0)
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().DateTime;
        if (DateTimeOffset.TryParse(text, out var parsed))
            return parsed.ToLocalTime().DateTime;
        return DateTime.Now;
    }

    static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    static double? Number(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    static long Long(JsonElement element, string name) =>
        (long)Math.Round(Number(element, name) ?? 0);
}
