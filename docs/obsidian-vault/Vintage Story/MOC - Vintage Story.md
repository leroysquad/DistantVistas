---
tags: [vintage-story, distant-vistas]
aliases: [Vintage Story MOC, DV MOC]
---

# MOC — Vintage Story / Distant Vistas

Map of content for the **Distant Vistas** Vintage Story LOD mod. Copy this folder to `ObsidianVault/Projects/Vintage Story/`.

## Project hub

- [[Distant Vistas]] — mod overview, repo, version arc 1.0.23–1.0.32
- [[Pipeline - Cursor Agents]] — agent workflow (speed → research/SIMD → review → Mods gate)
- [[Playtest Log - 2026-09-07]] — recent session findings

## Login bake & scouts

- [[Login Bake & Scouts]] — `LodScoutViewer`, no teleport, exact XYZ, mesh dwell rules
- [[Login Bake Efficiency]] — ArrayPool, Span, TPL, GC, SIMD-after-GetColor plan

## Farseer & horizon

- [[Farseer Companion]] — gray tent, black tips, +700 onset, sky gap
- [[ValksFuzzyClouds Rain Backlog]] — precip template design (not implemented)

## Season & colour

- [[Season Colors & Canopy]] — GetColor, frost, 30-day refresh, green flip

## Audits & backlog

- [[Neglected Issues Audit]] — playtest items dropped during bake tunnel vision
- [[User-Mentioned Unfixed]] — user-spoken backlog (Private Citizen)

## Performance sources

- [[Sources - C Sharp Performance]] — MSR / TPL / ArrayPool / SIMD citations

## Primary sources (repo copies)

| Note | Repo path |
|------|-----------|
| Login bake efficiency | `Sources/login-bake-efficiency.md` |
| Neglected issues audit | `Sources/neglected-issues-audit.md` |
| User-mentioned unfixed | `Sources/user-mentioned-unfixed.md` |
| Season colour audit | `Sources/season-color-audit-1.0.19.md` |

## Graph

Open [[Distant Vistas Graph.canvas]] for a visual neuron map.

## Quick links (repo)

- `CHANGELOG.md` — release notes 1.0.23+
- `DESIGN.md` — architecture
- `DistantVistas/src/Render/` — login bake, Farseer, season bake
- `tests/VintageHorizons.Checks/LoginSweepChecks.cs` — gate & overlay pins
