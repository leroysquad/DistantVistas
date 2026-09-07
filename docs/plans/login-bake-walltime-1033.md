# Login bake wall-time 1.0.33 / 1.0.34 / 1.0.35 / 1.0.36

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
| Overlay scout FIFO + fast capture→paint (1.0.34 stall fix) | `LodLoginScoutFill`, `LodLoginBakeInputLock` |

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

## 1.0.34 overlay stall fix (2026-09-07 playtest)

**Symptom:** `l0Count` plateau ~358; `scout-budget` shows `paintReadyQueued=0` while 16 scouts live; `heldNear` hundreds (near/far slot split starved FIFO); `avgNearTicks≈80` / `avgFarTicks≈400`; load-screen star jitter (`H-LOOK` delta storms).

**Root cause:** Pending keys inside spawn-solid 1024 were diverted to `heldNear` when filling “far” slots, while near slots dwelled 80 capture ticks and far slots hit 400-tick `maxWait` **without** paint handoff — empty `scoutReady` pipeline between bursts.

**Shipped:**

| Fix | Change |
|-----|--------|
| A | All 16 scouts share **FIFO pending** — flush legacy `heldNear`/`heldFar` each tick; no slot band starvation |
| A | Capture→paint in **≤16 ticks** typical (`MaxCaptureWaitTicks` 80→**16**); partial capture handoff at 256+ cols |
| B | `maxWait` 400→**120**; on timeout re-queue pending + `partialPaint` when capture exists (not silent drop) |
| B | `WaitForMesh` = telemetry only; spawn sweep via `RunSpawnDiskSweep`; mesh gate stays overlay **end** |
| C | `HoldLook` every overlay tick + server controls blocked; silent delta drain while `OverlayLookLocked` |

**Expect after fix:** `paintReadyQueued` &gt; 0 most seconds when scouts live; `heldNear`≈0; `avgNearTicks` / `avgFarTicks` in teens not 80/400; L0 count climbs past 358 without interval stalls.

## 1.0.35 overlay speed cut (2026-09-07 playtest follow-up)

**Symptom (1.0.34 stall fix OK):** `heldNear=0`, `paintReadyQueued` 58–76, finished climbing — but overlay still slow + hitchy. First 30s Stats: **2383 gen0**, **~9251 MB managed**; `GetColorCalls` avg ~540 / max ~1186; `maxPaintWallMs=200`; **16638 inline loads**; render schedule max ~7.6ms, quadtree walk max ~22ms.

**Hypotheses verified:**

| # | Hypothesis | Verdict | Fix |
|---|------------|---------|-----|
| 1 | Allocation churn in paint/GetColor | **Confirmed** — partial `InvalidatePaletteSnapshot` every budget slice rebuilt palette int[] for mesh; resume snapshot allocated every paint batch | Defer snapshot invalidation until section complete; throttle `SaveResumeSnapshot` (8 stops / 2s) |
| 2 | Too much work per paint tick | **Confirmed** — 200ms wall + 24 stops/tick spiked main thread | Wall **120ms**; **32** stops/tick; near-first paint queue |
| 3 | Redundant GetColor / cache gaps | **Confirmed** — 8×8 tile + per-column season rel still missed repeats | **16×16** tile cache; BlockId-only cache for climate-untinted; **16×16 season tile** cache |
| 4 | Scout WaitChunks waste | **Partial** — some `maxWait` remain | `MaxWaitTicks` 120→**96** (partial paint handoff unchanged) |
| 5 | Inline loads vs bake | **Confirmed** — 8 installs/tick during overlay | **2** installs/tick, **1ms** budget when `DeferLegacyHeal` |
| 6 | Visit order oversized for “near done” | **Confirmed** — uniform revisit subsample | Revisit + expire use `BudgetBootstrapVisitStops` (spawn **75%** of budget before rim) |

**Shipped:**

