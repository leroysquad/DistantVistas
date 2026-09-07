---
tags: [vintage-story, distant-vistas, performance]
aliases: [Bake efficiency, GetColor GC, LodBakeScratch]
---

# Login Bake Efficiency

Synthesis of `docs/plans/login-bake-efficiency.md` (ported as **1.0.32** on `cursor/1.0.26-catchup-playtest-e27c`). Full source: [[Sources/login-bake-efficiency]].

Related: [[Login Bake & Scouts]] · [[Sources - C Sharp Performance]] · [[Playtest Log - 2026-09-07]] · [[Pipeline - Cursor Agents]]

## Status table (1.0.32)

| Tier | Idea | Status |
|------|------|--------|
| A1 | Drain `scoutReady` under shared `MaxBakePerTick` while scouts stream | ✅ `PaintReadyScouts`, `MaxBakePerTick = 24` |
| A2 | Near mesh-gate inside 1024; far release after paint | ✅ `WaitForMesh` inside spawn-solid |
| A3 | GetColor ×4096 × stack; 8× `SampleTextureMean` per ground layer | ✅ `LodBakeScratch` + per-section BlockId texture-mean cache |
| A4 | Smaller batch radius beyond ~1024 | ✅ Neighbour bake radius 0 past spawn-solid |
| 5 | No `forceRecapture` in Mesh | ✅ `WaitChunks` near-only |
| 6 | `UpdatePartitioning` every ~8 ticks | ✅ `PartitioningEveryTicks = 8` via `LodVsCompat` |
| 7 | Throttle spawn reveal ring | ✅ `SpawnRevealEveryTicks = 4` |
| 8 | Progress/ETA uses wall time not 0.25s/stop | ✅ `NoteFinished` wall/delta |
| 9 | Farseer visit-mask debounce 500 ms | ✅ Stamp L0 keys + envelope disk |
| 10 | Empty mesh claims must not block scout slots | ✅ `HasDrawableMesh` wait; `HasEmptyMeshClaim` releases |
| B | Reuse ready / batch-candidate lists | ✅ In-place `CollectBatchBakeKeys` |
| B | `MaxBakePerTick` 12→16 | Absorbed as **24** |

**Invariants kept:** no player teleports; exact pickup XYZ; scout despawn on release; spawn-solid 1024; [[Farseer Companion|gray tent + black tips]].

## Playtest baseline (1.0.30)

From plan doc + [[Playtest Log - 2026-09-07]]:

| Metric | Observed |
|--------|----------|
| Progress | ~86 / 1680 stops (~8%) in sample session |
| GetColor work | Up to **4096 columns × ~16 stack samples** per L0 |
| GC | ~**7 GB** managed growth in ~30 s |
| Concurrency UI | Often **1/16** scouts early, then slow ramp |

**Bottleneck shape (pre-1.0.32):**

1. Serial `currentKey` / `BakeBatchAtStop`
2. Far mesh-wait holding slots
3. GetColor + `SampleTextureMean` cost — **not** scout count

1.0.31–1.0.32 address parallelism and GC; SIMD-after-GetColor still open.

## Technique 1 — ArrayPool + Span scratch

Rent fixed buffers from `ArrayPool<T>.Shared` instead of `new T[n]` in the 4096-column GetColor pass.

- Thread-local `BlockPos` avoids thousands of short-lived position objects.
- Per-section `Dictionary<BlockId, meanRgb>` reuses 8-sample texture mean.

**DV implementation:** `LodBakeScratch`, existing `LodSurfaceMix.Rent`, `LodMesher.RentCopy`.

Citations: [[Sources - C Sharp Performance#ArrayPool]] · [[Sources - C Sharp Performance#Span]]

## Technique 2 — Work-stealing vs main-thread GetColor

Scout slots = 16 stream centers. **GetColor stays on client main thread.** Parallelism helps mesh build / mip / persist, not raw `GetColor`.

Citation: [[Sources - C Sharp Performance#TPL design paper]]

## Technique 3 — GC: SOH churn vs LOH mesh buffers

Login bake GC = GetColor object traffic + mesh uploads + `List` growth in scout tick.

Pool mesher/GPU paths; eliminate per-column `BlockPos` and per-section arrays.

Citations: [[Sources - C Sharp Performance#Large object heap]] · [[Sources - C Sharp Performance#CLR 4.0 GC]]

## Technique 4 — SIMD after GetColor (not done)

`BlurLand` / `Quantize` are candidates **after** colors are sampled. `Block.GetColor` cannot be vectorized. Low priority while `BlurRadius = 0`.

Tracked by [[Pipeline - Cursor Agents|SIMD expert agent]] — do not duplicate on audit branches.

## Technique 5 — Spatial locality / two-tier scout gate

Near spawn: `HasDrawableMesh` before despawn. Far ring: paint + persist, mesh during drain.

See [[Login Bake & Scouts#Two-tier scout gate (1.0.31+)]].

## Branch warning

Original research: `cursor/bake-efficiency-research-5a8d` — **do not merge wholesale**. That branch still serializes `BakeBatchAtStop`, uses `IWorldAccessor.LoadedEntities`, lacks 1.22.7 shim.

## Verification

1. Cold login, empty/expired canvas (~1680 stops).
2. Overlay **16/16** scouts within seconds; % moves every few seconds.
3. After overlay: solid land at spawn; Farseer gray tent + black tips.
4. Esc / leave: scouts despawn; exact pickup XYZ.
5. `scripts/check.sh fast` on VS assembly machine.

## References

- Repo: `docs/plans/login-bake-efficiency.md`
- `DistantVistas/src/Render/LodBakeScratch.cs`
- `CHANGELOG.md` 1.0.31–1.0.32
