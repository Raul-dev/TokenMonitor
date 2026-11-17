/**
 * @local/dsh-token-logger — Host-only bundle для DeepSeek Harness.
 *
 * Подписывается на жизненный цикл сессий DSH, сворачивает события в SQLite
 * (`~/.dsh/tokens/tokens.db`) и догоняет историю через публичный
 * `ctx.sessionPersistence`. TokenMonitor читает эту БД только на чтение.
 *
 * Контракт: DeepSeekSetup/PLAN.md §5, схема: DeepSeekSetup/schema.sql.
 * Плагин никогда не пишет в журнал сессии и никогда не пробрасывает ошибки
 * в цикл агента (PLAN §5.3).
 */

import { homedir } from 'node:os';
import { join } from 'node:path';
import { importSessionById, runBackfill } from './src/backfill.js';
import { TokenStore } from './src/db.js';
import { createState, filterRecords, foldEvent, sessionRecord } from './src/fold.js';

/** Имя плагина для диагностики Loader. */
export const name = 'dsh-token-logger';

/** Плагину нужен только сервис сессий; persistence/tokenMeter — опциональны. */
export const inject = ['sessions'];

const DEFAULTS = {
  dbPath: null,
  capture: 'full',
  captureText: 'preview',
  previewChars: 200,
  backfillOnStart: true,
  backfillLimit: 50,
  flushIntervalMs: 250,
  flushMaxEvents: 200,
  retentionDays: 0,
  journalMode: 'wal',
  maxQueue: 10000,
  verbose: false,
};

const CAPTURE_MODES = new Set(['usage', 'full', 'raw']);
const TEXT_MODES = new Set(['off', 'preview', 'full']);

function defaultDbPath() {
  const home = (process.env.DSH_HOME ?? '').trim() || join(homedir(), '.dsh');
  return join(home, 'tokens', 'tokens.db');
}

function positiveInt(value, fallback, max) {
  const number = Number(value);
  if (!Number.isFinite(number) || number < 0) return fallback;
  return Math.min(Math.floor(number), max);
}

function normalizeConfig(config = {}) {
  const source = config && typeof config === 'object' ? config : {};
  return {
    dbPath: typeof source.dbPath === 'string' && source.dbPath.trim() ? source.dbPath.trim() : defaultDbPath(),
    capture: CAPTURE_MODES.has(source.capture) ? source.capture : DEFAULTS.capture,
    captureText: TEXT_MODES.has(source.captureText) ? source.captureText : DEFAULTS.captureText,
    previewChars: positiveInt(source.previewChars, DEFAULTS.previewChars, 10000) || DEFAULTS.previewChars,
    backfillOnStart: source.backfillOnStart !== false,
    backfillLimit: positiveInt(source.backfillLimit, DEFAULTS.backfillLimit, 100000),
    flushIntervalMs: positiveInt(source.flushIntervalMs, DEFAULTS.flushIntervalMs, 60000) || DEFAULTS.flushIntervalMs,
    flushMaxEvents: positiveInt(source.flushMaxEvents, DEFAULTS.flushMaxEvents, 100000) || DEFAULTS.flushMaxEvents,
    retentionDays: positiveInt(source.retentionDays, DEFAULTS.retentionDays, 100000),
    journalMode: source.journalMode === 'delete' ? 'delete' : 'wal',
    maxQueue: positiveInt(source.maxQueue, DEFAULTS.maxQueue, 1000000) || DEFAULTS.maxQueue,
    verbose: source.verbose === true,
  };
}

/**
 * @param {object} ctx контекст плагина Cordis
 * @param {object} [rawConfig] config строки бандла
 */
