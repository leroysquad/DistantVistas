# Distant Vistas — Complete Documentation Bundle (1.0.32)

Save to: `C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\docs\`

Author: IllLeroySquad · Fork: Vintage Horizons (AliasFactory, MIT) · VS 1.22.5–1.22.7

---

# PART 1 — ALL NUMBERS (GROK-HANDOFF)

## Authorship summary

| | Lines |
| --- | ---: |
| **IllLeroySquad** (all Cursor Agent commits = user-directed) | **28,852** |
| **AliasFactory** (Vintage Horizons foundation) | **18,575** |
| **Total repository** | **47,427** |

AliasFactory mod C# foundation only: **9,134 lines** (~9,000).

| Git author | Lines |
| --- | ---: |
| IllLeroySquad / leroysquad / Private Citizen (direct) | 6,002 |
| Cursor Agent (directed) | 22,850 |
| **IllLeroySquad total** | **28,852** |

## Full repository

| Category | Files | Lines | IllLeroySquad | AliasFactory |
| --- | ---: | ---: | ---: | ---: |
| Mod C# | 89 | 30,920 | 21,786 | 9,134 |
| Tests (C#) | 34 | 8,846 | 4,612 | 4,234 |
| Shaders (GLSL) | 10 | 1,285 | 535 | 750 |
| Scripts | 16 | 2,732 | 6 | 2,726 |
| Documentation | 16 | 3,195 | 1,535 | 1,660 |
| Mod metadata | 4 | 148 | 77 | 71 |
| Canvases | 2 | 301 | 301 | 0 |
| **Total** | **171** | **47,427** | **28,852** | **18,575** |

## C# detail

| | Mod C# | Tests | Total |
| --- | ---: | ---: | ---: |
| IllLeroySquad | 21,786 | 4,612 | 26,398 |
| AliasFactory | 9,134 | 4,234 | 13,368 |
| **Total** | **30,920** | **8,846** | **39,766** |

wc -l: mod 30,917 · tests 8,846 · C# 39,763 · shaders 1,282 · scripts 2,732

C# by area: Render 18,995 · Lod 4,529 · Net 3,800 · Storage 866 · entry 2,727
Largest file: LodTerrainRenderer.cs — 3,458 lines

Git commits (227): AliasFactory 114 · Cursor Agent 103 · IllLeroySquad 8 · Private Citizen 5 · leroysquad 1

## Shaders (per file)

| File | Lines | IllLeroySquad | AliasFactory |
| --- | ---: | ---: | ---: |
| lodterrain.fsh | 200 | 53 | 147 |
| lodterrain.vsh | 217 | 100 | 117 |
| farseer-region.fsh | 111 | 111 | 0 |
| farseer-region.vsh | 80 | 80 | 0 |
| region.fsh | 111 | 111 | 0 |
| region.vsh | 80 | 80 | 0 |
| chunkliquid.vsh | 139 | 0 | 139 |
| chunkopaque.vsh | 140 | 0 | 140 |
| chunktopsoil.vsh | 107 | 0 | 107 |
| chunktransparent.vsh | 100 | 0 | 100 |
| **Total** | **1,285** | **535** | **750** |

LOD + Farseer region shaders: 535 IllLeroySquad · 264 AliasFactory
Vanilla chunk overrides: 0 · 486

## Scripts

scripts/: 2,732 lines — IllLeroySquad 6 · AliasFactory 2,726

## Documentation (per file)

| File | Lines | IllLeroySquad | AliasFactory |
| --- | ---: | ---: | ---: |
| CHANGELOG.md | 1,050 | 690 | 360 |
| DESIGN.md | 938 | 0 | 938 |
| README.md | 266 | 47 | 219 |
| docs/OFFICIAL-DESCRIPTION.md | 148 | 148 | 0 |
| docs/RELEASING.md | 129 | 7 | 122 |
| docs/WHAT-WE-DO.md | 29 | 29 | 0 |
| docs/community + plans + audits | 664 | 643 | 21 |
| LICENSE | 21 | 0 | 21 |
| **Total** | **3,195** | **1,535** | **1,660** |

Mod metadata: 148 lines — IllLeroySquad 77 · AliasFactory 71
Canvases: 301 lines — IllLeroySquad 301

## World scale / login bake (1.0.32)

1 block ≈ 1 meter · 1 block² = 1 m² · 1 mile = 1,609.344 blocks · 1 sq mi = 2,589,988 block²

**Formula:** 4.5 × 750 + 700 = **4,075 block radius**

| Measure | Value |
| --- | ---: |
| Radius | 4,075 blocks (2.53 mi) |
| End to end | 8,150 blocks (5.06 mi) |
| Area (πr²) | 52,168,110 block² (20.1 sq mi) |
| L0 cell | 64×64 = 4,096 block² |
| L0 cells in disk | ~12,868 |
| Visit stops max | 1,680 |
| Parallel scouts | 16 |
| MaxBakePerTick | 24 |
| Spawn solid | 1,024 radius |
| Graphics hold | 750 blocks |
| .dvfar max | 262,144 blocks |

Old disks (blocks, not LOC): 1.0.1 radius 36,000 · 1.0.2 72,000 · 1.0.3 144,000 · 1.0.29+ 4,075

## modinfo one-liner

Official 1.0.32. 47,427 lines across 171 files: 28,852 IllLeroySquad, 18,575 AliasFactory (9,134 mod C# foundation). Client-side far terrain with persistent LOD cache, extended render distance, live seasonal colour, and spawn-centered login bake.

---

# PART 2 — OFFICIAL DESCRIPTION

## What this mod is

Distant Vistas is a client-side level-of-detail mod for **Vintage Story 1.22.5–1.22.7**. It extends how far you can see terrain beyond the vanilla view-distance slider by building and drawing a persistent far-terrain cache from chunk data your client already receives.

Works on singleplayer and multiplayer **without requiring a server install**. Optional server component shares cache work when both sides run matching versions.

Fork of **Vintage Horizons** (AliasFactory, MIT). Rendering draws on **Farseer** (Badgerson, MIT). Author: **IllLeroySquad**. https://github.com/leroysquad/DistantVistas

## What it does

**Extended render distance.** Terrain continues past the vanilla view ring. `.dvfar 0` = unlimited; `.dvdetail` controls coarser LOD start.

**Real 3D far terrain.** Mountains, overhangs, caves, forests, builds at distance; translucent water over lake/sea floors.

**Persistent per-world cache.** SQLite under VintagestoryData/ModData/distantvistas/. Grows as you explore.

**Live seasonal appearance.** Vegetation follows calendar month; frost on canopy tops; refresh on join after calendar changes.

**Join overlay.** Sixteen parallel scouts bake far land while you stay at spawn. Graphics view held at 750 blocks during overlay, then restored. Vanilla public servers without the mod: overlay skipped; capture as you travel.

**Visited land remains visible.** Meshed terrain stays drawn when you move away.

**Farseer skyline.** Both draw together: cached tiles on top; gray atmospheric tent + dark ridge tips at the join.

**Compatibility deferral.** Default: yields to ChunkLOD / TopoHorizon. `.dvdefer off` + restart to draw alongside.

## Capabilities

| Capability | Detail |
| --- | --- |
| Render range | Unlimited with `.dvfar 0`; cap up to 262,144 blocks |
| Detail | `.dvdetail [blocks]` default 512 |
| Login bake | 4,075-block radius disk; 16 scouts; 1,680 stops max |
| Spawn solid | 1,024 blocks from pickup |
| Server assist | Optional shared cache + `/dvgen` |
| Versions | VS 1.22.5, 1.22.6, 1.22.7 |

## Install

1. Drop `distantvistas_1.0.32.zip` in Mods (do not extract).
2. Fully quit Vintage Story, then start again.
3. Recommended: Client MeshRef Leak Fix (vsvaogc).

## Commands

| Command | Purpose |
| --- | --- |
| `.dvistas` | Status |
| `.dvdetail [blocks]` | Detail halving distance |
| `.dvfar <blocks>` | Cap LOD (`0` = unlimited) |
| `.dvdefer [on\|off]` | Defer to ChunkLOD / TopoHorizon |
| `/dvserver` | Server assist status |
| `/dvgen start [radius] [x z]` | Build LOD over unvisited terrain |

## Summary

Official **1.0.32** — **47,427 lines**, **171 files** — **28,852** IllLeroySquad, **18,575** AliasFactory (**9,134** mod C# foundation).

---

# PART 3 — ARCHITECTURE AND OVERHAULS

## Executive summary

Client-side LOD engine: capture → SQLite mip pyramid → 3D mesh → draw past view ring.

Required: ingest/store/mip/mesh/draw pipeline · render stack (fog, seasons, night, vanilla handoff) · login bake (16 scouts, GetColor, Farseer join) · optional server assist · 8,846 lines of local tests (no CI).

**28,852** directed lines on **~9,000** Horizons mod C# foundation.

## Pipeline

```text
ChunkDirty → snapshot → priority queue (hash-gated)
  → ChunkToLod (RLE columns) → LodStore (SQLite WAL)
    → MipPropagator → quadtree reload → worker mesh → GL upload
      → LodTerrainRenderer (Opaque 0.36, frustum cull, season tint, Farseer)
