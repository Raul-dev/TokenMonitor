using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TokenMonitor;

/// <summary>
/// Бюджет аккаунта OpenCode: лимиты OpenCode Go (5 часов, неделя, месяц)
/// и политики бюджета OpenCode Console.
/// </summary>
public sealed class OpenCodeBudgetProvider : IBudgetProvider
{
    const string DefaultConsoleUrl = "https://opencode.ai/console";
    const string GoUsageUrl = "https://opencode.ai/zen/go/v1/usage";

    static readonly HttpClient Http = GlobalRouteHttpClient.CreateClient(TimeSpan.FromSeconds(10));
    static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    readonly AppSettings settings;

    public OpenCodeBudgetProvider(AppSettings settings) => this.settings = settings;

    public Uri Endpoint
    {
        get
        {
            if (ReadService() is { } service
                && Uri.TryCreate(service.Url.TrimEnd('/') + "/api/experimental/usage/limits", UriKind.Absolute, out var serviceUri))
            {
                return serviceUri;
            }

            if (TryGetAccount() is { } account
                && Uri.TryCreate(account.ConsoleUrl.TrimEnd('/') + "/api/policies/current-budgets", UriKind.Absolute, out var accountUri))
            {
                return accountUri;
            }

            if (ReadGoCredential() is not null)
                return new Uri(GoUsageUrl);

            return new Uri(DefaultConsoleUrl);
        }
    }

    public bool IsAvailable => ReadService() is not null || TryGetAccount() is not null || ReadGoCredential() is not null;

    public async Task<BudgetInfo> GetAsync(CancellationToken token = default)
    {
        var failures = new List<string>();

        if (ReadService() is { } service)
        {
            var local = await GetFromServiceAsync(service, token);
            if (local.Error is null) return local;
            failures.Add(local.Error);
        }

        if (TryGetAccount() is { } account)
        {
            var direct = await GetFromAccountAsync(account, token);
            if (direct.Error is null) return direct;
            failures.Add(direct.Error);
        }

        if (ReadGoCredential() is { } go)
        {
            var goResult = await GetFromGoAsync(go, token);
            if (goResult.Error is null) return goResult;
            failures.Add(goResult.Error);
        }

        var error = failures.Count > 0
            ? string.Join("; ", failures.Distinct())
            : "аккаунт OpenCode не подключён: войдите в OpenCode";

        return Fail(error, Endpoint);
    }

    async Task<BudgetInfo> GetFromServiceAsync(OpenCodeService service, CancellationToken token)
    {
        var endpoint = new Uri(service.Url.TrimEnd('/') + "/api/experimental/usage/limits");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicToken(service.Password));
            request.Headers.TryAddWithoutValidation("User-Agent", "TokenMonitor/1.0");

            using var response = await Http.SendAsync(request, token);
            var body = await response.Content.ReadAsStringAsync(token);

            if (!response.IsSuccessStatusCode)
                return Fail($"OpenCode ответил {(int)response.StatusCode}", endpoint);

