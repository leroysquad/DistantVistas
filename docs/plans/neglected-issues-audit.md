# Neglected issues audit (1.0.26–1.0.32 catch-up)

**Branch audited:** `cursor/1.0.26-catchup-playtest-e27c` (HEAD **1.0.32** at audit time)  
**Audit branch:** `cursor/neglected-issues-audit-1ef2`  
**Related in-flight:** `cursor/bake-efficiency-research-5a8d` (merged ideas into 1.0.32; do not merge that branch wholesale — see `docs/plans/login-bake-efficiency.md`)

**Scope:** Playtest complaints that may have been dropped while work tunnel-visioned on login-bake scouts, GetColor GC, SIMD-after-GetColor research, near/far gates, and overlay speed. This doc is an honest “what we forgot” report for parent review/speed bots before Mods install.

**Legend:** 🔴 still broken · 🟡 partial / by-design trade-off · 🟢 fixed · 📋 document-only / back-burner

---

## Executive summary

| Priority | Count | Themes |
|----------|-------|--------|
| 🔴 P0 | 2 | Misleading “complete” skip UX; post-login `DiscoverOnly` can starve far capture |
| 🟡 P1 | 6 | Deferred holes after overlay; palette repair storms; expire-leftover scan; 90s overlay timeout escape; expire-recapture white strip; managed RAM after overlay |
| 🟢 P2 | 6 | Spawn disk centering, overlay snapshot gate, GPU mip drain, walk-away green, missing-tex scouts, 1.22.7 compile |
| 📋 P3 | 2 | Four-box Farseer sky-gap; ValksFuzzyClouds rain backlog |

**Recent focus verified in tree:** 16 parallel scouts, no player teleport, exact pickup XYZ, bake-while-streaming, near mesh-wait / far FlagBaked-release, separate `SweepLaneSpawn`/`SweepLaneScout`, `RequestGpuSwap` mip drain (not dispose-first), gray tent + black tips, Farseer onset +700 (4075 disk), `LodVsCompat` for VS 1.22.7.

**Small fixes on this audit branch (orthogonal to speed work):**
1. Skip notification names deferred incomplete regions when in-window skip runs (`DistantVistasModSystem.cs`).
2. `ClearMeshes()` resets `MeshPressureActive` so leave-world does not inherit pressure latch (`LodTerrainRenderer.cs`).
3. Test drift: `ExploreBakeChecks` `guard` variable; `LoginSweepChecks` `PaintRevision` 8→9.

---

## Ranked findings

### 🔴 P0 — Misleading in-window skip (“30-day complete” with holes)

**Status:** 🔴 UX / semantics — gate behaviour is intentional; messaging still implies completeness.

**Playtest:** 1.0.29 skip with **122 unfilled gaps** while notification says canvas complete.

**Evidence:**
- Gate skips **before** `FindMisses` when `VisitedKeyCount >= visited` and window holds (`LodLoginSweepGate.cs:63–75`).
- CHANGELOG 0.8.22 / 1.0.29: leftovers are **post-login frontier drip**, not overlay failure (`CHANGELOG.md:32–33`, `366–367`).
- Sweep can stamp `RecordSuccess` after resweep cap even if audit misses remain (`LodLoginBake.cs:912–918`, `1387–1390`).
- Renderer `LastUnfilledGaps` is a separate counter (no mesh at any LOD rung) — logged on Esc (`LodTerrainRenderer.cs:1703+`, `LodLoginBake.cs:208`).

**Follow-up:**
- ✅ Audit branch: append miss count to skip notification when `FindMisses` > 0.
- Consider telemetry field `deferredMissesAtSkip` in session JSON.
- Product decision: whether in-window skip should ever run when `FindMisses` or `LastUnfilledGaps` exceeds a threshold (currently **no** per 0.8.22).

---

### 🔴 P0 — `DiscoverOnly` + low `explorePending` yield after skip/sweep

**Status:** 🔴 partial — throttles exist; far land can look “stuck” for minutes after fast overlay skip.

**Evidence:**
- Skip and successful sweep both set `DiscoverOnly = true` (`DistantVistasModSystem.cs:1608–1613`, `LodLoginBake.cs:1365–1372`).
- `DiscoverOnly` caps apply budget to 1, limits recapture radius, pauses on capture backlog (`LodPipeline.cs:351–357`, `534–537`, `1050`).
- Frontier scout yields when `ExploreBake.PendingCount > 4` (`LodFrontierScout.cs:21`, `83–86`).
- `FarCoverageDiag` hypothesis **H-S3**: DiscoverOnly starves far capture (`FarCoverageDiag.cs:92–103`).
- Mitigation on sweep release: `PurgeUnloadedPendingColumns()` (`LodLoginBake.cs:1371`, `LodPipeline.cs:249+`).