```

Layers: Entry (ModSystem) · Lod/ (capture, mip) · Storage/ (SQLite) · Render/ (draw, login bake) · Net/ (assist, sweep, dvgen)

## Core data model

Section = 64×64 columns at detail D. Key = packed long. Column = vertical RLE. Palette per section. Mip = 2×2 downsample with early-out. Storage = WAL + applyToParent flags + chunk hashes.

## Rendering

LodTerrainRenderer 3,458 lines · greedy quads · frame-budgeted upload · ZFar extension · tint classes + live season shader · GetColor/FlagBaked/FlagFrost · LodRgbSimd · patched chunk shaders (4 files) · Farseer compositing + shader overlay.

## Login bake overhaul (from Horizons hop-teleport)

16 LodScoutViewerEntity streamers · player pinned at spawn · two-tier gate (1024 mesh-wait / outer FlagBaked) · 4,075-block disk · 1,680 stops · MaxBakePerTick 24 · texture-mean cache · ArrayPool · 75% far-ready gate.

## Nine overhauls from Horizons

1. Parent plates removed — wait for real L0
2. Visited land retention — no camera-window sky punch-through
3. Scout login — no hop teleport
4. Disk 4,075 blocks — replaced 36k–144k probes
5. Season + night colour — live ambient, GetColor bake, frost gates
6. Vanilla edge + ocean — shader patches, seabed seal, capture holes fixed
7. Farseer rebuilt — composited draw, shader overlay, gray/black skyline
8. Capture hardening — 16k pending ceiling, backpressure, hash gating
9. Server assist M7 — manifest, transfer, sweep, /dvgen

## Quality

scripts/check.sh: fast (~1s) · smoke (~5m) · matrix (~20m). 34 test files, 8,846 lines.

## One paragraph

Official quality required rebuilding Horizons draw path, VS-specific season/night/Farseer/vanilla-join fixes, production login bake at scale, crash-safe SQLite pyramid, optional server assist, ten shaders, 3,458-line renderer, and local test regimen. Horizons ~9k mod C#; ~22k directed work to 1.0.32.

---

# END OF BUNDLE

ModDB: edit at https://mods.vintagestory.at/distantvistas in your own browser on your PC.
Full HTML listing + math: docs/community/moddb-1.0.32-listing.html on GitHub branch cursor/official-description-6148.
