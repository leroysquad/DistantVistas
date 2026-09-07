---
tags: [vintage-story, distant-vistas]
aliases: [DV, DistantVistas]
---

# Distant Vistas

Client-side **extended-render-distance LOD** mod for [Vintage Story](https://www.vintagestory.at/). Fork of Vintage Horizons (MIT). Repo: `leroysquad/DistantVistas`.

Related: [[MOC - Vintage Story]] · [[Farseer Companion]] · [[Login Bake & Scouts]] · [[Season Colors & Canopy]]

## What it does

- Builds persistent LOD terrain from chunk data the client already receives — works on vanilla servers.
- Optional server assist (`Universal`, `requiredOnClient: true` when server installs).
- Join **login overlay** paints far land toward the [[Farseer Companion]] silhouette without moving the player ([[Login Bake & Scouts]]).
- Live seasonal tint in shader; visit bake stores block identity + [[Season Colors & Canopy|FlagBaked RGB]] where needed.

See `DESIGN.md` for pipeline: `ChunkDirty` → column RLE → SQLite → quadtree → `lodterrain` shader.

## Version arc 1.0.23 → 1.0.32

| Version | Theme |
|---------|--------|
| **1.0.23** | Sticky empty-mesh remesh; visit disk to Farseer onset; foliage Leaves GetColor path; paint rev 8 |
| **1.0.24** | No paint-revision login teleport; drop Esc-resume when in-window complete would skip |
| **1.0.25** | Pause-on-Start compat; scout fill without hops; Farseer onset +700 + gray smoke; low-ground mist; paint rev 9 |
| **1.0.26** | Fix `lodterrain.fsh` `float flat` → `flatness` (C7537, 0 meshes); bootstrap leftover gate |
| **1.0.27** | `LodScoutEntity` workers — player stays; PoS force-unpause; walk-away green fix; mip keeps FlagBaked canopy |
| **1.0.28** | Readable skyline — ridge ink, lighter mist; gray tent + black tips aesthetic locked |
| **1.0.29** | **Coverage only** — 4075-block disk (4.5×750+700); skip missing-tex white strip; Farseer look unchanged from 1.0.28 |
| **1.0.30** | `LodScoutViewer` player-style stream centers; exact pickup XYZ; spawn-first queue; mesh-gated snapshot; 6→later 16 scouts |
| **1.0.31** | 16 parallel scouts (8 near mesh-wait / 8 far FlagBaked-release); two-tier gate; 1.22.7 `LodVsCompat` |
| **1.0.32** | [[Login Bake Efficiency]] — `LodBakeScratch`, ArrayPool, texture-mean cache, `MaxBakePerTick=24`, Farseer mask debounce |

Current head on audit branches: **1.0.32** (`cursor/1.0.26-catchup-playtest-e27c`).

## Invariants (1.0.30+)

- **No player teleport** during overlay — scouts stream visit cells.
- **Exact pickup X/Y/Z** restored on success, Esc, fail, leave.
- **Spawn-solid 1024** blocks — drawable meshes underfoot before release.
- **4075 disk** — Farseer onset + 700 (`LodLoginBakeViewBoost.SweepVisitRadiusBlocks`).
- **Gray tent + black tips** — Farseer shader overlay pinned in tests.

## Open issues

See [[Neglected Issues Audit]] and [[User-Mentioned Unfixed]].

- 🔴 Skip UX says "complete" with deferred holes ([[Playtest Log - 2026-09-07]])
- 🔴 `DiscoverOnly` may starve far capture after skip
- 📋 [[ValksFuzzyClouds Rain Backlog]] — design only

## Verification

```bash
scripts/check.sh fast   # on machine with VS assemblies
```

Checklist in [[Neglected Issues Audit#Verification checklist (for parent bots)]].

## Agent workflow

[[Pipeline - Cursor Agents]] — do not merge `cursor/bake-efficiency-research-5a8d` wholesale (missing 1.22.7 shim).
