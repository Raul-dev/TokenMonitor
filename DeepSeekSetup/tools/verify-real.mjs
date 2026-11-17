// P0/приёмка: прогнать fold по реальному журналу DSH и сверить суммы (PLAN §9.2).
// node verify-real.mjs <journal.zstd> [projection.json]
import { readFileSync } from 'node:fs';
import { zstdDecompressSync } from 'node:zlib';
import { TokenStore } from '../plugin/src/db.js';
import { createState, foldEvent, sessionRecord } from '../plugin/src/fold.js';

const ZSTD_MAGIC = 0xfd2fb528;
const SKIP = 0xfffffff0;

function frameSize(buf, off) {
  if (off + 4 > buf.length) return -1;
  const magic = buf.readUInt32LE(off);
  if ((magic & SKIP) === 0x184d2a50) return off + 8 <= buf.length ? 8 + buf.readUInt32LE(off + 4) : -1;
  if (magic !== ZSTD_MAGIC) return -1;
  let p = off + 4;
  const fhd = buf[p++];
  const fcs = fhd >> 6;
  const single = (fhd >> 5) & 1;
  const checksum = (fhd >> 2) & 1;
  const did = fhd & 3;
  if (!single) p++;
  p += [0, 1, 2, 4][did];
  p += fcs === 0 ? (single ? 1 : 0) : fcs === 1 ? 2 : fcs === 2 ? 4 : 8;
  for (;;) {
    if (p + 3 > buf.length) return -1;
    const bh = buf[p] | (buf[p + 1] << 8) | (buf[p + 2] << 16);
    p += 3;
    const last = bh & 1;
    const type = (bh >> 1) & 3;
    const size = bh >> 3;
    if (type === 0 || type === 2) p += size;
    else if (type === 1) p += 1;
    else return -1;
    if (p > buf.length) return -1;
    if (last) break;
  }
  if (checksum) p += 4;
  return p - off;
}

function readJournal(file) {
  const buf = readFileSync(file);
  let off = 0;
  let text = '';
  let frames = 0;
  while (off < buf.length) {
    const size = frameSize(buf, off);
    if (size <= 0) break;
    if ((buf.readUInt32LE(off) & SKIP) !== 0x184d2a50) {
      text += zstdDecompressSync(buf.subarray(off, off + size)).toString('utf8');
      frames++;
    }
    off += size;
  }
  const lines = text.split('\n').filter((line) => line.trim());
  return { frames, perLine: lines.length, records: lines.map((line) => JSON.parse(line)) };
}

const journalPath = process.argv[2];
const projectionPath = process.argv[3];
const { frames, perLine, records } = readJournal(journalPath);
const header = records.find((record) => record.type === 'session');
const events = records.filter((record) => Number.isInteger(record.seq));

console.log('header records:', records.filter((record) => record.type === 'session').length,
  'first:', JSON.stringify(header));

const store = new TokenStore({ dbPath: ':memory:', logger: { warn() {}, info() {} } }).open();
try {
  const state = createState();
  store.enqueue([sessionRecord(header, { now: Date.now(), source: 'backfill' })]);
  let batch = [];
  for (const event of events) {
    batch.push(...foldEvent(state, event, {
      sessionId: header.id,
      now: Date.now(),
      capture: 'full',
      captureText: 'preview',
      previewChars: 200,
      source: 'backfill',
    }));
    if (batch.length > 400) {
      store.writeRecords(batch);
      batch = [];
    }
  }
  store.writeRecords(batch);

  const session = store.prepare('SELECT * FROM v_session_summary WHERE session_id = ?').get(header.id);
  const steps = store.prepare('SELECT COUNT(*) n, SUM(input_tokens) i, SUM(output_tokens) o, SUM(cache_read_tokens) cr, SUM(total_tokens) t FROM steps').get();
  const usage = store.prepare('SELECT COUNT(*) n, SUM(total_tokens) t FROM usage_samples').get();
  const tools = store.prepare('SELECT COUNT(*) n, SUM(CASE WHEN is_error = 1 THEN 1 ELSE 0 END) e FROM tool_calls').get();
  const turns = store.prepare('SELECT COUNT(*) n FROM turns').get();
  const health = store.prepare('SELECT * FROM v_ingest_health').get();

  console.log('journal frames/records:', frames, perLine, 'events:', events.length);
  console.log('session row:', JSON.stringify(session));
  console.log('steps:', JSON.stringify(steps));
  console.log('usage_samples:', JSON.stringify(usage));
  console.log('tool_calls:', JSON.stringify(tools), 'turns:', turns.n);
  console.log('health:', JSON.stringify(health));

  let ok = true;
  const check = (name, actual, expected) => {
    const pass = String(actual) === String(expected);
    if (!pass) ok = false;
    console.log(`${pass ? 'OK  ' : 'FAIL'} ${name}: ${actual} (expected ${expected})`);
  };
  check('steps count', steps.n, session.steps);
  check('steps sum == usage sum', steps.t, usage.t);
  check('usage samples == assistant/message usage rows',
    usage.n, events.filter((event) => event.type === 'assistant/message' && event.data?.usage).length);

  if (projectionPath) {
    const proj = JSON.parse(readFileSync(projectionPath, 'utf8'));
    const totals = proj.record?.rows?.tokenUsage?.val?.totals;
    const stats = proj.record?.rows?.sessionStats?.val;
    if (totals) {
      check('uncached input', steps.i, totals.uncachedInputTokens);
      check('output', steps.o, totals.outputTokens);
      check('cache read', steps.cr, totals.cacheReadTokens);
      check('total tokens', steps.t,
        totals.uncachedInputTokens + totals.outputTokens + totals.cacheReadTokens + totals.cacheWriteTokens);
    }
    if (stats) check('steps vs sessionStats.steps', steps.n, stats.steps);
  }

  store.close();
  process.exit(ok ? 0 : 1);
} catch (error) {
  console.error('verify failed:', error);
  store.close();
  process.exit(1);
}
