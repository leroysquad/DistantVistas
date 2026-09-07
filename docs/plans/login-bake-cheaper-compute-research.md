# Login bake — cheaper compute research (1.0.35+)

**Branch:** `cursor/login-bake-cheaper-compute-research-beee`  
**Audience:** speed/implementer bot, coordinator  
**Scope:** Make the **same** visual and coverage outcome **easier to compute** — less waste, better scheduling, fewer allocations, provably equivalent reuse — **not** fewer painted cells, shorter disk, duller colors, or skipping `GetColor` without equivalence proof.  
**Prior art in-repo:** [`login-bake-efficiency.md`](login-bake-efficiency.md), [`login-bake-walltime-1033.md`](login-bake-walltime-1033.md), [`simd-after-getcolor.md`](simd-after-getcolor.md)

---

## Non-negotiables (user clarification)

These are **fixed product requirements**. Research may only rank techniques that preserve them **bit-for-bit or by provably equivalent rules already in the bake path**.

| Fixed | What it means for research |
|-------|---------------------------|
| **Seasonal / GetColor terrain colors** | Every painted column must reflect live `Block.GetColor` / `GetColorWithoutTint` semantics for the captured block stack at login season. No procedural substitutes, no parent-fill approximations, no coarser sampling grids, no quantization that dulls snow/dirt. **Reuse is allowed only when inputs match** (same blockId, position, climate/season context) and output is identical. |
| **View distance / login disk coverage** | Full bootstrap: **~1680 L0 cells**, **4075-block radius** disk, spawn-solid **~1024**, Farseer onset / far horizon, gap audit honesty. No fewer visit stops, no smaller radius, no “good enough” horizon holes. |
| **Scout / pickup invariants** | No player teleports; scout entities; exact pickup restore; Farseer gray tent + black tips; no false complete. |
| **GetColor API** | Scalar on client main thread; no SIMD inside `GetColor`. Post-GetColor SIMD already shipped. |

**In scope:** scheduling, IO shaping, allocation/GC reduction, cache deduplication, visit **order** (not count), batching equivalent work, cheaper **post-sample** math, eliminating redundant API calls for **identical** inputs.

**Out of scope:** anything that trades color fidelity or disk/coverage for speed.

---

## Executive summary

For DV, “easier for the computer to compute” means **the same 1680-stop, full-grid, live-season bake — with less wasted work per finished L0**:

- **WaitChunks starvation** — scouts idle while chunk IO saturates → zero useful compute (`paintReadyQueued` empty ~96% in freeze logs).
- **Redundant GetColor** — ~540 calls/paint batch despite caches; identical `(blockId, tile, Y, season)` repeats across columns and L0 sections.
- **GC tax** — ~7–9 GB managed / 30s from palette snapshots, resume allocs, and hot-path churn — slows the same scalar work.

**Pipeline:**

```
WaitChunks → Capture → GetColor paint → mesh → SQLite mip → draw
   ↑ sched/IO           ↑ full 64×64      ↑ dedupe only    ↑ same output   ↑ batch I/O
```

Industry patterns that **transfer without compromising colors or coverage**:

