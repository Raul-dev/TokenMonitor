/**
 * Обёртка над `node:sqlite` (DatabaseSync): батч-запись, транзакции, WAL,
 * пересчёт агрегатов, диагностика ingest_runs, retention.
 *
 * Единственный писатель БД (PLAN §4). Все ошибки записи логируются и
 * проглатываются: плагин не имеет права ломать сессию агента.
 */

import { mkdirSync, statSync } from 'node:fs';
import { dirname } from 'node:path';
import { DatabaseSync } from 'node:sqlite';
import { DDL, SCHEMA_VERSION } from './schema.js';

const TABLE_KEYS = {
  session: ['session_id'],
  turn: ['session_id', 'turn'],
  step: ['session_id', 'turn', 'step'],
  attempt: ['session_id', 'seq'],
  retry: ['session_id', 'seq'],
  usage: ['session_id', 'seq'],
  context: ['session_id', 'seq'],
  tool: ['session_id', 'call_id'],
  raw: ['session_id', 'seq'],
  cursor: ['session_id'],
  compaction: ['session_id', 'start_seq'],
};

const TABLES = {
  session: 'sessions',
  turn: 'turns',
  step: 'steps',
  attempt: 'attempts',
  retry: 'retries',
  usage: 'usage_samples',
  context: 'context_samples',
  tool: 'tool_calls',
  raw: 'raw_events',
  cursor: 'session_cursors',
  compaction: 'compactions',
};

/** Особые выражения ON CONFLICT: «первое значение выигрывает» и монотонные счётчики. */
const CONFLICT_EXPRESSIONS = {
  session: {
    created_ms: 'MIN(sessions.created_ms, excluded.created_ms)',
    updated_ms: 'MAX(sessions.updated_ms, excluded.updated_ms)',
    last_seq: 'MAX(sessions.last_seq, excluded.last_seq)',
    event_count: 'MAX(sessions.event_count, excluded.event_count)',
    first_seen_ms: 'MIN(sessions.first_seen_ms, excluded.first_seen_ms)',
    last_ingest_ms: 'MAX(sessions.last_ingest_ms, excluded.last_ingest_ms)',
  },
  turn: {
    prompt_preview: 'COALESCE(turns.prompt_preview, excluded.prompt_preview)',
    prompt_chars: 'COALESCE(turns.prompt_chars, excluded.prompt_chars)',
    started_ms: 'COALESCE(turns.started_ms, excluded.started_ms)',
  },
};

export class TokenStore {
  /**
   * @param {object} options
   * @param {string} options.dbPath путь к tokens.db (':memory:' в тестах)
   * @param {object} [options.logger]
   * @param {'usage'|'full'|'raw'} [options.capture]
   * @param {number} [options.maxQueue]
   * @param {number} [options.retentionDays]
   * @param {'wal'|'delete'} [options.journalMode]
   */
  constructor(options) {
    this.dbPath = options.dbPath;
    this.logger = options.logger ?? { warn() {}, info() {} };
    this.capture = options.capture ?? 'full';
    this.maxQueue = options.maxQueue ?? 10000;
    this.retentionDays = options.retentionDays ?? 0;
    this.journalMode = options.journalMode ?? 'wal';
    this.db = null;
    this.queue = [];
    this.dropped = 0;
    this.inTransaction = false;
    this.statements = new Map();
    this.runId = null;
    this.errorCount = 0;
    this.lastError = null;
    this.empty = false;
  }

  open() {
    if (this.db) return this;
    if (this.dbPath !== ':memory:') {
      mkdirSync(dirname(this.dbPath), { recursive: true });
    }
    this.db = new DatabaseSync(this.dbPath);
    this.exec('PRAGMA busy_timeout = 5000;');
    this.exec('PRAGMA foreign_keys = ON;');
    this.exec('PRAGMA synchronous = NORMAL;');
    if (this.journalMode === 'wal') this.exec('PRAGMA journal_mode = WAL;');
    this.exec('PRAGMA wal_autocheckpoint = 2000;');
    this.migrate();
    this.beginRun('live');
    if (this.retentionDays > 0) this.prune();
    return this;
  }

