import base64
import json
import os
import sqlite3
import urllib.request
from pathlib import Path

p = Path(os.path.expandvars(r"%APPDATA%\Cursor\User\globalStorage\state.vscdb"))
token = sqlite3.connect(p.as_uri() + "?mode=ro&immutable=1", uri=True).execute(
    "SELECT value FROM ItemTable WHERE key='cursorAuth/accessToken'"
).fetchone()[0].strip()
payload = json.loads(base64.urlsafe_b64decode(token.split(".")[1] + "=" * (-len(token.split(".")[1]) % 4)))
sub = payload["sub"]
cookie = f"{sub}%3A%3A{token}"
headers = {
    "Cookie": f"WorkosCursorSessionToken={cookie}",
    "Origin": "https://cursor.com",
    "Referer": "https://cursor.com/dashboard",
    "Accept": "application/json",
    "User-Agent": "Mozilla/5.0",
}


def get(url):
    req = urllib.request.Request(url, method="GET", headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            raw = resp.read().decode("utf-8", "replace")
            print(url, resp.status, raw[:1500])
    except urllib.error.HTTPError as e:
        print(url, "HTTP", e.code, e.read().decode("utf-8", "replace")[:800])


get("https://cursor.com/api/usage-summary")
get("https://cursor.com/api/auth/stripe")
get("https://cursor.com/api/dashboard/teams")
