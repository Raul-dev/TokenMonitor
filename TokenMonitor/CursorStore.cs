using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TokenMonitor;

public sealed class CursorStore
{
    readonly AppSettings settings;
    readonly CursorClient client;

    List<CursorUsageEvent> dashboardCache = new();
    DateTime dashboardCacheAt = DateTime.MinValue;
    DateTime lastRemoteAttempt = DateTime.MinValue;
    string? dashboardError;

    public CursorStore(AppSettings settings)
    {
        this.settings = settings;
        client = new CursorClient(settings);
    }

    public string EventsPath => string.IsNullOrWhiteSpace(settings.Cursor.EventsPath)
        ? CursorClient.DefaultEventsPath
        : settings.Cursor.EventsPath;

    public async Task<DataSnapshot> LoadAsync(bool refreshRemote, CancellationToken token = default)
    {
        var due = refreshRemote || lastRemoteAttempt == DateTime.MinValue
            || (DateTime.UtcNow - lastRemoteAttempt).TotalSeconds >= Math.Max(30, settings.BudgetRefreshSeconds);
        if (due)
        {
            lastRemoteAttempt = DateTime.UtcNow;
            var now = DateTime.Now;
            var start = now.AddDays(-40);
            var (events, error) = await client.GetDashboardEventsAsync(start, now.AddDays(1), token);
            dashboardError = error;
            if (events.Count > 0)
            {
                dashboardCache = events;
                dashboardCacheAt = DateTime.UtcNow;
            }
        }

        var local = LoadLocal();
        var merged = Merge(dashboardCache, local);
        var sessions = ToSessions(merged);

        var signature = (long)merged.Count;
        if (merged.Count > 0) signature = signature * 31 + merged.Max(item => item.Time.Ticks);

        var eventsPath = EventsPath;
        if (!string.IsNullOrWhiteSpace(dashboardError) && local.Count == 0)
            eventsPath = dashboardError;

        return new DataSnapshot
        {
            Sessions = sessions,
            Live = merged
                .OrderBy(item => item.Time)
                .Select(ToLive)
                .ToList(),
            Signature = signature,
            DbPath = client.TryLoadSession(out _, out _, out var source, out var authError)
                ? source
                : authError ?? CursorClient.DefaultStateDbPath,
            EventsPath = File.Exists(EventsPath) ? EventsPath : eventsPath,
            LoadedAt = DateTime.Now,
        };
    }

    public Task<CursorPeriodUsage> GetPeriodAsync(CancellationToken token = default) =>
        client.GetPeriodAsync(token);

    List<CursorUsageEvent> LoadLocal()
    {
        var list = new List<CursorUsageEvent>();
        if (!File.Exists(EventsPath)) return list;

        foreach (var line in ReadShared(EventsPath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{') continue;

            try
            {
                using var document = JsonDocument.Parse(trimmed);
                var root = document.RootElement;
                var timeText = Text(root, "time");
                var time = DateTime.TryParse(timeText, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed.ToLocalTime()
                    : DateTime.Now;

                list.Add(new CursorUsageEvent
                {
                    Time = time,
                    ConversationId = Text(root, "sessionId") ?? string.Empty,
                    MessageId = Text(root, "messageId") ?? string.Empty,
                    Model = Text(root, "model") ?? string.Empty,
                    Prompt = Text(root, "prompt") ?? string.Empty,
                    Workspace = Text(root, "workspace") ?? string.Empty,
                    Tokens = new TokenCounts(
                        Number(root, "input"),
                        Number(root, "output"),
                        Number(root, "reasoning"),
                        Number(root, "cacheRead"),
                        Number(root, "cacheWrite")),
                    DurationMs = Number(root, "durationMs"),
                    Source = Text(root, "source") ?? "hook",
                });
            }
            catch (JsonException)
            {
            }
        }

        return list;
    }

    static List<CursorUsageEvent> Merge(List<CursorUsageEvent> dashboard, List<CursorUsageEvent> local)
    {
        var map = new Dictionary<string, CursorUsageEvent>(StringComparer.Ordinal);
        foreach (var item in dashboard.Concat(local))
        {
            var key = item.MessageId.Length != 0
                ? "m:" + item.MessageId
                : $"t:{item.Time.Ticks}:{item.ConversationId}:{item.Tokens.Total}";
            if (!map.TryGetValue(key, out var existing) || item.Source == "hook")
                map[key] = item;
            else if (existing.Prompt.Length == 0 && item.Prompt.Length != 0)
                map[key] = item;
        }

        return map.Values.ToList();
    }

    static List<SessionData> ToSessions(List<CursorUsageEvent> events)
    {
        var groups = new Dictionary<string, List<CursorUsageEvent>>(StringComparer.Ordinal);

        foreach (var item in events.OrderBy(value => value.Time))
        {
            var id = string.IsNullOrWhiteSpace(item.ConversationId)
                ? "day:" + item.Time.ToString("yyyy-MM-dd")
                : item.ConversationId;
            if (!groups.TryGetValue(id, out var list))
            {
                list = new List<CursorUsageEvent>();
                groups[id] = list;
            }
            list.Add(item);
        }

        var sessions = new List<SessionData>();
        foreach (var (id, items) in groups)
        {
            var firstPrompt = items.Select(item => item.Prompt).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? string.Empty;
            var workspace = items.Select(item => item.Workspace).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? string.Empty;
            var last = items[^1];

            var session = new SessionData
            {
                Id = id,
                Title = firstPrompt.Length == 0 ? ShortId(id) : Fmt.Preview(firstPrompt, 70),
                Directory = workspace,
                Model = last.Model,
                Created = items[0].Time,
                Updated = last.Time,
            };

            foreach (var item in items)
            {
                var request = new RequestData
                {
                    Id = item.MessageId.Length != 0 ? item.MessageId : Guid.NewGuid().ToString("N"),
                    SessionId = id,
                    Prompt = item.Prompt,
                    Time = item.Time,
                    Session = session,
                    Tokens = item.Tokens,
                };

                request.Steps.Add(new StepData
                {
                    Id = request.Id,
                    Time = item.Time,
                    Completed = item.DurationMs > 0 ? item.Time.AddMilliseconds(item.DurationMs) : item.Time,
                    Model = item.Model,
                    Tokens = item.Tokens,
                });

                session.Requests.Add(request);
                session.Tokens += item.Tokens;
            }

            sessions.Add(session);
        }

        return sessions;
    }

    static LiveEvent ToLive(CursorUsageEvent item) => new()
    {
        Time = item.Time,
        SessionId = item.ConversationId,
        MessageId = item.MessageId,
        Model = item.Model,
        Steps = 1,
        Input = item.Tokens.Input,
        Output = item.Tokens.Output,
        Reasoning = item.Tokens.Reasoning,
        CacheRead = item.Tokens.CacheRead,
        CacheWrite = item.Tokens.CacheWrite,
        Billed = item.Tokens.Total,
        Context = item.Tokens.Input + item.Tokens.CacheRead,
        DurationMs = item.DurationMs,
    };

    static string ShortId(string id)
    {
        if (id.StartsWith("day:", StringComparison.Ordinal)) return "день " + id[4..];
        return id.Length <= 12 ? id : id[..8] + "…";
    }

    static IEnumerable<string> ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Split('\n');
    }

    static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : "";

    static long Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;
}
