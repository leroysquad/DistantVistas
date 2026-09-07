# User-mentioned unfixed backlog (Private Citizen)

**Base branch:** `cursor/1.0.26-catchup-playtest-e27c` (1.0.32 at audit time)  
**Audit branch:** `cursor/user-mentioned-unfixed-8587`  
**Cross-check:** `docs/plans/neglected-issues-audit.md` on `cursor/neglected-issues-audit-1ef2` (generic playtest audit — this doc is **user-spoken** backlog only)

**Legend:** 🔴 unfixed · 🟡 partial / trade-off · 🟢 fixed · 📋 document-only / back-burner

---

## Executive summary

| Priority | Count | Themes |
|----------|-------|--------|
| 🔴 P0 | 2 | Sky gap / land short of Farseer onset; in-window skip UX (“complete” with holes) |
| 🟡 P1 | 5 | Four-box Farseer rim; ~30-day full-LOD season refresh; DiscoverOnly far drip; expire white strip; bake speed |
| 🟢 P2 | 6 | Tree/bush GetColor; walk-away green; Farseer silhouette; PoS compat; login holes/offset; player teleports |
| 📋 P3 | 2 | ValksFuzzyClouds rain curtain; photorealistic shaders (declined) |

**Small fixes on this branch (orthogonal to scout/SIMD work):**
1. In-window skip notification names deferred incomplete regions (`DistantVistasModSystem.cs`).
2. Parent coverage / gap-fill uses `HasDrawableMesh` not sticky `emptyMeshKeys` (`LodTerrainRenderer.cs`).
3. `ClearMeshes()` resets mesh-pressure latch (`LodTerrainRenderer.cs`).

---

## Item-by-item

### 1. Sky gap between FlagBaked LOD land and Farseer onset

**User ask (paraphrase):** Land/meshes stop short of the Farseer silhouette; the strip reads as white sky instead of smoke/fog.

**Status:** 🟡 **partial** — mitigations shipped; narrow band can still appear when capture envelope or meshed radius lags Farseer onset (~4.5× VD + 700 ≈ 4075 blocks at VD 750).

**Evidence:**
- Login disk targets Farseer onset + 700 (`LodLoginBakeViewBoost.cs`, `CoverageChecks.cs`).
- Farseer shader clamps sky mix, gray tent + black tips (`assets/farseer/shaders/region.fsh`; `StaticAssetChecks.FarseerOverlay`).
- `FarseerVisitOnset.LogHandoffGap` hypotheses H-A/H-E track envelope vs onset vs mesh distance (`FarseerVisitOnset.cs`).
- Post-login `DiscoverOnly` + frontier drip may leave outer ring unpainted for minutes (`LodPipeline.cs`, `FarCoverageDiag` H-S3).

**Next fix priority:** **P0** — telemetry when `gapMeshToOnset > 256`; consider raising far-ready % or scout ring clamp after skip.

---

### 2. Four cardinal-box Farseer rim plan

**User ask:** Explicitly back-burnered architectural plan (four region tiles vs DV disk).

**Status:** 📋 **back-burner** — no implementation; mitigations only.

**Evidence:**
- Single Farseer region overlay + visit-onset mask (`FarseerVisitOnset.cs`).
- `LodCloudHorizon` stretches clouds toward rim.
- Neglected audit: “Add `docs/farseer-sky-gap.md`” — not written on base branch.

**Next fix priority:** **P3** — document expected narrow band + four-tile sketch only; no code unless tiny safe hook.

---

### 3. Tree/bush season + frost colors wrong while ground correct

**User ask:** Pure GetColor / color maps at canopy Y; CrownSide / ApplyTop issues.

**Status:** 🟢 **fixed** (1.0.19–1.0.27) · monitor in playtest.

**Evidence:**
- `LodCanopyGray.ApplyTop` reverted to identity — leaves keep live GetColor + frost (`LodCanopyGray.cs:151–158`).
- `LodMesher.CrownSideBlocks` + `FrostFaceColor` apply frost on walls/crown, not baked into RGB (`LodMesher.cs:44–84`).
- `LodSurfaceMix` / `IsSeasonFoliage` path for leaves + bushes; `FrostSeasonMin = 0.75` (`LodSeasonBake.cs`, `docs/season-color-audit-1.0.19.md`).
- Tests: `SeasonBakeChecks.cs`, `MesherChecks.cs`, `MipChecks.FlagBakedCanopySurvivesMip`.

**Next fix priority:** **P2** — regression watch only; no knob change without in-game A/B.

---

### 4. ~30-day season refresh on all LODs (liked 1.0.23 gray-smoke look)

**User ask:** Full canvas recapture on expire, gray-smoke Farseer look across LODs.

**Status:** 🟡 **partial** — expire path exists; not guaranteed same visual on every LOD rung in one session.