export function apply(ctx, rawConfig = {}) {
  const config = normalizeConfig(rawConfig);
  const log = makeLogger(ctx);
  const getService = (serviceName) => (typeof ctx.get === 'function' ? ctx.get(serviceName) : undefined);

  let store;
  try {
    store = new TokenStore({
      dbPath: config.dbPath,
      logger: log,
      capture: config.capture,
      maxQueue: config.maxQueue,
      retentionDays: config.retentionDays,
      journalMode: config.journalMode,
    }).open();
  } catch (error) {
    log.warn(`token-logger: disabled, cannot open ${config.dbPath}: ${error?.message ?? error}`);
    return;
  }

  if (config.verbose) log.info(`token-logger: writing to ${config.dbPath}`);

  const states = new Map();
  const importing = new Set();
  const logged = { errors: 0 };
  let closed = false;

  const warnLimited = (message) => {
    logged.errors++;
    if (logged.errors <= 5 || logged.errors % 100 === 0) log.warn(message);
  };

  const persistence = () => getService('sessionPersistence');

  const flush = () => {
    if (closed) return;
    try {
      store.drainNow();
    } catch (error) {
      warnLimited(`token-logger: flush failed: ${error?.message ?? error}`);
    }
  };

  const scheduleImport = (sessionId) => {
    if (!config.backfillOnStart || importing.has(sessionId)) return;
    importing.add(sessionId);
    queueMicrotask(() => {
      void (async () => {
        try {
          await importSessionById(store, persistence(), sessionId, config, log);
        } catch (error) {
          warnLimited(`token-logger: import failed for ${sessionId}: ${error?.message ?? error}`);
        } finally {
          importing.delete(sessionId);
        }
      })();
    });
  };

  const track = (session) => {
    const id = session?.id;
    if (!id) return null;
    let state = states.get(id);
    if (!state) {
      state = createState();
      states.set(id, state);
      try {
        store.enqueue([sessionRecord(session.header, { now: Date.now(), source: 'live' })]);
        flush();
      } catch (error) {
        warnLimited(`token-logger: cannot register ${id}: ${error?.message ?? error}`);
      }
      if (typeof session.seq === 'number' && store.cursorFor(id) < session.seq - 1) {
        scheduleImport(id);
      }
    }
    return state;
  };

  // Сессия уже могла существовать до активации плагина.
  try {
    const existing = typeof ctx.sessions?.list === 'function' ? ctx.sessions.list() : [];
    for (const session of existing) track(session);
  } catch (error) {
    warnLimited(`token-logger: cannot enumerate live sessions: ${error?.message ?? error}`);
  }

  ctx.on('session/created', (session) => {
    try {
      track(session);
    } catch (error) {
      warnLimited(`token-logger: session/created failed: ${error?.message ?? error}`);
    }
  });

  ctx.on('session/event', (session, event) => {
    try {
      const state = track(session);
      if (!state) return;
      const records = foldEvent(state, event, {
        sessionId: session.id,
        now: Date.now(),
        capture: config.capture,
        captureText: config.captureText,
        previewChars: config.previewChars,
        source: 'live',
      });
      store.enqueue(filterRecords(records, config.capture));
      if (store.pending >= config.flushMaxEvents) flush();
    } catch (error) {
      warnLimited(`token-logger: session/event failed: ${error?.message ?? error}`);
    }
  });

  ctx.on('session/disposed', (session) => {
    try {
      flush();
    } catch {
    }
    states.delete(session?.id);
  });

  ctx.on('session/flush', () => {
    flush();
  });

  ctx.effect(() => {
    const timer = setInterval(flush, config.flushIntervalMs);
    return () => clearInterval(timer);
  });

  ctx.effect(() => () => {
    closed = true;
    try {
      store.close();
    } catch {
    }
  });

  if (config.backfillOnStart) {
    queueMicrotask(() => {
      void (async () => {
        const runId = store.createRun('startup-backfill');
        try {
          const summary = await runBackfill({
            store,
            persistence: persistence(),
            config,
            logger: log,
          });
          store.finishRun(runId, {
            seen: summary.seen,
            imported: summary.imported,
            events: summary.events,
          });
        } catch (error) {
          store.finishRun(runId, { errors: 1, errorText: String(error?.message ?? error) });
          warnLimited(`token-logger: startup backfill failed: ${error?.message ?? error}`);
        }
      })();
    });
  }
}

function makeLogger(ctx) {
  const warn = typeof ctx.logger?.warn === 'function' ? ctx.logger.warn.bind(ctx.logger) : console.warn.bind(console);
  const info = typeof ctx.logger?.info === 'function' ? ctx.logger.info.bind(ctx.logger) : console.info.bind(console);
  return {
    warn: (message) => warn(message),
    info: (message) => info(message),
  };
}
