# Login bake efficiency plan

Ported onto `cursor/1.0.26-catchup-playtest-e27c` as **1.0.32**. Original research: `cursor/bake-efficiency-research-5a8d` (do not merge that branch — it still serializes `BakeBatchAtStop`, uses `IWorldAccessor.LoadedEntities`, and lacks the 1.22.7 shim).

## Status

| Tier | Idea | This branch |
|------|------|-------------|
| A1 | Drain `scoutReady` under shared MaxBakePerTick while scouts stream | Done (`PaintReadyScouts`, `MaxBakePerTick = 24`) |
| A2 | Near mesh-gate inside SpawnSolidRadiusBlocks; far release after paint | Done (`WaitForMesh` inside 1024) |
| A3 | GetColor ×4096 × stack; SampleTextureMean 8× per ground layer | Done (`LodBakeScratch` + per-section BlockId texture-mean cache) |
| A4 | Smaller batch radius beyond ~1024 | Done (leftover neighbour bake radius 0 past spawn-solid; overlay paints the visit cell) |
| SIMD | Post-GetColor BlurLand / Quantize / RGB pack (`LodRgbSimd`) | **Done** (AVX2 → Vector128 → scalar; bit-identical; not inside `GetColor`) |
| 5 | No forceRecapture in Mesh | Done (WaitChunks near-only, `forceRecapture: false`; Paint/Mesh never sweep) |
| 6 | UpdatePartitioning every ~8 ticks (pinned scouts) | Done (`PartitioningEveryTicks = 8` via `LodVsCompat`) |
| 7 | Throttle spawn reveal ring | Done (`SpawnRevealEveryTicks = 4`) |
| 8 | Progress/ETA assumes 0.25s/stop | Done (`NoteFinished` uses wall / delta; measured min 0.02s) |
| 9 | Farseer visit-mask 256×256 on HasDataSet churn | Done (500ms debounce; stamp L0 keys + envelope disk; skip height enrich during overlay) |
| 10 | Drain/stabilize + emptyMeshKeys must not block scout slots | Done (`HasDrawableMesh` wait; `HasEmptyMeshClaim` releases the slot) |
| B | Reuse ready / batch-candidate / despawn lists | Done (in-place `CollectBatchBakeKeys` into `stopBakeKeys`) |
| B | `MaxBakePerTick` 12→16 | Absorbed as **24** so parallel captures drain in one overlay tick |
| B | ArrayPool mix / halo / blur scratch | Done (`LodSurfaceMix.Rent` + halo + `blurScratch`) |

Invariants kept: no player teleports; exact pickup XYZ; scout despawn on slot release / Reset / overlay end; spawn-solid 1024; Farseer gray tent + black tips.

## Playtest baseline (1.0.30)

| Metric | Observed |
|--------|----------|
| Progress | ~86 / 1680 stops (~8%) in sample session |
| GetColor work | Up to **4096 columns × ~16 stack samples** per L0 |
| GC | ~7 GB managed growth in ~30 s |
| Concurrency UI | Often **1/16** scouts active early, then slow ramp |

Bottleneck shape: (1) serial `currentKey` / `BakeBatchAtStop`, (2) far mesh-wait holding slots, (3) GetColor + SampleTextureMean cost — **not** scout count.

## Techniques (citations)

### 1. Array pooling + Span scratch

Rent fixed-size buffers from `ArrayPool<T>.Shared` instead of `new T[n]` in the 4096-column GetColor pass. Thread-local `BlockPos` avoids thousands of short-lived position objects. Per-section `Dictionary<BlockId, meanRgb>` reuses the 8-sample texture mean.

- Microsoft Learn, [ArrayPool\<T\>](https://learn.microsoft.com/en-us/dotnet/api/system.buffers.arraypool-1)
- Adam Sitnik, [Pooling large arrays with ArrayPool](https://adamsitnik.com/Array-Pool/) (2018)
- Microsoft Learn, [Span\<T\>](https://learn.microsoft.com/en-us/dotnet/fundamentals/runtime-libraries/system-span%7Bt%7D)

**DV:** `LodBakeScratch`, `LodSurfaceMix.Rent` / halo / `blurScratch` (`ArrayPool`), existing `LodMesher.RentCopy`.

### 2. Work-stealing vs main-thread GetColor

Scout slots are 16 stream centers. **GetColor stays on the client main thread.** Parallelism helps mesh build / mip / persist, not raw `GetColor`.

- Leijen et al., [The design of a Task Parallel Library](https://www.microsoft.com/en-us/research/wp-content/uploads/2009/09/TheDesignOfATaskParallelLibraryoopsla2009.pdf) (OOPSLA 2009)

### 3. GC: SOH churn vs LOH mesh buffers

Login bake GC mixed GetColor object traffic + mesh uploads + `List` growth in the scout tick. Pool mesher/GPU paths; eliminate per-column `BlockPos` and per-section arrays.

- Microsoft Learn, [Large object heap](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/large-object-heap)
- Maoni Stephens, [CLR 4.0 GC / background GC](https://devblogs.microsoft.com/dotnet/so-whats-new-in-the-clr-4-0-gc/)

### 4. SIMD after GetColor (done)

`LodRgbSimd` vectorizes **post-GetColor** work only: `BlurLand` (two box-blur passes, including the production `BlurRadius = 0` mask/alpha copy over 4096 cells), `Quantize` / `QuantizeSpan`, and RGB pack/unpack over already-sampled `int[]` buffers. Path: AVX2 8-wide, then portable `Vector128` (SSE2 / NEON; same hardware as `System.Numerics.Vector.IsHardwareAccelerated`), then scalar. `ForceScalar` in checks proves **bit-identical** integer averages (truncated `r/n`) and the same `0xFFBBGGRR` pack as `LodSurfaceMix.Pack`. Vintage Story `Block.GetColor` / `GetColorWithoutTint` stay scalar on the client main thread.

Production bake still does **not** quantize the 64×64 grid (that would checkerboard snow/dirt). `LodSurfaceMix.Quantize` and `LodRgbSimd.QuantizeSpan` are live APIs; overlay `BlurLand` always runs the SIMD kernel.

Full loop inventory, intrinsic tiers, fallback, and reasoned cost: [`simd-after-getcolor.md`](simd-after-getcolor.md).

- Microsoft Learn, [Use SIMD-accelerated types](https://learn.microsoft.com/en-us/dotnet/standard/simd)
- Microsoft Learn, [Vector256\<T\>](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.intrinsics.vector256-1) / [Vector128\<T\>](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.intrinsics.vector128-1)
- Microsoft Learn, [System.Numerics.Vector\<T\>](https://learn.microsoft.com/en-us/dotnet/api/system.numerics.vector-1)
- Intel, [AVX2](https://www.intel.com/content/www/us/en/docs/intrinsics-guide/index.html#avx2techs=AVX2)

### 5. Spatial locality / two-tier scout gate

Near spawn: keep `HasDrawableMesh` before despawn. Far ring: paint + persist, mesh during drain.

## Verification

1. Cold login, empty/expired canvas (~1680 stops).
2. Overlay 16/16 scouts within seconds; % moves every few seconds.
3. After overlay: solid land at spawn; Farseer gray tent + black tips.
4. Esc / leave: scouts despawn; exact pickup XYZ.
5. `scripts/check.sh fast` on a machine with Vintage Story assemblies.
