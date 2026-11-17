// Поиск по установленным пакетам DSH (asar-aware): proxy-настройки и HTTP-клиент.
// ELECTRON_RUN_AS_NODE=1 <DeepSeek Harness.exe> search-proxy.mjs <asar node_modules dir> [regex]
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';

const [root, patternSource] = process.argv.slice(-2);
const pattern = new RegExp(patternSource ?? 'proxy', 'i');
const maxFile = 1_500_000;
const exts = new Set(['.js', '.mjs', '.cjs', '.ts', '.json', '.md', '.yml', '.yaml']);

const packages = readdirSync(root, { withFileTypes: true })
  .filter((entry) => entry.isDirectory() && entry.name.startsWith('dsh'))
  .map((entry) => entry.name)
  .sort();

const hits = [];
let scanned = 0;

function walk(dir, depth) {
  if (depth > 3) return;
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.name === 'node_modules' || entry.name === '.git' || entry.name === 'test' || entry.name === 'tests') continue;
    const full = join(dir, entry.name);
    if (entry.isDirectory()) { walk(full, depth + 1); continue; }
    const dot = entry.name.lastIndexOf('.');
    if (dot < 0 || !exts.has(entry.name.slice(dot))) continue;
    let info;
    try { info = statSync(full); } catch { continue; }
    if (!info.isFile() || info.size > maxFile) continue;
    let text;
    try { text = readFileSync(full, 'utf8'); } catch { continue; }
    scanned++;
    if (!pattern.test(text)) continue;
    const lines = text.split('\n');
    lines.forEach((line, index) => {
      if (!pattern.test(line)) return;
      hits.push({ file: full, line: index + 1, text: line.trim().slice(0, 240) });
    });
  }
}

for (const name of packages) {
  const dir = join(root, name);
  try { walk(dir, 0); } catch { }
}

console.log(`scanned files: ${scanned}; hits: ${hits.length}`);
const limit = Number(process.env.HIT_LIMIT ?? 120);
for (const hit of hits.slice(0, limit)) {
  console.log(`${hit.file.replace(root, '')}:${hit.line}: ${hit.text}`);
}
