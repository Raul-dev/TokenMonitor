using System.IO;
using Microsoft.Data.Sqlite;

namespace TokenMonitor;

/// <summary>
/// Читатель базы плагина @local/dsh-token-logger (DeepSeek Harness).
///
/// Единственный контракт — представления <c>v_*</c> из
/// <c>DeepSeekSetup/schema.sql</c>. Store ничего не пишет в базу:
/// после Open выполняется <c>PRAGMA query_only = 1</c> (PLAN §7.2).
/// </summary>
public sealed class DeepSeekStore
{
    static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Максимальная версия схемы, которую понимает эта сборка TokenMonitor.</summary>
    public const int SupportedSchemaVersion = 1;

    public string DbPath { get; }

    public DeepSeekStore(string dbPath) => DbPath = dbPath;

    public DeepSeekStore(AppSettings settings) : this(settings.DeepSeek.DbPath ?? SettingsStore.DefaultDeepSeekDbPath)
    {
    }

    public DataSnapshot Load()
    {
        if (!File.Exists(DbPath))
        {
            return DataSnapshot.Empty(
                DbPath,
                $"БД плагина не найдена: {DbPath}. Установите плагин @local/dsh-token-logger и выполните запрос в DeepSeek Harness.");
        }

        try
        {
            using var connection = Open();
            var note = CheckSchema(connection);

            var sessions = LoadSessions(connection);
            if (sessions.Count == 0)
            {
                return new DataSnapshot
                {
                    Sessions = new List<SessionData>(),
                    Live = new List<LiveEvent>(),
                    Signature = 0,
                    DbPath = DbPath,
                    EventsPath = EventsPathText,
                    LoadedAt = DateTime.Now,
                    LoadError = note.Length > 0 ? note : "плагин ещё не записал ни одной сессии",
                    ContextWindow = LoadContextWindow(connection),
                };
            }

            var ids = sessions.Select(session => session.Id).ToList();
            LoadTurns(connection, sessions, ids);
            LoadSteps(connection, sessions, ids);
            var live = LoadLive(connection);
            var signature = ComputeSignature(connection);

            return new DataSnapshot
            {
                Sessions = sessions,
                Live = live,
                Signature = signature,
                DbPath = DbPath,
                EventsPath = EventsPathText,
                LoadedAt = DateTime.Now,
                LoadError = note,
                ContextWindow = LoadContextWindow(connection),
            };
        }
        catch (SqliteException error)
        {
            return DataSnapshot.Empty(DbPath, $"SQLite: {error.Message}");
        }
        catch (Exception error)
        {
            return DataSnapshot.Empty(DbPath, $"ошибка чтения {Path.GetFileName(DbPath)}: {error.Message}");
        }
    }

    public string EventsPathText => "шаги из v_step_feed (tokens.db плагина token-logger)";

