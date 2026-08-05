#!/usr/bin/env bash
set -euo pipefail

# Sandboxed Vintage Story DEDICATED SERVER for VintageHorizons multiplayer testing.
# Vanilla (no mods installed server-side) - that's the point: the mod must work
# with a client-side-only install. Same isolation rules as test-client.sh.
#
# Config: port 42425 by default (override with VH_TEST_PORT), no auth
# (offline/local), not advertised, joining players get the admin role so
# auto-explore teleports work. The game's CLI takes a single --withconfig, so
# extra config must go through the variables here rather than a second flag.
# Console output: .testdata/server/console.log (previous run kept as .prev).

source "$(dirname "${BASH_SOURCE[0]}")/test-lib.sh"

DATA="$VH_SANDBOX/server"
PIDFILE="$DATA/server.pid"
PORT="${VH_TEST_PORT:-42425}"

vh_guard_not_running "Test server" "$PIDFILE"

mkdir -p "$DATA/tmp" "$DATA/Mods"
export TMPDIR="$DATA/tmp"

# A writable console. Scenarios type server commands (echo "/vhgen start 8" >
# console.in) without needing a client. The holder process keeps the FIFO open
# between writers - without it, the first echo's close delivers EOF to the server
# console. test-stop.sh kills the holder via its pidfile.
FIFO="$DATA/console.in"
if [[ -f "$DATA/console-stdin.pid" ]]; then
    kill "$(cat "$DATA/console-stdin.pid")" 2>/dev/null || true
    rm -f "$DATA/console-stdin.pid"
fi
rm -f "$FIFO"
mkfifo "$FIFO"
# The holder must not inherit this script's stdout/stderr: a caller reading our
# output through a pipe would otherwise wait on the holder forever.
sleep infinity > "$FIFO" 2>/dev/null < /dev/null &
echo $! > "$DATA/console-stdin.pid"
export VH_STDIN="$FIFO"

# A server-side mod left over from a previous benchmark makes the server demand it
# from every joining client, which shows up as "You are missing 1 mods to join this
# server" and looks like a bug in our own mod. bench.sh manages this set explicitly;
# a plain run must at least say what is installed.
leftover="$(ls -A "$DATA/Mods" 2>/dev/null || true)"
if [[ -n "$leftover" ]]; then
    echo "NOTE: server-side mods present, clients will be required to have them:" >&2
    echo "$leftover" | sed 's/^/  /' >&2
    echo "  (rm -rf $DATA/Mods/* for a vanilla server)" >&2
fi

# Wait for the port to actually clear before the first attempt, rather than launching
# into a bind failure and hoping a fixed retry budget outlasts TIME_WAIT.
#
# That budget has now been raised twice after the fact, four attempts at 10s and then six
# at 15s, and it failed a third time: a matrix run that restarts the server twice in a row
# exhausted 90s of retries and took the whole suite down at scenario 14 of 17. Guessing how
# long the kernel will hold a socket is the wrong shape of answer. This waits on the
# condition itself, so the port either clears or the message says it never did.
wait_for_port_to_clear() {
    local waited=0 limit=240
    while [ "$waited" -lt "$limit" ]; do
        if ! ss -tanH "sport = :$PORT" 2>/dev/null | grep -q .; then
            [ "$waited" -gt 0 ] && echo "Test server: port $PORT cleared after ${waited}s" >&2
            return 0
        fi
        [ "$waited" = 0 ] && echo "Test server: waiting for port $PORT to clear" >&2
        sleep 5
        waited=$((waited + 5))
    done
    # Not fatal on its own: fall through to the retry loop, which reports what the server
    # actually said. A held port with no owner we can see is worth the launch attempt.
    echo "Test server: port $PORT still held after ${limit}s, launching anyway" >&2
    return 0
}
wait_for_port_to_clear

# Retry anyway: the wait above closes the common case, and a bind failure can still be
# transient. Anything else fails loudly with the console tail.
for attempt in 1 2 3 4 5 6; do
    ready=0
    if vh_launch "Test server" "$PIDFILE" "$DATA/console.log" \
        dotnet VintagestoryServer.dll --dataPath "$DATA" \
        --withconfig="{ Port: $PORT, VerifyPlayerAuth: false, WhitelistMode: 'off', AdvertiseServer: false, DefaultRoleCode: 'admin' }" \
        "$@"
    then
        # Wait for readiness here rather than making every caller know the marker --
        # getting that wrong once launched a client against a server that never started.
        vh_wait_for "$DATA/console.log" "Dedicated Server now running" 180 "$PIDFILE" && ready=1
    fi

    if [ "$ready" = 1 ]; then
        echo "  port $PORT, dataPath $DATA"
        exit 0
    fi

    rm -f "$PIDFILE"
    if grep -q "Address already in use" "$DATA/console.log" 2>/dev/null && [ "$attempt" -lt 6 ]; then
        echo "Test server: port $PORT still in use, retrying in 15s (attempt $attempt)" >&2
        sleep 15
        continue
    fi

    echo "Test server failed to become ready. Last lines of console.log:" >&2
    tail -n 20 "$DATA/console.log" >&2 || true
    exit 1
done