  get sizeBytes() {
    try {
      return this.dbPath === ':memory:' ? 0 : statSync(this.dbPath).size;
    } catch {
      return 0;
    }
  }

  exec(sql) {
    this.db.exec(sql);
  }

  prepare(sql) {
    let stmt = this.statements.get(sql);
    if (!stmt) {
      stmt = this.db.prepare(sql);
      this.statements.set(sql, stmt);
    }
    return stmt;
  }

  migrate() {
    // Представления — производный контракт: пересоздаём их при каждом открытии,
    // чтобы добавленные колонки появлялись и в уже существующей БД.
    this.exec(`
      DROP VIEW IF EXISTS v_ingest_health;
      DROP VIEW IF EXISTS v_tool_timing;
      DROP VIEW IF EXISTS v_cache_efficiency;
      DROP VIEW IF EXISTS v_daily_by_model;
      DROP VIEW IF EXISTS v_step_feed;
      DROP VIEW IF EXISTS v_turn_summary;
      DROP VIEW IF EXISTS v_session_summary;
    `);
    this.exec(DDL);
    const row = this.prepare("SELECT value FROM meta WHERE key = 'schema_version'").get();
    const current = row ? Number(row.value) : 0;
    if (current > SCHEMA_VERSION) {
      throw new Error(
        `tokens.db has schema_version ${current}, this token-logger supports ${SCHEMA_VERSION}. Update the plugin.`,
      );
    }
    const now = Date.now();
    if (!row) {
      this.prepare("INSERT INTO meta(key, value) VALUES('schema_version', ?)").run(String(SCHEMA_VERSION));
      this.prepare("INSERT INTO meta(key, value) VALUES('created_ms', ?)").run(String(now));
      this.prepare("INSERT INTO meta(key, value) VALUES('writer', 'dsh-token-logger')").run();
    } else if (current < SCHEMA_VERSION) {
      this.prepare("UPDATE meta SET value = ? WHERE key = 'schema_version'").run(String(SCHEMA_VERSION));
    }
  }

  beginRun(mode) {
    const info = this.prepare('INSERT INTO ingest_runs(started_ms, mode) VALUES(?, ?)').run(Date.now(), mode);
    this.runId = Number(info.lastInsertRowid);
    return this.runId;
  }

  /** Создать отдельный прогон (backfill), не переключая live-счётчик. */
  createRun(mode) {
    const info = this.prepare('INSERT INTO ingest_runs(started_ms, mode) VALUES(?, ?)').run(Date.now(), mode);
    return Number(info.lastInsertRowid);
  }

  /** Завершить отдельный прогон статистикой. */
  finishRun(id, { seen = 0, imported = 0, events = 0, errors = 0, errorText = null } = {}) {
    try {
      this.prepare(
        'UPDATE ingest_runs SET finished_ms = ?, sessions_seen = ?, sessions_imported = ?, events_written = ?, error_count = ?, error_text = ? WHERE id = ?',
      ).run(Date.now(), seen, imported, events, errors, errorText, id);
    } catch {
    }
  }

  touchRun(eventsWritten, errors = 0, errorText = null) {
    if (this.runId == null) return;
    try {
      this.prepare(
        'UPDATE ingest_runs SET finished_ms = ?, events_written = events_written + ?, error_count = error_count + ?, error_text = COALESCE(?, error_text) WHERE id = ?',
      ).run(Date.now(), eventsWritten, errors, errorText, this.runId);
    } catch {
    }
  }

  /** @param {object[]} records */
  enqueue(records) {
    if (!records || records.length === 0) return;
    if (this.queue.length >= this.maxQueue) {
      this.dropped += records.length;
      if (this.dropped % 500 < records.length) {
        this.logger.warn(`token-logger: queue overflow, dropped ${this.dropped} records`);
      }
      return;
    }
    for (const record of records) this.queue.push(record);
  }

