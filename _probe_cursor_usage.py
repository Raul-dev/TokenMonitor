import json
import os
import sqlite3
from pathlib import Path

def open_db(p):
    uri = Path(p).as_uri() + "?mode=ro&immutable=1"
    return sqlite3.connect(uri, uri=True)

p = Path(os.path.expandvars(r"%APPDATA%\Cursor\User\globalStorage\state.vscdb"))
con = open_db(p)

ids = [
    "cdc1bc22-a5c5-4c70-8414-9577e51f8e5f",
    "20b9c1f8-acbd-4c46-aa16-a478e3f0e887",
    "c57ce659-7d57-4289-92ee-3df49a3f77ee",
]

for cid in ids:
    print("\n========", cid)
    val = con.execute("SELECT value FROM cursorDiskKV WHERE key=?", (f"composerData:{cid}",)).fetchone()
    if not val:
        print(" no composerData")
        continue
    data = json.loads(val[0])
    for k in ("usageData", "promptTokenBreakdown", "contextTokensUsed", "contextTokenLimit", "contextUsagePercent", "name", "subtitle"):
        v = data.get(k)
        s = json.dumps(v, ensure_ascii=False) if isinstance(v, (dict, list)) else v
        print(f" {k}:", (str(s)[:800] if s is not None else None))

    headers = data.get("fullConversationHeadersOnly") or []
    print(" headers", len(headers), "sample", headers[:2])

print("\n=== search composerData for non-zero usageData ===")
count = 0
nonzero = 0
for key, value in con.execute("SELECT key, value FROM cursorDiskKV WHERE key LIKE 'composerData:%'"):
    count += 1
    if not value:
        continue
    text = value.decode("utf-8", "replace") if isinstance(value, bytes) else value
    data = json.loads(text)
    usage = data.get("usageData")
    ctx = data.get("contextTokensUsed")
    if usage or (ctx and ctx != 0):
        nonzero += 1
        if nonzero <= 5:
            print(key, "ctx", ctx, "usage", json.dumps(usage, ensure_ascii=False)[:500] if usage else None)
print("composerData count", count, "nonzero", nonzero)

print("\n=== search bubbles for non-zero inputTokens (limit 30 hits) ===")
hits = 0
scanned = 0
# sql like is faster than loading all
for key, value in con.execute("SELECT key, value FROM cursorDiskKV WHERE key LIKE 'bubbleId:%' AND typeof(value)='text' AND value LIKE '%\"inputTokens\":%' LIMIT 400"):
    scanned += 1
    text = value
    if '"inputTokens":0' in text.replace(" ", "") and '"outputTokens":0' in text.replace(" ", ""):
        continue
    obj = json.loads(text)
    tc = obj.get("tokenCount") or {}
    inp = tc.get("inputTokens") or 0
    out = tc.get("outputTokens") or 0
    extra = {k: tc[k] for k in tc if k not in ("inputTokens", "outputTokens")}
    if inp or out or extra:
        hits += 1
        if hits <= 8:
            print(key)
            print("  tokenCount", tc)
            print("  type", obj.get("type"), "createdAt", obj.get("createdAt"))
            print("  extra token fields", extra)
            # other usage fields
            for k in obj:
                if "token" in k.lower() or k in ("usage", "usageData"):
                    if k != "tokenCount":
                        print(" ", k, str(obj[k])[:200])
    if hits >= 8:
        break
print("hits", hits, "scanned", scanned)

print("\n=== composer: prefix sample keys ===")
for r in con.execute("SELECT key FROM cursorDiskKV WHERE key LIKE 'composer:%' LIMIT 20"):
    print(" ", r[0])