| Change | File |
|--------|------|
| 16×16 GetColor cache tile (was 8×8) | `LodBakeScratch.GetColorCacheKey` |
| BlockId-only GetColor cache for climate-untinted | `LodBakeScratch`, `LodSeasonBake.SampleVanillaColor` |
| 16×16 season-rel tile cache | `LodBakeScratch`, `LodSurfaceMix.ReadSeasonRel` |
| No partial `InvalidatePaletteSnapshot` during chunked paint | `LodSeasonBake.BakeSectionFromVisitChunkedBody` |
| Paint wall 200→**120** ms; `MaxBakePerTick` 24→**32** | `LodLoginBake` |
| Near-first + partial-resume paint queue | `LodLoginBake.PrioritizePaintQueue` |
| Throttled resume snapshot (8 finished / 2s) | `LodLoginBake.MaybeSaveResumeSnapshot` |
| Overlay inline load cap 8→**2** @ **1ms** | `LodPipeline.InstallLoadedSections` |
| Revisit/expire spawn-first budgeting (75% inner) | `LodLoginSweepBootstrap` |
| Scout chunk wait 120→**96** ticks | `LodLoginScoutFill` |

Invariants unchanged: no teleports; scout viewers; exact pickup; spawn-solid 1024; Farseer gray tent; no SIMD inside GetColor; look lock; no false-complete.

**Expect after 1.0.35:**

| Signal | Before (1.0.34) | Target |
|--------|-----------------|--------|
| First-30s gen0 | ~2383 | **&lt;800** (fewer palette snapshot + resume allocs) |
| Managed heap @ 30s | ~9251 MB | **&lt;4000 MB** (lower churn + throttled installs) |
| `GetColorCalls` / paint batch (H-PAINT) | avg ~540 | **&lt;250** (wider cache + season tile) |
| `maxPaintWallMs` (H-PAINT) | 200 | **120** |
| Inline loads / 30s | ~16638 | **&lt;5000** |
| Spawn neighbourhood FlagBaked | lags rim | **Done first** (spawn-first queue + revisit budget) |
| `paintReadyQueued` | healthy | stays **&gt;0** when scouts live |
| Look lock | intact | no `H-LOOK` delta storms |

## 1.0.35 WaitChunks freeze fix (~13:50 PT playtest)

**Symptom:** After paint was flowing (`finished` climbing, `paintReadyQueued` ~60), overlay **froze**: `paintReadyQueued=0`, `scoutReady=0`, **`nearLive=16`** stuck, **`avgNearTicks` ~77**, only **`maxWait`** releases (112), **zero `painted`**, **3420 WaitChunks** phase logs, **zero H-PAINT** batches.

**Root cause:** All 16 spawn-disk scouts parked in **WaitChunks** waiting for **all four** map chunks. Chunk IO saturated → no Capture → no paint handoff. **`maxWait` requeued without paint** when `CapturedColumns &lt; 256`, so slots immediately respawned on the same near keys — a **WaitChunks dead-end**, not a FIFO stall.

**Shipped (scout unblock):**

| Fix | Change |
|-----|--------|
| Partial map escalation | `AnyMapChunksLoaded` + **32-tick** force **WaitChunks→Capture** |
| Tiered partial paint | `PartialCaptureMin`: 256 → 64 → 16 → **1** by wait age |
| Full-grid rotation | All **16** in WaitChunks **≥48 ticks** → **`waitExpirePaint` / `partialPaint`** handoff (min 1 col) |
| Near WaitChunks cap | **`MaxNearWaitChunksLive=8`** — new slots prefer far ring while spawn streams catch up |
| Key deferral | **`waitRetries`** + **`SelectPendingIndex`** skips hot stuck keys when alternatives exist |
| Capture timeout | **`captureTimeout` / `waitExpirePaint`** when capture idle but columns exist |
| Store resident | **`TryGetSection`** loads disk section before partial / expire handoff |

