# Login bake efficiency plan

Research branch `cursor/bake-efficiency-research-5a8d` (1.0.32). Base: 1.0.30 scout-viewer login bake on `cursor/1.0.26-catchup-playtest-e27c`.

## Playtest baseline (1.0.30)

| Metric | Observed |
|--------|----------|
| Progress | ~86 / 1680 stops (~8%) in sample session |
| Per-stop wall time | ~1–3 s |
| GetColor work | Up to **4096 columns × ~16 stack samples** per L0 via `BakeSectionFromVisit` / `LodSurfaceMix.SampleColumnStack` |
| GC | ~7 GB managed growth in ~30 s |
| Concurrency UI | Often **1/16** scouts active early, then slow ramp |

Bottleneck shape: **(1)** Vintage Story `Block.GetColor` on the main thread, **(2)** per-stop allocations feeding Gen0/LOH, **(3)** serial `BakeBatchAtStop` vs parallel scout streaming, **(4)** mesh-gate waits at each stop.

---

## Top 5 techniques (with citations)

### 1. Array pooling + `Span<T>` scratch (allocation-free hot loops)

**What:** Rent fixed-size buffers from `ArrayPool<T>.Shared`, slice with `Span<T>` / `stackalloc` for per-tick work instead of `new T[n]` in loops.

**Why it applies:** Each L0 stop touches 4096 columns; `BakeSectionFromVisit` previously allocated `Block?[4096]`, `int[4096]`, `bool[4096]` plus thousands of `BlockPos` per section. That matches the “frequent short-lived arrays” case pooling was designed for.

