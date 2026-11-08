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
    "Referer": "https://cursor.com/dashboard/usage",
    "Accept": "application/json",
    "Content-Type": "application/json",
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
    "sec-fetch-site": "same-origin",
    "sec-fetch-mode": "cors",
    "sec-fetch-dest": "empty",
}

urls = [
    ("GET", "https://cursor.com/api/dashboard/get-filtered-usage-events"),
    ("GET", "https://cursor.com/api/dashboard/get-aggregated-usage-events"),
    ("GET", "https://cursor.com/api/usage-events"),
    ("POST", "https://cursor.com/api/dashboard/get-filtered-usage-events"),
    ("POST", "https://www.cursor.com/api/dashboard/get-filtered-usage-events"),
]


def call(method, url, body=None):
    req = urllib.request.Request(url, data=body, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=12) as resp:
            raw = resp.read().decode("utf-8", "replace")
            print(method, url, resp.status, raw[:300])
    except urllib.error.HTTPError as e:
        print(method, url, "HTTP", e.code, e.read().decode("utf-8", "replace")[:250])
    except Exception as e:
        print(method, url, type(e).__name__, e)


body = json.dumps({"page": 1, "pageSize": 5, "startDate": "0", "endDate": "9999999999999"}).encode()
for method, url in urls:
    call(method, url, body if method == "POST" else None)
