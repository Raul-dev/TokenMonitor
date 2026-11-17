// Диагностика tokens.db: node query-db.mjs [path] [limit]
import { DatabaseSync } from 'node:sqlite';

const path = process.argv[2] ?? 'C:/Users/raul/.dsh/tokens/tokens.db';
const limit = Number(process.argv[3] ?? 5);
const db = new DatabaseSync(path, { readOnly: true });

const q = (label, sql, params = []) => {
  try {
    console.log(`--- ${label}`);
    console.log(JSON.stringify(db.prepare(sql).all(...params), null, 1));
  } catch (error) {
    console.log(`--- ${label} FAILED: ${error.message}`);
  }
};

q('sessions', 'SELECT session_id, title, event_count, last_seq, updated_ms FROM sessions ORDER BY updated_ms DESC');
q('steps per session', 'SELECT session_id, COUNT(*) AS n, MAX(turn || "." || step) AS last_step, SUM(total_tokens) AS tokens, SUM(tools_count) AS tools FROM steps GROUP BY session_id');
q('last steps', 'SELECT session_id, turn, step, total_tokens, context_used_tokens, tools_count, retries, attempts, duration_ms FROM v_step_feed ORDER BY time_ms DESC LIMIT ?', [limit]);
q('tools', 'SELECT name, COUNT(*) AS n, SUM(is_error) AS errors, AVG(duration_ms) AS avg_ms FROM tool_calls GROUP BY name ORDER BY n DESC LIMIT ?', [limit]);
q('retries', 'SELECT event_kind, COUNT(*) AS n FROM retries GROUP BY event_kind');
q('runs', 'SELECT id, mode, finished_ms, sessions_seen, sessions_imported, events_written, error_count, error_text FROM ingest_runs');
q('health', 'SELECT * FROM v_ingest_health');
q('meta', 'SELECT key, value FROM meta');
q('integrity', 'PRAGMA integrity_check');
q('daily', 'SELECT * FROM v_daily_by_model WHERE total_tokens > 0');
db.close();
