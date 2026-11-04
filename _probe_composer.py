import os
import sqlite3
from pathlib import Path

root = Path(os.path.expandvars(r"%APPDATA%\Cursor\User"))
dbs = [root / "globalStorage" / "state.vscdb"]
ws = root / "workspaceStorage"
if ws.exists():
    dbs.extend(ws.glob("*/state.vscdb"))

needles = ("token", "usage", "composerData", "composer.", "aiService")
for db in dbs[:20]:
    try:
        con = sqlite3.connect(db.as_uri() + "?mode=ro&immutable=1", uri=True)
        keys = [r[0] for r in con.execute("SELECT key FROM ItemTable")]
    except Exception as e:
        print("fail", db, e)
        continue
    hits = [k for k in keys if any(n.lower() in k.lower() for n in needles)]
    if hits:
        print(db.parent.name, "hits", len(hits))
        for k in hits[:40]:
            print(" ", k)
