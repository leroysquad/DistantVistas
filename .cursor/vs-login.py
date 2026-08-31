#!/usr/bin/env python3
"""Seed a Vintage Story client session into a clientsettings.json.

The graphical client validates a cached session (sessionkey + sessionsignature)
offline on startup; without one it stops at the login screen and never joins a
server. This lets a headless Cloud Agent client authenticate so the `smoke` and
`matrix` check tiers can run.

Credentials come from environment variables (set them as Cloud Agent Secrets,
never on the command line or in the repo):

  Login with account credentials (re-issues the account session -- see WARNING):
    VS_EMAIL          account email
    VS_PASSWORD       account password
    VS_TOTP_SECRET    base32 TOTP secret, ONLY if the account has 2FA enabled

  Or reuse an already-issued session (does NOT invalidate other logins):
    VS_SESSIONKEY, VS_SESSIONSIGNATURE, and optionally VS_ENTITLEMENTS,
    VS_MPTOKEN, VS_EMAIL (stored as useremail).

WARNING: a fresh credential login issues a new session and the Vintage Story
auth service keeps only one session per account, so logging in here invalidates
the session on any other machine (you'll be prompted to log in there once more).
Use the VS_SESSIONKEY path to avoid that.

Usage: vs-login.py <path-to-clientsettings.json>
"""

import base64
import hashlib
import hmac
import json
import os
import struct
import sys
import time
import urllib.request

AUTH_URL = "https://auth.vintagestory.at/v2/gamelogin"


def totp_now(secret_b32: str) -> str:
    """RFC 6238 TOTP, 6 digits, SHA1, 30s step -- what VS 2FA expects."""
    key = base64.b32decode(secret_b32.strip().replace(" ", "").upper())
    counter = int(time.time()) // 30
    mac = hmac.new(key, struct.pack(">Q", counter), hashlib.sha1).digest()
    offset = mac[-1] & 0x0F
    code = (struct.unpack(">I", mac[offset:offset + 4])[0] & 0x7FFFFFFF) % 1_000_000
    return f"{code:06d}"


def post(payload: dict) -> dict:
    data = json.dumps(payload).encode()
    req = urllib.request.Request(
        AUTH_URL, data=data, headers={"Content-Type": "application/json"}
    )
    with urllib.request.urlopen(req, timeout=30) as resp:
        return json.loads(resp.read().decode())


def login() -> dict:
    email = os.environ.get("VS_EMAIL", "").strip()
    password = os.environ.get("VS_PASSWORD", "")
    if not email or not password:
        sys.exit("vs-login: VS_EMAIL and VS_PASSWORD are required for a credential login")

    body = {"email": email, "password": password}
    totp_secret = os.environ.get("VS_TOTP_SECRET", "").strip()
    if totp_secret:
        body["totpCode"] = totp_now(totp_secret)

    result = post(body)

    # 2FA: the first call hands back a prelogintoken; resend with the code.
    if not result.get("valid") and result.get("reason") == "requiretotpcode":
        if not totp_secret:
            sys.exit("vs-login: account requires 2FA; set VS_TOTP_SECRET (base32)")
        result = post({
            "email": email,
            "password": password,
            "prelogintoken": result.get("prelogintoken", ""),
            "totpCode": totp_now(totp_secret),
        })

    if not result.get("valid"):
        sys.exit(f"vs-login: login failed: {result.get('reason', 'unknown')}")

    return {
        "sessionkey": result.get("sessionkey", ""),
        "sessionsignature": result.get("sessionsignature", ""),
        "entitlements": result.get("entitlements", ""),
        "mptoken": result.get("mptoken", ""),
        "useremail": email,
        "_playername": result.get("playername", ""),
    }


def from_env_session() -> dict:
    return {
        "sessionkey": os.environ["VS_SESSIONKEY"],
        "sessionsignature": os.environ["VS_SESSIONSIGNATURE"],
        "entitlements": os.environ.get("VS_ENTITLEMENTS", ""),
        "mptoken": os.environ.get("VS_MPTOKEN", ""),
        "useremail": os.environ.get("VS_EMAIL", ""),
        "_playername": os.environ.get("VS_PLAYERNAME", ""),
    }


def main() -> None:
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    settings_path = sys.argv[1]

    if os.environ.get("VS_SESSIONKEY") and os.environ.get("VS_SESSIONSIGNATURE"):
        session = from_env_session()
        source = "provided session token"
    else:
        session = login()
        source = "credential login"

    settings = {}
    if os.path.exists(settings_path):
        with open(settings_path) as fh:
            settings = json.load(fh)
    ss = settings.setdefault("stringSettings", {})
    for key in ("sessionkey", "sessionsignature", "entitlements", "mptoken", "useremail"):
        ss[key] = session[key]

    os.makedirs(os.path.dirname(os.path.abspath(settings_path)), exist_ok=True)
    with open(settings_path, "w") as fh:
        json.dump(settings, fh, indent=2)

    who = session.get("_playername") or "(name in session)"
    print(f"vs-login: seeded session for {who} via {source} -> {settings_path}")


if __name__ == "__main__":
    main()
