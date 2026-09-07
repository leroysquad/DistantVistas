# Login bake — cheaper compute research (1.0.35+)

**Branch:** `cursor/login-bake-cheaper-compute-research-beee`  
**Audience:** speed/implementer bot, coordinator  
**Scope:** Reduce **CPU work per useful bake result**, not idle-wait tuning alone.  
**Prior art in-repo:** [`login-bake-efficiency.md`](login-bake-efficiency.md), [`login-bake-walltime-1033.md`](login-bake-walltime-1033.md), [`simd-after-getcolor.md`](simd-after-getcolor.md)

---

## Executive summary

For Distant Vistas (DV), “easier for the computer to compute” means **fewer scalar `Block.GetColor` / `GetColorWithoutTint` calls and fewer bytes touched per L0 cell that actually ships to the player**, while keeping scout-based capture, spawn-solid ~1024, Farseer gray tent + black tips, and honest completion.

**Measured pain (1.0.35 logs):**

| Signal | Observation | Compute interpretation |
|--------|-------------|------------------------|
| `WaitChunks` | avg ~55 ticks; 96% samples `paintReadyQueued=0` | Scouts idle; **zero GetColor** but slots pinned — throughput collapse |
| GetColor / GC | ~540 calls/batch; ~7–9 GB managed / 30s | **Dominant CPU + allocator cost** even when paint flows |
| Work model | 1680 L0 × up to 4096 cols × stack layers | ~32k scalar API calls/L0 worst case; caches/stack early-exit already cut ~8–15× |

**Pipeline stages (research mapping):**

```
WaitChunks → Capture → GetColor paint → mesh → SQLite mip → draw
   ↑ IO/sched          ↑ column bitmask   ↑ scalar API   ↑ post-GetColor OK   ↑ batch I/O
```

Industry and academic terrain systems rarely solve this exact problem (client-side **seasonal block-color capture** into a mip pyramid), but they **do** solve adjacent problems with transferable patterns:

1. **Hierarchical / progressive bake** — ship coarse truth first, refine later (geometry clipmaps, MegaTexture mip fallbacks, ROAM/CBT incremental refinement).
2. **Importance scheduling** — spend compute where screen-space error or player-visible priority is high (chunk priority queues, screen-space error metrics).
3. **Amortized shading** — reuse texel/block decisions across space and time (virtual texturing page caches, tile atlases, block-mean caches).
4. **Allocation discipline** — hot-path pooling in .NET game loops (ArrayPool, struct spans, object reuse).

DV has already implemented several A-tier ideas (stack top-only, texture-mean skip, 16×16 GetColor cache, chunked paint, spawn-first visit budget, WaitChunks escalation). **Remaining headroom is mostly: (a) fewer columns painted per far L0, (b) fewer stack samples per column, (c) cross-cell reuse without lying about gaps, (d) decouple scout count from chunk-IO contention, (e) stop doing full L0 work when mip/parent already sufficient for horizon.**

---

## Ranked recommendations

Grades: **A** = high compute win, acceptable risk if verified; **B** = solid but smaller or riskier; **C** = incremental, research-only, or mostly wall-time not compute.

### A-tier (implementer should verify first)

