#!/usr/bin/env python3
"""Check the benchmark's settle criterion against traces whose answers are known.

    bench/settle-criterion-check.py

BenchModSystem decides when a waypoint has stopped loading and may be measured. Getting
that wrong is expensive and invisible: settle too early and the numbers include the load
burst, which is what made two runs of the same build disagree by 29.7%.

The criterion is arithmetic, so it can be checked here without a game, a client or a
session. This mirrors HalvesAgree() and its constants. Keep the two in step; if you change
StabilityWindows or StabilityTolerance in the C#, change them here and re-run.

Every trace below has an answer a human can state in advance. Two real defects were
caught this way rather than in a benchmark run:

  - a short baseline accepted a slow drift as settled, because two adjacent windows
    differ by very little during one;
  - a one-sided "has it stopped getting faster" test fired while the frame time was still
    CLIMBING, which is what happens while terrain loads in.
"""
import random
import statistics
import sys

# Mirrors BenchModSystem.
WINDOW_SEC = 3.0
STABILITY_WINDOWS = 6
STABILITY_TOLERANCE = 0.05
MIN_SETTLE = 12.0
MAX_SETTLE = 75.0


def settle(frame_ms_at):
    """Returns (seconds_until_settled, reason), mirroring Settled()/HalvesAgree()."""
    medians, window, t, window_started = [], [], 0.0, 0.0

    while t < MAX_SETTLE:
        ms = frame_ms_at(t)
        window.append(ms)
        t += ms / 1000.0

        if t - window_started >= WINDOW_SEC and len(window) > 8:
            medians.append(statistics.median(window))
            if len(medians) > STABILITY_WINDOWS:
                medians.pop(0)
            window, window_started = [], t

            if t >= MIN_SETTLE and len(medians) == STABILITY_WINDOWS:
                half = STABILITY_WINDOWS // 2
                older = statistics.median(medians[:half])
                recent = statistics.median(medians[half:])
                if older > 0 and abs(older - recent) / older < STABILITY_TOLERANCE:
                    return t, "settled"

    return MAX_SETTLE, "timed out"


def cases():
    rnd = random.Random(7)
    return [
        # name, trace, must settle inside this window (None = must time out)
        ("flat from the start",
         lambda t: 10 + rnd.gauss(0, 0.2), (MIN_SETTLE, 25)),
        ("noisy but flat (the ridge-east shape)",
         lambda t: 15 + rnd.gauss(0, 4) + (25 if rnd.random() < 0.03 else 0), (MIN_SETTLE, 30)),
        ("warm-up, 5x faster over 40s then flat",
         lambda t: (50 - 40 * min(t, 40) / 40) + rnd.gauss(0, 0.3), (40, 70)),
        ("warm-up, 2x faster over 30s then flat",
         lambda t: (20 - 10 * min(t, 30) / 30) + rnd.gauss(0, 0.3), (30, 60)),
        ("sustained 2%/s drift, never steady",
         lambda t: 30 * (0.98 ** t), None),
        # This one exists to pin the BASELINE LENGTH rather than the tolerance. It drifts
        # about 2.4% per window, which any short comparison waves through, and about 7%
        # across the six-window span, which is caught. Without it a two-window baseline
        # passes every other case here.
        ("slow 0.8%/s drift, caught only by a long baseline",
         lambda t: 30 * (0.992 ** t), None),
        ("worse then better, terrain loading in",
         lambda t: 10 + 15 * max(0.0, 1 - abs(t - 15) / 15) + rnd.gauss(0, 0.3), (16, 40)),
    ]


failures = 0
print()
print(f"  settle criterion: {STABILITY_WINDOWS} windows of {WINDOW_SEC:.0f}s, "
      f"halves within {STABILITY_TOLERANCE:.0%}, floor {MIN_SETTLE:.0f}s, ceiling {MAX_SETTLE:.0f}s")
print()

for name, trace, expected in cases():
    at, why = settle(trace)

    if expected is None:
        ok = why == "timed out"
        want = "must never settle"
    else:
        lo, hi = expected
        ok = why == "settled" and lo <= at <= hi
        want = f"settle between {lo:.0f}s and {hi:.0f}s"

    if not ok:
        failures += 1
    print(f"  {'ok ' if ok else 'X  '}{name:<40}{at:>6.1f}s {why:<10}  {want}")

print()
print(f"  {failures} failures")
print()
sys.exit(1 if failures else 0)
