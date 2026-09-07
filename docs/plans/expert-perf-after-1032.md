# Expert perf validation — after 1.0.32 (tip `57ccf05`)

**Validator:** EXPERT PERF bot (post Speed `bc-7f03d897` + SIMD `bc-c875eb9a`)  
**Branch:** `cursor/1.0.26-catchup-playtest-e27c` (~1.0.32)  
**Compare:** https://github.com/leroysquad/DistantVistas/compare/main...cursor/1.0.26-catchup-playtest-e27c  
**Date:** 2026-09-07

## Executive verdict

**GO — send to independent REVIEW BOT.** No additional safe perf change must land first.

The historical 86/1680 crawl was correctly diagnosed: serial `BakeBatchAtStop`, far mesh-wait pinning all scout slots, and uncached `GetColor`/`SampleTextureMean` churn — not insufficient scout count. Speed + SIMD + 1.0.32 follow-ups address those causes in code. What remains dominant is **scalar `Block.GetColor` on the main thread** and **post-sweep mesh drain/stabilize** — architectural limits, not missed micro-wins on this tip.

---

## Code verification (claims checked, not trusted blindly)

| Claim | Verified in code | Notes |
|-------|------------------|-------|
| `PaintReadyScouts` + `MaxBakePerTick = 24` | `LodLoginBake.cs` L35, L773–799 | Overlay paints budgeted ready scouts per tick; not one serial `currentKey` |
| Near mesh-gate 1024 / far FlagBaked release | `LodLoginScoutFill.cs` L367–376, L217–236 | `WaitForMesh = near`; far scouts `ReleaseSlot` after paint |
| Per-section `SampleTextureMean` cache | `LodBakeScratch.cs`, `LodSeasonBake.SampleTextureMean` | `BeginSectionTextureMeans` / `TryGetSectionTextureMean` per L0 bake |
| Leftover neighbour radius 0 past spawn-solid | `BatchBakeRadiusFor` returns 0 outside 1024² | `BakeBatchAtStop` retained for expire leftovers only; overlay path is `PaintReadyScouts` |
| No `SweepLoadedColumns`/`forceRecapture` on mesh/paint | `LodLoginScoutFill` L165–170 `forceRecapture: false`; spawn sweep same | Near scout sweep only, throttled |
| Partition every 8 ticks | `PartitioningEveryTicks = 8` | `LodVsCompat.TryUpdatePartitioning` gated |
| Spawn reveal every 4 ticks | `SpawnRevealEveryTicks = 4` | `GrowRevealAroundSpawn` throttled |
| Wall/delta ETA | `LodLoginSweepTiming.NoteFinished` + `MeasuredMinSecPerStop = 0.02` | Parallel 24-scout ticks not clamped to 0.25s hop fiction |
| Farseer visit-mask debounce | `FarseerVisitOnset.MaskRebuildMinMs = 500` | Stamp L0 keys + envelope; not full 256×256 rebuild per `HasDataSet` |
| Sticky empty-mesh claim release | `HasEmptyMeshClaim` in Mesh phase L232–236 | Slot releases; spawn gate still uses `HasDrawableMesh` |
| `ArrayPool` scratch | `LodSurfaceMix.Rent`, halo, blur; `LodBakeScratch` column meta | Thread-local rent/return |
| `LodRgbSimd` post-GetColor | `LodRgbSimd.cs`, wired from `LodSurfaceMix.BlurLand`/`Quantize` | AVX2 → Vector128 → Vector → scalar; `ForceScalar` bit-identical tests |
| `Block.GetColor` stays scalar | No `GetColor` in `LodRgbSimd`; `SampleVanillaColor` uses `LodBakeScratch.Pos` | Correct boundary |
| Neglected + P0 follow-ups | `neglected-issues-followup.md`, `user-mentioned-unfixed-followup.md` | Skip thresholds, frontier yield 24, sky-gap rim pull, `HasDrawableMesh` parent coverage |

**Dead code note (non-blocking):** `BakeBatchAtStop` is defined but not called on the overlay hot path; expire leftovers use `DrainExpireLeftovers` → `TryBakeOne`. Checks keep the symbol for leftover-era contract. No perf impact.

