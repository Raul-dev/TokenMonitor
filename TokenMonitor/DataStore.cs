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
    public string LoadError { get; init; } = string.Empty;
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
        List<SessionData> sessions;
        List<LiveEvent> live;
        long signature;
        string loadError = string.Empty;

        try
        {
            using var connection = Open();

            var hasV2 = HasTable(connection, "session_v2") && HasTable(connection, "session_message");
            var hasV1 = HasTable(connection, "session") && HasTable(connection, "message");

            if (hasV2)
            {
                sessions = LoadSessionsV2(connection);
                live = LiveFromRequests(sessions);
                signature = ComputeSignatureV2(connection);
                loadError = $"V2: {sessions.Count} сессий";
            }
            else if (hasV1)
            {
                sessions = LoadSessions(connection);
                live = LoadLive();
                signature = ComputeSignature(connection);
                loadError = $"V1: {sessions.Count} сессий";
            }
            else
            {
                sessions = new List<SessionData>();
                live = LoadLive();
                signature = 0;
                loadError = "нет таблиц session/session_v2";
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            sessions = new List<SessionData>();
            live = LoadLive();
            signature = 0;
            loadError = $"SQLite ошибка: {ex.Message}";
        }
        catch (Exception ex)
        {
            sessions = new List<SessionData>();
            live = LoadLive();
            signature = 0;
            loadError = $"ошибка: {ex.Message}";
        }

        return new DataSnapshot
        {
            Sessions = sessions,
            Live = live,
            Signature = signature,
            DbPath = DbPath,
            EventsPath = EventsPath,
            LogPath = LogPath,
            LoadedAt = DateTime.Now,
            LoadError = loadError,
        };
    }

    static bool HasTable(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
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

    List<SessionData> LoadSessions(SqliteConnection connection)
    {
        var sessions = new Dictionary<string, SessionData>(StringComparer.Ordinal);
        var requests = new Dictionary<string, RequestData>(StringComparer.Ordinal);
        var users = new Dictionary<string, (DateTime Time, string Text)>(StringComparer.Ordinal);

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

    // opencode >= 2.x хранит историю в session_v2/session_message:
    // сообщение — строка с type ('user'/'assistant'/...) и JSON в data.
    // Запрос = user-сообщение + все assistant-сообщения до следующего user.
    List<SessionData> LoadSessionsV2(SqliteConnection connection)
    {
        var sessions = new Dictionary<string, SessionData>(StringComparer.Ordinal);
        var open = new Dictionary<string, RequestData>(StringComparer.Ordinal);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, IFNULL(parent_id, ''), IFNULL(title, ''), directory,
                       IFNULL(json_extract(model, '$.id'), ''), time_created, time_updated
                FROM session_v2
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
                SELECT id, session_id, type, time_created, data
                FROM session_message
                ORDER BY session_id, seq
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var sessionId = reader.GetString(1);
                var type = reader.GetString(2);
                var createdColumn = reader.GetInt64(3);
                var data = reader.GetString(4);

                if (!sessions.TryGetValue(sessionId, out var session)) continue;

                using var document = JsonDocument.Parse(data);
                var root = document.RootElement;

                if (type == "user")
                {
                    var time = JsonTime(root, "time", "created") is { } userCreated && userCreated > 0
                        ? FromMs(userCreated)
                        : FromMs(createdColumn);
                    var text = JsonText(root, "text");
                    if (text.Length == 0) text = JsonText(root, "metadata", "displayText");

                    var request = new RequestData
                    {
                        Id = id,
                        SessionId = sessionId,
                        Session = session,
                        Time = time,
                        Prompt = text,
                    };
                    session.Requests.Add(request);
                    open[sessionId] = request;
                    continue;
                }

                if (type != "assistant") continue;

                var created = JsonTime(root, "time", "created") is { } stepCreated && stepCreated > 0
                    ? FromMs(stepCreated)
                    : FromMs(createdColumn);
                var completedValue = JsonTime(root, "time", "completed");
                var completed = completedValue > 0 ? FromMs(completedValue) : created;

                var tokens = new TokenCounts(
                    JsonNumber(root, "tokens", "input"),
                    JsonNumber(root, "tokens", "output"),
                    JsonNumber(root, "tokens", "reasoning"),
                    JsonNumber(root, "tokens", "cache", "read"),
                    JsonNumber(root, "tokens", "cache", "write"));

                var model = JsonText(root, "model", "id");
                if (model.Length == 0) model = JsonText(root, "modelID");

                if (!open.TryGetValue(sessionId, out var request2))
                {
                    request2 = new RequestData
                    {
                        Id = id,
                        SessionId = sessionId,
                        Session = session,
                        Time = created,
                    };
                    session.Requests.Add(request2);
                    open[sessionId] = request2;
                }

                request2.Steps.Add(new StepData
                {
                    Id = id,
                    Time = created,
                    Completed = completed,
                    Model = model,
                    Tokens = tokens,
                });
                request2.Tokens += tokens;
                session.Tokens += tokens;
            }
        }

        foreach (var session in sessions.Values)
        {
            session.Requests.Sort((a, b) => a.Time.CompareTo(b.Time));
        }

        return sessions.Values.ToList();
    }

    static long JsonNumber(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
                return 0;
        }
        return current.ValueKind == JsonValueKind.Number ? current.GetInt64() : 0;
    }

    static string JsonText(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
                return string.Empty;
            if (current.ValueKind == JsonValueKind.String) return current.GetString() ?? string.Empty;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() ?? string.Empty : string.Empty;
    }

    static long JsonTime(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
                return 0;
        }
        return current.ValueKind == JsonValueKind.Number ? current.GetInt64() : 0;
    }

    long ComputeSignatureV2(SqliteConnection connection)
    {
        try
        {
            var result = 17L;

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT IFNULL(MAX(time_updated), 0), COUNT(*) FROM session_message";
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    result = result * 31 + reader.GetInt64(0);
                    result = result * 31 + reader.GetInt64(1);
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT IFNULL(MAX(time_updated), 0), COUNT(*) FROM session_v2";
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    result = result * 31 + reader.GetInt64(0);
                    result = result * 31 + reader.GetInt64(1);
                }
            }

            return result;
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    // В opencode 2.x плагин token-stats.ts несовместим со встроенным SDK,
    // поэтому ленту строим из самих данных: один запрос = user + его шаги.
    List<LiveEvent> LiveFromRequests(List<SessionData> sessions)
    {
        var events = new List<LiveEvent>();

        foreach (var session in sessions)
        {
            foreach (var request in session.Requests)
            {
                if (request.Steps.Count == 0) continue;

                var last = request.Steps[^1];
                var end = last.Completed > DateTime.MinValue ? last.Completed : last.Time;

                events.Add(new LiveEvent
                {
                    Time = end,
                    SessionId = session.Id,
                    MessageId = last.Id,
                    Model = last.Model,
                    Steps = request.Steps.Count,
                    Input = request.Tokens.Input,
                    Output = request.Tokens.Output,
                    Reasoning = request.Tokens.Reasoning,
                    CacheRead = request.Tokens.CacheRead,
                    CacheWrite = request.Tokens.CacheWrite,
                    Billed = request.Tokens.Total,
                    Context = last.Tokens.Input + last.Tokens.CacheRead,
                    DurationMs = request.DurationMs,
                });
            }
        }

        events.Sort((a, b) => a.Time.CompareTo(b.Time));
        if (events.Count > 500) events = events.GetRange(events.Count - 500, 500);
        return events;
    }

    long ComputeSignature(SqliteConnection connection)
    {
        try
        {
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
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return 0;
        }
        catch (Exception)
        {
            return 0;
        }
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