            using var document = JsonDocument.Parse(body, JsonOptions);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null)
                return Fail("аккаунт OpenCode Console не подключён", endpoint);

            return Build(data, endpoint.ToString());
        }
        catch (Exception error)
        {
            return Fail(error.Message, endpoint);
        }
    }

    async Task<BudgetInfo> GetFromGoAsync(OpenCodeGoCredential credential, CancellationToken token)
    {
        var endpoint = new Uri(GoUsageUrl);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Key);
            request.Headers.TryAddWithoutValidation("User-Agent", "TokenMonitor/1.0");

            using var response = await Http.SendAsync(request, token);
            var body = await response.Content.ReadAsStringAsync(token);

            if (!response.IsSuccessStatusCode)
            {
                return response.StatusCode == HttpStatusCode.Unauthorized
                    ? Fail("ключ OpenCode Go недействителен: войдите в OpenCode", endpoint)
                    : Fail($"OpenCode Go ответил {(int)response.StatusCode}", endpoint);
            }

            using var document = JsonDocument.Parse(body, JsonOptions);
            var root = document.RootElement;
            var usage = root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object
                ? usageElement
                : root;

            var meters = ReadMeters(usage);
            return MakeResult(meters, new List<BudgetEntry>(), credential.Label, endpoint.ToString(), Text(root, "plan"));
        }
        catch (Exception error)
        {
            return Fail(error.Message, endpoint);
        }
    }

    async Task<BudgetInfo> GetFromAccountAsync(OpenCodeAccount account, CancellationToken token)
    {
        var console = account.ConsoleUrl.TrimEnd('/');
        var origin = Uri.TryCreate(console, UriKind.Absolute, out var consoleUri)
            ? consoleUri.GetLeftPart(UriPartial.Authority)
            : "https://opencode.ai";

        var usageUrl = $"{origin}/zen/go/v1/usage";
        var budgetsUrl = $"{console}/api/policies/current-budgets";
        var sourceUrl = $"{console}/{account.OrgId}/activity/overview";

        try
        {
            var usageTask = GetJsonAsync(usageUrl, account, "x-opencode-org-id", token);
            var budgetsTask = GetJsonAsync(budgetsUrl, account, "x-org-id", token);
            await Task.WhenAll(usageTask, budgetsTask);

            var usage = await usageTask;
            var budgets = await budgetsTask;

            try
            {
                var meters = new List<BudgetMeter>();
                string? plan = null;

                if (usage.Document is not null)
                {
                    var root = usage.Document.RootElement;
                    if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
                        meters = ReadMeters(usageElement);
                    else if (root.TryGetProperty("go", out var goElement) && goElement.ValueKind == JsonValueKind.Object)
                        meters = ReadMeters(goElement);

                    plan = Text(root, "plan");
                }

                var budgetEntries = budgets.Document is not null
                    ? ReadBudgets(budgets.Document.RootElement)
                    : new List<BudgetEntry>();

                if (meters.Count == 0 && budgetEntries.Count == 0)
                {
                    var error = usage.Error ?? budgets.Error ?? "OpenCode не вернул данные о бюджете";
                    return Fail(error, new Uri(sourceUrl));
                }

                return MakeResult(meters, budgetEntries, account.Name, sourceUrl, plan);
            }
            finally
            {
                usage.Document?.Dispose();
                budgets.Document?.Dispose();
            }
        }
        catch (Exception error)
        {
            return Fail(error.Message, new Uri(sourceUrl));
        }
    }

    async Task<(JsonDocument? Document, string? Error)> GetJsonAsync(
        string url,
        OpenCodeAccount account,
        string orgHeader,
        CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.Access);
            request.Headers.TryAddWithoutValidation(orgHeader, account.OrgId);
            request.Headers.TryAddWithoutValidation("User-Agent", "TokenMonitor/1.0");

            using var response = await Http.SendAsync(request, token);
            var body = await response.Content.ReadAsStringAsync(token);

            if (!response.IsSuccessStatusCode)
            {
                return response.StatusCode == HttpStatusCode.Unauthorized
                    ? (null, "токен OpenCode истёк: запустите OpenCode, чтобы обновить вход")
                    : (null, $"OpenCode ответил {(int)response.StatusCode}");
            }

            return (JsonDocument.Parse(body, JsonOptions), null);
        }
        catch (Exception error)
        {
            return (null, error.Message);
        }
    }

    static BudgetInfo Build(JsonElement data, string fallbackSource)
    {
        var meters = data.TryGetProperty("go", out var go) && go.ValueKind == JsonValueKind.Object
            ? ReadMeters(go)
            : new List<BudgetMeter>();

        var budgets = data.TryGetProperty("budgets", out var budgetElement) && budgetElement.ValueKind == JsonValueKind.Array
            ? ReadBudgets(budgetElement)
            : new List<BudgetEntry>();

        string? alias = null;
        string? source = null;
        if (data.TryGetProperty("workspace", out var workspace) && workspace.ValueKind == JsonValueKind.Object)
        {
            alias = Text(workspace, "name") ?? Text(workspace, "id");
            source = Text(workspace, "usageURL");
        }

        var plan = Text(data, "plan");
        return MakeResult(meters, budgets, alias, source ?? fallbackSource, plan);
    }

    static BudgetInfo MakeResult(
        List<BudgetMeter> meters,
        List<BudgetEntry> budgets,
        string? alias,
        string? source,
        string? plan)
    {
        var primary = budgets
            .OrderBy(entry => entry.Remaining)
            .ThenByDescending(entry => entry.Limit > 0 ? entry.Spent / entry.Limit : 0)
            .FirstOrDefault();

        if (primary is not null)
        {
            return new BudgetInfo
            {
                Display = BudgetDisplay.Money,
                Spend = primary.Spent,
                MaxBudget = primary.Limit,
                ResetAt = primary.ResetAt,
                Alias = alias,
                SourceUrl = source,
                Plan = plan,
                Meters = meters,
                Provider = "OpenCode",
            };
        }

        if (meters.Count > 0)
        {
            var monthly = meters.FirstOrDefault(meter => meter.Label == "мес") ?? meters[^1];
            return new BudgetInfo
            {
                Display = BudgetDisplay.Percent,
                Percent = monthly.Percent,
                ResetAt = monthly.ResetAt,
                Alias = alias,
                SourceUrl = source,
                Plan = plan,
                Meters = meters,
                Provider = "OpenCode",
            };
        }

        return new BudgetInfo
        {
            Error = "OpenCode не вернул данные о бюджете",
            SourceUrl = source,
            Provider = "OpenCode",
        };
    }

    static List<BudgetMeter> ReadMeters(JsonElement parent)
    {
        var meters = new List<BudgetMeter>();
        Add("rolling", "5ч", "5 часов");
        Add("weekly", "нед", "неделя");
        Add("monthly", "мес", "месяц");
        return meters;

        void Add(string name, string label, string title)
        {
            if (!parent.TryGetProperty(name, out var meter) || meter.ValueKind != JsonValueKind.Object) return;

            var limited = meter.TryGetProperty("limited", out var limitedElement) && limitedElement.ValueKind == JsonValueKind.True;
            if (!limited && Text(meter, "status") == "rate-limited") limited = true;

            meters.Add(new BudgetMeter(
                label,
                title,
                Number(meter, "percent") ?? 0,
                Time(meter, "resetsAt"),
                limited));
        }
    }

    static List<BudgetEntry> ReadBudgets(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) return new List<BudgetEntry>();

        var budgets = new List<BudgetEntry>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var limit = Number(item, "limit") ?? Number(item, "limitDollars");
            if (limit is null) continue;

            budgets.Add(new BudgetEntry(
                limit.Value,
                Number(item, "spent") ?? Number(item, "spentDollars") ?? 0,
                Time(item, "resetsAt")));
        }

        return budgets;
    }

    OpenCodeService? ReadService()
    {
        foreach (var path in ServicePaths())
        {
            try
            {
                if (!File.Exists(path)) continue;

                using var document = JsonDocument.Parse(File.ReadAllText(path), JsonOptions);
                var root = document.RootElement;
                var url = Text(root, "url");
                var password = Text(root, "password");

                if (!string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(password))
                    return new OpenCodeService(url.TrimEnd('/'), password);
            }
            catch
            {
            }
        }

        return null;
    }

    static IEnumerable<string> ServicePaths()
    {
        var stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrWhiteSpace(stateHome))
            yield return Path.Combine(stateHome, "opencode", "service.json");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".local", "state", "opencode", "service.json");
        yield return Path.Combine(home, ".config", "opencode", "service.json");
    }

    static string DataDir()
    {
        var configured = Environment.GetEnvironmentVariable("OPENCODE_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return string.IsNullOrWhiteSpace(dataHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "opencode")
            : Path.Combine(dataHome, "opencode");
    }

    OpenCodeGoCredential? ReadGoCredential()
    {
        var configuredKey = settings.OpenCode.ApiKey;
        if (!string.IsNullOrWhiteSpace(configuredKey)
            && configuredKey.StartsWith("oc_sk_", StringComparison.OrdinalIgnoreCase))
        {
            return new OpenCodeGoCredential(configuredKey, "OpenCode Go");
        }

        var dbPath = Path.Combine(DataDir(), "opencode.db");
        if (!File.Exists(dbPath)) return null;

        return ReadGoCredential(dbPath, activeOnly: true)
            ?? ReadGoCredential(dbPath, activeOnly: false);
    }

    static OpenCodeGoCredential? ReadGoCredential(string dbPath, bool activeOnly)
    {
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 5,
            };

            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();

            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA busy_timeout = 3000;";
            pragma.ExecuteNonQuery();

            using var command = connection.CreateCommand();
            command.CommandText = activeOnly
                ? "SELECT label, value FROM credential WHERE integration_id = 'opencode-go' AND active = 1 ORDER BY time_updated DESC LIMIT 1"
                : "SELECT label, value FROM credential WHERE integration_id = 'opencode-go' ORDER BY active DESC, time_updated DESC LIMIT 1";

            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;

            return ParseGoCredential(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1));
        }
        catch
        {
            return null;
        }
    }

    static OpenCodeGoCredential? ParseGoCredential(string? label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        try
        {
            using var document = JsonDocument.Parse(value, JsonOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var key = Text(root, "key") ?? Text(root, "access");
            if (string.IsNullOrWhiteSpace(key)) return null;

            return new OpenCodeGoCredential(key, string.IsNullOrWhiteSpace(label) ? "OpenCode Go" : label);
        }
        catch
        {
            return null;
        }
    }

    OpenCodeAccount? TryGetAccount()
    {
        var configuredKey = settings.OpenCode.ApiKey;
        var configuredOrg = settings.OpenCode.OrgId;

        if (!string.IsNullOrWhiteSpace(configuredKey) && !string.IsNullOrWhiteSpace(configuredOrg))
            return new OpenCodeAccount(configuredKey, configuredOrg, DefaultConsoleUrl, null);

        var baseDir = DataDir();

        foreach (var name in new[] { "account.json", "auth.json" })
        {
            var account = ReadAccountFile(Path.Combine(baseDir, name));
            if (account is not null) return account;
        }

        return null;
    }

    OpenCodeAccount? ReadAccountFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            using var document = JsonDocument.Parse(File.ReadAllText(path), JsonOptions);
            var root = document.RootElement;

            if (root.TryGetProperty("accounts", out var accounts) && accounts.ValueKind == JsonValueKind.Object)
            {
                string? activeId = null;
                if (root.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.Object
                    && active.TryGetProperty("opencode", out var activeElement) && activeElement.ValueKind == JsonValueKind.String)
                {
                    activeId = activeElement.GetString();
                }

                OpenCodeAccount? fallback = null;
                foreach (var entry in accounts.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                    if (Text(entry.Value, "serviceID") != "opencode") continue;

                    var credential = entry.Value.TryGetProperty("credential", out var credentialElement)
                        ? credentialElement
                        : entry.Value;

                    var parsed = ParseCredential(credential, entry.Value);
                    if (parsed is null) continue;

                    if (activeId is not null && Text(entry.Value, "id") == activeId) return parsed;
                    fallback ??= parsed;
                }

                if (fallback is not null) return fallback;
            }

            if (root.TryGetProperty("opencode", out var legacy) && legacy.ValueKind == JsonValueKind.Object)
                return ParseCredential(legacy);
        }
        catch
        {
        }

        return null;
    }

    OpenCodeAccount? ParseCredential(JsonElement credential, JsonElement? entry = null)
    {
        if (credential.ValueKind != JsonValueKind.Object) return null;

        var access = settings.OpenCode.ApiKey;
        if (string.IsNullOrWhiteSpace(access))
            access = Text(credential, "access") ?? Text(credential, "key");

        var orgId = settings.OpenCode.OrgId;
        var server = default(string);
        var name = default(string);

        var metadata = default(JsonElement?);
        if (credential.TryGetProperty("metadata", out var credentialMetadata) && credentialMetadata.ValueKind == JsonValueKind.Object)
            metadata = credentialMetadata;
        else if (entry is { } entryValue
            && entryValue.TryGetProperty("metadata", out var entryMetadata)
            && entryMetadata.ValueKind == JsonValueKind.Object)
            metadata = entryMetadata;

        if (metadata is { } meta)
        {
            if (string.IsNullOrWhiteSpace(orgId)) orgId = Text(meta, "orgID") ?? Text(meta, "orgId");
            server = Text(meta, "server");
            name = Text(meta, "orgName") ?? Text(meta, "email");
        }

        if (string.IsNullOrWhiteSpace(orgId)) orgId = Text(credential, "orgID") ?? Text(credential, "orgId");
        server ??= Text(credential, "server");

        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(orgId)) return null;

        return new OpenCodeAccount(
            access,
            orgId,
            string.IsNullOrWhiteSpace(server) ? DefaultConsoleUrl : server,
            name);
    }

    static string BasicToken(string password) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + password));

    static BudgetInfo Fail(string error, Uri source) => new()
    {
        Error = error,
        SourceUrl = source.ToString(),
        Provider = "OpenCode",
    };

    static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    static double? Number(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    static DateTime? Time(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)) return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var milliseconds))
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToLocalTime().DateTime;

        if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var parsed))
            return parsed.ToLocalTime().DateTime;

        return null;
    }

    sealed record OpenCodeService(string Url, string Password);

    sealed record OpenCodeGoCredential(string Key, string Label);

    sealed record OpenCodeAccount(string Access, string OrgId, string ConsoleUrl, string? Name);

    sealed record BudgetEntry(double Limit, double Spent, DateTime? ResetAt)
    {
        public double Remaining => Math.Max(0, Limit - Spent);
    }
}