**Follow-up (speed agents may touch):**
- Log `explorePending` on skip-path NDJSON (success path already logs at `LodLoginBake.cs:1447`).
- Revisit `MaxExplorePendingYield = 4` vs 16 scout parallelism.
- Fix test drift: `ExploreBakeChecks.cs:91` expected `remaining` but code uses `guard` (`LodExploreBake.cs:191`).

---

### 🟡 P1 — Login overlay ends with deferred holes (by design)

**Status:** 🟡 partial — not a regression; 420s / ~7 min budget + 4075 disk cannot mesh every L0.

**Evidence:** CHANGELOG 1.0.29 (`CHANGELOG.md:32–33`); `LodFrontierScout` post-login drip; resweep cap continues anyway (`LodLoginBake.cs:912–918`).

**Follow-up:** Frontier drip metrics in HUD/telemetry; optional config “strict complete before skip stamp”.

---

### 🟡 P1 — `DrainLoginMip` / GPU invalidate before swap

**Status:** 🟢 fixed in 1.0.30+ · 🟡 dead API remains

**Evidence:**
- `DrainLoginMip` → `ProcessPropagation(..., RequestGpuSwap)` (`LodPipeline.cs:1272–1275`).
- `RequestGpuSwap` marks dirty + force remesh, no dispose (`LodWorld.cs:378–387`).
- `InvalidateGpuMesh` only on season-wide refresh (`LodTerrainRenderer.cs:3385–3397`); pipeline delegate assigned but unused on login path.
- Tests: `LoginSweepChecks.cs:997–1000`, `ExploreBakeChecks.cs:43–44`.

**Follow-up:** Remove or document `pipeline.InvalidateGpuMesh` as season-only.

---

### 🟢 P2 — Spawn-offset baked disk / not centered on player

**Status:** 🟢 fixed (1.0.30–1.0.32)

**Evidence:**
- Spawn-first visit budget ~⅔ inner 1024 m (`LodLoginSweepBootstrap.cs:85–89`, `599–645`).
- Pickup XYZ pinned every GUI frame (`LodLoginBake.cs:392–433`); scouts use viewer entities, player stays (`LodLoginScoutFill.cs:118–140`).
- Separate `SweepLaneSpawn` / `SweepLaneScout` (`LodPipeline.cs:498–522`).
- 4075 disk = Farseer onset +700 (`LodLoginBakeViewBoost.cs:83–89`).
- Tests: `LoginSweepChecks.cs:1057–1214`.

**Residual:** `PlanRevisitIncomplete` subsamples from **live** player pos, not frozen pickup (`LodLoginSweepBootstrap.cs:343–351`) — edge case for mid-game incomplete repair after movement.

---

### 🟢 P2 — Early overlay snapshot before land/smoke/background visible

**Status:** 🟢 fixed (1.0.30); 🟡 90s escape hatch

**Evidence:**
- Removed 3s stabilize timeout (`LodLoginBake.cs:44–48`, CHANGELOG 1.0.30).
- `TickStabilizing` requires: spawn drawable (`CountMissingSpawnDrawable` == 0), far ≥75% of 4075, drain empty, 4×90-frame ≤28 ms (`LodLoginBake.cs:1301–1332`).
- Mesh schedule/upload under splash; GPU draw gated separately (`LoginSweepChecks.cs:355–362`).
- `BeginDraining()` no longer hides splash early (`LodLoginBake.cs:1258–1259`).
- **90s timeout** can still release with holes (`1314–1320`) — intentional for stuck machines.

**Follow-up:** Telemetry when `SpawnReadyTimeoutSec` forces release.

---

### 🟢 P2 — Walk-away green flip / `FlagBaked` / sticky `hasMesh`

**Status:** 🟢 fixed (1.0.27+)

**Evidence:**
- `RemeshStaleLiveTintParent` when child FlagBaked, parent live-tint (`LodTerrainRenderer.cs:1168–1169`, `1997–2016`).
- Mip preserves FlagBaked canopy RGB (`MipChecks.cs:51–81`; CHANGELOG 1.0.27).
- Drawable vs empty-claim split: `HasDrawableMesh` vs `HasAnyMesh`; `ClearEmptyMeshClaim` on force remesh (`LodTerrainRenderer.cs:684–706`, `LodWorld.cs:372–395`).
- `FarCoverageDiag` H-E monitors sticky claims (`FarCoverageDiag.cs:89–96`).

**Follow-up:** Queue parent remesh on overlay success, not only during draw descent.

---

### 🟢 P2 — Missing-tex near-white / white sky-strip at land–Farseer join