  get pending() {
    return this.queue.length;
  }

  /** Синхронный сброс очереди: одна транзакция на батч (PLAN §5.9). */
  drainNow() {
    if (!this.db || this.queue.length === 0 || this.inTransaction) return 0;
    const batch = this.queue;
    this.queue = [];
    const steps = new Set();
    const turns = new Set();
    this.inTransaction = true;
    try {
      this.exec('BEGIN IMMEDIATE;');
      for (const record of batch) this.applyRecord(record, steps, turns);
      this.aggregate(steps, turns);
      this.exec('COMMIT;');
      this.touchRun(batch.length);
      return batch.length;
    } catch (error) {
      try {
        this.exec('ROLLBACK;');
      } catch {
      }
      this.errorCount++;
      this.lastError = String(error?.message ?? error);
      this.logger.warn(`token-logger: drain failed: ${this.lastError}`);
      this.touchRun(0, 1, this.lastError);
      return 0;
    } finally {
      this.inTransaction = false;
    }
  }

  /** Записать записи прямо сейчас (backfill порциями). */
  writeRecords(records) {
    this.enqueue(records);
    return this.drainNow();
  }

  applyRecord(record, steps, turns) {
    const values = {};
    for (const [key, value] of Object.entries(record.values ?? {})) {
      if (value !== undefined) values[key] = value;
    }

    if (record.kind === 'tool' && values.name === undefined) {
      this.updateToolOnly(values);
      this.track(values, steps, turns);
      return;
    }

    const keys = TABLE_KEYS[record.kind];
    const table = TABLES[record.kind];
    if (!keys || !table) {
      this.logger.warn(`token-logger: unknown record kind ${record.kind}`);
      return;
    }
    if (record.kind === 'session') {
      // Сессия могла появиться в БД не через session/created (плагин включён
      // посреди сессии) — заполняем обязательные NOT NULL поля.
      const now = Date.now();
      values.created_ms ??= now;
      values.updated_ms ??= now;
      values.first_seen_ms ??= now;
      values.last_ingest_ms ??= now;
    }
    if (record.kind === 'step') {
      values.updated_ms ??= Date.now();
    }
    const columns = Object.keys(values);
    if (columns.length === 0) return;
    for (const key of keys) {
      if (values[key] === undefined) {
        this.logger.warn(`token-logger: ${record.kind} record without ${key}`);
        return;
      }
    }

    const overrides = CONFLICT_EXPRESSIONS[record.kind] ?? {};
    const updates = columns
      .filter((column) => !keys.includes(column))
      .map((column) => `${column} = ${overrides[column] ?? `COALESCE(excluded.${column}, ${table}.${column})`}`);
    const placeholders = columns.map(() => '?').join(', ');
    const sql = updates.length > 0
      ? `INSERT INTO ${table}(${columns.join(', ')}) VALUES(${placeholders}) ON CONFLICT(${keys.join(', ')}) DO UPDATE SET ${updates.join(', ')}`
      : `INSERT INTO ${table}(${columns.join(', ')}) VALUES(${placeholders}) ON CONFLICT(${keys.join(', ')}) DO NOTHING`;

    this.prepare(sql).run(...columns.map((column) => values[column]));
    this.track(values, steps, turns);
  }

  updateToolOnly(values) {
    const assignments = [];
    const params = [];
    for (const [key, value] of Object.entries(values)) {
      if (['session_id', 'call_id', 'name'].includes(key)) continue;
      assignments.push(`${key} = COALESCE(?, ${key})`);
      params.push(value);
    }
    if (assignments.length === 0) return;
    params.push(values.session_id, values.call_id);
    const sql = `UPDATE tool_calls SET ${assignments.join(', ')} WHERE session_id = ? AND call_id = ?`;
    const result = this.prepare(sql).run(...params);
    if (Number(result.changes ?? 0) === 0) {
      // Результат пришёл без tool/call (например, плагин подключён посреди шага).
      this.logger.warn(`token-logger: tool/result without tool/call for ${values.call_id}`);
    }
  }

