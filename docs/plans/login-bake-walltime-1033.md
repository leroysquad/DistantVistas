# Login bake wall-time 1.0.33

Target: cold-login overlay bake **under ~2–3 minutes** for a typical **1680 L0** revisit on a mid/high PC (stretch **~90s** when capture keeps 16 scouts fed).

Prior art: `docs/plans/login-bake-efficiency.md` (1.0.32 A/B tiers + SIMD). Expert perf ranking (post-1.0.32 playtest, ~23 min wall):

1. Scalar `Block.GetColor` × up to 4096 cols × stack layers per L0 — **dominant**
2. Chunk stream + capture idle per scout
3. Near spawn mesh-wait (8 scouts, spawn-solid 1024 — intentional)
4. Post-sweep mesh drain/stabilize tail after % = 100
5. SQLite persistence

## Before / after wall-time math

Assumptions: 1680 visit stops, 64×64 L0, ~70% captured columns per section, VS ~20 client ticks/s during overlay.

### 1.0.32 measured shape (~23 min playtest)

| Phase | Model | Wall |
|-------|--------|------|
| Sweep GetColor | 1680 × full `BakeSectionFromVisit` per stop; up to **4096 × 16** GetColor + **8× GetColorWithoutTint** per ground layer | ~18–20 min |
| Scout stream/capture idle | 16 scouts but paint blocks ticks when one L0 finishes synchronously | folded above |
| Drain + stabilize | Up to **1800** drain ticks + **4×** 90-frame median windows; horizon mesh wait up to **90s** | ~3–5 min |

Effective **~0.82 s/stop** (1380 s / 1680). ETA math at 0.25 s/stop fallback was misleading once parallel scouts landed — real cost is **GetColor count × scalar cost**, not stop count alone.

### 1.0.33 expected (code shipped)

| Lever | GetColor / section (typical) | Factor |
|-------|------------------------------|--------|
| `StackDeterminedByTopOnly` — snow, water, canopy, autumn plants keep top sample | ~50% cols × 1 layer + ~50% × ~4 layers ≈ **2.5 layers/col** vs 8–16 | **~3–6×** |
| `NeedsTextureMean` — skip 8× `GetColorWithoutTint` when `winter < DeepWinterCamouflageStart` | 0 texture pulls spring–autumn | **~2–3×** on ground stacks |
| Per-section GetColor cache (`8×8` climate tile + blockId + Y) | ~3× reuse on soil/grass repeats | **~2–3×** on remainder |
| **Combined GetColor path** | ~32k → ~**2–4k** effective scalar calls / L0 | **~8–15×** |
| `MaxPaintWallMsPerTick = 120` + `BakeSectionFromVisitChunked` resume | No multi-minute single tick; ~120 ms paint / overlay tick | UI + overlap with scouts |
| Batched `DrainLoginPersistence` per paint batch | 1 fsync batch / tick vs / stop | small |
| Stabilize: `StabilizeWindowsRequired` 4→**2**; release when spawn solid + far ready even if horizon `RenderDirty` | drain tail | **~30–60 s** saved |

**Sweep phase estimate:** 1680 × (**0.05–0.10 s**/stop) ≈ **84–168 s** (1.4–2.8 min).

**End-to-end:** **~2–3 min** typical; **~90–120 s** stretch on fast PC + summer/autumn (more top-only + no texture mean). Near-spawn **8× mesh-wait** and chunk cold-start remain; honest floor **~90 s** on first join.

Remaining bottleneck if still >3 min: (1) near mesh-wait pinning slots, (2) chunk stream on slow disks, (3) deep-winter columns that still need full stack + texture mean.

## Shipped (1.0.33)