**Evidence:**
- `LodLoginSweepBootstrap.PlanSeasonExpired` + `LodLoginSweepWindow.RecaptureReason` (30 in-game days, 30 wall-clock days, calendar month).
- `PaintRevision = 9` bumps alone no longer force teleport (`LodLoginSweepWindow.cs:87–93`).
- Live tint refresh is incremental (`LodTerrainRenderer.RefreshSeasonalState`, 30 s lattice) — not a full rebake.
- Farseer gray-smoke shader pinned in tests (`StaticAssetChecks.FarseerOverlay`).

**Next fix priority:** **P1** — after Mods install, verify expire overlay re-paints far L1/L2 FlagBaked; queue parent remesh on overlay success.

---

### 5. Far land flips green when walking away

**User ask:** Unbaked / FlagBaked / live-tint parent mismatch after walk-back.

**Status:** 🟢 **fixed** (1.0.27+).

**Evidence:**
- `RemeshStaleLiveTintParent` when child has `FlagBaked`, parent still live-tint (`LodTerrainRenderer.cs:1997–2016`).
- Mip preserves FlagBaked canopy RGB (`MipChecks.cs:51–81`).
- Shader band 3 skips live green tint on baked rows (`StaticAssetChecks.cs:159`).

**Next fix priority:** **P2** — queue parent remesh at overlay release, not only during draw descent (residual from neglected audit).

---

### 6. Sticky `emptyMeshKeys` counting as hasMesh — blocks remesh/gap-fill

**User ask:** Empty GPU upload claims block parent coverage and remesh.

**Status:** 🟡 **partial** — split `HasDrawableMesh` vs `HasAnyMesh` landed in 1.0.27; parent coverage paths still consulted `HasAnyMesh` in places.

**Evidence:**
- `emptyMeshKeys` set on zero-index upload (`LodTerrainRenderer.UploadFinishedMeshes`).
- `ClearEmptyMeshClaim` on `MarkChanged` / `RequestGpuSwap` (`LodWorld.cs:376–395`).
- `AllChildrenCovered` uses `HasDrawableMesh` for children (`LodTerrainRenderer.cs:1104`).
- **This branch:** parent gap-fill / `PreferParentCoverage` / `SkipDrawTooFine` use `HasDrawableMesh` so empty claims cannot fake coverage.

**Next fix priority:** **P1** — merged on this branch; watch `FarCoverageDiag` H-E in playtest.

---

### 7. Farseer silhouette too sky-blended / ink / thick low smoke

**User ask:** Darken tips, lighten + thin mist (1.0.28 claimed).

**Status:** 🟢 **fixed** (1.0.28–1.0.30); 1.0.29 explicitly kept 1.0.28 look.

**Evidence:**
- `region.fsh`: ridge ink, paler thinner mist, gray tent body, black tips, less low-ground smoke (`assets/farseer/shaders/region.fsh:10–80`).
- `StaticAssetChecks.FarseerOverlay` pins strings (no 0.78 ink wall, no onsetMist lean).
- CHANGELOG 1.0.28–1.0.30.

**Next fix priority:** **P2** — verify in playtest only; do not rebalance without user sign-off.

---

### 8. Pause-on-Start real compat with mod installed

**User ask:** Force-unpause was workaround; PoS may not be in Mods folder.

**Status:** 🟢 **implemented** · 🟡 Esc path semantics.

**Evidence:**
- `LodPauseOnStartCompat.KeepUnpaused` during overlay; `RestoreAfterLoginBake` on success/skip (`LodPauseOnStartCompat.cs`).
- Mod id `pauseonstart`; safe when absent (`IsInstalled` guard).
- Esc cancel does **not** call `RestoreAfterLoginBake` by design (CHANGELOG 1.0.27).
- Tests: `LoginSweepChecks.cs:363–364`, `423–424`.

**Next fix priority:** **P2** — document Esc leaves game unpaused; fragile `TryOpenIngameMenu` GUI scan.

---

### 9. Login “Loading…” stuck after visit-sweep handover (0.8.x)

**User ask:** Rejoin deadlock after handover.

**Status:** 🟢 **fixed** (0.8.21+); residual risk only during character/class UI.

**Evidence:**
- `LodLoginBakeCharacterWait.IsPending` is dialog-only, not `!PlayerReadyFired` (`LodLoginBakeCharacterWait.cs:14–19`).
- CHANGELOG 0.8.21, 0.8.26–0.8.28 black-screen / handover fixes.
- Tests: `LoginSweepChecks.CharacterWait`.

**Next fix priority:** **P3** — regression watch; no open repro on 1.0.32 tree.

---

### 10. ValksFuzzyClouds distant rain curtain

**User ask:** Scout-sample precip → tag cloud → template until dry — **backlog design only**.

**Status:** 📋 **not in repo** — external mod; no references to Valks/FuzzyClouds.

**Evidence:**
- DV patches `CloudRenderer` / `FluffyClouds` via `LodCloudHorizon.cs` only.
- Neglected audit: document mod matrix.

