import os
import sqlite3
from pathlib import Path

def open_db(p):
    uri = Path(p).as_uri() + "?mode=ro&immutable=1"
    return sqlite3.connect(uri, uri=True)

p = Path(os.path.expandvars(r"%APPDATA%\Cursor\User\globalStorage\state.vscdb"))
con = open_db(p)
print("=== global state tables ===")
for r in con.execute("SELECT name FROM sqlite_master WHERE type='table'"):
    print(" ", r[0])
keys = [r[0] for r in con.execute("SELECT key FROM ItemTable")]
print("keys", len(keys))
needles = ("composer", "bubble", "token", "usage", "aichat", "conversation", "agent", "cursorDiskKV", "chat")
hits = [k for k in keys if any(n.lower() in k.lower() for n in needles)]
print("hits", len(hits))
for k in hits[:120]:
    print(" ", k)

print("\n=== conversation-search.db ===")
p2 = Path(os.path.expandvars(r"%APPDATA%\Cursor\User\globalStorage\conversation-search.db"))
con2 = open_db(p2)
for r in con2.execute("SELECT name FROM sqlite_master WHERE type='table'"):
    print(" table", r[0])
    cols = list(con2.execute(f"PRAGMA table_info({r[0]})"))
    print("  cols", [(c[1], c[2]) for c in cols])
    try:
        n = con2.execute(f"SELECT COUNT(*) FROM [{r[0]}]").fetchone()[0]
        print("  count", n)
        if n:
            row = con2.execute(f"SELECT * FROM [{r[0]}] LIMIT 1").fetchone()
            preview = []
            for x in row:
                if x is None:
                    preview.append(None)
                elif isinstance(x, bytes):
                    preview.append(f"bytes:{len(x)}")
                elif isinstance(x, str):
                    preview.append(x[:180])
                else:
                    preview.append(x)
            print("  sample", preview)
    except Exception as e:
        print("  err", e)

print("\n=== agent-host data.sqlite ===")
p3 = Path(os.path.expandvars(r"%APPDATA%\Cursor\User\globalStorage\anysphere.cursor-agent-host\data.sqlite"))
con3 = open_db(p3)
for r in con3.execute("SELECT name FROM sqlite_master WHERE type='table'"):
    print(" table", r[0])
    cols = list(con3.execute(f"PRAGMA table_info({r[0]})"))
    print("  cols", [(c[1], c[2]) for c in cols])
    try:
        n = con3.execute(f"SELECT COUNT(*) FROM [{r[0]}]").fetchone()[0]
        print("  count", n)
        if n:
            row = con3.execute(f"SELECT * FROM [{r[0]}] LIMIT 1").fetchone()
            preview = []
            for x in row:
                if x is None:
                    preview.append(None)
                elif isinstance(x, bytes):
                    preview.append(f"bytes:{len(x)}")
                elif isinstance(x, str):
                    preview.append(x[:180])
                else:
                    preview.append(x)
            print("  sample", preview)
    except Exception as e:
        print("  err", e)