---

## Ranked remaining bottlenecks (after 1.0.32 lands)

Ordered by expected share of **cold login wall time** (overlay + drain + stabilize). Magnitudes are reasoned from structure and 1.0.30 playtest shape, not re-benchmarked on this VM (no Vintage Story assemblies here).

### 1. Scalar `Block.GetColor` × column stack (dominant during sweep)

- **Where:** `LodSeasonBake.SampleVanillaColor` → `SampleColumnStack` (up to `StackDepth = 16` layers × up to 4096 captured columns per L0).
- **Why still #1:** Vintage Story API is client-main-thread and branchy (climate, season, block state). SIMD cannot enter without breaking correctness.
- **Mitigation already on tip:** `LodBakeScratch` thread-local `BlockPos`; per-section texture-mean cache (one 8× `GetColorWithoutTint` per `BlockId`, not ×4096); `MaxBakePerTick = 24` caps frame spikes.
- **Residual:** ~tens of thousands of GetColor calls per painted L0; at 1680 stops this is still minutes of CPU on a typical PC.

### 2. Chunk stream + capture pipeline latency (scout-bound)

- **Where:** `LodLoginScoutFill` WaitChunks → `QueueL0SectionForce` → Capture idle wait (`MaxCaptureWaitTicks = 80`).
- **Why:** Each visit cell must stream map chunks and finish voxel capture before paint. Sixteen scouts overlap this, but each stop still waits on I/O and worker capture.
- **Not fixable** by more scouts without more server KeepLoaded pressure (already capped elsewhere).

### 3. Near spawn mesh-wait (intentional, localized)

- **Where:** 8 near scouts (`MaxNearConcurrent`) through `Phase.Mesh` until `HasDrawableMesh` or `HasEmptyMeshClaim` release.
- **Why:** Spawn-solid gate (`SpawnSolidRadiusBlocks = 1024`) requires drawable meshes before overlay hides. Far ring deliberately does **not** wait — FlagBaked paint releases slots.
- **Residual:** Can slow **near** slot turnover; no longer blocks all 16 scouts (historical bug).

### 4. Post-sweep drain + stabilize (dominant perceived “still on splash”)

- **Where:** `Phase.Draining` (mip 16/tick, persistence 8/tick, `RenderDirty` mesh queue) then `Phase.Stabilizing` (spawn missing count, `FarthestMeshedDistance` vs 75% Farseer onset, up to `SpawnReadyTimeoutSec = 90`).
- **Why:** Visit % can hit 100% while hundreds of horizon meshes and spawn holes still build. User experiences this as login wall time after the counter stops moving.
- **Not a bake-efficiency miss** — separate mesh-upload / GPU path.

### 5. SQLite persistence during sweep (secondary)

- **Where:** `DrainLoginPersistence(1)` per `BakeAndPersist`, plus drain-phase budgets.
- **Smaller** than GetColor after pooling; still visible on slow disks.

### 6. `GetColorWithoutTint` for texture mean (tertiary, mostly cached)

- **Where:** `SampleTextureMean` — 8 samples per **distinct** `BlockId` per section.
- **Was** a major multiplier pre-cache; now amortized across 4096 columns.

### 7. Post-GetColor SIMD blur / quantize (negligible)

- **Where:** `LodRgbSimd.BlurLandOnce` ×2 at `BlurRadius = 0` (mask/alpha copy over 4096 cells).
- **Measured expectation:** sub-millisecond per L0 vs seconds of GetColor. Correct to ship; not a login-wall lever.

### 8. Farseer visit-mask / height enrich (fixed on tip)

- **Was:** 256×256 mask rebuild on every `HasDataSet` stamp during overlay.
- **Now:** 500ms debounce; height enrich deferred until overlay ends. **No longer ranks** as overlay bottleneck.

---

## Cold login wall time — what still dominates?

```text
[Sweeping  ~5–70% UI]  GetColor (1) + stream/capture (2) + near mesh-wait (3)
        ↓
[Auditing]             gap audit / expire leftovers (bounded 256 L0, 16/tick)
        ↓
[Draining  ~80–85%]    horizon mesh build + mip + persist (4)
        ↓
[Stabilizing ~85–100%] spawn-solid meshes + 75% far-ready + frame stability (4)
```

