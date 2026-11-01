using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace TokenMonitor;

public sealed class DataSnapshot
{
    public List<SessionData> Sessions { get; init; } = new();
    public List<LiveEvent> Live { get; init; } = new();
    public long Signature { get; init; }
    public string DbPath { get; init; } = string.Empty;
    public string EventsPath { get; init; } = string.Empty;
    public string LogPath { get; init; } = string.Empty;
    public DateTime LoadedAt { get; init; }
}

public sealed class DataStore
{
    static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public string BaseDir { get; }
    public string DbPath => Path.Combine(BaseDir, "opencode.db");
    public string EventsPath => Path.Combine(BaseDir, "token-events.jsonl");
    public string LogPath => Path.Combine(BaseDir, "token-stats.log");

    public DataStore(string? baseDir = null)
    {
        BaseDir = baseDir
            ?? Environment.GetEnvironmentVariable("OPENCODE_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "opencode");
    }

    public DataSnapshot Load()
    {
        var sessions = LoadSessions();
        return new DataSnapshot
        {
            Sessions = sessions,
            Live = LoadLive(),
            Signature = ComputeSignature(),
            DbPath = DbPath,
            EventsPath = EventsPath,
            LogPath = LogPath,
            LoadedAt = DateTime.Now,
        };
    }

    SqliteConnection Open()
    {
        if (!File.Exists(DbPath)) throw new FileNotFoundException($"Не найдена база opencode: {DbPath}");

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5,
        };

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 3000;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    static DateTime FromMs(long ms) => ms <= 0 ? DateTime.MinValue : Epoch.AddMilliseconds(ms).ToLocalTime();

    List<SessionData> LoadSessions()
    {
        var sessions = new Dictionary<string, SessionData>(StringComparer.Ordinal);
        var requests = new Dictionary<string, RequestData>(StringComparer.Ordinal);
        var users = new Dictionary<string, (DateTime Time, string Text)>(StringComparer.Ordinal);

        using var connection = Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, IFNULL(parent_id, ''), title, directory,
                       IFNULL(json_extract(model, '$.id'), ''), time_created, time_updated
                FROM session
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var session = new SessionData
                {
                    Id = reader.GetString(0),
                    ParentId = reader.GetString(1),
                    Title = reader.GetString(2),
                    Directory = reader.GetString(3),
                    Model = reader.GetString(4),
                    Created = FromMs(reader.GetInt64(5)),
                    Updated = FromMs(reader.GetInt64(6)),
                };
                sessions[session.Id] = session;
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, session_id, time_created,
                       IFNULL(json_extract(data, '$.role'), ''),
                       IFNULL(json_extract(data, '$.parentID'), ''),
                       IFNULL(json_extract(data, '$.tokens.input'), 0),
                       IFNULL(json_extract(data, '$.tokens.output'), 0),
                       IFNULL(json_extract(data, '$.tokens.reasoning'), 0),
                       IFNULL(json_extract(data, '$.tokens.cache.read'), 0),
                       IFNULL(json_extract(data, '$.tokens.cache.write'), 0),
                       IFNULL(json_extract(data, '$.time.completed'), 0),
                       IFNULL(json_extract(data, '$.modelID'), '')
                FROM message
                ORDER BY time_created
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var sessionId = reader.GetString(1);
                var created = FromMs(reader.GetInt64(2));
                var role = reader.GetString(3);

                if (role == "user")
                {
                    users[id] = (created, string.Empty);
                    continue;
                }

                if (role != "assistant") continue;
                if (!sessions.TryGetValue(sessionId, out var session)) continue;

                var parentId = reader.GetString(4);
                var tokens = new TokenCounts(
                    reader.GetInt64(5),
                    reader.GetInt64(6),
                    reader.GetInt64(7),
                    reader.GetInt64(8),
                    reader.GetInt64(9));
                var completed = reader.GetInt64(10);
                var model = reader.GetString(11);

                var requestId = parentId.Length == 0 ? id : parentId;
                var requestKey = sessionId + "|" + requestId;

                if (!requests.TryGetValue(requestKey, out var request))
                {
                    var start = users.TryGetValue(requestId, out var user) ? user.Time : created;
                    request = new RequestData
                    {
                        Id = requestId,
                        SessionId = sessionId,
                        Session = session,
                        Time = start,
                    };
                    requests[requestKey] = request;
                }

                request.Steps.Add(new StepData
                {
                    Id = id,
                    Time = created,
                    Completed = completed == 0 ? created : FromMs(completed),
                    Model = model,
                    Tokens = tokens,
                });
                request.Tokens += tokens;
                session.Tokens += tokens;
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT p.message_id, IFNULL(json_extract(p.data, '$.text'), '')
                FROM part p
                JOIN message m ON m.id = p.message_id
                WHERE json_extract(m.data, '$.role') = 'user'
                  AND json_extract(p.data, '$.type') = 'text'
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var messageId = reader.GetString(0);
                var text = reader.GetString(1);
                if (text.Length == 0) continue;
                if (!users.TryGetValue(messageId, out var user)) continue;
                users[messageId] = (user.Time, user.Text.Length == 0 ? text : user.Text + "\n" + text);
            }
        }

        foreach (var request in requests.Values)
        {
            if (users.TryGetValue(request.Id, out var user))
            {
                request.Prompt = user.Text;
                request.Time = user.Time;
            }
            request.Session.Requests.Add(request);
        }