**Status:** 🟢 fixed on login/scout path · 🟡 expire-recapture exception

**Evidence:**
- Scouts skip bake when map chunks never arrived (`LodLoginScoutFill.cs:174–181`; test `LoginSweepChecks.cs:546–549`).
- Batch bake skips unloaded maps (`LodLoginBake.cs:1027–1030`, `1040–1045`).
- `LodPaletteRepair` near-white / sky-missing guards (`LodPaletteRepair.cs:14–76`).
- Farseer shader: no sky ring bleach (`StaticAssetChecks.cs:225–287`; CHANGELOG 1.0.29).
- **Exception:** `AllowExpireNoMapSample` during month expire recapture (`LodSeasonBake.cs:142–147`, `591–610`; enabled in `LodLoginBake.cs:1085–1088`).

**Follow-up:** Gate expire samples with `IsMissingTextureWhite` before write (mirror scout skip).

---

### 🟡 P1 — Pause-on-Start real compat

**Status:** 🟢 implemented · 🟡 Esc path semantics

**Evidence:**
- `LodPauseOnStartCompat.KeepUnpaused` during overlay; `RestoreAfterLoginBake` on success/skip (`LodPauseOnStartCompat.cs:22–36`; `LodLoginBake.cs:350`, `1414–1416`; `DistantVistasModSystem.cs:1592`, `1618`).
- Esc cancel: `RestoreAfterLoginBake` **not** called by design (`LodPauseOnStartCompat.cs:29–30`; CHANGELOG 1.0.27).
- Tests: `LoginSweepChecks.cs:363–364`, `423–424`, `509–512`.

**Follow-up:** Document Esc leaves game unpaused; fragile `TryOpenIngameMenu` GUI name scan.

---

### 🟢 P2 — Esc cancel/resume scout keys; bootstrap resume leftover stops

**Status:** 🟢 fixed (1.0.26–1.0.32)

**Evidence:**
- Esc → snapshot with scout keys (`LodLoginBake.cs:194–229`, `1671–1694`).
- `ShouldDropLeftoverResume` when in-window complete would skip (`LodLoginSweepGate.cs:32–37`, `98–110`).
- Oversized resume replan (`LodLoginBake.cs:297–304`).
- Expire leftovers drained at tick budget (`LodLoginBake.cs:1158–1219`).
- Tests: `LoginSweepChecks.cs:502–506`, `822–845`, `928–929`.

**Follow-up:** Cap `CollectExpireLeftovers` scan scope on large caches (can queue thousands unrelated to scout progress).

---

### 🟡 P1 — Palette “no colour” black repair storms (~3419 entries)

**Status:** 🟡 mechanism OK; first-load scale risk

**Evidence:**
- `NeedsColor(0)` + load-time `RefreshStoredPalette` (`LodPaletteRepair.cs:14–15`; `DistantVistasModSystem.cs:828–871`).
- Each repair → `MarkChanged` + `PaletteEntriesRepaired++` (`LodPipeline.cs:908–911`).
- Session log: “repaired N palette entries…” (`DistantVistasModSystem.cs:1830–1835`).
- CHANGELOG root cause: black ground from unresolved block codes (`CHANGELOG.md:853–869`).
- Tool: `scripts/scan-cache-palettes.py`.
- Tests: `StoreChecks.cs:237–268`.

**Follow-up:** Batch repair with throttled remesh; one-shot migration; remove hardcoded Windows debug path in `RefreshStoredPalette` (`DistantVistasModSystem.cs:860`).

---

### 🟡 P1 — Perf outside bake: leave-world mesh pressure, managed GB after overlay

**Status:** 🟡 partial

**Evidence:**
- Mesh pressure hysteresis (`LodTerrainRenderer.cs:2116–2198`); eviction only when pressure + not overlay (`2883–2885`).
- Leave-world: `ClearMeshes()`, pipeline close, drain mesh results (`DistantVistasModSystem.cs:1927–1975`).
- **~7 GB managed growth in ~30 s** during bake (`docs/plans/login-bake-efficiency.md:24`).
- `MeshPressureActive` was **not** reset in `ClearMeshes()` — fixed on audit branch.

**Follow-up:** Post-overlay scratch pool release + managed MB telemetry; throttle cold section install after overlay (`InstallBudgetMs = 2.0`).

---

### 📋 P3 — Four-box Farseer / sky-gap

**Status:** 📋 back-burnered — mitigations exist, no “four-box” doc

