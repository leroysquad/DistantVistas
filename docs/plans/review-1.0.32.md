# Independent review — Distant Vistas 1.0.32

**Branch:** `cursor/1.0.26-catchup-playtest-e27c`  
**Compare:** [main…tip](https://github.com/leroysquad/DistantVistas/compare/main...cursor/1.0.26-catchup-playtest-e27c)  
**Reviewer:** Independent review bot (pre-mod install gate)  
**Date:** 2026-09-07  

## Verdict: **PASS-WITH-NOTES**

**Mods install: YES**

The tip branch matches documented 1.0.32 behavior for product invariants, bake-efficiency claims, neglected/user P0 follow-ups, and ship hygiene. Static code review and contract-test source inspection support install. Automated `scripts/check.sh` was not executed in this cloud VM (`dotnet` unavailable); treat local `scripts/check.sh fast` as recommended confirmation before ModDB publish.

---

## Checklist

| Area | Result | Notes |
|------|--------|-------|
| No player teleport sweeps | **PASS** | `LodLoginBake` pins pickup via `PinPickupPose` / `ApplyExactPickup` every tick; sweep uses `LodLoginScoutFill` + `LodScoutViewerEntity` stream centers. No call sites for `ApplyQuiet(` during overlay. |
| LodScoutViewer-style centers | **PASS** | `LodScoutViewerEntity` (not `EntityPlayer`) spawned per visit cell; host KeepLoaded + client visible ring. |
| Dwell then full despawn | **PASS** | Scout phases: WaitChunks → Capture → Paint → (Mesh near only) → `ReleaseSlot` / `DespawnOne` / `DespawnAll` on teardown. |
| Exact pickup XYZ (+ facing) | **PASS** | `pickupX/Y/Z`, `pickupYaw/Pitch` captured at overlay start; `WriteExactPickup` uses raw doubles, no floor/chunk snap. |
| Spawn-solid near (~1024) | **PASS** | `SpawnSolidRadiusBlocks = 1024`; near scouts `WaitForMesh` until `HasDrawableMesh`. |
| No silent “complete” with large gaps | **PASS** | `LodLoginSweepGate.MaxSkipMisses` / `MaxSkipUnfilledGaps` = **32**; above forces scout fill; below names deferred frontier drip. |
| Farseer onset ~4.5×VD+700 | **PASS** | `LodCoveragePolicy.HorizonDrawScale = 4.5f`, `FarseerOnsetExtraBlocks = 700f`; `CoverageChecks` asserts math. |
| Gray tent + black tips | **PASS** | `farseer-region.fsh` / `region.fsh`: `smokeGray`, `inkAmt` clamp `0.0–0.24`, ridge `height01²`; `StaticAssetChecks.FarseerOverlay`. |
| No photoreal shaders in DV | **PASS** | No PBR/photoreal strings in assets; Farseer overlay is mist/ink wash over stock heightmap, not baked photoreal. |
| SIMD only after GetColor | **PASS** | `LodRgbSimd` has no `GetColor` / `GetColorWithoutTint`; `LodSurfaceMix` delegates blur/quantize; `LoginSweepChecks.PostGetColorSimd`. |
| MaxBakePerTick = 24 | **PASS** | `LodLoginBake.MaxBakePerTick`; `PaintReadyScouts` drains `scoutReady` per tick. |
| Near/far scout release | **PASS** | `MaxNearConcurrent = 8` mesh-wait, `MaxFarConcurrent = 8` paint-release; `emptyMeshKeys` releases slot. |
| Texture-mean cache | **PASS** | `LodBakeScratch.BeginSectionTextureMeans` / `TryGetSectionTextureMean` per BlockId per L0. |
| ArrayPool scratch | **PASS** | `LodBakeScratch.RentColumnMeta`, `LodSurfaceMix.Rent`, `LodRgbSimd` plane pools. |
| ETA honesty | **PASS** | `LodLoginSweepTiming.NoteFinished` uses wall/finishes; `MeasuredMinSecPerStop = 0.02` (not hop-era 0.25s clamp). |
| Skip/gap honesty (>32) | **PASS** | `AllowsInWindowSkip` + tests in `LoginSweepChecks`; Esc-resume drop wording names deferred holes. |
| Pause-on-Start | **PASS** | `LodPauseOnStartCompat.KeepUnpaused` during overlay; `RestoreAfterLoginBake` after success. |
| Shader `flat` → `flatness` | **PASS** | `lodterrain.fsh` uses `flatness`; `StaticAssetChecks` guards C7537. |
| Empty-mesh sky holes | **PASS** | Parent coverage / eviction use `HasDrawableMesh`; sticky `emptyMeshKeys` cannot fake land (`user-mentioned` follow-up). |
| White strip at Farseer join | **PASS** | `FarseerOnsetScaleForMeshedRim` pulls smoke to meshed rim when lag >256 blocks; `region.fsh` unchanged. |
| Season/canopy GetColor | **PASS (monitored)** | 1.0.19 audit + `SeasonBakeChecks` / `LodCanopyGray.IsSeasonFoliagePath`; no open P0 on tip follow-up docs. Residual risk: exotic mod blocks untested in CI. |
| modinfo / csproj version | **PASS** | Both **1.0.32**; `StaticAssetChecks.VersionAgreement`. |
| CHANGELOG 1.0.32 | **PASS** | Entry documents bake speed, SIMD, neglected + user P0 items, verify steps. |
| Zip build path | **PASS** | `scripts/package.sh` → `dist/distantvistas_1.0.32.zip` from Release `Mods/distantvistas`. |
| SIMD / contract tests | **PASS (source)** | `SeasonBakeChecks.SimdAfterGetColor`, `LoginSweepChecks.PostGetColorSimd`, gate threshold tests. Runtime not executed here. |
| Lessons file on tip | **NOTE** | `.cursor/rules/distant-vistas-lessons.mdc` absent on tip **and** `origin/main` (local-only convention). |

---

## 1. Product invariants

### No player teleport sweeps
`LodLoginBake` documents and implements scout-only coverage. `TickSweeping` calls `scoutFill.Tick` and `PaintReadyScouts`; player position is held with `PinPickupPose` each frame. `LodLoginBakePlayerMove.ApplyQuiet` has **no overlay call sites** — only `ApplyExactPickup` / `ApplyQuietFrom(restorePos)` for restore safety.

Legacy hop vocabulary remains in comments, `StopPhase` enum, and status key `"teleports-begin"`, but `LogTeleportBegin` explicitly logs *“player stays at pickup”*. Cosmetic only.

### Scout centers, dwell, despawn
`LodScoutViewerEntity` is a real `Entity` with `AllowOutsideLoadedRange`, `AlwaysActive`, `StoreWithChunk => false`. Near ring waits for drawable mesh; far ring releases after FlagBaked paint. `ReleaseSlot` despawns viewer and clears keep-loaded neighbourhood.

### Exact pickup restore
`ApplyExactPickup` writes doubles to `Pos` and `ServerPos` with yaw/pitch; used on overlay end, Esc, and every pinned frame.

### Gap / skip honesty
`LodLoginSweepGate` blocks in-window skip when `FindMisses` or `LastUnfilledGaps` exceed **32**; smaller counts skip with explicit “frontier drip” wording.

### Farseer onset and look
Onset distance: `viewDistance * 4.5 + 700`. Visual: gray smoke body (`smokeGray`), capped `inkAmt`, ridge darkening — not empty black flattening. `FarseerVisitOnset` debounces mask rebuild (500 ms) and pulls onset uniform when meshes lag silhouette by >256 blocks.

### SIMD boundary
`LodRgbSimd` operates on `int[]` buffers post-sample. `Block.GetColor` stays in `LodSeasonBake` on the main thread. Production `BlurRadius` remains **0**; SIMD still runs mask/alpha copy path (documented).

---

## 2. Bake / speed claims vs code

| Claim (CHANGELOG / plans) | Code anchor |
|---------------------------|-------------|
| 16 scouts, 8 near / 8 far | `LodLoginScoutFill.MaxConcurrent`, `MaxNearConcurrent`, `MaxFarConcurrent` |
| `MaxBakePerTick = 24` | `LodLoginBake.MaxBakePerTick`, `PaintReadyScouts` |
| Parallel `scoutReady` drain | `TickSweeping` → `PaintReadyScouts` (not serial `currentKey`) |
| Texture-mean cache | `LodBakeScratch` per-section `Dictionary<BlockId,int>` |
| ArrayPool columns / mix / blur | `LodBakeScratch.RentColumnMeta`, `LodSurfaceMix.Rent`, `LodRgbSimd` planes |
| Visit-cell-only past 1024 | `BatchBakeRadiusFor` returns 0 outside `SpawnSolidRadiusBlocks` |
| Frontier yield 24 | `LodFrontierScout.MaxExplorePendingYield = 24` |
| Expire leftover cap 256 L0 | `MaxExpireLeftoverKeys = 256` |
| Missing-tex white strip guard | `LodSeasonBake.RejectExpireMissingTex` + `IsMissingTextureWhite` |

`BakeBatchAtStop` remains in source but has **no callers** (dead hop-era path); active path is scout + `PaintReadyScouts`. Harmless; optional cleanup later.

---

## 3. Regression risks reviewed

| Risk | Status |
|------|--------|
| Pause-on-Start freeze | Mitigated: force-unpause during overlay, restore pause + menu after. |
| `float flat` shader reserved word | Fixed: `flatness` in `lodterrain.fsh`; static guard. |
| Sticky `emptyMeshKeys` blocking coverage | Fixed: `HasDrawableMesh` for parent/gap/eviction; empty claim releases scout slot. |
| White sky strip at Farseer join | Fixed: `FarseerOnsetScaleForMeshedRim` + overlap 128; tests in `CoverageChecks`. |
| Season / canopy GetColor | Addressed in 1.0.19+ with `FlagFrost` gating, `IsSeasonFoliagePath`, mip canopy RGB; not re-opened on tip follow-up audits. Playtest across seasons still advised. |

---

## 4. Ship hygiene

- **Version:** `modinfo.json` and `DistantVistas.csproj` both `1.0.32`.
- **CHANGELOG:** 1.0.32 section complete with verify bullets and zip install instructions.
- **Package:** `scripts/package.sh` builds Release and writes `dist/distantvistas_1.0.32.zip`.
- **Tests:** Extensive static + logic checks in `tests/VintageHorizons.Checks/` guard scouts, SIMD isolation, gate thresholds, Farseer overlay, and coverage math. Recommend `scripts/check.sh fast` on a machine with .NET 10 before ModDB upload.

---

## 5. Lessons file

`.cursor/rules/distant-vistas-lessons.mdc` is **not** on this tip or on `origin/main`. Acceptable if maintained locally only; consider committing a sanitized copy if cloud agents should inherit playtest lessons.

---

## 6. Install gate decision

**Mods install: YES**

**Reasons:**
1. All mandatory product invariants are implemented and backed by contract tests in source.
2. 1.0.32 bake-efficiency and SIMD work matches `login-bake-efficiency.md` and `simd-after-getcolor.md`.
3. Neglected-issue and user-mentioned P0 follow-ups are marked done in code and follow-up plan docs.
4. Version, changelog, and packaging path align for `distantvistas_1.0.32.zip`.
5. No blocking defect found that would require a code fix before install.

**Caveats (notes, not blockers):**
- Run `scripts/check.sh fast` (or full matrix) locally before publishing; this review environment lacked `dotnet`.
- Legacy “teleport” strings in logs/comments may confuse log readers; behavior is scout-based.
- Season colour correctness for third-party block mods is not fully enumerable in CI.
- Cold playtest: confirm 16/16 scouts, no holes underfoot, gray/black Farseer join without white strip, exact pickup XYZ after overlay/Esc.

**Do not:** install to production worlds without backup; do not extract zip; fully quit VS between mod swaps (per CHANGELOG).

---

## Files consulted (representative)

- `DistantVistas/src/Render/LodLoginBake.cs`, `LodLoginScoutFill.cs`, `LodScoutViewerEntity.cs`
- `DistantVistas/src/Render/LodRgbSimd.cs`, `LodBakeScratch.cs`, `LodSurfaceMix.cs`
- `DistantVistas/src/Render/LodLoginSweepGate.cs`, `FarseerVisitOnset.cs`, `LodCoveragePolicy.cs`
- `DistantVistas/src/Render/LodPauseOnStartCompat.cs`, `LodTerrainRenderer.cs`
- `DistantVistas/modinfo.json`, `CHANGELOG.md`, `scripts/package.sh`
- `tests/VintageHorizons.Checks/LoginSweepChecks.cs`, `SeasonBakeChecks.cs`, `CoverageChecks.cs`, `StaticAssetChecks.cs`
- `docs/plans/login-bake-efficiency.md`, `simd-after-getcolor.md`, `neglected-issues-followup.md`, `user-mentioned-unfixed-followup.md`
