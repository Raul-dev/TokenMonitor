/**
 * DDL базы токен-логгера. Источник истины — DeepSeekSetup/schema.sql
 * (schema_version = 1). Здесь тот же DDL без PRAGMA: режимы задаёт db.js.
 */

export const SCHEMA_VERSION = 1;

export const DDL = `
CREATE TABLE IF NOT EXISTS meta (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
) STRICT;

CREATE TABLE IF NOT EXISTS sessions (
    session_id        TEXT PRIMARY KEY,
    parent_session_id TEXT,
    origin            TEXT,
    delegation_depth  INTEGER,
    agent_preset      TEXT,
    cwd               TEXT,
    title             TEXT,
    provider          TEXT,
    model             TEXT,
    reasoning_effort  TEXT,
    created_ms        INTEGER NOT NULL,
    updated_ms        INTEGER NOT NULL,
    first_seen_ms     INTEGER NOT NULL,
    last_ingest_ms    INTEGER NOT NULL,
    last_seq          INTEGER NOT NULL DEFAULT -1,
    event_count       INTEGER NOT NULL DEFAULT 0,
    is_seeded         INTEGER NOT NULL DEFAULT 0,
    format_version    INTEGER,
    source            TEXT NOT NULL DEFAULT 'live'
) STRICT;

CREATE INDEX IF NOT EXISTS idx_sessions_updated ON sessions(updated_ms DESC);
CREATE INDEX IF NOT EXISTS idx_sessions_cwd     ON sessions(cwd);
CREATE INDEX IF NOT EXISTS idx_sessions_parent  ON sessions(parent_session_id);

CREATE TABLE IF NOT EXISTS turns (
    session_id      TEXT NOT NULL,
    turn            INTEGER NOT NULL,
    started_ms      INTEGER,
    ended_ms        INTEGER,
    end_kind        TEXT,
    end_error_code  TEXT,
    end_error_text  TEXT,
    steps_count     INTEGER NOT NULL DEFAULT 0,
    prompt_preview  TEXT,
    prompt_chars    INTEGER,
    PRIMARY KEY (session_id, turn),
    FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
) STRICT;

CREATE INDEX IF NOT EXISTS idx_turns_started ON turns(started_ms DESC);

CREATE TABLE IF NOT EXISTS steps (
    session_id            TEXT NOT NULL,
    turn                  INTEGER NOT NULL,
    step                  INTEGER NOT NULL,

    started_ms            INTEGER,
    ended_ms              INTEGER,
    duration_ms           INTEGER,
    ttft_ms               INTEGER,
    decode_ms             INTEGER,
    llm_ms                INTEGER,

    provider              TEXT,
    model                 TEXT,
    reasoning_effort      TEXT,
    request_header_seq    INTEGER,
    request_context_seq   INTEGER,

    assistant_seq         INTEGER,
    assistant_message_id  TEXT,
    interrupted           INTEGER NOT NULL DEFAULT 0,
    usage_source          TEXT NOT NULL DEFAULT 'none',
    usage_seq             INTEGER,

    input_tokens          INTEGER NOT NULL DEFAULT 0,
    prompt_tokens_total   INTEGER NOT NULL DEFAULT 0,
    output_tokens         INTEGER NOT NULL DEFAULT 0,
    reasoning_tokens      INTEGER NOT NULL DEFAULT 0,
    cache_read_tokens     INTEGER NOT NULL DEFAULT 0,
    cache_write_tokens    INTEGER NOT NULL DEFAULT 0,
    total_tokens          INTEGER NOT NULL DEFAULT 0,

    context_pressure_tokens  INTEGER,
    context_projected_tokens INTEGER,
    context_surface_tokens   INTEGER,
    context_window           INTEGER,
    ctx_system_tokens        INTEGER,
    ctx_tools_tokens         INTEGER,
    ctx_message_tokens       INTEGER,

    attempts              INTEGER NOT NULL DEFAULT 0,
    retries               INTEGER NOT NULL DEFAULT 0,
    tools_count           INTEGER NOT NULL DEFAULT 0,
    tool_errors           INTEGER NOT NULL DEFAULT 0,
    stream_records        INTEGER,
    content_chars         INTEGER,
    reasoning_chars       INTEGER,

    updated_ms            INTEGER NOT NULL,
    PRIMARY KEY (session_id, turn, step),
    FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
) STRICT;

CREATE INDEX IF NOT EXISTS idx_steps_time    ON steps(ended_ms DESC);
CREATE INDEX IF NOT EXISTS idx_steps_model   ON steps(provider, model, ended_ms DESC);
CREATE INDEX IF NOT EXISTS idx_steps_session ON steps(session_id, turn, step);

CREATE TABLE IF NOT EXISTS attempts (
    session_id     TEXT NOT NULL,
    seq            INTEGER NOT NULL,
    time_ms        INTEGER NOT NULL,
    turn           INTEGER NOT NULL,
    step           INTEGER NOT NULL,
    stream_records INTEGER,
    stream_chars   INTEGER,
    PRIMARY KEY (session_id, seq),
    FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
) STRICT;

CREATE INDEX IF NOT EXISTS idx_attempts_step ON attempts(session_id, turn, step);

CREATE TABLE IF NOT EXISTS retries (
    session_id       TEXT NOT NULL,
    seq              INTEGER NOT NULL,
    time_ms          INTEGER NOT NULL,
    turn             INTEGER NOT NULL,
    step             INTEGER NOT NULL,
    event_kind       TEXT NOT NULL,
    retry_id         TEXT,
    provider         TEXT,
    mode             TEXT,
    policy_key       TEXT,
    failure_code     TEXT,
    failure_text     TEXT,
    scheduled_delay_ms INTEGER,
    PRIMARY KEY (session_id, seq),
    FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
) STRICT;

CREATE INDEX IF NOT EXISTS idx_retries_step ON retries(session_id, turn, step);

CREATE TABLE IF NOT EXISTS usage_samples (
    session_id           TEXT NOT NULL,
    seq                  INTEGER NOT NULL,
    time_ms              INTEGER NOT NULL,
    turn                 INTEGER,
    step                 INTEGER,
    provider             TEXT,
    model                TEXT,
    input_tokens         INTEGER,
    output_tokens        INTEGER,
    reasoning_tokens     INTEGER,
    cache_read_tokens    INTEGER,
    cache_write_tokens   INTEGER,
    total_tokens         INTEGER,
    raw_json             TEXT NOT NULL,
    PRIMARY KEY (session_id, seq),
    FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
) STRICT;

CREATE INDEX IF NOT EXISTS idx_usage_time ON usage_samples(time_ms DESC);
CREATE INDEX IF NOT EXISTS idx_usage_step ON usage_samples(session_id, turn, step);

CREATE TABLE IF NOT EXISTS context_samples (
    session_id       TEXT NOT NULL,
    seq              INTEGER NOT NULL,
    time_ms          INTEGER NOT NULL,
    turn             INTEGER,
    step             INTEGER,
    pressure_tokens  INTEGER,
    projected_tokens INTEGER,
    surface_tokens   INTEGER,
    context_window   INTEGER,
    system_tokens    INTEGER,
    tools_tokens     INTEGER,
    message_tokens   INTEGER,
    PRIMARY KEY (session_id, seq),
    FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
) STRICT;

CREATE INDEX IF NOT EXISTS idx_context_step ON context_samples(session_id, turn, step);

CREATE TABLE IF NOT EXISTS tool_calls (
    session_id      TEXT NOT NULL,
    call_id         TEXT NOT NULL,
    turn            INTEGER,
    step            INTEGER,
    name            TEXT NOT NULL,
    called_ms       INTEGER,
    finished_ms     INTEGER,
    duration_ms     INTEGER,
    arguments_chars INTEGER,
    arguments_preview TEXT,
    result_chars    INTEGER,
    result_preview  TEXT,
    is_error        INTEGER NOT NULL DEFAULT 0,
    error_name      TEXT,
    error_code      TEXT,
    error_reason    TEXT,
    meta_json       TEXT,
    PRIMARY KEY (session_id, call_id),
    FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
) STRICT;

CREATE INDEX IF NOT EXISTS idx_tool_name ON tool_calls(name, called_ms DESC);
CREATE INDEX IF NOT EXISTS idx_tool_step ON tool_calls(session_id, turn, step);

CREATE TABLE IF NOT EXISTS compactions (
    session_id        TEXT NOT NULL,
    start_seq         INTEGER NOT NULL,
    end_seq           INTEGER,
    turn              INTEGER,
    started_ms        INTEGER,
    ended_ms          INTEGER,
    reason            TEXT,
    replaced_from_seq INTEGER,
    replaced_to_seq   INTEGER,
    tokens_before     INTEGER,
    tokens_after      INTEGER,
    summary_chars     INTEGER,
    PRIMARY KEY (session_id, start_seq),
    FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
) STRICT;

CREATE TABLE IF NOT EXISTS raw_events (
    session_id  TEXT NOT NULL,
    seq         INTEGER NOT NULL,
    time_ms     INTEGER NOT NULL,
    type        TEXT NOT NULL,
    surface_op  TEXT,
    ignorable   INTEGER NOT NULL DEFAULT 0,
    bytes       INTEGER NOT NULL,
    data_json   TEXT NOT NULL,
    ingested_ms INTEGER NOT NULL,
    PRIMARY KEY (session_id, seq),
    FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
) STRICT;

CREATE INDEX IF NOT EXISTS idx_raw_type ON raw_events(type, time_ms DESC);

CREATE TABLE IF NOT EXISTS session_cursors (
    session_id   TEXT PRIMARY KEY,
    last_seq     INTEGER NOT NULL,
    last_time_ms INTEGER,
    updated_ms   INTEGER NOT NULL,
    reason       TEXT NOT NULL
) STRICT;

CREATE TABLE IF NOT EXISTS ingest_runs (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    started_ms        INTEGER NOT NULL,
    finished_ms       INTEGER,
    mode              TEXT NOT NULL,
    sessions_seen     INTEGER NOT NULL DEFAULT 0,
    sessions_imported INTEGER NOT NULL DEFAULT 0,
    events_written    INTEGER NOT NULL DEFAULT 0,
    error_count       INTEGER NOT NULL DEFAULT 0,
    error_text        TEXT
) STRICT;

CREATE VIEW IF NOT EXISTS v_session_summary AS
SELECT
    s.session_id,
    s.parent_session_id,
    s.origin,
    s.agent_preset,
    s.cwd,
    s.title,
    s.provider,
    s.model,
    s.created_ms,
    s.updated_ms,
    s.event_count,
    COALESCE((SELECT COUNT(*) FROM turns t WHERE t.session_id = s.session_id), 0) AS turns,
    COALESCE((SELECT COUNT(*) FROM steps p WHERE p.session_id = s.session_id), 0) AS steps,
    COALESCE((SELECT SUM(p.input_tokens)       FROM steps p WHERE p.session_id = s.session_id), 0) AS input_tokens,
    COALESCE((SELECT SUM(p.output_tokens)      FROM steps p WHERE p.session_id = s.session_id), 0) AS output_tokens,
    COALESCE((SELECT SUM(p.reasoning_tokens)   FROM steps p WHERE p.session_id = s.session_id), 0) AS reasoning_tokens,
    COALESCE((SELECT SUM(p.cache_read_tokens)  FROM steps p WHERE p.session_id = s.session_id), 0) AS cache_read_tokens,
    COALESCE((SELECT SUM(p.cache_write_tokens) FROM steps p WHERE p.session_id = s.session_id), 0) AS cache_write_tokens,
    COALESCE((SELECT SUM(p.total_tokens)       FROM steps p WHERE p.session_id = s.session_id), 0) AS total_tokens,
    COALESCE((SELECT MAX(p.context_pressure_tokens) FROM steps p WHERE p.session_id = s.session_id), 0) AS max_context_tokens,
    COALESCE((SELECT MAX(p.turn || '.' || p.step)   FROM steps p WHERE p.session_id = s.session_id), '') AS max_step,
    COALESCE((SELECT SUM(p.tools_count)        FROM steps p WHERE p.session_id = s.session_id), 0) AS tool_calls,
    COALESCE((SELECT SUM(p.tool_errors)        FROM steps p WHERE p.session_id = s.session_id), 0) AS tool_errors
FROM sessions s;

CREATE VIEW IF NOT EXISTS v_turn_summary AS
SELECT
    t.session_id,
    t.turn,
    t.started_ms,
    t.ended_ms,
    CASE WHEN t.started_ms IS NOT NULL AND t.ended_ms IS NOT NULL
         THEN t.ended_ms - t.started_ms END AS duration_ms,
    t.end_kind,
    t.end_error_code,
    t.prompt_preview,
    t.steps_count,
    COALESCE(SUM(p.input_tokens), 0)       AS input_tokens,
    COALESCE(SUM(p.output_tokens), 0)      AS output_tokens,
    COALESCE(SUM(p.reasoning_tokens), 0)   AS reasoning_tokens,
    COALESCE(SUM(p.cache_read_tokens), 0)  AS cache_read_tokens,
    COALESCE(SUM(p.cache_write_tokens), 0) AS cache_write_tokens,
    COALESCE(SUM(p.total_tokens), 0)       AS total_tokens,
    COALESCE(MAX(p.context_pressure_tokens), 0) AS max_context_tokens,
    COALESCE(SUM(p.tools_count), 0)        AS tool_calls,
    COALESCE(SUM(p.tool_errors), 0)        AS tool_errors,
    (SELECT p2.model FROM steps p2
      WHERE p2.session_id = t.session_id AND p2.turn = t.turn AND p2.model IS NOT NULL
      ORDER BY p2.step DESC LIMIT 1) AS model
FROM turns t
LEFT JOIN steps p ON p.session_id = t.session_id AND p.turn = t.turn
GROUP BY t.session_id, t.turn;

CREATE VIEW IF NOT EXISTS v_step_feed AS
SELECT
    p.session_id,
    p.turn,
    p.step,
    p.started_ms AS started_ms,
    p.ended_ms AS time_ms,
    p.duration_ms,
    p.provider,
    p.model,
    p.total_tokens,
    p.input_tokens,
    p.output_tokens,
    p.reasoning_tokens,
    p.cache_read_tokens,
    p.cache_write_tokens,
    p.context_pressure_tokens,
    p.context_window,
    p.tools_count,
    p.retries,
    p.attempts,
    p.usage_source,
    p.interrupted,
    p.prompt_tokens_total,
    p.input_tokens + p.cache_read_tokens AS context_used_tokens
FROM steps p;

CREATE VIEW IF NOT EXISTS v_daily_by_model AS
SELECT
    date(p.ended_ms / 1000, 'unixepoch') AS day_utc,
    p.provider,
    p.model,
    COUNT(*)                        AS steps,
    SUM(p.input_tokens)             AS input_tokens,
    SUM(p.output_tokens)            AS output_tokens,
    SUM(p.reasoning_tokens)         AS reasoning_tokens,
    SUM(p.cache_read_tokens)        AS cache_read_tokens,
    SUM(p.cache_write_tokens)       AS cache_write_tokens,
    SUM(p.total_tokens)             AS total_tokens,
    SUM(p.duration_ms)              AS duration_ms
FROM steps p
GROUP BY day_utc, p.provider, p.model;

CREATE VIEW IF NOT EXISTS v_cache_efficiency AS
SELECT
    p.session_id,
    p.turn,
    p.step,
    p.ended_ms AS time_ms,
    p.model,
    p.input_tokens,
    p.cache_read_tokens,
    p.cache_write_tokens,
    CASE WHEN (p.input_tokens + p.cache_read_tokens) > 0
         THEN p.cache_read_tokens * 100.0 / (p.input_tokens + p.cache_read_tokens)
         ELSE 0 END AS cache_hit_percent
FROM steps p;

CREATE VIEW IF NOT EXISTS v_tool_timing AS
SELECT
    name,
    COUNT(*)                                              AS calls,
    SUM(CASE WHEN is_error = 1 THEN 1 ELSE 0 END)         AS errors,
    AVG(duration_ms)                                      AS avg_duration_ms,
    MAX(duration_ms)                                      AS max_duration_ms,
    SUM(COALESCE(result_chars, 0))                        AS result_chars_total
FROM tool_calls
GROUP BY name;

CREATE VIEW IF NOT EXISTS v_ingest_health AS
SELECT
    (SELECT MAX(finished_ms) FROM ingest_runs)                       AS last_run_finished_ms,
    (SELECT COUNT(*) FROM sessions)                                  AS sessions,
    (SELECT COUNT(*) FROM steps)                                     AS steps,
    (SELECT COUNT(*) FROM raw_events)                                AS raw_events,
    (SELECT MAX(updated_ms) FROM sessions)                           AS last_session_update_ms,
    (SELECT MAX(time_ms) FROM usage_samples)                         AS last_usage_ms;
`;
