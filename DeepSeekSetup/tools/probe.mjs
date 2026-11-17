// P0-проба: агрегирует формы payload по журналу DSH (без записи в него).
// node probe.mjs <journal> [aspect]
import { readFileSync } from 'node:fs';
import { zstdDecompressSync } from 'node:zlib';

const ZSTD_MAGIC = 0xfd2fb528;
const SKIP = 0xfffffff0;
function frameSize(buf, off) {
  if (off + 4 > buf.length) return -1;
  const magic = buf.readUInt32LE(off);
  if ((magic & SKIP) === 0x184d2a50) return off + 8 <= buf.length ? 8 + buf.readUInt32LE(off + 4) : -1;
  if (magic !== ZSTD_MAGIC) return -1;
  let p = off + 4;
  const fhd = buf[p++];
  const fcs = fhd >> 6, single = (fhd >> 5) & 1, sum = (fhd >> 2) & 1, did = fhd & 3;
  if (!single) p++;
  p += [0, 1, 2, 4][did];
  p += fcs === 0 ? (single ? 1 : 0) : fcs === 1 ? 2 : fcs === 2 ? 4 : 8;
  for (;;) {
    if (p + 3 > buf.length) return -1;
    const bh = buf[p] | (buf[p + 1] << 8) | (buf[p + 2] << 16);
    p += 3;
    const last = bh & 1, type = (bh >> 1) & 3, size = bh >> 3;
    if (type === 0 || type === 2) p += size;
    else if (type === 1) p += 1;
    else return -1;
    if (p > buf.length) return -1;
    if (last) break;
  }
  if (sum) p += 4;
  return p - off;
}

const buf = readFileSync(process.argv[2]);
const aspect = process.argv[3] ?? 'all';
let off = 0, text = '';
while (off < buf.length) {
  const size = frameSize(buf, off);
  if (size <= 0) break;
  if ((buf.readUInt32LE(off) & SKIP) !== 0x184d2a50) text += zstdDecompressSync(buf.subarray(off, off + size)).toString('utf8');
  off += size;
}
const events = text.split('\n').filter(Boolean).map((l) => JSON.parse(l));

const blockTypes = new Map();
const streamTypes = new Map();
const toolErrors = [];
let usageMissing = 0, usageExtra = new Set(), streamLen = [];
let interruptedRows = 0;

for (const ev of events) {
  const d = ev.data ?? {};
  if (ev.type === 'assistant/message') {
    if (!d.usage) usageMissing++;
    else for (const k of Object.keys(d.usage)) usageExtra.add(k);
    if (d.interrupted) interruptedRows++;
    streamLen.push(Array.isArray(d.stream) ? d.stream.length : -1);
    for (const b of d.message?.content ?? []) {
      const key = `${b.type}${b.type === 'reasoning' ? '' : ''}`;
      blockTypes.set(key, (blockTypes.get(key) ?? 0) + 1);
    }
    for (const r of d.stream ?? []) streamTypes.set(r.type, (streamTypes.get(r.type) ?? 0) + 1);
  }
  if (ev.type === 'tool/result' && (d.message?.isError || d.error)) {
    toolErrors.push({ seq: ev.seq, data: d });
  }
}

console.log('aspect:', aspect, 'events:', events.length);
if (aspect === 'all' || aspect === 'blocks') console.log('content blocks:', [...blockTypes]);
if (aspect === 'all' || aspect === 'stream') console.log('stream record types:', [...streamTypes], 'lens:', streamLen.slice(0, 10), 'max', Math.max(...streamLen));
if (aspect === 'all' || aspect === 'usage') console.log('usage keys:', [...usageExtra], 'missing usage on assistant/message:', usageMissing, 'interrupted:', interruptedRows);
if (aspect === 'all' || aspect === 'errors') console.log('tool errors:', JSON.stringify(toolErrors.slice(0, 3), null, 2).slice(0, 3000));
