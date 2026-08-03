#!/usr/bin/env python3
"""Compare two benchmark CSVs waypoint by waypoint.

    scripts/bench-ab-compare.py <before.csv> <after.csv>

scripts/bench-compare.py already merges every recorded label into one wide table, which
is the right shape for "how does this mod compare with Farseer". This is the other
question: one change, measured twice, is the difference real?

Frame time is the column to read, not fps. Frame time is what the change adds or removes
and it averages honestly; fps is its reciprocal, so an fps average is dominated by the
cheapest frames and understates exactly the stutter a distance mod is judged on. Both are
printed, and the verdict uses frame time.

The 1% low column is the one that matters most here. A change that lifts the average and
leaves the 1% low alone has moved work around rather than removed it.
"""
import csv
import sys

if len(sys.argv) != 3:
    sys.exit(__doc__)


def load(path):
    try:
        with open(path, newline="") as f:
            return {r["waypoint"]: r for r in csv.DictReader(f)}
    except FileNotFoundError:
        sys.exit(f"no such CSV: {path}\n(the run probably failed; check the client log)")


before, after = load(sys.argv[1]), load(sys.argv[2])
shared = [w for w in before if w in after]

if not shared:
    sys.exit("the two runs share no waypoint; they are not comparable")

missing = sorted(set(before) ^ set(after))
if missing:
    print(f"  NOTE: waypoints in only one run, skipped: {', '.join(missing)}\n")

COLUMNS = [("frame_ms_avg", "frame ms", "spread_pct"),
           ("frame_ms_1pct_low", "1% low ms", "spread_1pct_pct"),
           ("managed_mb", "managed MB", None)]


def noise_floor(wp, spread_key):
    """How far the laps of each run disagreed with themselves, whichever was worse.

    A change smaller than this is not distinguishable from the harness measuring the
    same thing twice. Runs recorded before the harness tracked laps have no spread
    column; those fall back to a flat 10%, which is generous and says so."""
    if spread_key is None:
        return None
    try:
        return max(float(before[wp][spread_key]), float(after[wp][spread_key]))
    except (KeyError, ValueError, TypeError):
        return None


real, noisy = 0, 0
for key, title, spread_key in COLUMNS:
    print(f"  {title}")
    print(f"  {'waypoint':<20}{'before':>10}{'after':>10}{'change':>10}{'noise':>9}   verdict")
    print("  " + "-" * 72)
    for wp in shared:
        x, y = float(before[wp][key]), float(after[wp][key])
        pct = (y - x) / x * 100 if x else 0.0
        floor = noise_floor(wp, spread_key)
        shown = f"{floor:>8.1f}%" if floor is not None else "       ?"

        if floor is None:
            verdict = "no lap data"
        elif abs(pct) <= floor:
            verdict = "inside noise"
            noisy += 1
        else:
            verdict = "BETTER" if pct < 0 else "WORSE"
            real += 1

        print(f"  {wp:<20}{x:>10.2f}{y:>10.2f}{pct:>9.1f}%{shown}   {verdict}")
    print()

print(f"  {real} measurements outside the noise floor, {noisy} inside it.")
print()
print("  'noise' is how far that waypoint's own laps disagreed, in whichever run")
print("  disagreed more. A change smaller than it is not a result, whatever sign it has.")
print()
print("  A gate run (the same build twice) sets the noise floor. Anything smaller than")
print("  that floor is not a result, whatever sign it carries.")