**Sources:**
- Microsoft Learn, [ArrayPool\<T\>](https://learn.microsoft.com/en-us/dotnet/api/system.buffers.arraypool-1) (2024)
- Adam Sitnik, [Pooling large arrays with ArrayPool](https://adamsitnik.com/Array-Pool/) (2018) — LOH pressure, prefer `ArrayPool.Shared`
- Microsoft Learn, [Span\<T\>](https://learn.microsoft.com/en-us/dotnet/fundamentals/runtime-libraries/system-span%7Bt%7D) — stack-backed views without heap boxing

**DV mapping:** `LodBakeScratch`, existing `LodSurfaceMix.Rent`, `LodMesher.RentCopy` (already pooled). Extend to batch-candidate lists, explore-bake column queues.

---

### 2. Work-stealing parallelism for independent L0 bakes (with main-thread GetColor gate)

**What:** TPL / `Parallel.For` + thread-pool work stealing distributes CPU-bound work; child tasks use per-thread local queues (LIFO) with steal-from-tail for balance.

**Why it applies:** Scout slots already run 16 concurrent stream centers, but **GetColor must stay on the client main thread** (Vintage Story API). Parallelism still helps **mesh build** (`LodMesher` on workers), **mip merge**, SQLite persist batches, and **spatial indexing** — not raw `GetColor` unless we snapshot block/climate data first.

**Sources:**
- Leijen et al., [The design of a Task Parallel Library](https://www.microsoft.com/en-us/research/wp-content/uploads/2009/09/TheDesignOfATaskParallelLibraryoopsla2009.pdf) (OOPSLA 2009)
- Microsoft Learn, [Parallel performance (work stealing)](https://learn.microsoft.com/en-us/archive/msdn-magazine/2007/october/parallel-performance-optimize-managed-code-for-multi-core-machines) (2007)
- Microsoft Learn, [TaskScheduler work stealing](https://learn.microsoft.com/en-us/dotnet/fundamentals/runtime-libraries/system-threading-tasks-taskscheduler) (.NET 4+)

**DV mapping (A-tier):** Pipeline **capture → (main) tint bake → (worker) mesh** with a bounded channel; never call `GetColor` off-thread without a documented snapshot contract.

---

### 3. GC latency: SOH churn vs LOH mesh buffers

**What:** Background GC reduces Gen2 pause times but **LOH allocations (>85 KB)** are expensive to collect and not compacted by default; allocation spikes during BGC can throttle threads.

**Why it applies:** `LodMesher` already pools thread-static `Buffers` to avoid 240 KB/list regrowth per job. Login bake GC spikes likely combine **GetColor** object traffic + **mesh uploads** + **List growth** in scout tick.

**Sources:**
- Maoni Stephens, [CLR 4.0 GC / background GC](https://devblogs.microsoft.com/dotnet/so-whats-new-in-the-clr-4-0-gc/) (2008)
- Microsoft Learn, [Large object heap](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/large-object-heap)
- Microsoft Learn, [Background GC](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/background-gc)
- dotnet/runtime BOTR, [Garbage collection design](https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/botr/garbage-collection.md) (Maoni Stephens, 2015)

**DV mapping:** Continue pooling mesher/GPU paths; eliminate per-column `BlockPos` and per-section array allocations (1.0.32). Profile with `dotnet-gcdump` during overlay; avoid `GC.Collect` on the game thread.

---

### 4. SIMD / `Vector<T>` for post-GetColor numeric passes (not for `GetColor` itself)

**What:** Hardware intrinsics and `Vector128/256<T>` accelerate bulk numeric kernels (blur, quantize, RGB pack) when data is already in managed arrays.

**Why it applies:** `LodSurfaceMix.BlurLand`, `Quantize`, and palette packing are pure integer math over `int[]` — good SIMD candidates **after** colors are sampled. `Block.GetColor` is opaque engine code and cannot be vectorized.

**Sources:**
- Microsoft Learn, [Use SIMD and hardware intrinsics in .NET](https://learn.microsoft.com/en-us/dotnet/standard/simd) (2024)
- `.NET` hardware intrinsics overview — `System.Runtime.Intrinsics` (.NET Core 3.0+)

**DV mapping (B-tier future):** Vectorize `BlurLandOnce` / `Quantize` when `BlurRadius > 0`; keep scalar fallback. Low priority while `BlurRadius = 0`.

---

### 5. Spatial locality for terrain sampling (column-major, halo reuse)

**What:** Grid traversal with **spatial locality** (column-major, reuse climate queries, halo buffers) reduces cache misses and redundant map-chunk lookups — standard in terrain/LOD literature (e.g. chunked heightfields, Greedy meshing on regular grids).

**Why it applies:** `SampleColumnStack` walks Y then repeats `ReadSeasonRel` / frost probes per layer; neighbour batch bake sorts by distance but still random-accesses world columns.

**Sources:**
- Microsoft Research / industry practice: Greedy meshing on uniform grids (cf. `LodMesher` horizontal greedy planes — already implemented)
- For incremental meshing in games: common pattern is **dirty chunk queues** + **region coherence** (Minecraft-style section updates)

**DV mapping (A-tier):**  
- **Near mesh-gate:** Do not release scout until `HasDrawableMesh` for primary + spawn disk (keep 1.0.30 behaviour).  
- **Far capture-release:** Despawn scout after paint queued; defer mesh wait to drain phase for cells outside `SpawnSolidRadiusBlocks`.  
- Cache `seasonRel` / frost weight per `(cx,cz)` chunk column for the 16-layer stack walk.

---

## Change tiers

### A-tier — architectural (not in 1.0.32)

| Idea | Rationale | Risk |
|------|-----------|------|
| **Decouple scout mesh-gate by distance** | Near spawn: keep `HasDrawableMesh` before despawn. Far ring: paint + persist, mesh during drain. | Medium — must not weaken spawn solid-mesh gate |
| **Multi-stop bake queue** | While scouts stream, bake ready keys from `scoutReady` without serial `currentKey` | Low if GetColor stays main-thread and budgeted |
| **Raise effective concurrency** | Investigate why UI shows 1/16 (chunk load, `MaxWaitTicks`, host KeepLoaded) | Needs playtest telemetry |
| **GetColor snapshot lane** | Pre-sample tint inputs into a struct buffer on main thread; workers only pack palette | High — must match vanilla tints exactly |
| **Channel-based pipeline** | `System.Threading.Channels` for capture results → bake budget → mesh workers | Medium — ordering vs SQLite persist |
| **SIMD blur/quantize** | Only if blur re-enabled | Low |

**Invariants (do not break):**
- Real player never teleports; exact pickup XYZ pin.
- `LodScoutViewerEntity` despawn on slot release / `Reset` / overlay end.
- Spawn near-field solid mesh before overlay hide (`SpawnSolidRadiusBlocks`, `SpawnReadyTimeoutSec`).

### B-tier — shipped in 1.0.32

| Change | File(s) | Expected effect |
|--------|---------|-----------------|
| `LodBakeScratch` — thread-local `BlockPos` + column meta arrays | `LodBakeScratch.cs`, `LodSeasonBake.cs`, `LodSurfaceMix.cs` | Cuts thousands of `BlockPos` and ~50 KB arrays per L0 bake |
| Reuse scout `ready` list | `LodLoginScoutFill.cs` | Removes `List<long>` alloc per tick |
| Reuse batch-bake candidate list | `LodLoginBake.cs` | Removes sort list alloc per stop |
| Reuse despawn entity list | `LodScoutViewerEntity.cs` | Minor teardown alloc savings |
| `MaxBakePerTick` 12 → **16** | `LodLoginBake.cs` | ~33% more GetColor throughput per frame when CPU-bound (still frame-yielding) |

---

## Verification

1. **Cold login** on a world with empty / expired canvas (bootstrap ~1680 stops).
2. Confirm overlay progress moves faster than 1.0.30 (stops/min, or time to 50%).
3. Watch concurrency HUD: scout count should climb toward 16 when bandwidth allows.
4. After overlay: **solid land at spawn**, no holes inside `SpawnSolidRadiusBlocks`; Farseer gray tent + silhouettes to onset.
5. Esc / world-leave: scouts despawn; player at exact pickup XYZ.
6. `dotnet run` in `tests/VintageHorizons.Checks` — login sweep checks pass.

Optional: compare `debug-40cccb` / status writer `secPerStop` before vs after on same seed.

---

## References (quick list)

1. Sitnik — ArrayPool / LOH (2018)  
2. Microsoft Learn — Span, ArrayPool, SIMD, LOH, Background GC (2015–2024)  
3. Leijen et al. — TPL work stealing (OOPSLA 2009)  
4. Dijkstra / Arora et al. — work-stealing deque (cited in TPL paper)  
5. dotnet/runtime BOTR — GC design (Maoni Stephens, 2015)

---

## Next agent

If implementing A-tier **far capture-release**, start in `LodLoginScoutFill` phase `Mesh`: branch on distance from pickup vs `LodLoginBake.SpawnSolidRadiusBlocks`. Keep near scouts on current mesh-wait path. Add playtest gate in `LoginSweepChecks` for the distance constant.