**Bottom line:** After 1.0.32, the sweep phase is **throughput-limited by scalar GetColor and capture**, not scout count or SIMD. The **tail** is often **mesh drain/stabilize**, especially first join on large disks (~1680 stops, 4075 radius).

---

## Safe future ideas (not required before review)

| Idea | Safety | Expected gain |
|------|--------|---------------|
| Skip GetColor on unchanged captured columns (palette hash) | Medium — needs audit for season drift | Medium on revisit/expire |
| Raise `MaxBakePerTick` with frame budget guard | Medium — Windows NR risk | Low–medium |
| Batch persistence writes | Low–medium | Low on fast SSD |
| Dead-code remove `BakeBatchAtStop` if expire path never needs neighbour batch | Low | None (cleanup only) |

None of these are **critical** blockers; review can proceed without them.

---

## NEVER do (unsafe / invariant-breaking)

1. **SIMD inside `Block.GetColor` / `GetColorWithoutTint`** — client-only state, climate maps, texture RNG; breaks bit identity and thread rules. `LodRgbSimd` correctly stops at sampled `int[]` buffers.

2. **Player teleports for bake coverage** — invariant: pickup XYZ + yaw/pitch exact restore; scouts are `LodScoutViewerEntity` stream centers only.

3. **False-complete with gaps** — do not raise skip thresholds or bypass `FindMisses` / spawn-solid / far-ready gates to shorten overlay. In-window skip already honest below 32 misses (`neglected-issues`).

4. **Photoreal / full-scene bake-in during login** — would multiply GetColor and GPU work by orders of magnitude; contradicts FlagBaked + gray tent aesthetic.

5. **`forceRecapture: true` on scout/paint ticks** — recapture storm pinned 16 slots and blew GC (documented in `LodLoginScoutFill`).

6. **Quantize the production 64×64 grid** — checkerboards snow/dirt (`LodSurfaceMix.QuantizeStep` API only).

7. **Treat `HasEmptyMeshClaim` as land for spawn gate** — causes sky gaps; parent coverage must use `HasDrawableMesh`.

8. **More than 16 scouts without server KeepLoaded redesign** — historical diagnosis: not the bottleneck; adds churn.

9. **Rebuild Farseer 256×256 visit mask every `HasDataSet` add** — fixed; reverting regresses overlay CPU.

10. **Hop-era 256-neighbour `BakeBatchAtStop` on overlay path** — UI freeze at 1/16 scouts; replaced by visit-cell `PaintReadyScouts`.

---

## Go / no-go for REVIEW BOT

| Criterion | Status |
|-----------|--------|
| A-tier bake parallelism + mesh gate | ✅ Landed |
| B-tier GC / texture-mean cache | ✅ Landed |
| Post-GetColor SIMD + tests | ✅ Landed (`SimdAfterGetColor`, `PostGetColorSimd`) |
| P0 sky-gap / skip / emptyMesh | ✅ Landed (see follow-up docs) |
| Invariants: no teleport, gray tent + black tips, exact XYZ | ✅ Preserved in code + checks |
| Critical safe fix still missing | ❌ None identified |

### Decision: **GO**

Independent REVIEW BOT should run **playtest + `scripts/check.sh fast`** on a Vintage Story 1.22.7 machine. Focus review on:

1. Overlay reaches **16/16** scouts and **% advances every few seconds** (not 1/16 stuck).
2. Post-overlay: **no holes underfoot** inside 1024; **gray tent + black tips** at Farseer join (no white sky strip).
3. **Same pickup XYZ** after overlay / Esc.
4. Esc-resume honest wording for leftover holes (`9edc6a1`).

Do **not** block review waiting for GetColor SIMD or more scouts.

---

## References

- `docs/plans/login-bake-efficiency.md` — tier table (all marked done on tip)
- `docs/plans/simd-after-getcolor.md` — SIMD scope and cost model
- `docs/plans/neglected-issues-followup.md`
- `docs/plans/user-mentioned-unfixed-followup.md`
- `CHANGELOG.md` §1.0.32
