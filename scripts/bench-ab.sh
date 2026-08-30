#!/usr/bin/env bash
set -euo pipefail

# Run scripts/bench.sh against a RESTORED world, so two runs can be compared.
#
#   scripts/bench-ab.sh snapshot                     freeze the current world as pristine
#   scripts/bench-ab.sh run <label> [bench.sh args]  restore, then bench
#   scripts/bench-ab.sh gate [suffix]                run the same config twice, report spread
#   scripts/bench-ab.sh compare <a> <b>              compare two recorded labels
#
# Why this exists. bench.sh installs mods, pins vsync, starts the server and connects.
# It does not reset anything. The server savegame and the client LOD cache both persist
# and both grow while the route is walked, so run N+1 starts with more terrain already
# cached than run N did. An A/B that ignores this measures run order.
#
# The state that matters is exactly two directories:
#   .testdata/server/Saves            the world the route walks through
#   .testdata/ModData/distantvistas the client's LOD cache for that world
#
# Both are restored wholesale, including -wal and -shm sidecars. A SQLite database
# copied without its write-ahead log is a different database.
#
# The gate is not optional ceremony. Two identical runs that disagree by more than the
# effect being chased mean this harness cannot answer the question, and the honest
# result is to say so rather than to report the difference as a finding.

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$ROOT/scripts/test-lib.sh"

PRISTINE="$VH_SANDBOX/bench/pristine"
BENCH_OUT="$VH_SANDBOX/bench"
STATE_DIRS=("server/Saves" "ModData/distantvistas")

usage() {
    sed -n '4,12p' "$0" | sed 's/^# \{0,1\}//'
    exit 2
}

# Everything must be stopped before the state directories are touched. Copying a
# savegame out from under a running server yields a torn file that looks like a
# corrupt world several runs later, long after the cause is out of sight.
stop_everything() {
    "$ROOT/scripts/test-stop.sh" >/dev/null 2>&1 || true
    vh_wait_stopped "$VH_SANDBOX/test-instance.pid" 120 || true
    vh_wait_stopped "$VH_SANDBOX/server/server.pid" 120 || true
}

do_snapshot() {
    stop_everything

    for rel in "${STATE_DIRS[@]}"; do
        if [[ ! -d "$VH_SANDBOX/$rel" ]]; then
            echo "bench-ab: $VH_SANDBOX/$rel does not exist - run a bench once first" >&2
            exit 1
        fi
    done

    rm -rf "$PRISTINE"
    mkdir -p "$PRISTINE"
    for rel in "${STATE_DIRS[@]}"; do
        mkdir -p "$PRISTINE/$(dirname "$rel")"
        cp -a "$VH_SANDBOX/$rel" "$PRISTINE/$rel"
    done

    git -C "$ROOT" rev-parse --short HEAD > "$PRISTINE/taken-at-commit" 2>/dev/null || true
    echo "Pristine world captured: $(du -sh "$PRISTINE" | cut -f1) in $PRISTINE"
}

do_restore() {
    [[ -d "$PRISTINE" ]] || { echo "bench-ab: no pristine snapshot; run 'snapshot' first" >&2; exit 1; }
    stop_everything

    for rel in "${STATE_DIRS[@]}"; do
        rm -rf "${VH_SANDBOX:?}/$rel"
        mkdir -p "$VH_SANDBOX/$(dirname "$rel")"
        cp -a "$PRISTINE/$rel" "$VH_SANDBOX/$rel"
    done

    # Read the restored world back, so every run starts with it in the page cache.
    #
    # Measured: the first run of a gate read a 171 MB world off cold disk and its first
    # lap ran at 82 fps; the second run found the same files already in RAM and managed
    # 369 fps at the same waypoint. Same build, same world, four and a half times apart,
    # entirely because one went second.
    #
    # Warming rather than dropping. Dropping the cache is undone by anything else that
    # touches the files, while warming only has to succeed once.
    for rel in "${STATE_DIRS[@]}"; do
        find "$VH_SANDBOX/$rel" -type f -exec cat {} + > /dev/null 2>&1 || true
    done
    echo "  world restored from pristine, page cache primed"
}

do_run() {
    local label="${1:-}"
    [[ -n "$label" ]] || usage
    shift

    do_restore
    echo "  measuring '$label' at commit $(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || echo unknown)"
    git -C "$ROOT" rev-parse HEAD > "$BENCH_OUT/$label.commit" 2>/dev/null || true

    # A failed run must not abort a batch: the record of which run failed is itself
    # a result, and the remaining configurations are still worth measuring.
    local failed=0
    "$ROOT/scripts/bench.sh" "$label" "$@" || failed=1

    # What was actually measured, hashed from the mods bench.sh installed rather than
    # from whatever the source tree holds now. Rebuilding between two runs of a gate
    # would otherwise compare two different builds and call the difference a finding.
    if [[ -d "$VH_SANDBOX/Mods" ]]; then
        find "$VH_SANDBOX/Mods" -type f -exec sha256sum {} + 2>/dev/null \
            | awk '{print $1}' | sort | sha256sum | cut -d' ' -f1 > "$BENCH_OUT/$label.modhash"
    fi

    if [[ "$failed" == 1 ]]; then
        echo "bench-ab: run '$label' FAILED (see $VH_SANDBOX/Logs/client-main.log)" >&2
        return 1
    fi
}

# Two runs of the same thing. The spread between them is the noise floor, and no
# claim smaller than it means anything.
do_gate() {
    local suffix="${1:-}"
    shift || true
    local a="gate${suffix}-a" b="gate${suffix}-b"

    do_run "$a" "$@" || true
    do_run "$b" "$@" || true
    do_compare "$a" "$b"
}

do_compare() {
    local a="${1:-}" b="${2:-}"
    [[ -n "$a" && -n "$b" ]] || usage

    if [[ -f "$BENCH_OUT/$a.modhash" && -f "$BENCH_OUT/$b.modhash" ]]; then
        if ! diff -q "$BENCH_OUT/$a.modhash" "$BENCH_OUT/$b.modhash" >/dev/null; then
            echo
            echo "  WARNING: '$a' and '$b' measured DIFFERENT mod builds." >&2
            echo "  For a gate that invalidates the result entirely. For an A/B it is" >&2
            echo "  only correct if the build is the thing under test." >&2
            echo
        fi
    fi

    python3 "$ROOT/scripts/bench-ab-compare.py" "$BENCH_OUT/$a.csv" "$BENCH_OUT/$b.csv"
}

case "${1:-}" in
    snapshot) shift; do_snapshot ;;
    restore)  shift; do_restore ;;
    run)      shift; do_run "$@" ;;
    gate)     shift; do_gate "$@" ;;
    compare)  shift; do_compare "$@" ;;
    *)        usage ;;
esac
