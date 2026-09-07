---
tags: [vintage-story, distant-vistas, backlog]
aliases: [User backlog, Private Citizen backlog]
---

# User-Mentioned Unfixed

**User-spoken** backlog distilled from playtest feedback (Private Citizen). Cross-check [[Neglected Issues Audit]] for generic playtest audit — this doc is **user voice only**.

**Source branch:** `cursor/user-mentioned-unfixed-8587`  
**Base:** `cursor/1.0.26-catchup-playtest-e27c` (1.0.32)  
Full doc: [[Sources/user-mentioned-unfixed]]

Related: [[MOC - Vintage Story]] · [[Farseer Companion]] · [[Login Bake & Scouts]]

**Legend:** 🔴 unfixed · 🟡 partial · 🟢 fixed · 📋 back-burner

## Executive summary

| Priority | Count | Themes |
|----------|-------|--------|
| 🔴 P0 | 2 | Sky gap; skip "complete" with holes |
| 🟡 P1 | 5 | Four-box rim; 30-day season refresh; DiscoverOnly; expire white strip; bake speed |
| 🟢 P2 | 6 | Tree/bush GetColor; green flip; Farseer silhouette; PoS; holes/offset; teleports |
| 📋 P3 | 2 | [[ValksFuzzyClouds Rain Backlog]]; photorealistic shaders (declined) |

## Item index

| # | Topic | Status | Note |
|---|-------|--------|------|
| 1 | Sky gap at Farseer onset | 🟡 | [[Farseer Companion#Sky gap]] |
| 2 | Four-box Farseer rim | 📋 | Back-burner architecture |
| 3 | Tree/bush season + frost | 🟢 | [[Season Colors & Canopy]] |
| 4 | ~30-day season refresh all LODs | 🟡 | `PlanSeasonExpired`; live tint incremental |
| 5 | Far land green when walking away | 🟢 | 1.0.27+ `RemeshStaleLiveTintParent` |
| 6 | Sticky `emptyMeshKeys` | 🟡 | Split `HasDrawableMesh` vs `HasAnyMesh` |
| 7 | Farseer silhouette ink/smoke | 🟢 | 1.0.28–1.0.30 |
| 8 | Pause-on-Start compat | 🟢 | `LodPauseOnStartCompat` |
| 9 | Loading stuck after handover | 🟢 | 0.8.21+ |
| 10 | ValksFuzzyClouds rain | 📋 | [[ValksFuzzyClouds Rain Backlog]] |
| 11 | Photorealistic shaders | 🟢 absent | User declined bake-in |
| 12 | Player teleports during bake | 🟢 | [[Login Bake & Scouts#No player teleport]] |
| 13 | Login holes / offset / early snapshot | 🟢 | 1.0.30+; 🟡 90s timeout |
| 14 | Skip 30-day complete with gaps | 🔴 UX | Gate intentional; messaging fix on audit branch |
| 15 | Bake too slow (86/1680) | 🟡 | [[Login Bake Efficiency]] / [[Pipeline - Cursor Agents]] |

## 🔴 P0 detail

### Skip "complete" with gaps

User hated incomplete canvas labeled complete. `LodLoginSweepGate` skips when visited count met even if `FindMisses > 0`. Audit branch appends: *"(N region(s) still incomplete — post-login frontier drip will fill)"*.

### Sky gap

Land stops short of Farseer silhouette → white sky strip. Mitigations in 1.0.29+; narrow band may remain. Telemetry when `gapMeshToOnset > 256`.

## 🟡 P1 detail

- **30-day refresh:** expire overlay exists; not guaranteed same visual on every LOD rung in one session. `PaintRevision = 9` no longer forces teleport.
- **Bake speed:** track `cursor/bake-efficiency-research-*` — do not duplicate on audit branch.

## Small fixes on audit branch

1. Skip notification deferred region count.
2. Parent coverage uses `HasDrawableMesh` not sticky `emptyMeshKeys`.
3. `ClearMeshes()` resets mesh-pressure latch.

## Do-not-duplicate

- SIMD after GetColor
- Full four-box Farseer implementation
- ValksFuzzyClouds rain feature
- Player visit teleports

## Verification (before Mods install)

Same checklist as [[Neglected Issues Audit#Verification checklist (for parent bots)]].

## References

- `docs/plans/user-mentioned-unfixed.md`
- `docs/plans/neglected-issues-audit.md`
- `CHANGELOG.md` 1.0.26–1.0.32