| Change | File |
|--------|------|
| Per-section GetColor cache (8×8 tile + blockId + Y) | `LodBakeScratch`, `LodSeasonBake.SampleVanillaColor` |
| Stack early exit when top decides `FinishColumnPaint` | `LodSurfaceMix.StackDeterminedByTopOnly` |
| Skip `SampleTextureMean` outside deep-winter camouflage | `LodSurfaceMix.NeedsTextureMean` |
| Time-budgeted overlay paint + partial L0 resume | `LodLoginBake` → `BakeSectionFromVisitChunked` |
| Batched SQLite drain after paint batch | `LodLoginBake.PaintReadyScouts` |
| Shorter stabilize + post-overlay horizon mesh release when spawn solid + far ready | `LodLoginBake.TickStabilizing` |
| Scout-path NDJSON sequence (`H-SCOUT-SEQ`: spawn/phase/release/budget/thrash) | `LodScoutSeqDiag`, `LodLoginScoutFill`, `LodScoutHostSystem`, `LodScoutViewerEntity` |
| **PlayModeBakeBudget** after soft-release (steady background GetColor/mesh/SQLite) | `PlayModeBakeBudget`, `LodExploreBake`, `LodPipeline`, `LodTerrainRenderer` |

Invariants kept: no player teleports; exact pickup XYZ; scout despawn; spawn-solid 1024; Farseer gray tent + black tips; no false-complete; no SIMD inside GetColor; no forceRecapture on scout ticks.

## PlayModeBakeBudget (post soft-release)

After overlay releases (spawn solid + far ready), unfinished horizon GetColor / mesh / SQLite continues under **DiscoverOnly** with per-tick caps so normal movement stays hitch-free. Full canvas completion takes **longer in wall clock while playing** — intentional; status stays honest (no fake 100%).

| Knob | Baseline | Motion / hitch | Paused / idle |
|------|----------|----------------|---------------|
| GetColor wall / tick | **2.0 ms** | ×0.45 motion, ×0.35 hitch | ×2.5 |
| Columns / drain | **48** | scaled down | scaled up |
| Mesh schedules + fill / frame | **4 + 4** | scaled down | scaled up |
| Mesh uploads / frame | **3** | scaled down | scaled up |
| SQLite rows / tick | **1** | 0 on apply spike | up to 2 |
| Mip propagations / tick | **2** | min 1 | up to 5 |

**Near-first:** `ReprioritizeNear` when the player moves ≥6 blocks/s avg; `QueueExploreBakeNearPlayer` uses a 5×5 L0 ring under play budget. Frontier scout pauses when budget tier is `motion`, `hitch`, or `apply-spike`.

**Trade:** playable within ~2–3 min overlay + spawn solid; remaining ~hundreds of L0 may need **tens of minutes** of background trickle during play (faster if paused). Filter `H-PLAY-BUDGET` in `debug-40cccb.log` for live tier + pending counts.

## Rejected

| Idea | Why |
|------|-----|
| SIMD inside `GetColor` / `GetColorWithoutTint` | Hard product rule; VS API stays scalar on main thread |
| More than 16 scouts as the only lever | Stream/capture bound; does not cut GetColor count |
| Lower `StackDepth` globally | Changes camouflage mix quality |
| Skip gap audit / fake 100% | Hard product rule |
| Full-section sync bake with higher `MaxBakePerTick` only | Still freezes client minutes per tick |
| `BlurRadius > 0` removal on login | Already 0; chunked path matches |

## Verify (playtest)

1. Cold login, expired/empty canvas (~1680 stops).
2. Overlay **16/16** scouts within seconds; **% moves every few seconds** (not 86/1680 crawl).
3. ETA shows **minutes not ~23m** after first paint batch.
4. After overlay: spawn solid; Farseer gray tent + black tips; exact pickup XYZ.
5. `scripts/check.sh fast` — SIMD bit-identical, new contract strings, skip honesty.

## Scout sequence log (H-SCOUT-SEQ)

Filter `debug-40cccb.log` for `"hypothesisId":"H-SCOUT-SEQ"`. Read in time order: `scout-spawn` → `scout-phase` (WaitChunks→Capture) → `scout-release` (`painted` = normal capture handoff to paint queue). `scout-budget` ~1/s shows slot pressure (`nearLive`/`farLive`, `held*`, `spawnsLastSec`/`releasesLastSec`, paint caps). `scout-thrash` = slot lived &lt;5 ticks or same key respawned within 2s — thrashing, not steady throughput.