  track(values, steps, turns) {
    const sessionId = values.session_id;
    if (!sessionId) return;
    if (typeof values.turn === 'number' && typeof values.step === 'number') {
      steps.add(`${sessionId}\u0000${values.turn}\u0000${values.step}`);
    } else if (typeof values.turn === 'number') {
      turns.add(`${sessionId}\u0000${values.turn}`);
    }
  }

  aggregate(steps, turns) {
    for (const key of steps) {
      const [sessionId, turn, step] = key.split('\u0000');
      this.prepare(
        `UPDATE steps SET
           tools_count = (SELECT COUNT(*) FROM tool_calls c
                           WHERE c.session_id = steps.session_id AND c.turn = steps.turn AND c.step = steps.step),
           tool_errors = (SELECT COUNT(*) FROM tool_calls c
                           WHERE c.session_id = steps.session_id AND c.turn = steps.turn AND c.step = steps.step
                             AND c.is_error = 1),
           attempts    = (SELECT COUNT(*) FROM attempts a
                           WHERE a.session_id = steps.session_id AND a.turn = steps.turn AND a.step = steps.step),
           retries     = (SELECT COUNT(*) FROM retries r
                           WHERE r.session_id = steps.session_id AND r.turn = steps.turn AND r.step = steps.step
                             AND r.event_kind = 'scheduled')
         WHERE session_id = ? AND turn = ? AND step = ?`,
      ).run(sessionId, Number(turn), Number(step));
      turns.add(`${sessionId}\u0000${turn}`);
    }

    for (const key of turns) {
      const [sessionId, turn] = key.split('\u0000');
      this.prepare(
        `UPDATE turns SET steps_count = (
           SELECT COUNT(*) FROM steps p
            WHERE p.session_id = turns.session_id AND p.turn = turns.turn AND p.ended_ms IS NOT NULL)
         WHERE session_id = ? AND turn = ?`,
      ).run(sessionId, Number(turn));
    }
  }

  cursorFor(sessionId) {
    try {
      const row = this.prepare('SELECT last_seq FROM session_cursors WHERE session_id = ?').get(sessionId);
      return row ? Number(row.last_seq) : -1;
    } catch {
      return -1;
    }
  }

  hasSession(sessionId) {
    try {
      return Boolean(this.prepare('SELECT 1 AS x FROM sessions WHERE session_id = ?').get(sessionId));
    } catch {
      return false;
    }
  }

  prune() {
    const cutoff = Date.now() - this.retentionDays * 24 * 60 * 60 * 1000;
    try {
      const sessions = this.prepare('DELETE FROM sessions WHERE updated_ms < ?').run(cutoff);
      const raw = this.prepare('DELETE FROM raw_events WHERE time_ms < ?').run(Date.now() - (this.retentionDays / 3) * 24 * 60 * 60 * 1000);
      if (Number(sessions.changes ?? 0) + Number(raw.changes ?? 0) > 0) {
        this.logger.info(`token-logger: retention removed ${sessions.changes} sessions, ${raw.changes} raw events`);
      }
    } catch (error) {
      this.logger.warn(`token-logger: retention failed: ${error?.message ?? error}`);
    }
  }

  /** Барьер долговечности для session/flush. */
  async flushAsync() {
    this.drainNow();
  }

  close() {
    if (!this.db) return;
    try {
      this.drainNow();
      this.touchRun(0);
    } catch {
    }
    try {
      this.db.close();
    } catch {
    }
    this.db = null;
    this.statements.clear();
  }
}

/** Открыть БД и вернуть хранилище (удобно для тестов и backfill). */
export function openTokenStore(options) {
  return new TokenStore(options).open();
}