    SqliteConnection Open()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            // ReadWrite + query_only: так Microsoft.Data.Sqlite корректно читает WAL,
            // а запись запрещена на уровне самого соединения.
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
            DefaultTimeout = 5,
        };

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA query_only = 1; PRAGMA busy_timeout = 3000;";
        command.ExecuteNonQuery();

        return connection;
    }

    static string CheckSchema(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM meta WHERE key = 'schema_version'";
            var scalar = command.ExecuteScalar();
            if (scalar is null) return "в базе нет meta.schema_version";

            var version = Convert.ToInt32(scalar);
            return version > SupportedSchemaVersion
                ? $"схема БД v{version} новее поддерживаемой v{SupportedSchemaVersion}; обновите TokenMonitor"
                : string.Empty;
        }
        catch (SqliteException error)
        {
            return $"нет таблицы meta: {error.Message}";
        }
    }

    static DateTime FromMs(long ms) => ms <= 0 ? DateTime.MinValue : Epoch.AddMilliseconds(ms).ToLocalTime();

    static bool HasColumn(SqliteConnection connection, string view, string column)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{view}') WHERE name = $name";
            command.Parameters.AddWithValue("$name", column);
            return Convert.ToInt64(command.ExecuteScalar() ?? 0L) > 0;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    static long Int64(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? 0 : reader.GetInt64(index);

    static string Text(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? string.Empty : reader.GetString(index);

    List<SessionData> LoadSessions(SqliteConnection connection)
    {
        var sessions = new List<SessionData>();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, IFNULL(parent_session_id, ''), IFNULL(title, ''), IFNULL(cwd, ''),
                   IFNULL(model, ''), IFNULL(created_ms, 0), IFNULL(updated_ms, 0),
                   IFNULL(input_tokens, 0), IFNULL(output_tokens, 0), IFNULL(reasoning_tokens, 0),
                   IFNULL(cache_read_tokens, 0), IFNULL(cache_write_tokens, 0)
            FROM v_session_summary
            ORDER BY updated_ms DESC
            LIMIT 200
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            sessions.Add(new SessionData
            {
                Id = reader.GetString(0),
                ParentId = reader.GetString(1),
                Title = reader.GetString(2),
                Directory = reader.GetString(3),
                Model = reader.GetString(4),
                Created = FromMs(Int64(reader, 5)),
                Updated = FromMs(Int64(reader, 6)),
                Tokens = new TokenCounts(
                    Int64(reader, 7),
                    Int64(reader, 8),
                    Int64(reader, 9),
                    Int64(reader, 10),
                    Int64(reader, 11)),
            });
        }

        return sessions;
    }

    void LoadTurns(SqliteConnection connection, List<SessionData> sessions, List<string> ids)
    {
        var byId = sessions.ToDictionary(session => session.Id, StringComparer.Ordinal);
        var parameters = new List<string>();

        using var command = connection.CreateCommand();
        for (var index = 0; index < ids.Count; index++)
        {
            var name = $"$s{index}";
            parameters.Add(name);
            command.Parameters.AddWithValue(name, ids[index]);
        }

        command.CommandText = $"""
            SELECT session_id, turn, IFNULL(prompt_preview, ''), IFNULL(started_ms, 0),
                   IFNULL(duration_ms, 0), IFNULL(input_tokens, 0), IFNULL(output_tokens, 0),
                   IFNULL(reasoning_tokens, 0), IFNULL(cache_read_tokens, 0),
                   IFNULL(cache_write_tokens, 0), IFNULL(steps_count, 0), IFNULL(total_tokens, 0)
            FROM v_turn_summary
            WHERE session_id IN ({string.Join(", ", parameters)})
            ORDER BY started_ms
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!byId.TryGetValue(reader.GetString(0), out var session)) continue;

            var turn = reader.GetInt64(1);
            var request = new RequestData
            {
                Id = "turn-" + turn,
                SessionId = session.Id,
                Session = session,
                Prompt = reader.GetString(2),
                Time = FromMs(Int64(reader, 3)),
                Tokens = new TokenCounts(
                    Int64(reader, 5),
                    Int64(reader, 6),
                    Int64(reader, 7),
                    Int64(reader, 8),
                    Int64(reader, 9)),
            };

            session.Requests.Add(request);
        }
    }

    void LoadSteps(SqliteConnection connection, List<SessionData> sessions, List<string> ids)
    {
        var requests = new Dictionary<string, RequestData>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            foreach (var request in session.Requests)
            {
                requests[session.Id + "|" + request.Id] = request;
            }
        }

        var parameters = new List<string>();
        using var command = connection.CreateCommand();
        for (var index = 0; index < ids.Count; index++)
        {
            var name = $"$s{index}";
            parameters.Add(name);
            command.Parameters.AddWithValue(name, ids[index]);
        }

        // started_ms — добавленная колонка v_step_feed; со старой базой работаем без неё.
        var hasStarted = HasColumn(connection, "v_step_feed", "started_ms");
        var startColumn = hasStarted ? "IFNULL(started_ms, 0)" : "IFNULL(time_ms, 0)";
        var filter = hasStarted ? "(time_ms IS NOT NULL OR started_ms IS NOT NULL)" : "time_ms IS NOT NULL";

        command.CommandText = $"""
            SELECT session_id, turn, step, {startColumn}, IFNULL(time_ms, 0), IFNULL(duration_ms, 0),
                   IFNULL(model, ''), IFNULL(input_tokens, 0), IFNULL(output_tokens, 0),
                   IFNULL(reasoning_tokens, 0), IFNULL(cache_read_tokens, 0),
                   IFNULL(cache_write_tokens, 0)
            FROM v_step_feed
            WHERE session_id IN ({string.Join(", ", parameters)}) AND {filter}
            ORDER BY session_id, turn, step
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var sessionId = reader.GetString(0);
            var turn = reader.GetInt64(1);
            var step = reader.GetInt64(2);

            if (!requests.TryGetValue(sessionId + "|turn-" + turn, out var request)) continue;

            var startedMs = Int64(reader, 3);
            var ended = Int64(reader, 4);
            var duration = Int64(reader, 5);
            if (startedMs == 0 && ended > 0 && duration > 0) startedMs = Math.Max(0, ended - duration);

            // Шаг ещё не закрыт: показываем его как текущий (Completed = start).
            var completed = ended > 0 ? FromMs(ended) : FromMs(startedMs);
            var started = startedMs > 0 ? FromMs(startedMs) : completed;

            request.Steps.Add(new StepData
            {
                Id = $"{sessionId}:{turn}:{step}",
                Time = started,
                Completed = completed,
                Model = reader.GetString(6),
                Tokens = new TokenCounts(
                    Int64(reader, 7),
                    Int64(reader, 8),
                    Int64(reader, 9),
                    Int64(reader, 10),
                    Int64(reader, 11)),
            });
        }

        foreach (var session in sessions)
        {
            foreach (var request in session.Requests)
            {
                if (request.Time == DateTime.MinValue && request.Steps.Count > 0)
                    request.Time = request.Steps[0].Time;
            }

            session.Requests.RemoveAll(request => request.Steps.Count == 0);
        }
    }

    static List<LiveEvent> LoadLive(SqliteConnection connection)
    {
        var live = new List<LiveEvent>();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT IFNULL(time_ms, 0), session_id, turn, step, IFNULL(model, ''),
                   IFNULL(input_tokens, 0), IFNULL(output_tokens, 0), IFNULL(reasoning_tokens, 0),
                   IFNULL(cache_read_tokens, 0), IFNULL(cache_write_tokens, 0),
                   IFNULL(total_tokens, 0), IFNULL(context_used_tokens, 0), IFNULL(duration_ms, 0)
            FROM v_step_feed
            WHERE time_ms IS NOT NULL
            ORDER BY time_ms DESC
            LIMIT 200
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            live.Add(new LiveEvent
            {
                Time = FromMs(Int64(reader, 0)),
                SessionId = reader.GetString(1),
                MessageId = $"{reader.GetInt64(2)}:{reader.GetInt64(3)}",
                Model = reader.GetString(4),
                Steps = 1,
                Input = Int64(reader, 5),
                Output = Int64(reader, 6),
                Reasoning = Int64(reader, 7),
                CacheRead = Int64(reader, 8),
                CacheWrite = Int64(reader, 9),
                Billed = Int64(reader, 10),
                Context = Int64(reader, 11),
                DurationMs = Int64(reader, 12),
            });
        }

        return live;
    }

    static long ComputeSignature(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT IFNULL(last_session_update_ms, 0), IFNULL(steps, 0), IFNULL(sessions, 0)
                FROM v_ingest_health
                """;
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return 0;
            var updated = Int64(reader, 0);
            var steps = Int64(reader, 1);
            var sessions = Int64(reader, 2);
            return updated * 1_000_003L + steps * 1_009L + sessions;
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    static long LoadContextWindow(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT IFNULL(MAX(context_window), 0)
                FROM steps
                WHERE session_id = (SELECT session_id FROM sessions ORDER BY updated_ms DESC LIMIT 1)
                """;
            return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Токены за период для «бюджета» (PLAN §7.6): у маршрута deepseek-account
    /// нет цены, поэтому показываем токены, кэш-хит и заполненность окна.
    /// </summary>
    public BudgetStats LoadBudgetStats(DateTime since)
    {
        if (!File.Exists(DbPath)) return BudgetStats.Empty;

        try
        {
            using var connection = Open();

            long monthTokens = 0;
            long input = 0;
            long cacheRead = 0;
            long requests = 0;

            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT IFNULL(SUM(total_tokens), 0), IFNULL(SUM(input_tokens), 0),
                           IFNULL(SUM(cache_read_tokens), 0), COUNT(*)
                    FROM steps
                    WHERE ended_ms >= $since
                    """;
                command.Parameters.AddWithValue("$since", new DateTimeOffset(since.ToUniversalTime()).ToUnixTimeMilliseconds());
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    monthTokens = Int64(reader, 0);
                    input = Int64(reader, 1);
                    cacheRead = Int64(reader, 2);
                    requests = Int64(reader, 3);
                }
            }

            long pressure = 0;
            long window = 0;
            long sessionTokens = 0;
            string sessionTitle = string.Empty;

            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT IFNULL(MAX(context_pressure_tokens), 0), IFNULL(MAX(context_window), 0),
                           IFNULL(SUM(total_tokens), 0)
                    FROM steps
                    WHERE session_id = (SELECT session_id FROM sessions ORDER BY updated_ms DESC LIMIT 1)
                    """;
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    pressure = Int64(reader, 0);
                    window = Int64(reader, 1);
                    sessionTokens = Int64(reader, 2);
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT IFNULL(title, '') FROM v_session_summary ORDER BY updated_ms DESC LIMIT 1";
                sessionTitle = command.ExecuteScalar() as string ?? string.Empty;
            }

            return new BudgetStats(monthTokens, input, cacheRead, requests, pressure, window, sessionTokens, sessionTitle);
        }
        catch (SqliteException)
        {
            return BudgetStats.Empty;
        }
    }
}

public readonly record struct BudgetStats(
    long MonthTokens,
    long Input,
    long CacheRead,
    long Requests,
    long LastContext,
    long ContextWindow,
    long LastSessionTokens,
    string LastSessionTitle)
{
    public static readonly BudgetStats Empty = new(0, 0, 0, 0, 0, 0, 0, string.Empty);

    public bool HasData => MonthTokens > 0 || Requests > 0;

    public double CacheHitPercent => Input + CacheRead == 0 ? 0 : CacheRead * 100.0 / (Input + CacheRead);

    public double ContextPercent => ContextWindow <= 0 ? 0 : LastContext * 100.0 / ContextWindow;
}