**Expect after fix:** H-PAINT batches resume within seconds of a WaitChunks pile-up; `paintReadyQueued&gt;0`; `painted` releases return; `avgNearTicks` stays in **teens–30s**, not **77+** with zero paint; `maxWait`-only slices alternate with **`partialPaint` / `waitExpirePaint` / `painted`**.

## 1.0.36 WaitChunks wait cuts (~13:57 PT playtest)

**Symptom (1.0.35 partial unblock):** WaitChunks still **2710 vs Capture 1279**; WaitChunks `ticksInPhase` **avg 55 / max 119**; `paintReadyQueued` **zero in ~96%** of scout-budget samples; `avgNearTicks` **~62**; **maxWait 104 vs painted 114** (~1:1); early window `farLive=16` `avgFarTicks=120`.

**Root cause:** 1.0.35 escalation (**32/48 tick** thresholds, **256→1** partial ladder tied to **72–96 tick** waits) still parked scouts **~1–2 s** in WaitChunks before Capture/paint. **16 far WaitChunks** competed for chunk IO while paint queue starved. **maxWait requeue** still consumed ~half of releases without productive paint.

**Shipped:**

| Fix | Change |
|-----|--------|
| Hard Capture deadline | **8 ticks (~400 ms)** — every scout enters Capture (no full-map gate) |
| Shorter safety caps | `MaxWaitTicks` 96→**24**; `MaxCaptureWaitTicks` 16→**6** |
| Early paint handoff | **4+ ticks** `PartialCaptureMin` (64→16→4→1); dedicated **12-tick** handoff pass |
| Faster rotation | **6+** scouts WaitChunks **≥16 ticks** → paint (was all **16 @ 48**) |
| Concurrency caps | Near WaitChunks **4** (was 8); **new far cap 6** — pending skips saturated band |
| Faster stream | `RevealGrowPerTick` **8**; `RequestUpRetryTicks` **8** |
| Capture stall | **`captureStall`** release + requeue after **6** capture ticks (no slot parking) |
| Key churn | `MaxWaitKeyedRetries` **1** |

**Expect after 1.0.36:**

| Signal | 1.0.35 playtest | Target |
|--------|-----------------|--------|
| WaitChunks `ticksInPhase` avg | ~55 | **teens (~12–18)** |
| WaitChunks max | ~119 | **&lt;32** typical |
| `paintReadyQueued` &gt; 0 | ~4% of samples | **&gt;50%** while scouts live |
| `maxWait` vs `painted` | ~1:1 | **`painted` ≫ `maxWait`** |
| `avgNearTicks` / `avgFarTicks` | ~62 / ~120 | **teens–25** |
| Capture vs WaitChunks phase count | 1279 vs 2710 | **Capture ≥ WaitChunks** |
| Freeze dead-end | rare | **none** (Capture @ 8 + handoff @ 4) |

## 1.0.36 ~358 band fix (~14:01 PT playtest)

**Symptom:** `finished` climbs **229→358** then stalls; `paintReadyQueued=0` in **119/124** budget samples; `nearLive=8` `farLive=8`; **WaitChunks 3878 vs Capture 755**; `maxWait 181` vs `painted 129`; `avgNearTicks≈48` `avgFarTicks≈96`.

**Root cause:** After inner spawn keys finish, remaining pending is outer **Farseer rim revisits**. Scouts park in **WaitChunks** streaming visit-cell chunks while **HasDataSet** sections on disk already hold captured columns. WaitChunks caps + maxWait requeue starve `scoutReady` / `paintReadyQueued` — same ~306–358 band as pre-1.0.36.

**Shipped (358 unblock):**

