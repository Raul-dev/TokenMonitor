import os
import sqlite3
from pathlib import Path
from collections import Counter

def open_db(p):
    uri = Path(p).as_uri() + "?mode=ro&immutable=1"
    return sqlite3.connect(uri, uri=True)

p = Path(os.path.expandvars(r"%APPDATA%\Cursor\User\globalStorage\state.vscdb"))
con = open_db(p)

print("=== composerHeaders ===")
cols = list(con.execute("PRAGMA table_info(composerHeaders)"))
print("cols", [(c[1], c[2]) for c in cols])
n = con.execute("SELECT COUNT(*) FROM composerHeaders").fetchone()[0]
print("count", n)
if n:
    row = con.execute("SELECT * FROM composerHeaders LIMIT 1").fetchone()
    for i, (name, _) in enumerate((c[1], c[2]) for c in cols):
        val = row[i]
        if isinstance(val, bytes):
            print(f"  {name}: bytes {len(val)}")
        elif isinstance(val, str):
            print(f"  {name}: {val[:400]}")
        else:
            print(f"  {name}: {val}")

print("\n=== cursorDiskKV ===")
cols = list(con.execute("PRAGMA table_info(cursorDiskKV)"))
print("cols", [(c[1], c[2]) for c in cols])
n = con.execute("SELECT COUNT(*) FROM cursorDiskKV").fetchone()[0]
print("count", n)
keys = [r[0] for r in con.execute("SELECT key FROM cursorDiskKV")]
print("sample keys 40:")
for k in keys[:40]:
    print(" ", k)

prefixes = Counter()
for k in keys:
    prefixes[k.split(":")[0] if ":" in k else k.split(".")[0]] += 1
print("\nprefixes:")
for k, v in prefixes.most_common(40):
    print(f"  {k}: {v}")

# look for token/usage in values of a few composer keys
print("\n=== composer-like keys ===")
comp = [k for k in keys if "composer" in k.lower() or "bubble" in k.lower() or "agent" in k.lower()]
print("composer-like", len(comp))
for k in comp[:30]:
    print(" ", k)

# dump one value that might have tokens
needles = ("tokenUsage", "inputTokens", "usage", "tokenCount")
print("\n=== searching values for token fields (first 200 kv rows) ===")
found = 0
for key, value in con.execute("SELECT key, value FROM cursorDiskKV LIMIT 200"):
    if value is None:
        continue
    text = value.decode("utf-8", "replace") if isinstance(value, bytes) else str(value)
    if any(n in text for n in needles):
        print("HIT", key, text[:500])
        found += 1
        if found >= 8:
            break
print("found", found)

print("\n=== agent-host tables empty? ===")
p3 = Path(os.path.expandvars(r"%APPDATA%\Cursor\User\globalStorage\anysphere.cursor-agent-host\data.sqlite"))
print("exists", p3.exists(), "size", p3.stat().st_size if p3.exists() else 0)
try:
    con3 = sqlite3.connect(str(p3))
    print(list(con3.execute("SELECT name FROM sqlite_master WHERE type='table'")))
except Exception as e:
    print("err", e)
