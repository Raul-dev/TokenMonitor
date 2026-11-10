import json
import os
import sqlite3
from pathlib import Path
from collections import Counter

def open_db(p):
    uri = Path(p).as_uri() + "?mode=ro&immutable=1"
    return sqlite3.connect(uri, uri=True)

p = Path(os.path.expandvars(r"%APPDATA%\Cursor\User\globalStorage\state.vscdb"))
con = open_db(p)

cid = "cdc1bc22-a5c5-4c70-8414-9577e51f8e5f"
val = con.execute("SELECT value FROM cursorDiskKV WHERE key=?", (f"composerData:{cid}",)).fetchone()
data = json.loads(val[0])
headers = data.get("fullConversationHeadersOnly") or []
print("headers", len(headers), "types", Counter(h.get("type") for h in headers))

# pick a user bubble (type 1) and assistant (type 2)
user = next((h for h in headers if h.get("type") == 1), None)
ai = next((h for h in headers if h.get("type") == 2 and h.get("grouping", {}).get("hasText")), None)
print("user header", user)
print("ai header", ai)

for h in (user, ai):
    if not h:
        continue
    bid = h["bubbleId"]
    key = f"bubbleId:{cid}:{bid}"
    row = con.execute("SELECT value FROM cursorDiskKV WHERE key=?", (key,)).fetchone()
    print("\n====", key, "found", bool(row), "len", len(row[0]) if row else 0)
    if not row:
        continue
    obj = json.loads(row[0])
    print("keys", list(obj.keys()))
    # dump interesting fields
    for k in sorted(obj.keys()):
        v = obj[k]
        if v in (None, "", [], {}, False, 0):
            continue
        if k in ("text", "richText", "thinking", "thinkingStyle", "capabilityName"):
            print(f"  {k}:", str(v)[:180])
        elif isinstance(v, (dict, list)):
            s = json.dumps(v, ensure_ascii=False)
            if any(n in s.lower() for n in ("token", "usage", "model")) or len(s) < 400:
                print(f"  {k}:", s[:400])
        else:
            print(f"  {k}:", str(v)[:200])

print("\n=== SQL count of non-zero inputTokens ===")
# sqlite json_extract if available
try:
    n = con.execute("SELECT COUNT(*) FROM cursorDiskKV WHERE key LIKE 'bubbleId:%' AND json_extract(value, '$.tokenCount.inputTokens') > 0").fetchone()[0]
    print("json_extract input>0", n)
    n2 = con.execute("SELECT COUNT(*) FROM cursorDiskKV WHERE key LIKE 'bubbleId:%' AND json_extract(value, '$.tokenCount.outputTokens') > 0").fetchone()[0]
    print("json_extract output>0", n2)
    row = con.execute("SELECT key, json_extract(value, '$.tokenCount.inputTokens'), json_extract(value, '$.tokenCount.outputTokens') FROM cursorDiskKV WHERE key LIKE 'bubbleId:%' AND (json_extract(value, '$.tokenCount.inputTokens') > 0 OR json_extract(value, '$.tokenCount.outputTokens') > 0) LIMIT 5").fetchall()
    print("samples", row)
except Exception as e:
    print("json_extract err", e)

print("\n=== other token field names in composerData keys across all ===")
fields = Counter()
for key, value in con.execute("SELECT key, value FROM cursorDiskKV WHERE key LIKE 'composerData:%'"):
    obj = json.loads(value)
    for k in obj:
        if "token" in k.lower() or "usage" in k.lower():
            fields[k] += 1
print(fields)

print("\n=== workspace.json for a workspace ===")
ws = Path(os.path.expandvars(r"%APPDATA%\Cursor\User\workspaceStorage\5fb8a836288a83009e9b1925c1ba0c7f"))
print(list(ws.iterdir())[:20] if ws.exists() else "no")
wsdb = ws / "state.vscdb"
if wsdb.exists():
    c2 = open_db(wsdb)
    print("ws tables", [r[0] for r in c2.execute("SELECT name FROM sqlite_master WHERE type='table'")])
    keys = [r[0] for r in c2.execute("SELECT key FROM ItemTable")]
    print("ws keys", len(keys))
    for k in keys:
        if any(n in k.lower() for n in ("composer", "token", "usage", "agent")):
            print(" ", k)