| Fix | Change |
|-----|--------|
| Resident paint handoff | **`TryResidentPaintHandoff`**: HasDataSet + **≥64 cols** → **`residentPaint`** without full map wait |
| Starve mode | **`paintStarveTicks`** watchdog (≥8 ticks empty paint + live scouts) → **`SetPaintStarving`** |
| Tighter starve waits | Force Capture **4 ticks**; rotate **8 ticks / min 4**; Capture handoff **tick 1** |
| Resident-first pending | **`SelectPendingIndex`** scores **`ResidentCaptureCols`**; bypass WaitChunks caps when starving |
| Key retry bypass | Keys with **≥64 resident cols** skip **`MaxWaitKeyedRetries`** deferral |
| Telemetry | **`paintStarveTicks`** in H-SCOUT-SEQ scout-budget |

**Expect after 358 fix:**

| Signal | 1.0.35 @358 stall | Target |
|--------|-------------------|--------|
| `finished` past 358 | stalls | **climbs through outer rim** |
| `paintReadyQueued` | ~0% | **>0 within 8 ticks of starve** |
| WaitChunks vs Capture | 3878 vs 755 | **`residentPaint` releases**; Capture catches up |
| `maxWait` vs `painted` | 181 vs 129 | **`painted` ≫ `maxWait`** |
| Release reasons | maxWait-heavy | **`residentPaint` / `residentStarve` / `waitExpirePaint`** |

## 1.0.37 cheaper-compute A-tier

**Research:** `docs/plans/login-bake-cheaper-compute-research.md` (branch `cursor/login-bake-cheaper-compute-research-beee`).

**Non-negotiables preserved:** full seasonal GetColor, full **4075** disk / **~1680** stops, scout invariants.

**Shipped (A-tier, in order):**

| ID | Fix | Change |
|----|-----|--------|
| **A2** | Cliff breaker | **`PendingPickScore`**: prefer keys with **4/4 map chunks loaded**; **`spawnCooldown`** **4 ticks** after `maxWait` / `captureStall`; **`chunkPressure`** when starving + **≥12** WaitChunks (Capture **3 ticks**, rotate **6/min 3**, bypass caps) |
| **A1** | GetColor dedup | **`BeginOverlayGetColorCache`**: cross-L0 tile+blockId+Y reuse (bit-identical to section cache); H-PAINT **`getColorHits` / `getColorMisses`** |
| **A3** | GC hygiene | **`resumePendingScratch` / `resumeCompletedScratch`** reuse in `SaveResumeSnapshot`; overlay cache scoped lifecycle |

**Optional next:** soft-release overlay (Plan B) — threshold from **N(R_hold)×f_w**, not ~600; fade cosmetic only. **Not in 1.0.38.**

**Expect after 1.0.37:**

| Signal | 1.0.36 @358 | Target |
|--------|-------------|--------|
| `finished` past 358 | may stall | **>500 climbing** |
| `paintReadyQueued==0` | ~96% at cliff | **&lt;10%** |
| WaitChunks : Capture | ~5:1 | **&lt;2:1** |
| `maxWait` vs `painted` | ~1:1 | **`painted` ≫ `maxWait`** |
| H-PAINT `getColorCalls`/batch | ~540 | **&lt;300** (dedup) |
| `chunkPressure` in budget | n/a | **true** during rim IO saturation |

## 1.0.38 warm-ring cliff (~358 stall — 1.0.37 failed)

**1.0.37 playtest (runId 1037):** `finished=358` dead-end; `paintReadyQueued=0` (25/25); `paintStarveTicks=431`; **`chunkPressure:false`**; **`captureStall` 733** vs **`painted` 6** / `residentPaint` 16; `nearLive=16` `farLive=0`; avg ticks ~5 (thrash not wait). Meshes 87/8000 — **not a mesh cap**.

### Root cause (geometry + scheduling, not magic numbers)

Two radii overlap:

| Ring | Radius | Role |
|------|--------|------|
| **Stream hold** | `SweepBoostViewDistanceBlocks` ≈ **750** blocks | Vanilla keeps map chunks warm — Capture can run |
| **Spawn-solid / near scout** | **1024** blocks | Scouts use `WaitForMesh=true`, mesh gate at overlay end |