**Next fix priority:** **P3** — design note only; do not implement unless tiny hook appears.

---

### 11. Photorealistic shaders — companion-only, not baked into DV

**User ask:** User declined baking photorealistic shaders into DV.

**Status:** 🟢 **confirmed absent**.

**Evidence:**
- Repo grep: no `photorealistic` / `Photorealistic` in source or assets.
- Farseer overlay is MIT region shader + DV markers (`DV_FARSEER_OVERLAY`), not a photorealistic pack.

**Next fix priority:** **P3** — none.

---

### 12. Player teleports during bake / scout entities not used

**User ask:** Player hopped during overlay; wanted scout entities.

**Status:** 🟢 **fixed** (1.0.30–1.0.32).

**Evidence:**
- `LodLoginScoutFill` + `LodScoutEntity` stream visit cells; player stays at pickup (`LodLoginBake.cs:721–722`, `1096–1099`).
- `FreezePickupPose` / `ApplyExactPickup` every overlay frame.
- Tests: `LoginSweepChecks` — no `ApplyQuiet` hops, no “quiet teleports begin”.
- `RestorePlayerPose` uses pickup doubles only at end/Esc.

**Next fix priority:** **P3** — verify sibling tip branches do not reintroduce hops before merge.

---

### 13. Login holes / offset from spawn / snapshot too early

**User ask:** Claimed fixed 1.0.30 — verify.

**Status:** 🟢 **fixed** (1.0.30–1.0.32) · 🟡 90 s timeout escape.

**Evidence:**
- Spawn-first visit budget ~⅔ inner 1024 m (`LodLoginSweepBootstrap.cs`).
- Pickup XYZ pinned; separate `SweepLaneSpawn` / `SweepLaneScout`.
- `TickStabilizing`: spawn drawable == 0, far ≥ 75% of 4075, drain empty, frame-time window (`LodLoginBake.cs:1301–1332`).
- Removed 3 s stabilize shortcut (CHANGELOG 1.0.30).
- **90 s `SpawnReadyTimeoutSec`** can still release with holes.

**Next fix priority:** **P2** — telemetry on timeout-forced release.

---

### 14. Skip 30-day complete with gaps

**User ask:** Hated incomplete canvas labeled complete.

**Status:** 🔴 **UX unfixed** · gate behaviour intentional.

**Evidence:**
- `LodLoginSweepGate.Decide` skips when `VisitedKeyCount >= visited` even if `FindMisses` > 0 (`LodLoginSweepGate.cs:63–75`).
- CHANGELOG 0.8.22 / 1.0.29: leftovers are post-login frontier drip.
- Notification previously said only “visited canvas complete within 30-day window”.
- **This branch:** appends “(N region(s) still incomplete — post-login frontier drip will fill)” when misses exist.

**Next fix priority:** **P0** — messaging fix merged here; product decision still open on strict skip threshold.

---

### 15. Bake too slow (86/1680)

**User ask:** Sibling efficiency work; note only.

**Status:** 🟡 **in flight elsewhere** — 1.0.32 improved scouts/GetColor GC; full SIMD-after-GetColor not done.

**Evidence:**
- `docs/plans/login-bake-efficiency.md` — 16 parallel scouts, `LodBakeScratch`, ArrayPool.
- CHANGELOG 1.0.31–1.0.32.
- Do not duplicate on this branch.

**Next fix priority:** **P1** — track `cursor/bake-efficiency-research-*` / speed bots.

---

## Do-not-duplicate (in-flight)

- SIMD after GetColor (`BlurLand` / `Quantize`).
- `MaxBakePerTick`, parallel scout tuning, near mesh-wait / far FlagBaked-release gates.
- Full four-box Farseer rim implementation.
- ValksFuzzyClouds rain feature.
- Photorealistic shader bake-in.
- Reintroducing player visit teleports.

---

## Verification checklist (parent before Mods install)

1. Cold login, expired canvas → full overlay, 16 scouts, **no player teleport**.
2. In-window rejoin → skip notification mentions **deferred region count** if any.
3. After overlay/skip → solid spawn 1024, Farseer gray/black silhouette, minimal white join strip.
4. Walk away → no green flip on FlagBaked canopy.
5. Pause-on-Start installed → overlay ticks; menu returns after success/skip.
6. Esc mid-overlay → resume or drop per gate; exact pickup XYZ on return.
7. `scripts/check.sh fast` green on VS assembly machine.

---

## References

- `docs/plans/neglected-issues-audit.md` (`cursor/neglected-issues-audit-1ef2`)
- `docs/plans/login-bake-efficiency.md`
- `docs/season-color-audit-1.0.19.md`
- `CHANGELOG.md` 1.0.26–1.0.32
- `tests/VintageHorizons.Checks/LoginSweepChecks.cs`
- `tests/VintageHorizons.Checks/StaticAssetChecks.cs`
- `tests/VintageHorizons.Checks/SeasonBakeChecks.cs`
