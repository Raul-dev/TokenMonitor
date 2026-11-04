import base64
import json
import os
import sqlite3
import urllib.request
from pathlib import Path

p = Path(os.path.expandvars(r"%APPDATA%\Cursor\User\globalStorage\state.vscdb"))
uri = p.as_uri() + "?mode=ro&immutable=1"
con = sqlite3.connect(uri, uri=True)
token = con.execute("SELECT value FROM ItemTable WHERE key='cursorAuth/accessToken'").fetchone()[0].strip()
if token.startswith('"') and token.endswith('"'):
    token = json.loads(token)

def b64url(seg: str) -> bytes:
    pad = "=" * (-len(seg) % 4)
    return base64.urlsafe_b64decode(seg + pad)

payload = json.loads(b64url(token.split(".")[1]))
sub = payload.get("sub", "")
print("jwt_keys", sorted(payload.keys()))
print("sub_prefix", sub.split("|")[-1][:12] if sub else "")
print("token_type", payload.get("type"))
print("aud", payload.get("aud"))

cookie = f"{sub}%3A%3A{token}"


def call(method: str, path: str, body=None):
    req = urllib.request.Request(
        "https://cursor.com" + path,
        data=None if body is None else json.dumps(body).encode(),
        method=method,
        headers={
            "Cookie": f"WorkosCursorSessionToken={cookie}",
            "Origin": "https://cursor.com",
            "Content-Type": "application/json",
            "User-Agent": "TokenMonitor/1.0",
        },
    )
    try:
        with urllib.request.urlopen(req, timeout=20) as resp:
            raw = resp.read()
            print(path, resp.status, "bytes", len(raw))
            return json.loads(raw.decode() or "null")
    except urllib.error.HTTPError as e:
        raw = e.read().decode("utf-8", "replace")
        print(path, "HTTP", e.code, raw[:400])
        return None


me = call("GET", "/api/auth/me")
if isinstance(me, dict):
    print("me_keys", sorted(me.keys()))
    print("me_id", me.get("id"), "email_len", len(str(me.get("email") or "")))

user = me.get("sub") if isinstance(me, dict) else None
usage = call("GET", f"/api/usage?user={user or sub}")
if isinstance(usage, dict):
    print("usage_keys", sorted(usage.keys())[:40])
    print("startOfMonth", usage.get("startOfMonth"))
    for k, v in usage.items():
        if isinstance(v, dict):
            print(" usage", k, {kk: v.get(kk) for kk in list(v)[:8]})

period = call("POST", "/api/dashboard/get-current-period-usage", {"teamId": 0})
if isinstance(period, dict):
    print("period_keys", sorted(period.keys()))
    print("period_sample", json.dumps(period)[:800])

agg = call(
    "POST",
    "/api/dashboard/get-aggregated-usage-events",
    {"teamId": 0, "startDate": "0", "endDate": "9999999999999", "userId": me.get("id") if me else None},
)
if isinstance(agg, dict):
    print("agg_keys", sorted(agg.keys()))
    print("agg_sample", json.dumps(agg)[:1200])

ev = call(
    "POST",
    "/api/dashboard/get-filtered-usage-events",
    {
        "teamId": 0,
        "startDate": "0",
        "endDate": "9999999999999",
        "userId": me.get("id") if me else None,
        "page": 1,
        "pageSize": 5,
    },
)
if isinstance(ev, dict):
    print("ev_keys", sorted(ev.keys()))
    print("count", ev.get("totalUsageEventsCount"))
    items = ev.get("usageEventsDisplay") or ev.get("usageEvents") or []
    print("first_item_keys", sorted(items[0].keys()) if items else None)
    if items:
        sample = dict(items[0])
        print("first_item", json.dumps(sample)[:1500])