| ID | Technique | Expected compute win | Risk to visuals | Impl. cost | Sources |
|----|-----------|---------------------|-----------------|-----------|---------|
| **A1** | **Distance-tiered column density** — paint full 64×64 only inside spawn-solid; far ring paint **32×32 or 16×16** stratified/downsampled columns, upsample in `LodSurfaceMix` / mesh | **2–4×** fewer GetColor calls on ~60–70% of visit stops | Medium — must preserve Farseer silhouette + no false-complete near spawn | Medium — `LodSeasonBake` column stride + blur aware of subsample | [Geometry clipmaps](https://www.hhoppe.com/geomclipmap.pdf) (screen-uniform tessellation); [GPU Gems 2 Ch.2](https://developer.nvidia.com/gpugems/gpugems2/part-i-geometric-complexity/chapter-2-terrain-rendering-using-gpu-based-geometry) (inactive fine levels); [ROAM variance LOD](https://doi.org/10.1155/2008/753584) |
| **A2** | **Mip-parent seeding / refine-on-demand** — for far L0, if parent L1 palette exists, **inherit + delta** only where capture differs from parent mean; full GetColor only on disagreement columns | **1.5–3×** on homogeneous biome rings | Medium — seasonal edges at L0/L1 boundaries need halo rules | Medium-high — `LodPipeline` parent read, `LodSeasonBake` diff mask | [Adaptive terrain streaming](https://jmarvie.com/publication/2009_02_wscg/adaptiveStreamingAndRenderingOfLargeTerrains.pdf) (progressive LOD load); [MegaTexture mip fallback](https://mrelusive.com/publications/presentations/2010_gtc/GTC_2010_Virtual_Textures.pdf) |
| **A3** | **BlockId + climate macro-cell cache across sections** — extend per-section cache to **cross-L0** keyed by `(blockId, climateRegionId, seasonBucket, Y-band)` for repeat soil/grass/snow; optional **persistent SQLite cache** of macro-cell RGB | **1.5–2×** on repeat biomes (logs still show ~540 calls/batch) | Low if climate bucket ≥16×16 and deep-winter exempt | Low-medium — `LodBakeScratch`, optional `LodPaletteEntry` side table | [Far Cry 4 AVT](https://doi.org/10.1201/b21261-13) (bake once, reuse); [UberBake](https://doi.org/10.1145/3386569.3392394) (precompute reuse) |
| **A4** | **Scout IO shaping: fewer near WaitChunks waiters** — cap concurrent chunk-pressure scouts (already `MaxNearWaitChunksLive=8`); add **distance-weighted spawn rate** so far scouts don’t compete for same map chunks as spawn disk | **Indirect** — restores paint throughput; cuts **wasted** WaitChunks ticks (no GetColor while stuck) | Low — product already allows partial capture | Low — `LodLoginScoutFill` scheduling | [Minecraft `ChunkTaskPriorityQueue`](https://mappings.dev/1.21.1/net/minecraft/server/level/ChunkTaskPriorityQueue.html); [VoxelLodTerrain clipbox streaming](https://voxel-tools.readthedocs.io/en/latest/api/VoxelLodTerrain/) |
| **A5** | **Column-level stack budget by surface class** — formalize rules: bare rock/sand/water → 1 sample; uniform grass → top + optional texture mean in deep winter only; keep multi-layer only where `FinishColumnPaint` needs mix | **1.3–2×** incremental atop `StackDeterminedByTopOnly` | Medium — camouflage regressions in deep winter | Low — `LodSurfaceMix.FinishColumnPaint` | [Serious Sam 4 terrain slides](https://gpuopen.com/download/gdc-2019-agtd2-4-million-acres-serious-sam-4.pdf) (near/mid/far material tiers) |

### B-tier

| ID | Technique | Expected compute win | Risk | Cost | Sources |
|----|-----------|---------------------|------|------|---------|
| **B1** | **Heightfield-first far pass** — capture column tops only → fast height+tint mesh; defer full stack paint to `PlayModeBakeBudget` | **2×** on far ring overlay phase | Medium-high — Farseer gray tent must still read correct canopy/snow vote | Medium — split `LodSeasonBake` phases | [Cinevva progressive load](https://app.cinevva.com/guides/landscape-generation-browser) (geometry first, textures second); [SS4 impostor + far splat](https://gpuopen.com/download/gdc-2019-agtd2-4-million-acres-serious-sam-4.pdf) |
| **B2** | **Screen-space error visit ordering** — reorder bootstrap stops by projected L0 pixel size at login camera, not just spawn-first disk budget | Better **useful work / GetColor** ratio early | Low for ordering only | Low — `LodLoginSweepBootstrap.BudgetBootstrapVisitStops` | [Error-bounded terrain](https://vca.informatik.uni-rostock.de/~schumann/papers/2008+/error%20bounded%20GPU-supported%20terrain%20visualization.pdf); [Vis98 priority-queue RQT](https://www.ifi.uzh.ch/dam/jcr:ffffffff-82b7-d340-ffff-ffff923549a3/Vis98.pdf) |
| **B3** | **Temporal reuse across logins** — store per-column `(blockId, seasonHash, rgb)` in SQLite; skip GetColor when world blockId + calendar bucket unchanged | **Large on revisit**; cold join modest | Medium — block changes, season drift | Medium — extend canvas schema | [MegaTexture page residency](https://www.shlom.dev/articles/how-virtual-textures-really-work/); DV canvas already persists |
| **B4** | **Halo-aware cross-L0 batching** — paint 3×3 L0 neighbourhood in one GetColor pass sharing `BlurWithHalo` scratch (already exists) to cut duplicate edge column work | **10–20%** on interior columns | Low | Medium — batch scheduler in `LodLoginBake.PaintReadyScouts` | [Terrain bake margin tiles](https://github.com/achimala/TheLongSilence/blob/4845c1df/tools/bake_terrain.py); `LodSurfaceMix.BlurWithHalo` |
| **B5** | **Aggressive GC hygiene** — struct-column iterator, eliminate per-batch `List` growth, `ObjectPool<BlockPos>` already partially via `LodBakeScratch.Pos` | Cuts **7–9 GB** churn → faster effective compute | Low | Low-medium — audit `BakeSectionFromVisitChunkedBody` | [ArrayPool](https://learn.microsoft.com/en-us/dotnet/api/system.buffers.arraypool-1); [Adam Sitnik LOH](https://adamsitnik.com/Array-Pool/) |
| **B6** | **SQLite write coalescing / lazy mip** — defer L2+ mip propagation during overlay; single WAL batch per paint slice | Small CPU; reduces I/O interference with chunk load | Low | Low — `LodPipeline`, `PlayModeBakeBudget` | [UberBake amortized bounces](https://cs.dartmouth.edu/~wjarosz/publications/seyb20uberbake-small.pdf) |

### C-tier (research / long tail)

| ID | Technique | Notes | Sources |
|----|-----------|-------|---------|
| **C1** | SIMD inside GetColor | **Hard banned** — post-GetColor only | [`simd-after-getcolor.md`](simd-after-getcolor.md) |
| **C2** | >16 scouts | Does not reduce GetColor count; worsens IO | [`login-bake-walltime-1033.md`](login-bake-walltime-1033.md) §Rejected |
| **C3** | GPU compute GetColor | VS API client-main-thread scalar — impractical without fork | — |
| **C4** | Full impostor replace for far L0 | Conflicts with seasonal block truth + canopy gray rules | [Unity terrain impostor discussions](https://discussions.unity.com/t/terrain-mesh-imposter-workflow-questions/718992) |
| **C5** | Transvoxel-style seam mesh | DV is palette grid not isosurface mesh | [Transvoxel](https://github.com/reromanlee/Transvoxel) |
| **C6** | Wang tile nonperiodic splat | Interesting for **distant noise** but not for exact VS colors | [GPU Gems 2 Ch.12](https://developer.nvidia.com/gpugems/gpugems2/part-ii-shading-lighting-and-shadows/chapter-12-tile-based-texture-mapping) |

---

## Mapping findings → DV pipeline

### Stage 1: WaitChunks (chunk stream / scout scheduling)

**Problem:** All 16 scouts in WaitChunks → zero Capture → zero paint; avg ~55 ticks; IO saturation ([`login-bake-walltime-1033.md`](login-bake-walltime-1033.md) §1.0.35 freeze).

**Industry pattern:** Priority queues with **blocking chunk budgets** (Minecraft `ChunkTaskPriorityQueue.acquire` / `maxTasks`) and **concentric clipbox streaming** (Voxel Tools `STREAMING_SYSTEM_CLIPBOX`) — load near viewer first, limit concurrent in-flight work.

**DV files (hypotheses):**

| Concept | File / symbol |
|---------|----------------|
| Scout phases, partial capture | `LodLoginScoutFill` — `PartialCaptureMin`, `WaitChunksForceCaptureTicks`, `MaxNearWaitChunksLive` |
| Chunk visibility / reveal | `LodScoutViewerEntity`, `LodLoginScoutFill.NearRevealChunks` / `FarRevealChunks` |
| Sequence telemetry | `LodScoutSeqDiag` (`H-SCOUT-SEQ`) |
| Visit ordering | `LodLoginSweepBootstrap.BudgetBootstrapVisitStops` |

**Compute lever:** Not fewer cycles per GetColor, but **higher paint-queue occupancy** → less wall-clock per finished L0. Shaping **who waits** (A4) avoids compute starvation.

---

### Stage 2: Capture (column bitmask)

**Problem:** Partial capture already hands off at tiered mins (256→64→16→1); still pays full paint cost for captured columns only.

**Industry pattern:** Progressive terrain loaders initialize child blocks from **parent LOD samples** then refine ([Jmarvie 2009](https://jmarvie.com/publication/2009_02_wscg/adaptiveStreamingAndRenderingOfLargeTerrains.pdf)).

**DV files:**

| Concept | File |
|---------|------|
| Column capture | `LodScoutEntity`, `LodSeasonBake.TryResolveLiveSurface` |
| Captured mask | `LodSection.Captured[]`, `LodSection.GridSize` (64) |
| Snow vote | `LodSeasonBake.ComputeSnowVote` |

**Compute lever:** Capture **fewer columns** far from spawn (A1) or capture full grid but **paint subset** with parent fill (A2).

---

### Stage 3: GetColor paint (dominant CPU)

**Problem:** Scalar `Block.GetColor` × columns × stack; texture mean 8× `GetColorWithoutTint` in deep winter; GC from palette snapshots.

**Industry pattern:**

- **Virtual texturing / AVT:** bake material combinations once at needed resolution, sample cached pages at runtime ([Chen 2016 AVT](https://doi.org/10.1201/b21261-13), [id MegaTexture](https://mrelusive.com/publications/presentations/2010_gtc/GTC_2010_Virtual_Textures.pdf)).
- **Importance:** only shade texels that change perceptual error ([Ray guiding for lightmaps](https://scholarsarchive.byu.edu/cgi/viewcontent.cgi?article=1662&context=etd)).
- **Spatial reuse:** macro-cell means + atlases ([GPU Gems 2 terrain masks](https://advances.realtimerendering.com/s2007/Andersson-TerrainRendering(Siggraph07)-CourseNotes.pdf)).

**DV files:**

| Concept | File |
|---------|------|
| Main bake | `LodSeasonBake.BakeSectionFromVisit`, `BakeSectionFromVisitChunked` |
| Stack / mean | `LodSurfaceMix.FinishColumnPaint`, `StackDeterminedByTopOnly`, `NeedsTextureMean` |
| Caches | `LodBakeScratch.GetColorCacheKey`, `TryGetSeasonTile`, blockId-only path |
| Color sample | `LodSeasonBake.SampleVanillaColor` |
| Post-GetColor SIMD | `LodRgbSimd`, `LodSurfaceMix.BlurLand` |
| Paint budget | `LodLoginBake.PaintReadyScouts`, `MaxPaintWallMsPerTick`, `MaxBakePerTick` |

**Compute lever:** A1, A2, A3, A5 directly reduce API calls. B5 reduces GC tax on same calls.

---

### Stage 4: Mesh

**Problem:** Near spawn mesh-wait is intentional; far ring meshes during drain/play budget.

**Industry pattern:** **Impostors / far splat** for distance ([SS4 GDC 2019](https://gpuopen.com/download/gdc-2019-agtd2-4-million-acres-serious-sam-4.pdf)); DV already uses palette mesh not full voxel mesh.

**DV files:** `LodMesher`, `LodLoginBake.SpawnSolidRadiusBlocks`, `LodLoginSweepGate`, `PlayModeBakeBudget`

**Compute lever:** Cheaper **palette** (fewer painted columns) → cheaper mesh upload; defer far mesh under budget (already `PlayModeBakeBudget`).

---

### Stage 5: SQLite mip pyramid

**Problem:** Persistence competes with chunk IO during overlay.

**DV files:** `LodPipeline.DrainLoginPersistence`, `LodLoginBake` batched drain, mip propagation in pipeline

**Compute lever:** B6 — lazy mip for far cells during overlay; compute parent mip from subsampled child (pairs with A1/A2).

---

### Stage 6: Draw

**Problem:** Inline loads (~16k/30s in 1.0.34) steal main-thread time adjacent to bake.

**DV files:** `LodPipeline.InstallLoadedSections`, `LodTerrainRenderer`

**Compute lever:** Already capped (2 installs/tick @ 1ms); keep bake from enqueueing unnecessary reloads (defer `InvalidatePaletteSnapshot` — shipped 1.0.35).

---

## “Do not do” list (product rules + research dead ends)

| Do not | Why |
|--------|-----|
| Player teleports | Scouts only — hard constraint |
| SIMD inside `Block.GetColor` / `GetColorWithoutTint` | Hard constraint; VS client API |
| False “complete” with large near gaps | Gap audit / spawn-solid honesty |
| Skip spawn-solid ~1024 full fidelity | Player-visible rule |
| Change Farseer gray tent + black tips semantics | Canopy/snow vote rules |
| `forceRecapture` on scout ticks | Regresses capture cost |
| Raise scout count alone past 16 | IO contention; no GetColor reduction |
| Global lower `StackDepth` without class rules | Breaks deep-winter camouflage |
| `BlurRadius > 0` on login grid without art sign-off | Checkerboard / smear risk |
| Full-section sync bake + higher `MaxBakePerTick` only | Minutes-long single tick freeze |
| Replace far terrain with non-VS procedural color | Breaks seasonal truth |
| GPU GetColor without VS engine support | Not viable in mod context |

---

## Suggested experiment plan (speed bot)

Run on cold canvas ~1680 L0; filter `H-SCOUT-SEQ`, `H-PAINT`, `H-LOOK`. Compare first 30s: `GetColorCalls`, gen0, managed MB, `paintReadyQueued`, L0/min.

### Experiment 1 — A1: Distance-tiered column paint (highest ROI)

1. Add `PaintColumnStride` by distance from spawn: `1` inside 1024, `2` at 1024–2500, `4` beyond (tune).
2. Uncaptured stride columns: fill from bilinear of painted neighbours or parent L1 cell.
3. **Verify:** spawn-solid visual parity; Farseer horizon no moiré; `GetColorCalls` avg **&lt;150** on far-heavy batches.
4. **Files:** `LodSeasonBake.BakeSectionFromVisitChunkedBody`, `LodSurfaceMix` fill helpers, tests in `SeasonBakeChecks`.

### Experiment 2 — A3: Cross-section macro-cell cache

1. Promote `LodBakeScratch` dictionary to session-scope `MacroColorCache` keyed by `(blockId, climateTile16, yBand, seasonTile16)`.
2. On hit: skip `SampleVanillaColor` API; copy cached RGB into column stack.
3. Invalidate bucket on calendar month change only (overlay is frozen time).
4. **Verify:** deep-winter columns with texture mean still call `NeedsTextureMean` path; cache hit rate in `ColorPathDiag`.
5. **Files:** `LodBakeScratch`, `LodSeasonBake.SampleVanillaColor`, `ColorPathDiag`.

### Experiment 3 — A4 + telemetry: WaitChunks pressure governor

1. When `nearLive` in WaitChunks ≥ `MaxNearWaitChunksLive` **and** `paintReadyQueued==0` for &gt;1s, pause new near spawns 8 ticks; prefer far-ring keys from `BudgetBootstrapVisitStops` outer 25%.
2. Log `chunkPressure` counter in `H-SCOUT-SEQ`.
3. **Verify:** `paintReadyQueued > 0` ≥90% of samples when scouts live; `avgNearTicks` &lt;35; no increase in gap audit failures.
4. **Files:** `LodLoginScoutFill.SelectPendingIndex`, `LodLoginSweepBootstrap`.

**Success criteria (aggregate):**

| Metric | 1.0.35 baseline | Target after A-tier bundle |
|--------|-----------------|----------------------------|
| `GetColorCalls` / batch | ~540 | **&lt;200** |
| gen0 @ 30s | ~2383 | **&lt;800** |
| Managed MB @ 30s | ~9251 | **&lt;4000** |
| Useful L0/min (overlay) | (measure) | **+30%** at same wall cap |
| `paintReadyQueued==0` | ~96% samples during freeze | **&lt;10%** |

---

## Research bibliography (external)

### Terrain LOD & streaming

- Losasso & Hoppe, *Geometry Clipmaps* (SIGGRAPH 2004) — https://www.hhoppe.com/geomclipmap.pdf  
- Losasso & Hoppe, *GPU-Based Geometry Clipmaps* (GPU Gems 2, Ch.2) — https://developer.nvidia.com/gpugems/gpugems2/part-i-geometric-complexity/chapter-2-terrain-rendering-using-gpu-based-geometry  
- Jmarvie et al., *Adaptive Streaming and Rendering of Large Terrains* (WSCG 2009) — https://jmarvie.com/publication/2009_02_wscg/adaptiveStreamingAndRenderingOfLargeTerrains.pdf  
- Deliot et al., *Concurrent Binary Trees for Large-Scale Game Components* (SIGGRAPH 2021) — https://advances.realtimerendering.com/s2021/Siggraph21%20Terrain%20Tessellation.pdf  
- Lengyel, *Transvoxel* (seamless voxel LOD) — https://github.com/reromanlee/Transvoxel  
- Voxel Tools, *VoxelLodTerrain* / clipbox streaming — https://voxel-tools.readthedocs.io/en/latest/api/VoxelLodTerrain/

### Virtual texturing & bake amortization

- Mittring, *Advanced Virtual Texture Topics* (SIGGRAPH 2008)  
- Van Waveren & Hart, *Virtual Texturing* (NVIDIA GTC 2010) — https://mrelusive.com/publications/presentations/2010_gtc/GTC_2010_Virtual_Textures.pdf  
- Chen, *Adaptive Virtual Textures* (Far Cry 4, 2016) — https://doi.org/10.1201/b21261-13  
- Seyb et al., *UberBake* (SIGGRAPH 2020) — https://doi.org/10.1145/3386569.3392394  
- Silvennoinen & Sloan, *Ray Guiding for Production Lightmap Baking* (SIGGRAPH Asia 2019)

### Scheduling & importance

- Minecraft `ChunkTaskPriorityQueue` — https://mappings.dev/1.21.1/net/minecraft/server/level/ChunkTaskPriorityQueue.html  
- Schumann & Müller, *Error-bounded GPU-supported terrain visualisation* — https://vca.informatik.uni-rostock.de/~schumann/papers/2008+/error%20bounded%20GPU-supported%20terrain%20visualization.pdf  
- Pajarola & Gobbetti, *Vis98 RQT priority refinement* — https://www.ifi.uzh.ch/dam/jcr:ffffffff-82b7-d340-ffff-ffff923549a3/Vis98.pdf  
- Duchaineau et al., *ROAM* (cited in ROAM games paper) — https://doi.org/10.1155/2008/753584

### Far terrain / impostors

- Croteau et al., *Serious Sam 4: Million Acres* (AMD GDC 2019) — https://gpuopen.com/download/gdc-2019-agtd2-4-million-acres-serious-sam-4.pdf  
- Cinevva, *Landscape Generation with Dynamic LOD* (progressive load ordering) — https://app.cinevva.com/guides/landscape-generation-browser

### .NET allocation discipline

- Microsoft, *ArrayPool&lt;T&gt;* — https://learn.microsoft.com/en-us/dotnet/api/system.buffers.arraypool-1  
- Sitnik, *Pooling large arrays with ArrayPool* — https://adamsitnik.com/Array-Pool/  
- Microsoft, *Large object heap* — https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/large-object-heap

---

## Coordinator handoff (PR summary)

**Title:** docs: login-bake cheaper-compute research (A-tier ranked)

**Summary for speed bot:**

DV login bake is **GetColor-bound** (~32k scalar calls/L0 worst case; ~540/batch after 1.0.35 caches). WaitChunks IO stalls cause **zero compute** phases (`paintReadyQueued` empty ~96%). Post-GetColor SIMD is done; further wins require **fewer GetColor invocations** and **less work per far L0**, not more scouts or longer paint walls alone.

**Top 3 experiments (in order):**

1. **A1** — Distance-tiered column stride (full 64×64 near 1024; 32×32 or 16×16 far) with parent/halo fill.  
2. **A3** — Session macro-cell color cache across L0 sections `(blockId, climate/season tile)`.  
3. **A4** — WaitChunks pressure governor so scouts don’t pile on saturated chunk IO.

**Do not:** teleports, SIMD-in-GetColor, fake complete, degrade spawn-solid or Farseer canopy rules.

**Verify with:** `H-PAINT` GetColorCalls, 30s gen0/MB, `H-SCOUT-SEQ` paintReadyQueued, cold 1680-stop playtest.