**Evidence:**
- Farseer onset 4.5× + 700 (`CoverageChecks.cs:195–207`; `FarseerVisitOnset.cs`).
- `LodCloudHorizon` stretches clouds to rim (`LodCloudHorizon.cs`; CHANGELOG 1.0.21).
- `FarseerFlickerDiag` for silhouette flicker.
- CHANGELOG 1.0.29 verify: “no big empty/white gap”.

**Follow-up:** Add `docs/farseer-sky-gap.md` (four region tiles vs DV disk, expected narrow band, cloud horizon).

---

### 📋 P3 — ValksFuzzyClouds rain backlog

**Status:** 📋 not in repo — external mod interaction

**Evidence:** No references to Valks/FuzzyClouds. DV patches `CloudRenderer` / `FluffyClouds` only (`LodCloudHorizon.cs:40–41`).

**Follow-up:** Document mod matrix (load order, rain particle stacking, workaround).

---

### 🟢 P2 — 1.22.7 compile breaks (`UpdatePartitioning`, `LoadedEntities`)

**Status:** 🟢 fixed via `LodVsCompat`

**Evidence:**
- Reflection shim (`LodVsCompat.cs:9–91`); used by scouts, player move, server despawn.
- CHANGELOG 1.0.31–1.0.32; efficiency doc warns old branch used direct API.
- Tests: `LoginSweepChecks.cs:644–694`.

**Follow-up:** Log once if reflection misses on future VS versions (currently silent `catch`).

---

## Season / tree / bush GetColor + frost vs ground; far land greening

**Status:** 🟢 mostly fixed (1.0.19–1.0.27) · monitor walk-away

| Area | File | Notes |
|------|------|-------|
| Frost gate | `LodSeasonBake.cs:252–290` | `FrostSeasonMin = 0.75`; late autumn only |
| Tree/bush foliage | `LodCanopyGray.cs`, `LodSurfaceMix.cs:297–328` | Leaves + bush paths; autumn keeps live GetColor |
| Walk-away green | `LodTerrainRenderer.cs:1168–2016` | `RemeshStaleLiveTintParent` |
| Audit matrix | `docs/season-color-audit-1.0.19.md` | Mid-autumn / frost / spring |
| Tests | `SeasonBakeChecks.cs` | Frost gate, bush GetColor, FlagFrost bits |

**Follow-up:** Test drift `PaintRevision` 8→9 in `LoginSweepChecks.cs:895`; strengthen parent remesh queue after overlay.

---

## Cross-cutting hygiene (not playtest items but neglected)

| Item | Severity | Location |
|------|----------|----------|
| Hardcoded Windows NDJSON paths | Low | `LodLoginSweepGate.cs:137+`, many agent-log regions |
| `PaintRevision` test expects 8, code is 9 | Low | `LoginSweepChecks.cs:895` vs `LodSurfaceMix.cs:23` |
| `ExploreBakeChecks` `remaining` vs `guard` | Low | `ExploreBakeChecks.cs:91` vs `LodExploreBake.cs:191` |
| `dotnet` / VS assemblies not in audit VM | Info | Run `scripts/check.sh fast` on game machine |

---

## Do-not-duplicate (in-flight elsewhere)

Assume these are actively worked unless audit proves regression:

- SIMD after GetColor (`BlurLand` / `Quantize`) — noted in efficiency plan as not done.
- `MaxBakePerTick`, ArrayPool, `LodBakeScratch`, parallel 16 scouts — 1.0.32.
- Near mesh-wait vs far FlagBaked-release gates — 1.0.31.
- Quieter low smoke, gray tent + black tips — 1.0.28–1.0.30.

---

## Verification checklist (for parent bots)

1. Cold login, expired canvas → full overlay, 16/16 scouts, no teleport.
2. In-window rejoin → skip notification mentions deferred regions if any.
3. After overlay/skip → solid spawn 1024, Farseer gray/black silhouette, no white join strip.
4. Walk away → no green flip on FlagBaked canopy.
5. Pause-on-Start → overlay ticks, menu returns after success/skip.
6. Esc mid-overlay → resume or drop per gate; exact pickup XYZ on return.
7. Leave world → next join clean; mesh pressure not latched from prior world.
8. `scripts/check.sh fast` green on VS assembly machine.

---

## References

- `CHANGELOG.md` — 1.0.26 through 1.0.32
- `docs/plans/login-bake-efficiency.md` — speed tier status
- `docs/season-color-audit-1.0.19.md` — season matrix
- `tests/VintageHorizons.Checks/LoginSweepChecks.cs` — gate, overlay, compat pins
- `tests/VintageHorizons.Checks/ExploreBakeChecks.cs` — pipeline hooks
- `tests/VintageHorizons.Checks/SeasonBakeChecks.cs` — frost / foliage
- `tests/VintageHorizons.Checks/StoreChecks.cs` — palette repair
