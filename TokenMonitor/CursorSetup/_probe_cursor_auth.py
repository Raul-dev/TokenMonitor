import json
import os
import sqlite3
from pathlib import Path

p = Path(os.path.expandvars(r"%APPDATA%\Cursor\User\globalStorage\state.vscdb"))
print("exists", p.exists(), p)
uri = p.as_uri() + "?mode=ro&immutable=1"
con = sqlite3.connect(uri, uri=True)
keys = [r[0] for r in con.execute("SELECT key FROM ItemTable WHERE key LIKE 'cursorAuth/%'")]
print("keys", keys)
for k in keys:
    val = con.execute("SELECT value FROM ItemTable WHERE key=?", (k,)).fetchone()[0]
    kind = "str" if isinstance(val, str) else type(val).__name__
    length = len(val) if isinstance(val, str) else -1
    starts = ""
    if isinstance(val, str):
        starts = val[:12].replace("\n", " ")
        if k.endswith("cachedScopedProfile"):
            try:
                parsed = json.loads(val)
                if isinstance(parsed, dict):
                    print(k, "json_keys", sorted(parsed.keys())[:30])
                    for name in ("authId", "email", "id", "sub", "membershipType"):
                        if name in parsed:
                            print(" ", name, str(parsed[name])[:80])
            except Exception as e:
                print(k, "json_fail", e)
    print(k, "kind", kind, "len", length, "starts", starts)
