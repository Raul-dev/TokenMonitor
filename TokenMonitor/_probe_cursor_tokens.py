import json
import os
import sqlite3
from pathlib import Path

def open_db(p):
    uri = Path(p).as_uri() + "?mode=ro&immutable=1"
    return sqlite3.connect(uri, uri=True)

p = Path(os.path.expandvars(r"%APPDATA%\Cursor\User\globalStorage\state.vscdb"))
con = open_db(p)

cid = "c57ce659-7d57-4289-92ee-3df49a3f77ee"

print("=== composerHeaders for current ===")
row = con.execute("SELECT composerId, workspaceId, createdAt, lastUpdatedAt, isArchived, isSubagent, value FROM composerHeaders WHERE composerId=?", (cid,)).fetchone()
if row:
    print("ws", row[1], "created", row[2], "updated", row[3], "arch", row[4], "sub", row[5])
    header = json.loads(row[6])
    print("header keys", sorted(header.keys()))
    for k in ("name", "title", "subtitle", "unifiedMode", "workspaceIdentifier", "filesChangedCount"):
        if k in header:
            print(f"  {k}:", header[k])
    print("header snippet", json.dumps(header, ensure_ascii=False)[:1500])
else:
    print("no header")
    print("sample ids:")
    for r in con.execute("SELECT composerId FROM composerHeaders ORDER BY lastUpdatedAt DESC LIMIT 10"):
        print(" ", r[0])

print("\n=== composerData current ===")
val = con.execute("SELECT value FROM cursorDiskKV WHERE key=?", (f"composerData:{cid}",)).fetchone()
if val:
    data = json.loads(val[0])
    print("keys", sorted(data.keys())[:80])
    headers = data.get("fullConversationHeadersOnly") or []
    print("bubbles in headers", len(headers))
    print("first 3 headers", headers[:3])
    # conversationMap?
    cmap = data.get("conversationMap") or {}
    print("conversationMap keys", list(cmap)[:10], "count", len(cmap))
    text = data.get("text") or ""
    print("text", text[:200])
    name = data.get("name") or data.get("title")
    print("name", name)
else:
    print("no composerData")

print("\n=== find a bubble with token fields in this composer ===")
rows = con.execute(
    "SELECT key, value FROM cursorDiskKV WHERE key LIKE ?",
    (f"bubbleId:{cid}:%",),
).fetchall()
print("bubbles", len(rows))
token_keys = set()
shown = 0
for key, value in rows:
    if not value:
        continue
    text = value.decode("utf-8", "replace") if isinstance(value, bytes) else value
    if "token" not in text.lower() and "usage" not in text.lower():
        continue
    obj = json.loads(text)
    # collect keys containing token/usage
    def walk(o, prefix=""):
        if isinstance(o, dict):
            for k, v in o.items():
                if any(n in k.lower() for n in ("token", "usage", "model")):
                    token_keys.add(k)
                    yield prefix + k, v if not isinstance(v, (dict, list)) else type(v).__name__
                if isinstance(v, (dict, list)):
                    yield from walk(v, prefix + k + ".")
        elif isinstance(o, list) and o and isinstance(o[0], dict):
            yield from walk(o[0], prefix + "[].")
    hits = list(walk(obj))
    if hits and shown < 4:
        print("\nBUBBLE", key, "type", obj.get("type"), "model", obj.get("modelName") or obj.get("modelInfo"))
        for k, v in hits[:30]:
            print(" ", k, "=", (str(v)[:200] if not isinstance(v, (dict, list)) else v))
        # print top-level keys
        print(" top keys", list(obj.keys())[:40])
        shown += 1

print("\nall token-ish keys seen:", sorted(token_keys))

print("\n=== composerHeaders recent 5 ===")
for r in con.execute("SELECT composerId, workspaceId, lastUpdatedAt, isSubagent, substr(value,1,300) FROM composerHeaders ORDER BY lastUpdatedAt DESC LIMIT 5"):
    print(r[0], r[1], r[2], r[3], r[4][:200])
