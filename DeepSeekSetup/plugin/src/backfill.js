/**
 * Backfill истории через публичный `ctx.sessionPersistence` (PLAN §5.5).
 *
 * Никакого чтения `.zstd` своими руками: backend сам мигрирует формат и
 * отдаёт валидные события. Идемпотентность обеспечивают первичные ключи
 * `(session_id, seq)` и `INSERT ... ON CONFLICT`.
 */

import { createState, filterRecords, foldEvent, sessionRecord } from './fold.js';

const BATCH_RECORDS = 600;
const READ_CHUNK = 1000;

const pause = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/**
 * Импортировать одну сессию целиком (с начала журнала).
 * @returns {Promise<{events: number, written: number}>}
 */
export async function importSession(store, persistence, snapshot, config, logger) {
  const header = snapshot.header;
  if (!header?.id) return { events: 0, written: 0 };

  const handle = await persistence.open(header.id, 'read');
  let written = 0;
  try {
    const expected = snapshot.eventCount;
    const state = createState();
    store.enqueue([sessionRecord(header, { now: Date.now(), source: 'backfill' })]);

    let offset = 0;
    let batch = [];
    for (;;) {
      if (typeof expected === 'number' && offset >= expected) break;
      const { events } = await handle.read(offset, READ_CHUNK);
      if (!events || events.length === 0) break;
      for (const event of events) {
        const records = foldEvent(state, event, {
          sessionId: header.id,
          now: Date.now(),
          capture: config.capture,
          captureText: config.captureText,
          previewChars: config.previewChars,
          source: 'backfill',
        });
        for (const record of filterRecords(records, config.capture)) batch.push(record);
        if (batch.length >= BATCH_RECORDS) {
          written += store.writeRecords(batch);
          batch = [];
          await pause(0);
        }
      }
      offset += events.length;
      if (typeof expected !== 'number' && events.length < READ_CHUNK) break;
    }
    if (batch.length > 0) written += store.writeRecords(batch);
    store.drainNow();
    return { events: offset, written };
  } finally {
    try {
      await handle.close();
    } catch {
    }
  }
}

/**
 * Импортировать одну сессию по id (для только что открытой/resume сессии).
 * @returns {Promise<boolean>} true, если сессия была импортирована
 */
export async function importSessionById(store, persistence, sessionId, config, logger) {
  if (typeof persistence?.stat !== 'function') return false;
  let snapshot;
  try {
    snapshot = await persistence.stat(sessionId);
  } catch (error) {
    logger?.warn?.(`token-logger: backfill stat failed for ${sessionId}: ${error?.message ?? error}`);
    return false;
  }
  if (!snapshot?.header) return false;
  const cursor = store.cursorFor(sessionId);
  if (typeof snapshot.eventCount === 'number' && cursor >= snapshot.eventCount - 1) return false;
  if (snapshot.formatStatus === 'migration-required') {
    logger?.warn?.(`token-logger: backfill skipped ${sessionId}: storage migration required`);
    return false;
  }
  const result = await importSession(store, persistence, snapshot, config, logger);
  logger?.info?.(`token-logger: backfilled ${sessionId}: ${result.events} events`);
  return true;
}

/**
 * Догнать историю при старте: последние `backfillLimit` сессий (PLAN §5.5).
 * @returns {Promise<{seen: number, imported: number, events: number}>}
 */
export async function runBackfill({ store, persistence, config, logger }) {
  const summary = { seen: 0, imported: 0, events: 0 };
  if (typeof persistence?.list !== 'function') {
    logger?.warn?.('token-logger: sessionPersistence is not available, backfill disabled');
    return summary;
  }

  let snapshots;
  try {
    snapshots = await persistence.list();
  } catch (error) {
    logger?.warn?.(`token-logger: backfill list failed: ${error?.message ?? error}`);
    return summary;
  }

  summary.seen = snapshots.length;
  const candidates = snapshots
    .filter((snapshot) => snapshot?.header?.id)
    .filter((snapshot) => {
      const cursor = store.cursorFor(snapshot.header.id);
      return typeof snapshot.eventCount !== 'number' || cursor < snapshot.eventCount - 1;
    })
    .sort((a, b) => (b.header.createdAt ?? 0) - (a.header.createdAt ?? 0))
    .slice(0, Math.max(0, config.backfillLimit));

  for (const snapshot of candidates) {
    if (snapshot.formatStatus === 'migration-required') continue;
    try {
      const result = await importSession(store, persistence, snapshot, config, logger);
      summary.imported++;
      summary.events += result.events;
    } catch (error) {
      logger?.warn?.(`token-logger: backfill failed for ${snapshot.header.id}: ${error?.message ?? error}`);
    }
    await pause(20);
  }

  logger?.info?.(`token-logger: backfill done: ${summary.imported}/${candidates.length} sessions, ${summary.events} events`);
  return summary;
}