1. **Priority scheduling / IO budgets** — same work, better throughput (chunk priority queues, clipbox streaming).
2. **Provably equivalent deduplication** — virtual-texture page caches, macro-cell RGB reuse when inputs unchanged ([AVT](https://doi.org/10.1201/b21261-13), [MegaTexture](https://mrelusive.com/publications/presentations/2010_gtc/GTC_2010_Virtual_Textures.pdf)).
3. **Allocation discipline** — ArrayPool, fewer ephemeral objects on the same GetColor loop ([ArrayPool](https://learn.microsoft.com/en-us/dotnet/api/system.buffers.arraypool-1)).
4. **Equivalent stack rules** — skip layers only when `FinishColumnPaint` outcome is unchanged (already: `StackDeterminedByTopOnly`, `NeedsTextureMean`).

**Rejected from prior draft:** distance-tiered column stride, mip-parent fill without full GetColor, heightfield-first deferred paint, impostor/procedural far color — all trade fidelity or coverage.

---

## Ranked recommendations

Grades: **A** = high win, preserves non-negotiables; **B** = solid incremental; **C** = long tail or rejected.

### A-tier (implementer should verify first)

| ID | Technique | Expected win | Why it preserves colors + coverage | Impl. cost | Sources |
|----|-----------|--------------|-----------------------------------|-----------|---------|
| **A1** | **Cross-L0 GetColor dedup cache** — session-scope cache keyed by exact inputs `(blockId, worldX, worldY, worldZ, seasonRel bucket)` or finer; hit = copy prior **identical** API result | **1.5–2.5×** fewer scalar calls on repeat soil/grass/snow | Same API output; no skipped columns | Low-medium — `LodBakeScratch`, `LodSeasonBake.SampleVanillaColor`, `ColorPathDiag` | [AVT bake-once reuse](https://doi.org/10.1201/b21261-13); [UberBake precompute](https://doi.org/10.1145/3386569.3392394) |
| **A2** | **WaitChunks / chunk-IO pressure governor** — cap near WaitChunks pile-up; stagger spawns so paint queue stays fed; same 16 scouts, same cells, less idle time | Restores throughput; cuts **wasted** ticks (not fewer GetColor per L0) | Full disk unchanged; paint when capture ready | Low — `LodLoginScoutFill`, `MaxNearWaitChunksLive`, `SelectPendingIndex` | [Minecraft `ChunkTaskPriorityQueue`](https://mappings.dev/1.21.1/net/minecraft/server/level/ChunkTaskPriorityQueue.html); [VoxelLodTerrain clipbox](https://voxel-tools.readthedocs.io/en/latest/api/VoxelLodTerrain/) |
| **A3** | **GC / allocation hygiene on paint path** — eliminate per-slice palette snapshot rebuilds (partially shipped 1.0.35), pool scratch lists, throttle resume snapshot, audit `BakeSectionFromVisitChunkedBody` allocs | Same GetColor count, **faster** effective compute; target gen0 &lt;800 @30s | No visual change | Low-medium — `LodLoginBake`, `LodSeasonBake`, `LodBakeScratch` | [ArrayPool](https://learn.microsoft.com/en-us/dotnet/api/system.buffers.arraypool-1); [LOH / Sitnik](https://adamsitnik.com/Array-Pool/) |
| **A4** | **Smarter visit order (same 1680 stops)** — spawn-first + **within-budget** reorder by chunk-residency / paint-queue depth so scouts hit already-loaded cells first | Higher L0/min at same wall cap; no fewer cells | Full 4075 disk; order only | Low — `LodLoginSweepBootstrap.BudgetBootstrapVisitStops` | [Vis98 priority-queue refinement](https://www.ifi.uzh.ch/dam/jcr:ffffffff-82b7-d340-ffff-ffff923549a3/Vis98.pdf); [error-bounded scheduling](https://vca.informatik.uni-rostock.de/~schumann/papers/2008+/error%20bounded%20GPU-supported%20terrain%20visualization.pdf) |
| **A5** | **Extend equivalent stack rules** — only where audit proves `FinishColumnPaint` identical to full stack (extend `StackDeterminedByTopOnly` / `NeedsTextureMean` with tests, not new approximations) | **1.2–1.5×** incremental | Same rules as 1.0.33 stack early-exit philosophy | Low — `LodSurfaceMix`, `SeasonBakeChecks` | [SS4 material tiers](https://gpuopen.com/download/gdc-2019-agtd2-4-million-acres-serious-sam-4.pdf) (concept: tiered **equivalent** detail) |

### B-tier

| ID | Technique | Expected win | Preserves non-negotiables? | Sources |
|----|-----------|--------------|---------------------------|---------|
| **B1** | **Temporal canvas reuse (revisit)** — SQLite stores `(blockId, seasonHash, rgb)` per column; skip GetColor only when world + calendar bucket **unchanged** since last bake | Large on **revisit**; modest cold | Yes — equivalence proof required | [MegaTexture residency](https://www.shlom.dev/articles/how-virtual-textures-really-work/) |
| **B2** | **Halo-aware cross-L0 paint batching** — share `BlurWithHalo` scratch across 3×3 neighbourhood; dedupe edge column GetColor when same block stack | **10–20%** | Yes — full 64×64 per L0 | [Terrain bake margins](https://github.com/achimala/TheLongSilence/blob/4845c1df/tools/bake_terrain.py); `LodSurfaceMix.BlurWithHalo` |
| **B3** | **SQLite / mip write batching** — defer L2+ mip propagation during overlay; coalesce WAL fsyncs | Less IO contention with chunk load | Yes — same final pyramid | [UberBake amortization](https://cs.dartmouth.edu/~wjarosz/publications/seyb20uberbake-small.pdf) |
| **B4** | **Inline load cap during overlay** (shipped 1.0.35) — keep mesh install budget low so GetColor thread breathes | Wall-time + hitch reduction | Yes | `LodPipeline.InstallLoadedSections` |
| **B5** | **Widen cache tiles cautiously** — 16×16 → 32×32 **only** where climate/season field is flat per block tests | Modest dedup gain | Risky — verify per-biome; demote if any tint drift | GPU Gems terrain masks |

### C-tier / rejected

| ID | Technique | Verdict | Why rejected |
|----|-----------|---------|--------------|
| **R1** | Distance-tiered column stride (32×32 / 16×16 far) | **REJECT** | Fewer painted cells; not full GetColor grid |
| **R2** | Mip-parent seeding / inherit + delta without full column GetColor | **REJECT** | Approximates seasonal color away from parent |
| **R3** | Heightfield-first; defer full stack paint to play mode | **REJECT** | Compromises overlay-time color truth for far ring |
| **R4** | Shorter bootstrap disk / fewer than ~1680 stops | **REJECT** | View distance / coverage |
| **R5** | Skip GetColor / procedural far splat / impostor color | **REJECT** | Color fidelity |
| **R6** | Quantize 64×64 login grid | **REJECT** | Dulls snow/dirt (already policy) |
| **R7** | SIMD inside GetColor | **REJECT** | Hard API rule |
| **R8** | >16 scouts as only lever | **REJECT** | IO waste; no dedup |
| **R9** | GPU GetColor without VS fork | **REJECT** | Not viable |
| **C1** | Transvoxel / Wang tiles | **N/A** | Wrong problem domain |

---

## Mapping findings → DV pipeline

### Stage 1: WaitChunks (scheduling — no coverage change)

**Problem:** All scouts in WaitChunks → no Capture → no paint; IO saturation.

**Lever:** A2 — same cells, better spawn timing and WaitChunks caps (`MaxNearWaitChunksLive`, partial escalation already shipped).

**Files:** `LodLoginScoutFill`, `LodScoutViewerEntity`, `LodScoutSeqDiag`, `LodLoginSweepBootstrap`

---

### Stage 2: Capture (full column grid)

**Problem:** Capture idle while waiting; partial handoff tiers (256→1) are product-allowed but must still paint **all captured columns** at full fidelity.

**Lever:** None that reduce capture count. Scheduling (A2, A4) only.

**Files:** `LodScoutEntity`, `LodSection.Captured[]`, `LodSeasonBake.TryResolveLiveSurface`

---

### Stage 3: GetColor paint (dominant CPU — dedupe & equivalent rules only)

**Problem:** ~540 GetColor calls/batch; redundant identical samples.

**Levers:** A1 (dedup cache), A5 (proven stack rules), B1 (revisit equivalence), B2 (halo batch dedup).

**Files:** `LodSeasonBake`, `LodSurfaceMix`, `LodBakeScratch`, `LodRgbSimd`, `LodLoginBake.PaintReadyScouts`

**Not allowed:** fewer columns, parent approximation, skip without proof.

---

### Stage 4: Mesh

**Lever:** Same palette → same mesh; defer **mesh build** scheduling via `PlayModeBakeBudget` is OK if **palette is already fully baked** for that L0. Do not defer GetColor for coverage cells.

**Files:** `LodMesher`, `PlayModeBakeBudget`, `LodLoginBake.SpawnSolidRadiusBlocks`

---

### Stage 5: SQLite mip pyramid

**Lever:** B3 — batch writes; final stored pyramid unchanged.

**Files:** `LodPipeline.DrainLoginPersistence`, `LodLoginBake`

---

### Stage 6: Draw

**Lever:** B4 — cap inline loads during overlay; same drawn coverage.

**Files:** `LodPipeline.InstallLoadedSections`, `LodTerrainRenderer`

---

## “Do not do” list

| Do not | Why |
|--------|-----|
| Compromise seasonal / GetColor colors | User non-negotiable |
| Reduce view distance, bootstrap radius, or visit stop count | User non-negotiable |
| Paint fewer than 64×64 columns per L0 (stride / subsample) | Fewer cells |
| Fill uncaptured columns from parent L1 / bilinear guess | Approximate color |
| Defer full GetColor for disk cells to “later” while claiming overlay complete | Coverage + honesty |
| Player teleports | Scout-only rule |
| SIMD inside `GetColor` | API rule |
| False complete with near gaps | Gap audit |
| Change Farseer gray tent + black tips | Canopy/snow vote |
| `forceRecapture` on scout ticks | Wasted capture |
| >16 scouts as only lever | IO contention |
| Global lower `StackDepth` without equivalence proof | Camouflage risk |
| Quantize login 64×64 grid | Color quality |
| Procedural / impostor far color | Non-VS truth |

---

## Suggested experiment plan (speed bot)

Cold canvas ~1680 L0, full 4075 disk. Filter `H-SCOUT-SEQ`, `H-PAINT`, `H-LOOK`. **Success = same visuals/coverage, lower waste metrics.**

### Experiment 1 — A1: Cross-L0 GetColor dedup cache

1. Session `MacroColorCache` with key at least `(blockId, x, y, z)` or `(blockId, climateTile16, y, seasonTile16)` — **must pass bit-identical tests vs uncached path**.
2. Log hit rate in `ColorPathDiag`; never hit on deep-winter texture-mean columns unless mean cache is separate.
3. **Verify:** `SeasonBakeChecks` golden columns; `GetColorCalls` drop with **unchanged** output hashes per L0.
4. **Files:** `LodBakeScratch`, `LodSeasonBake.SampleVanillaColor`, `ColorPathDiag`

### Experiment 2 — A2: WaitChunks pressure governor + telemetry

1. When all slots WaitChunks and `paintReadyQueued==0`, defer respawn of same hot keys (`waitRetries`); prefer pending keys whose map chunks already resident.
2. Log `chunkPressure` in `H-SCOUT-SEQ`.
3. **Verify:** `paintReadyQueued > 0` when scouts live; **1680 stops still planned**; gap audit pass.
4. **Files:** `LodLoginScoutFill`, `LodLoginSweepBootstrap`

### Experiment 3 — A3: GC hygiene audit

1. Profile `BakeSectionFromVisitChunkedBody` allocations; eliminate remaining per-slice snapshot rebuilds and `List` growth.
2. **Verify:** gen0 **&lt;800** @30s, managed **&lt;4 GB** @30s; **pixel-identical** bake output.
3. **Files:** `LodLoginBake`, `LodSeasonBake`, `LodBakeScratch`

**Success criteria (same coverage, less waste):**

| Metric | 1.0.35 baseline | Target |
|--------|-----------------|--------|
| Visit stops / disk | ~1680 / 4075 | **unchanged** |
| `GetColorCalls` / batch | ~540 | **&lt;300** via dedup only |
| gen0 @ 30s | ~2383 | **&lt;800** |
| Managed MB @ 30s | ~9251 | **&lt;4000** |
| `paintReadyQueued==0` during freeze | ~96% | **&lt;10%** |
| Visual / gap audit | pass | **pass** |

---

## Research bibliography (external)

### Scheduling & streaming (same work, better order)

- Minecraft `ChunkTaskPriorityQueue` — https://mappings.dev/1.21.1/net/minecraft/server/level/ChunkTaskPriorityQueue.html  
- Voxel Tools `VoxelLodTerrain` / clipbox — https://voxel-tools.readthedocs.io/en/latest/api/VoxelLodTerrain/  
- Pajarola & Gobbetti, Vis98 RQT priority — https://www.ifi.uzh.ch/dam/jcr:ffffffff-82b7-d340-ffff-ffff923549a3/Vis98.pdf  

### Provably equivalent reuse (not approximation)

- Chen, *Adaptive Virtual Textures* — https://doi.org/10.1201/b21261-13  
- Van Waveren & Hart, *Virtual Texturing* (GTC 2010) — https://mrelusive.com/publications/presentations/2010_gtc/GTC_2010_Virtual_Textures.pdf  
- Seyb et al., *UberBake* — https://doi.org/10.1145/3386569.3392394  
- Silvennoinen & Sloan, *Ray Guiding for Lightmap Baking* — https://scholarsarchive.byu.edu/cgi/viewcontent.cgi?article=1662&context=etd  

### Terrain LOD (reference only — most ideas here **reject** due to non-negotiables)

- Losasso & Hoppe, *Geometry Clipmaps* — https://www.hhoppe.com/geomclipmap.pdf  
- Jmarvie et al., adaptive terrain streaming — https://jmarvie.com/publication/2009_02_wscg/adaptiveStreamingAndRenderingOfLargeTerrains.pdf  
- Croteau et al., *Serious Sam 4* far terrain — https://gpuopen.com/download/gdc-2019-agtd2-4-million-acres-serious-sam-4.pdf  

### .NET allocation discipline

- Microsoft `ArrayPool<T>` — https://learn.microsoft.com/en-us/dotnet/api/system.buffers.arraypool-1  
- Sitnik, *Pooling large arrays* — https://adamsitnik.com/Array-Pool/  

---

## Coordinator handoff (PR summary)

**Title:** docs: login-bake cheaper-compute research (non-negotiables: full colors + full disk)

**Summary for speed bot:**

User clarified: **do not compromise seasonal GetColor quality or view distance / 4075-disk coverage.** Rank only techniques that deliver the **same** outcome with less waste.

**Top 3 experiments (in order):**

1. **A1** — Cross-L0 GetColor **dedup** cache (identical inputs → identical RGB; tests required).  
2. **A2** — WaitChunks / chunk-IO pressure governor (same 1680 stops, less idle starvation).  
3. **A3** — GC / allocation hygiene on chunked paint path (same pixels, less churn).

**Explicitly rejected:** column stride / fewer painted cells, mip-parent color fill, heightfield-first deferred paint, shorter disk, skip GetColor, procedural far color.

**Verify with:** pixel/golden tests + `H-PAINT` GetColorCalls + 30s gen0/MB + full gap audit + 1680-stop count unchanged.
