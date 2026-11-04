import json
import os
import sqlite3
import urllib.request
from pathlib import Path

p = Path(os.path.expandvars(r"%APPDATA%\Cursor\User\globalStorage\state.vscdb"))
token = sqlite3.connect(p.as_uri() + "?mode=ro&immutable=1", uri=True).execute(
    "SELECT value FROM ItemTable WHERE key='cursorAuth/accessToken'"
).fetchone()[0].strip()

paths = [
    ("GET", "https://cursor.com/api/usage-summary"),
    ("GET", "https://cursor.com/api/dashboard/get-current-period-usage"),
    ("GET", "https://www.cursor.com/api/usage-summary"),
    ("POST", "https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage"),
    ("POST", "https://api2.cursor.sh/aiserver.v1.AuthService/GetEmail"),
    ("POST", "https://api2.cursor.sh/aiserver.v1.DashboardService/GetAggregatedUsageEvents"),
]


def try_call(method, url, headers, body=None):
    req = urllib.request.Request(url, data=body, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            raw = resp.read()
            print(method, url, resp.status, "bytes", len(raw), "ct", resp.headers.get("content-type"))
            text = raw.decode("utf-8", "replace")
            print(" ", text[:400].replace("\n", " "))
    except urllib.error.HTTPError as e:
        raw = e.read().decode("utf-8", "replace")
        print(method, url, "HTTP", e.code, raw[:300].replace("\n", " "))
    except Exception as e:
        print(method, url, type(e).__name__, e)


cookie_headers = {
    "Cookie": f"WorkosCursorSessionToken=user_placeholder",
}

# first get sub from /api/auth/me already known; rebuild cookie properly
import base64
payload = json.loads(base64.urlsafe_b64decode(token.split(".")[1] + "=" * (-len(token.split(".")[1]) % 4)))
sub = payload["sub"]
cookie = f"{sub}%3A%3A{token}"

browser = {
    "Cookie": f"WorkosCursorSessionToken={cookie}",
    "Origin": "https://cursor.com",
    "Referer": "https://cursor.com/dashboard",
    "Content-Type": "application/json",
    "Accept": "application/json",
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
}

print("--- usage-summary ---")
try_call("GET", "https://cursor.com/api/usage-summary", browser)
print("--- usage-summary with auth user ---")
try_call("GET", f"https://cursor.com/api/usage-summary?user={sub}", browser)

print("--- POST get-current-period-usage with extra headers ---")
body = json.dumps({"teamId": 0}).encode()
try_call("POST", "https://cursor.com/api/dashboard/get-current-period-usage", browser, body)

print("--- POST with x-cursor ---")
h2 = dict(browser)
h2["Authorization"] = f"Bearer {token}"
try_call("POST", "https://cursor.com/api/dashboard/get-current-period-usage", h2, body)

print("--- api2 bearer ---")
api2 = {
    "Authorization": f"Bearer {token}",
    "Content-Type": "application/json",
    "Connect-Protocol-Version": "1",
    "User-Agent": "Cursor/TokenMonitor",
}
empty = b"{}"
for url in [
    "https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage",
    "https://api2.cursor.sh/aiserver.v1.DashboardService/GetAggregatedUsageEvents",
    "https://api2.cursor.sh/aiserver.v1.AuthService/GetAuthenticationTokens",
    "https://api2.cursor.sh/aiserver.v1.ServerConfigService/GetServerConfig",
    "https://api2.cursor.sh/aiserver.v1.DashboardService/GetPlanInfo",
]:
    try_call("POST", url, api2, empty)