Filled-disk L0 count at radius R (64-block cells): **N(R) ≈ π (R/64)²**

- At **750** blocks: N ≈ **431** L0 cells (warm capacity)
- At **358 finished**: inverse radius ≈ **683** blocks — progress exits warm halo before filling it
- **Cold-near annulus** (750–1024 blocks): still “near” scouts, **zero** warm map chunks

**Failure chain (1.0.37):**

1. Pending crosses into cold-near / cold-rim keys after ~358 warm completions
2. Force Capture @ 4 ticks **without loaded map** → zero `CapturedColumns`
3. `TryTimeoutHandoff` fails → **`captureStall`** → 4-tick cooldown → immediate respawn (**733** loops, ~5 ticks each)
4. All **16** near slots thrashing Capture — **`paintReadyQueued=0`**
5. `chunkPressure` required WaitChunks≥12 — scouts were in **Capture**, so governor **never fired**

**Not the cause:** mesh budget (87/8000), visit stop count cap, GetColor quality tradeoffs.

### Shipped (1.0.38)

| Fix | Mechanism |
|-----|-----------|
| **Paint-readiness Capture gate** | While paint starves: **no Capture** unless `loadedChunks≥1` or resident section exists — stops zero-column captureStall |
| **Resident handoff in Capture** | `TryResidentPaintHandoff` every Capture tick before stall |
| **Escalating captureStall cooldown** | `4 + retries²×2` ticks (cap 32) — breaks hot-loop |
| **Cold-near fleet cap** | Max **half** scouts on cold-near annulus keys while starving |
| **Pending score** | `loaded×10k + resident×10 − 50k` cold-near penalty |
| **chunkPressure** | `paintStarve ∧ liveScouts>0` — any phase |
| **A4 residency visit order** | `OrderVisitKeysByResidency` in bootstrap + pending resort every 32 starve ticks |
| **Telemetry** | `warm-ring-probe` at finished 320–400: pending residency, annulus counts, `finishedRadiusBlocks` vs `warmHoldBlocks` |
| **Stall forensics** | `stall-forensics` + `stalled-live-probe`: per-slot distance, loaded 0–4, reveal, host outcome, key thrash |
| **One cold frontier** | `MaxColdNearWaitChunksWhenStarving=1` — single annulus streamer while paint starves |

**Expect after 1.0.38:**

| Signal | 1.0.37 @358 | Target |
|--------|-------------|--------|
| `finished` past 358 | dead | **climbing** |
| `paintReadyQueued` | 0 (25/25) | **>0 most seconds** |
| `chunkPressure` | false @ starve | **true** |
| `captureStall` vs paint | 733 vs 6 | **captureStall ≪ painted/resident** |
| `warm-ring-probe` | n/a | pending **loaded1Plus** rises; **coldNear** throttled |

**Not in 1.0.38:** overlay fade / soft-release-at-N — cliff fix only. See Plan B below.

### User hypothesis — “can’t enter the next huge square” (verify hard)

**User intuition:** scout paints a big square, then seems unable to advance to the next one — stuck at the rim of progress.

**DV mapping (not discarded — confirmed by 1.0.37):**

| User sees | DV reality |
|-----------|------------|
| “Next huge square” | **L0 section** = 64×64 columns (4 underlying **map chunks**), not one mega-chunk |
| “Can’t get in / load it” | Pending keys in **cold-near annulus** (750–1024 blocks): still near scouts, **0 warm map chunks** |
| Stuck at ~358 | Warm halo exhausted at **~683 block** radius; next stops need **cold IO** the fleet never successfully resident |
| `paintReadyQueued=0` | Slots **`captureStall`**-loop on zero-column Capture (733× in 1.0.37), not entity blocked from entering a cell |

**Hypothesis verdict:** **PROVEN** (geometry + scheduling), not a scout-entity pathing bug. The scout **can** spawn at the visit cell; Capture fails because **map chunks are not loaded** for that L0 footprint while paint starves and the fleet thrashes forced Capture.

