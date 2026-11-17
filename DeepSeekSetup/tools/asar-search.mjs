// Поиск по файлам внутри app.asar через Electron-рантайм (обычный node не умеет asar).
// Запуск: ELECTRON_RUN_AS_NODE=1 "<DeepSeek Harness.exe>" asar-search.mjs <file> <regex>
import { readFileSync } from 'node:fs';

const [file, pattern] = process.argv.slice(-2);
const re = new RegExp(pattern);
const lines = readFileSync(file, 'utf8').split('\n');
let n = 0;
lines.forEach((line, i) => {
  if (!re.test(line)) return;
  n++;
  if (n > 60) return;
  console.log(`${i + 1}: ${line.slice(0, 400)}`);
});
console.error(`matches: ${n} of ${lines.length} lines in ${file}`);
