// Диагностический скрипт P0: печатает типы событий и образцы payload из журнала DSH.
// Журнал — конкатенация независимых zstd-фреймов (по кадру на сброс), поэтому делим сами.
// Использование: node inspect-journal.mjs <session.v4.jsonl.zstd> [type ...]
import { readFileSync } from 'node:fs';
import { zstdDecompressSync } from 'node:zlib';

const ZSTD_MAGIC = 0xfd2fb528;
const SKIPPABLE_MASK = 0xfffffff0;

function frameSize(buf, off) {
  if (off + 4 > buf.length) return -1;
  const magic = buf.readUInt32LE(off);
  if ((magic & SKIPPABLE_MASK) === 0x184d2a50) {
    if (off + 8 > buf.length) return -1;
    return 8 + buf.readUInt32LE(off + 4);
  }
  if (magic !== ZSTD_MAGIC) return -1;
  let p = off + 4;
  const fhd = buf[p++];
  const fcsFlag = fhd >> 6;
  const singleSegment = (fhd >> 5) & 1;
  const checksum = (fhd >> 2) & 1;
  const didFlag = fhd & 3;
  if (!singleSegment) p++; // Window_Descriptor
  p += [0, 1, 2, 4][didFlag];
  p += fcsFlag === 0 ? (singleSegment ? 1 : 0) : fcsFlag === 1 ? 2 : fcsFlag === 2 ? 4 : 8;
  for (;;) {
    if (p + 3 > buf.length) return -1;
    const bh = buf[p] | (buf[p + 1] << 8) | (buf[p + 2] << 16);
    p += 3;
    const last = bh & 1;
    const type = (bh >> 1) & 3;
    const size = bh >> 3;
    if (type === 0) p += size; // Raw_Block: size bytes of raw data follow
    else if (type === 1) p += 1; // RLE_Block: 1 byte
    else if (type === 2) p += size; // Compressed_Block
    else return -1; // reserved
    if (p > buf.length) return -1;
    if (last) break;
  }
  if (checksum) p += 4;
  return p - off;
}

const file = process.argv[2];
const rest = process.argv.slice(3);
const brief = rest[0] === '--brief';
const wanted = new Set(brief ? rest.slice(1) : rest);

function summarize(value, depth = 0) {
  if (value === null || typeof value !== 'object') {
    if (typeof value === 'string' && value.length > 300) return value.slice(0, 300) + `…(${value.length})`;
    return value;
  }
  if (Array.isArray(value)) {
    const head = value.slice(0, 2).map((v) => summarize(v, depth + 1));
    return value.length > 2 ? [...head, `…(${value.length})`] : head;
  }
  if (depth >= 4) return `{…${Object.keys(value).length} keys}`;
  const out = {};
  for (const [k, v] of Object.entries(value)) out[k] = summarize(v, depth + 1);
  return out;
}
const buf = readFileSync(file);

const chunks = [];
let off = 0;
let frames = 0;
while (off < buf.length) {
  const size = frameSize(buf, off);
  if (size <= 0) {
    console.error('cannot parse frame at', off, 'of', buf.length);
    break;
  }
  const magic = buf.readUInt32LE(off);
  if ((magic & SKIPPABLE_MASK) !== 0x184d2a50) {
    chunks.push(zstdDecompressSync(buf.subarray(off, off + size)).toString('utf8'));
    frames++;
  }
  off += size;
}
const text = chunks.join('');

const lines = text.split('\n').filter((l) => l.trim().length > 0);
const counts = new Map();
const samples = new Map();

for (const line of lines) {
  let ev;
  try { ev = JSON.parse(line); } catch { console.error('bad line', line.slice(0, 120)); continue; }
  counts.set(ev.type, (counts.get(ev.type) ?? 0) + 1);
  if (!samples.has(ev.type)) samples.set(ev.type, ev);
}

console.error('frames:', frames, 'events:', lines.length);
console.log('--- counts ---');
for (const [t, n] of [...counts].sort((a, b) => a[0].localeCompare(b[0]))) console.log(String(n).padStart(6), t);

for (const t of wanted) {
  const ev = samples.get(t);
  console.log('\n=== sample:', t, '===');
  const rendered = brief ? summarize(ev) : ev;
  console.log(JSON.stringify(rendered, null, 2).slice(0, 7000));
}