        foreach (var session in sessions.Values)
        {
            session.Requests.Sort((a, b) => a.Time.CompareTo(b.Time));
        }

        return sessions.Values.ToList();
    }

    long ComputeSignature()
    {
        using var connection = Open();
        var result = 17L;

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT IFNULL(MAX(time_updated), 0), COUNT(*) FROM message";
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                result = result * 31 + reader.GetInt64(0);
                result = result * 31 + reader.GetInt64(1);
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT IFNULL(MAX(time_updated), 0), COUNT(*) FROM session";
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                result = result * 31 + reader.GetInt64(0);
                result = result * 31 + reader.GetInt64(1);
            }
        }

        return result;
    }

    List<LiveEvent> LoadLive()
    {
        var events = new List<LiveEvent>();

        if (File.Exists(EventsPath))
        {
            foreach (var line in ReadShared(EventsPath))
            {
                if (TryParseJson(line, out var live)) events.Add(live);
            }
        }

        if (events.Count == 0 && File.Exists(LogPath))
        {
            foreach (var line in ReadShared(LogPath))
            {
                if (TryParseReport(line, out var live)) events.Add(live);
            }
        }

        events.Sort((a, b) => a.Time.CompareTo(b.Time));
        if (events.Count > 500) events = events.GetRange(events.Count - 500, 500);
        return events;
    }

    static IEnumerable<string> ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = reader.ReadToEnd();
        return text.Split('\n');
    }

    static bool TryParseJson(string line, out LiveEvent live)
    {
        live = null!;
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed[0] != '{') return false;

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;

            long Number(string name) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;

            string Text(string name) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

            var timeText = Text("time");
            var time = DateTime.TryParse(timeText, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed.ToLocalTime()
                : DateTime.Now;

            live = new LiveEvent
            {
                Time = time,
                SessionId = Text("sessionId"),
                MessageId = Text("messageId"),
                Model = Text("model"),
                Steps = (int)Number("steps"),
                Input = Number("input"),
                Output = Number("output"),
                Reasoning = Number("reasoning"),
                CacheRead = Number("cacheRead"),
                CacheWrite = Number("cacheWrite"),
                Billed = Number("billed"),
                Context = Number("context"),
                DurationMs = Number("durationMs"),
            };

            return live.SessionId.Length != 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    static readonly Regex ReportRegex = new(
        @"^(?<ts>\S+)\s+report\s+(?<session>\S+)\s+(?<billed>[0-9.,]+[kmb]?)\s+tokens\s+in\s+(?<duration>\S+)\s*\|(?<rest>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static readonly Regex SplitRegex = new(@"split\s+(?<in>[0-9.,]+[kmb]?)\s*\+\s*(?<out>[0-9.,]+[kmb]?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex StepsRegex = new(@"steps\s+(?<steps>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex ContextRegex = new(@"ctx\s+(?<ctx>[0-9.,]+[kmb]?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex ReasoningRegex = new(@"reasoning\s+(?<value>[0-9.,]+[kmb]?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static bool TryParseReport(string line, out LiveEvent live)
    {
        live = null!;
        var match = ReportRegex.Match(line.Trim());
        if (!match.Success) return false;

        if (!DateTime.TryParse(match.Groups["ts"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time))
            return false;

        var rest = match.Groups["rest"].Value;
        var split = SplitRegex.Match(rest);
        var steps = StepsRegex.Match(rest);
        var context = ContextRegex.Match(rest);
        var reasoning = ReasoningRegex.Match(rest);

        live = new LiveEvent
        {
            Time = time.ToLocalTime(),
            SessionId = match.Groups["session"].Value,
            Billed = ParseShort(match.Groups["billed"].Value),
            DurationMs = ParseDurationMs(match.Groups["duration"].Value),
            Input = split.Success ? ParseShort(split.Groups["in"].Value) : 0,
            Output = split.Success ? ParseShort(split.Groups["out"].Value) : 0,
            Steps = steps.Success ? int.Parse(steps.Groups["steps"].Value, CultureInfo.InvariantCulture) : 0,
            Context = context.Success ? ParseShort(context.Groups["ctx"].Value) : 0,
            Reasoning = reasoning.Success ? ParseShort(reasoning.Groups["value"].Value) : 0,
        };

        return true;
    }

    static long ParseShort(string text)
    {
        text = text.Trim().Replace(',', '.');
        if (text.Length == 0) return 0;

        var multiplier = 1.0;
        var last = char.ToLowerInvariant(text[^1]);
        if (last is 'k' or 'm' or 'b')
        {
            multiplier = last switch { 'k' => 1_000, 'm' => 1_000_000, _ => 1_000_000_000 };
            text = text[..^1];
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? (long)Math.Round(value * multiplier)
            : 0;
    }

    static long ParseDurationMs(string text)
    {
        long total = 0;
        foreach (Match match in Regex.Matches(text, @"(?<value>\d+(?:[.,]\d+)?)(?<unit>[hms])", RegexOptions.IgnoreCase))
        {
            var value = double.Parse(match.Groups["value"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            total += char.ToLowerInvariant(match.Groups["unit"].Value[0]) switch
            {
                'h' => (long)Math.Round(value * 3_600_000),
                'm' => (long)Math.Round(value * 60_000),
                _ => (long)Math.Round(value * 1_000),
            };
        }
        return total;
    }
}