**Telemetry added (runId 1038):**

| Event | Fields |
|-------|--------|
| `stall-forensics` | On `captureStall` / `maxWait`: `distBlocks`, `loadedMapChunks` 0–4, `revealRadius`, `holdRadius`, `hostOutcome` (sent/capped/pending/held/none), `stallCount`, `coldNear` |
| `stalled-live-probe` | Every 5s @ finished 320–400 + paintStarve≥8: per-slot phase, distance, loaded, reveal, host, `coldNearLive`, `zeroLoadedLive` |
| `scout-thrash` / `key-thrash` | Same key respawn &lt;2s; same key ≥3 stall releases |
| `warm-ring-probe` | Pending residency vs `finishedRadiusBlocks` vs `warmHoldBlocks` |
| `scout-host-up` | `capped` / `pending` when server hold cap blocks KeepLoaded |

**Fixes targeting this hypothesis (1.0.38):**

1. Never Capture cold keys without `loadedMapChunks≥1` or resident section (stops zero-column stall)
2. Resident handoff in Capture before stall
3. Escalating `captureStall` cooldown + key thrash defer
4. Cold-near fleet cap (half fleet) + **one cold-near WaitChunks streamer** (`MaxColdNearWaitChunksWhenStarving=1`)
5. Residency-ordered pending (A4) + `chunkPressure` on any live scout while paint starves

**Playtest pass criteria:** `finished` past 358; `stalled-live-probe` shows `zeroLoadedLive` falling as `loadedMapChunks` rises on annulus keys; `captureStall` ≪ `painted`; same-key `key-thrash` rare.

### Scouts primary; hop-unlock only if proven (1.0.38)

**User intent (locked for 1.0.38):**

- **Scouts remain the primary bake workers.** Sixteen parallel `LodScoutViewerEntity` stream anchors cover far more ground than one player could hop-sweep; the overlay already solved visible teleportation and look-glitch problems with `PinPickupPose` + look lock while scouts work visit cells.
- **Do not regress** to a single-player hop-teleport sweep as the main login algorithm.
- **Prefer** fixing scout/host chunk streaming (`RequestUp` → KeepLoaded → ForceSend, scheduling gates, IO budgets) so hops are not needed.
- **If and only if** playtest proves cold map-chunk streaming cannot unlock without real `IPlayer` presence at a region, an overlay **hop-unlock** is allowed — but only as a **stream / residency pump**, not as the baker:
  1. Move player invisibly behind overlay to a geometry-derived unlock point (look locked).
  2. Let the **full scout fleet** continue parallel WaitChunks → Capture → paint around that region while vanilla + host IO warm the annulus.
  3. When that unlock disk is fed (or progress stalls on IO despite held scouts), **hop the unlock point** outward again.
  4. **Restore exact pickup XYZ + look** at overlay end — unchanged from today.

**Product hierarchy:**

```text
Primary:  scout fleet (parallel capture + GetColor + mesh)
Fix now:  scout/host scheduling + IO (1.0.38)
Fallback: hop-unlock pump ONLY if scout KeepLoaded provably fails after fixes
Reject:   per-L0 player hops; hop-sweep replacing scouts; visible motion
```

#### Proof: Capture does **not** require player at the L0 cell

