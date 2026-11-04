import json, os, sqlite3
from pathlib import Path
p = Path(os.path.expandvars(r"%APPDATA%\Cursor\User\globalStorage\state.vscdb"))
c = sqlite3.connect(p.as_uri() + "?mode=ro&immutable=1", uri=True)
print("subagents", c.execute("SELECT COUNT(*) FROM composerHeaders WHERE isSubagent=1").fetchone())
row = c.execute("SELECT composerId, value FROM composerHeaders WHERE isSubagent=1 LIMIT 1").fetchone()
if row:
    d = json.loads(row[1])
    print("id", row[0])
    print("keys", sorted(d.keys()))
    for k in d:
        if "parent" in k.lower() or "sub" in k.lower():
            print(k, d[k])
# type 1 bubbles globally
n = c.execute("SELECT COUNT(*) FROM cursorDiskKV WHERE key LIKE 'bubbleId:%' AND instr(key,'/')=0 AND json_extract(value,'$.type')=1").fetchone()[0]
print("top-level user bubbles", n)
n2 = c.execute("SELECT COUNT(*) FROM cursorDiskKV WHERE key LIKE 'bubbleId:%' AND instr(key,'/')=0 AND length(ifnull(json_extract(value,'$.text'),''))>0").fetchone()[0]
print("top-level text bubbles", n2)
# modelConfig
v = c.execute("SELECT value FROM cursorDiskKV WHERE key LIKE 'composerData:%' AND json_extract(value,'$.name') LIKE 'Token monitor expansion'").fetchone()
if v:
    d = json.loads(v[0])
    print("modelConfig", json.dumps(d.get("modelConfig"), ensure_ascii=False)[:600])
print("top-level bubbles", c.execute("SELECT COUNT(*) FROM cursorDiskKV WHERE key LIKE 'bubbleId:%' AND instr(key,'/')=0").fetchone()[0])
print("pk", list(c.execute("PRAGMA table_info(cursorDiskKV)")))
