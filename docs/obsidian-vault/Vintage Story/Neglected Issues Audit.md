---
tags: [vintage-story, distant-vistas, audit]
aliases: [Neglected audit, Playtest audit]
---

# Neglected Issues Audit

Playtest complaints that may have been dropped while work focused on login-bake scouts, GetColor GC, SIMD research, and overlay speed.

**Source branch:** `cursor/neglected-issues-audit-1ef2`  
**Audited base:** `cursor/1.0.26-catchup-playtest-e27c` (1.0.32)  
Full doc: [[Sources/neglected-issues-audit]]

Related: [[User-Mentioned Unfixed]] · [[MOC - Vintage Story]] · [[Playtest Log - 2026-09-07]]

**Legend:** 🔴 broken · 🟡 partial / trade-off · 🟢 fixed · 📋 back-burner

## Executive summary

| Priority | Count | Themes |
|----------|-------|--------|
| 🔴 P0 | 2 | Misleading skip UX; `DiscoverOnly` starves far capture |
| 🟡 P1 | 6 | Deferred holes; palette repair storms; expire scan; 90s timeout; white strip; post-overlay RAM |
| 🟢 P2 | 6 | Spawn centering, snapshot gate, GPU mip drain, green flip, missing-tex scouts, 1.22.7 |
| 📋 P3 | 2 | Four-box Farseer sky-gap; [[ValksFuzzyClouds Rain Backlog]] |

## 🔴 P0 — Misleading in-window skip

**Playtest:** 1.0.29 skip with **122 unfilled gaps** while notification says canvas complete.

- Gate skips before `FindMisses` when `VisitedKeyCount >= visited` (`LodLoginSweepGate.cs`).
- Leftovers are **post-login frontier drip** by design (`CHANGELOG` 0.8.22 / 1.0.29).
- Audit branch fix: append miss count to skip notification.

**Follow-up:** telemetry `deferredMissesAtSkip`; product decision on strict skip threshold.

## 🔴 P0 — DiscoverOnly + low explorePending

After skip/sweep, `DiscoverOnly = true` caps apply budget, limits recapture radius.

- Frontier scout yields when `ExploreBake.PendingCount > 4`.
- `FarCoverageDiag` H-S3: DiscoverOnly starves far capture.

**Follow-up:** log `explorePending` on skip; revisit `MaxExplorePendingYield = 4` vs 16 scouts.

## 🟡 P1 highlights

| Issue | Notes |
|-------|-------|
| Deferred holes after overlay | 420s / ~7 min budget + 4075 disk cannot mesh every L0 — by design |
| `DrainLoginMip` / GPU swap | ✅ Fixed 1.0.30+ — `RequestGpuSwap` not dispose-first |
| Palette repair storms | ~3419 entries on first load — `LodPaletteRepair`, throttle remesh |
| Post-overlay ~7 GB managed | See [[Login Bake Efficiency]] |
| 90s `SpawnReadyTimeoutSec` | Can release with holes — intentional |

## 🟢 P2 fixed (verify in playtest)

- Spawn-offset disk → spawn-first budget + pickup XYZ ([[Login Bake & Scouts]])
- Early snapshot → `TickStabilizing` gates ([[Login Bake & Scouts#Snapshot / stabilize rules]])
- Walk-away green → `RemeshStaleLiveTintParent` ([[Season Colors & Canopy]])
- Missing-tex white strip → scout skip unloaded maps ([[Farseer Companion#Sky gap]])
- Esc/resume scout keys → 1.0.26–1.0.32
- 1.22.7 compile → `LodVsCompat` reflection shim

## 📋 P3 back-burner

- **Four-box Farseer rim** — mitigations only; no `docs/farseer-sky-gap.md` yet
- **ValksFuzzyClouds** — external mod; see [[ValksFuzzyClouds Rain Backlog]]

## Season / foliage cross-ref

🟢 Mostly fixed 1.0.19–1.0.27 — see [[Season Colors & Canopy]] and [[Sources/season-color-audit-1.0.19]].

## Do-not-duplicate (in-flight)

- SIMD after GetColor — [[Login Bake Efficiency#Technique 4 — SIMD after GetColor (not done)]]
- 1.0.32 scout/GetColor work — [[Login Bake Efficiency]]
- Farseer gray/black look — [[Farseer Companion]]

## Verification checklist (for parent bots)

1. Cold login, expired canvas → full overlay, 16/16 scouts, no teleport.
2. In-window rejoin → skip notification mentions deferred regions if any.
3. After overlay/skip → solid spawn 1024, Farseer gray/black silhouette, no white join strip.
4. Walk away → no green flip on FlagBaked canopy.
5. Pause-on-Start → overlay ticks; menu returns after success/skip.
6. Esc mid-overlay → resume or drop per gate; exact pickup XYZ.
7. Leave world → mesh pressure not latched.
8. `scripts/check.sh fast` green.

## Small fixes on audit branch

1. Skip notification names deferred incomplete regions (`DistantVistasModSystem.cs`).
2. `ClearMeshes()` resets `MeshPressureActive` (`LodTerrainRenderer.cs`).
3. Test drift: `ExploreBakeChecks` `guard`; `LoginSweepChecks` `PaintRevision` 8→9.

## References

- `docs/plans/neglected-issues-audit.md`
- `CHANGELOG.md` 1.0.26–1.0.32
- `tests/VintageHorizons.Checks/LoginSweepChecks.cs`