| Question | Evidence | Answer |
|----------|----------|--------|
| What does Capture read? | `LodLoginSweep.AllMapChunksLoaded` / `CountLoadedMapChunks` call `blockAccessor.GetMapChunk(cx, cz)` at the **L0 footprint** columns — not player position | **BlockAccessor residency**, not player standing on cell |
| Where do those chunks come from? | `LodScoutHostSystem.HoldAnchor`: server `LoadChunkColumnPriority(KeepLoaded=true)` + `ForceSendChunkColumn(realPlayer, cx, cz)`; client `SetChunkColumnVisible` at **scout** XYZ | **Scout anchor path** populates the same client accessor |
| Does VS auto-gen need `IPlayer` at the cell? | `LodScoutViewerEntity` / `LodScoutHostSystem` comments: auto-gen follows real players; scouts are **KeepLoaded + ForceSend workaround** — no dummy player | Server gen is triggered at **scout column**, delivery targets connected player |
| Is player teleported today? | `LodLoginBake.PinPickupPose` every tick; `ApplyExactPickup(..., requestChunks:false)`; scout fill via `LodLoginScoutFill.Tick` only | **No hops** during overlay sweep |
| 1.0.37 @358 — were scouts at visit cells? | `nearLive=16`, `captureStall` 733, scouts in **Capture** not blocked from spawning | Scouts **were** at cells; failure was **Capture before chunks resident**, not “can’t enter cell” |

**Conclusion:** the 358 cliff is **not** “map chunks won’t load unless the player is there.” Chunks **can** load at cold-near keys via scout `RequestUp` → KeepLoaded → ForceSend while the player stays at pickup. The 1.0.37 failure chain was **scheduling** (force Capture @ 4 ticks with `loadedMapChunks=0`, `captureStall` thrash, `chunkPressure` gated on WaitChunks only) plus **cold IO contention** (16 near scouts on annulus keys), not missing player presence.

#### How to disprove scout-sufficient in playtest (runId 1038)

If scout streaming is **broken/saturated** (not player-required), `stall-forensics` / `stalled-live-probe` will show:

| Pattern | Meaning |
|---------|---------|
| `hostOutcome=held`, `loadedMapChunks` stays 0 for many WaitChunks ticks, **no** `captureStall` thrash | KeepLoaded/ForceSend slow or starved — **fix host IO first**, not hop-unlock |
| `hostOutcome=capped` / `pending` frequent | `MaxConcurrentHolds=16` saturated — drain pending ups / boost ForceSend budget |
| `loadedMapChunks` rises 1→4 then Capture succeeds | Scout path **works** — 1.0.38 scheduling fix is sufficient |
| `hostOutcome=held`, `loadedMapChunks=0`, ticks ≫ `MaxWaitTicks`, **after** 1.0.38 gates | Rare — reconsider Plan C hop-unlock pump below |

Telemetry fields `captureRequiresPlayer:false` and `playerAtPickup:true` on stall events document the architectural assumption during playtest.

#### **Chosen branch: scout/host fix — scouts stay primary (1.0.38)**

**Why not hop-unlock now:**

1. **Scouts already implement the correct parallel path** — KeepLoaded + ForceSend at visit cells while player stays at pickup; a hop-unlock only warms one vanilla disk at a time and does not replace 16 parallel workers.
2. **1.0.37 evidence fits scheduling/saturation**, not player-absence — scouts reached Capture on cold keys without waiting for IO; the fleet thrashed instead of streaming.
3. **Efficiency** — scouts cover far more ground than hop-sweep; overlay already solved visible teleport / look-glitch with `PinPickupPose` + look lock.
4. **Fixes already shipped** — `CanEnterCapture`, one cold-near WaitChunks streamer, residency order, `chunkPressure`, stall forensics.

**Do not implement hop-unlock unless** playtest after 1.0.38 shows sustained `hostOutcome=held` + `loadedMapChunks=0` past wait budgets **after** scheduling gates **and** host IO fixes (ForceSend budget, hold drain, capped pending) are exhausted.

#### Plan C — hop-unlock pump (fallback only, not 1.0.38)

**Not a sweep replacement.** If Plan C is ever needed, the invisible player move is only a **stream / residency pump** so the scout fleet can keep parallel-capturing; all 16 slots remain primary bakers throughout.

Design from **warm-ring geometry**, not user guesses:

