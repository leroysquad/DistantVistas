---
tags: [vintage-story, distant-vistas, playtest]
aliases: [2026-09-07 playtest, Catchup playtest]
---

# Playtest Log — 2026-09-07

Session notes from catch-up playtest work on `cursor/1.0.26-catchup-playtest-e27c` (versions **1.0.28–1.0.32** arc).

Related: [[Distant Vistas]] · [[Login Bake Efficiency]] · [[Neglected Issues Audit]] · [[Farseer Companion]]

## Environment

- **Branch:** `cursor/1.0.26-catchup-playtest-e27c`
- **Versions exercised:** 1.0.28 → 1.0.32
- **Verify command:** `scripts/check.sh fast` (requires local VS assemblies)

## 1.0.28 — Readable skyline / holes context

**Shipped:**
- Ridge ink on mountain tips (readable against sky).
- Lighter, thinner distance mist.
- Gray tent + black tips aesthetic baseline (from 1.0.23/1.0.25).

**Playtest context for later versions:**
- Bootstrap planning showed **offset rim with holes underfoot** when visit budget was not spawn-first — fixed in 1.0.30 (`LodLoginSweepBootstrap.cs` comment: "1.0.28 playtest showed an offset rim with holes underfoot").

**Verify:** silhouette readable; mist lighter; LOD meshes > 0.

## 1.0.29 — Coverage + skip with gaps

**Shipped:**
- **4075-block** FlagBaked disk (4.5×750+700) — coverage only; Farseer shaders unchanged from 1.0.28.
- Scouts skip GetColor when map chunks never arrived (no missing-tex white strip).
- Post-login frontier drip for holes after ~420s overlay budget.

**Playtest finding:**
- **In-window skip** with **122 unfilled gaps** while UI implied completeness → 🔴 P0 in [[Neglected Issues Audit]] and [[User-Mentioned Unfixed]].
- Leftovers are intentional frontier drip, not overlay failure.

**Verify:** FlagBaked land meets gray/black Farseer silhouette; coverage larger than thin ~720-mesh playtest; meshes >> 180 drawn; player not hopped.

## 1.0.30 — Crawl 86/1680 + GC baseline

**Shipped:**
- `LodScoutViewer` player-style stream centers (6 concurrent → later 16).
- No player teleport; exact pickup XYZ every frame.
- Snapshot waits for spawn drawable + 75% far mesh before release.
- Mip drain keeps GPU meshes until swap-in (no dispose-first holes).

**Playtest metrics (efficiency plan baseline):**

| Metric | Value |
|--------|-------|
| Overlay progress | **~86 / 1680** stops (~8%) in sample window |
| Scout concurrency UI | Often **1/16** early, slow ramp |
| Managed GC | **~7 GB** growth in ~30 s |
| GetColor cost | Up to 4096 columns × ~16 stack per L0 |

These numbers drove [[Login Bake Efficiency]] tiers A/B for 1.0.31–1.0.32.

**Residual:** 90 s `SpawnReadyTimeoutSec` can release with holes.

## 1.0.31 — Scout parallelism

- 16/16 scouts (8 near mesh-wait / 8 far FlagBaked-release).
- GetColor across captured scouts per tick (not serial `BakeBatchAtStop`).
- Overlay % updates every detail change + ≥3 s.

**Target verify:** 16/16 within seconds; % moves every few seconds.

## 1.0.32 — GetColor GC cuts

- `LodBakeScratch`, ArrayPool, per-section texture-mean cache.
- `MaxBakePerTick = 24`; partitioning every 8 ticks; spawn reveal every 4 ticks.
- Farseer visit-mask 500 ms debounce.

See [[Login Bake Efficiency]] status table.

## Beehive NRE noise

**Observation:** `NullReferenceException` noise referencing **Beehive** (or similar worldgen) in client log during overlay / play.

**Assessment:** **Not a Distant Vistas defect** — no `Beehive` references in repo. Consistent with vanilla/worldgen Vegetation-pass fragility documented in `GenerateChecks.cs` (*"Vegetation NREs in vanilla worldgen without a Harmony guard we do not ship"*). Treat as **log noise** unless it correlates with bake abort or missing terrain.

**Action:** Filter from DV triage; do not block Mods gate on Beehive stack traces alone.

## Open after playtest

| Issue | Tracker |
|-------|---------|
| Skip UX with deferred holes | [[User-Mentioned Unfixed#Skip "complete" with gaps]] |
| DiscoverOnly far drip | [[Neglected Issues Audit#🔴 P0 — DiscoverOnly + low explorePending]] |
| Bake throughput | [[Login Bake Efficiency]] |
| Sky gap narrow band | [[Farseer Companion#Sky gap]] |

## Agent handoff

Next: [[Pipeline - Cursor Agents]] — speed bot validates 1.0.32 overlay metrics; SIMD expert owns post-GetColor path only.
