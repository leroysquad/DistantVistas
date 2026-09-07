# Login bake efficiency plan

Ported onto `cursor/1.0.26-catchup-playtest-e27c` as **1.0.32**. Original research: `cursor/bake-efficiency-research-5a8d` (do not merge that branch — it still serializes `BakeBatchAtStop`, uses `IWorldAccessor.LoadedEntities`, and lacks the 1.22.7 shim).

## Status

| Tier | Idea | This branch |
|------|------|-------------|
| A | All ~16 scouts work; UI must not sit on 1/16 | Done (8 near mesh-wait + 8 far paint-release; overlay `{live}/16 scouts`) |
| A | Near mesh-gate vs far capture-release | Done (`WaitForMesh` inside `SpawnSolidRadiusBlocks` 1024) |
| A | Bake `scoutReady` while others stream | Done (`PaintReadyScouts`, `MaxBakePerTick = 24`) |
| B | `LodBakeScratch` thread-local `BlockPos` + `ArrayPool` column arrays | Done |
| B | Reuse ready / batch-candidate / despawn lists | Done |
| B | `MaxBakePerTick` 12→16 | Absorbed as **24** so parallel captures drain in one overlay tick |

Invariants kept: no player teleports; exact pickup XYZ; scout despawn on slot release / Reset / overlay end; spawn-solid 1024; Farseer gray tent + black tips.

## Playtest baseline (1.0.30)

| Metric | Observed |
|--------|----------|
| Progress | ~86 / 1680 stops (~8%) in sample session |
| GetColor work | Up to **4096 columns × ~16 stack samples** per L0 |
| GC | ~7 GB managed growth in ~30 s |
| Concurrency UI | Often **1/16** scouts active early, then slow ramp |

Bottleneck shape: (1) main-thread `Block.GetColor`, (2) per-stop allocations, (3) serial `BakeBatchAtStop`, (4) mesh-gate waits at every stop including far.

## Techniques (citations)

### 1. Array pooling + Span scratch

Rent fixed-size buffers from `ArrayPool<T>.Shared` instead of `new T[n]` in the 4096-column GetColor pass. Thread-local `BlockPos` avoids thousands of short-lived position objects.

- Microsoft Learn, [ArrayPool\<T\>](https://learn.microsoft.com/en-us/dotnet/api/system.buffers.arraypool-1)
- Adam Sitnik, [Pooling large arrays with ArrayPool](https://adamsitnik.com/Array-Pool/) (2018)
- Microsoft Learn, [Span\<T\>](https://learn.microsoft.com/en-us/dotnet/fundamentals/runtime-libraries/system-span%7Bt%7D)

**DV:** `LodBakeScratch`, existing `LodSurfaceMix.Rent`, `LodMesher.RentCopy`.

### 2. Work-stealing vs main-thread GetColor

Scout slots are 16 stream centers. **GetColor stays on the client main thread.** Parallelism helps mesh build / mip / persist, not raw `GetColor`.

- Leijen et al., [The design of a Task Parallel Library](https://www.microsoft.com/en-us/research/wp-content/uploads/2009/09/TheDesignOfATaskParallelLibraryoopsla2009.pdf) (OOPSLA 2009)

### 3. GC: SOH churn vs LOH mesh buffers

Login bake GC mixed GetColor object traffic + mesh uploads + `List` growth in the scout tick. Pool mesher/GPU paths; eliminate per-column `BlockPos` and per-section arrays.

- Microsoft Learn, [Large object heap](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/large-object-heap)
- Maoni Stephens, [CLR 4.0 GC / background GC](https://devblogs.microsoft.com/dotnet/so-whats-new-in-the-clr-4-0-gc/)

### 4. SIMD after GetColor (not done)

`BlurLand` / `Quantize` are candidates **after** colors are sampled. `Block.GetColor` cannot be vectorized. Low priority while `BlurRadius = 0`.

### 5. Spatial locality / two-tier scout gate

Near spawn: keep `HasDrawableMesh` before despawn. Far ring: paint + persist, mesh during drain.

## Verification

1. Cold login, empty/expired canvas (~1680 stops).
2. Overlay 16/16 scouts within seconds; % moves every few seconds.
3. After overlay: solid land at spawn; Farseer gray tent + black tips.
4. Esc / leave: scouts despawn; exact pickup XYZ.
5. `scripts/check.sh fast` on a machine with Vintage Story assemblies.