| Parameter | Derivation |
|-----------|------------|
| **Unlock point spacing** | `ΔR ≈ f_overlap × R_hold` where `R_hold = SweepBoostViewDistanceBlocks` (750) and `f_overlap ≈ 0.85–0.95` so adjacent warm disks cover the cold annulus without gaps |
| **First unlock center** | Radius `R_hold` from pickup (edge of warm halo) — e.g. `~750–890` blocks for cold-near annulus 750–1024 |
| **Unlock count** | `⌈(R_spawn − R_hold) / ΔR⌉` for `R_spawn = SpawnSolidRadiusBlocks` (1024) → often **1 unlock** for near annulus; far ring stays on scout-only streaming |
| **During unlock** | Behind overlay: invisible `ApplyExactPickup` to unlock XYZ; **look lock unchanged**; **scout fleet unchanged** (all 16 slots parallel); view boost may follow unlock center for vanilla warm disk |
| **Advance unlock** | When `finished` within unlock disk crosses `⌈N(R_hold)×f_w⌉` or scout `loadedMapChunks` stall persists → next unlock point outward |
| **Restore** | Exact saved pickup XYZ + yaw/pitch via `restorePos` / `RestorePlayerPose` — same as today |
| **Reject** | Hop-sweep as main algorithm; per-L0 micro-hops; unlock before scout IO failure is proven; reducing scout concurrency “because player moved” |

Colors + full ~1680 / 4075 disk intent unchanged either branch.

## Plan B — soft-release threshold (geometry, not ~600)

**User clarification:** a ~600 `finished` cutoff is **not hard**. Derive release timing from **warm-ring / residency geometry** (same model as the ~358 cliff), not a magic constant.

**Goal (unchanged):** let the player into the world when **near looks good enough**, fade the overlay if feasible, and **keep baking the full ~1680 / 4075 disk** in the background via `PlayModeBakeBudget` + `LodExploreBake` — seasonal GetColor and view distance stay non-negotiable.

### Geometry (same as cliff)

L0 cells in a filled disk at radius R (64-block cells):

**N(R) ≈ π (R/64)²**

| Radius | N(R) L0 (approx) | Meaning |
|--------|------------------|---------|
| **750** blocks (stream hold) | **~431** | Warm map residency — overlay scouts paint reliably |
| **683** blocks (inverse of 358 finished) | **358** | Observed 1.0.37 cliff — warm halo exhausted |
| **1024** blocks (spawn-solid) | **~804** | Near mesh gate disk — drawable underfoot |
| **~890** blocks | **~600** | Illustrative only — equals N(R), not a target constant |

The old “~600” suggestion sits near **N(~890)** — between warm completion and full spawn-solid disk. That is a **consequence of geometry**, not a knob to hard-code.

### Proposed soft-release rule (future — after cliff playtest passes)

Release overlay (hand off to `PlayModeBakeBudget`) when **all** of:

1. **Spawn-solid gate** — existing `CountMissingSpawnDrawable` ≈ 0 inside 1024 (player can stand on painted land).
2. **Warm ring substantially fed** — `finished ≥ ⌈N(R_hold) × f_w⌉` where `R_hold = SweepBoostViewDistanceBlocks` and **f_w ≈ 0.85–0.95** (tune from playtest, not 600).
3. **Paint pipeline healthy** — `paintReadyQueued > 0` or `paintStarveTicks` below small epsilon for several seconds (cliff broken).

Background work continues for remaining pending (~1680 total budget, full 4075 disk) — no fewer stops, no coarser colors.

Optional fade: cosmetic only after (1)–(3); does **not** change bake scope.

**Implementation hook (not 1.0.38):** compare `finished` to `LodLoginScoutFill.WarmRingL0CellEstimate(viewBoost.SweepBoostViewDistanceBlocks)` × factor; log in `warm-ring-probe` alongside `warmL0Estimate` to calibrate **f_w** from real sessions.

**Reject:** fixed `finished >= 600`, releasing before spawn-solid, or shrinking visit disk to hit a number faster.
