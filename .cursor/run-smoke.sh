#!/usr/bin/env bash
set -euo pipefail

# Run the `smoke` (or `matrix`) check tier headlessly on a Cloud Agent.
#
# These tiers launch the real graphical client, which (a) needs an OpenGL context
# and (b) refuses to join a server until it has a valid cached account session.
# This wrapper supplies both: it seeds the session from Cloud Agent Secrets (see
# .cursor/vs-login.py) and runs under a display with software GL.
#
# Usage: .cursor/run-smoke.sh [smoke|matrix] [extra args passed through]
#   e.g. .cursor/run-smoke.sh smoke --settle 45

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TIER="${1:-smoke}"; shift || true

export DOTNET_ROOT="${DOTNET_ROOT:-/usr/share/dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export VINTAGE_STORY="${VINTAGE_STORY:-$HOME/Games/vintagestory$(
    grep -oP '"game":\s*"\K[^"]+' "$REPO_ROOT/DistantVistas/modinfo.json")}"
# llvmpipe: the VM has no GPU, so force Mesa's software renderer.
export LIBGL_ALWAYS_SOFTWARE=1
# Keep game ticks running without a focused window (README dev note).
export VINTAGEHORIZONS_AUTOUNPAUSE=1

# Seed the client session before the client launches, so it clears the login
# screen and auto-connects. .testdata is the client dataPath (see test-lib.sh).
SETTINGS="$REPO_ROOT/.testdata/clientsettings.json"
mkdir -p "$REPO_ROOT/.testdata"
echo "run-smoke: seeding client session"
python3 "$REPO_ROOT/.cursor/vs-login.py" "$SETTINGS"

# Prefer an existing display (the VM's VNC display), else spin up an Xvfb.
if [[ -n "${DISPLAY:-}" ]] && command -v xdpyinfo >/dev/null 2>&1 \
        && xdpyinfo >/dev/null 2>&1; then
    echo "run-smoke: using existing DISPLAY=$DISPLAY"
    exec "$REPO_ROOT/scripts/check.sh" "$TIER" "$@"
else
    echo "run-smoke: no usable DISPLAY, starting Xvfb"
    exec xvfb-run -a -s "-screen 0 1280x720x24" \
        "$REPO_ROOT/scripts/check.sh" "$TIER" "$@"
fi
