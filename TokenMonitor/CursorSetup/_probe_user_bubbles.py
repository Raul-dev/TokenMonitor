import json
import os
import sqlite3
from pathlib import Path
from collections import Counter

p = Path(os.path.expandvars(r"%APPDATA%\Cursor\User\globalStorage\state.vscdb"))
con = sqlite3.connect(p.as_uri() + "?mode=ro&immutable=1", uri=True)

cid = "cdc1bc22-a5c5-4c70-8414-9577e51f8e5f"
rows = con.execute("SELECT key, json_extract(value,'$.type'), json_extract(value,'$.createdAt'), substr(json_extract(value,'$.text'),1,80), json_extract(value,'$.tokenCount.inputTokens') FROM cursorDiskKV WHERE key LIKE ?", (f"bubbleId:{cid}:%",)).fetchall()
print("count", len(rows))
print("types", Counter(r[1] for r in rows))
print("with text", sum(1 for r in rows if r[3]))
print("\nfirst 15:")
for r in rows[:15]:
    print(r[0].split(":")[-1][:40], "type", r[1], "at", r[2], "text", r[3], "in", r[4])

print("\nuser type=1:")
users = [r for r in rows if r[1] == 1]
print(len(users))
for r in users[:8]:
    print(r[2], r[3])

print("\n=== composerHeaders all non-draft ===")
n = 0
for r in con.execute("SELECT composerId, workspaceId, datetime(lastUpdatedAt/1000,'unixepoch','localtime'), isSubagent, json_extract(value,'$.name'), json_extract(value,'$.isDraft') FROM composerHeaders ORDER BY lastUpdatedAt DESC"):
    n += 1
    if n <= 12:
        print(r)
print("total headers", n)
