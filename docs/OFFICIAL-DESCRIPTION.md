# Distant Vistas — Official Description (1.0.32)

Plain-language overview for ModDB, store listings, and release notes. Block-scale math
(radius, end-to-end, square blocks) lives in `docs/community/moddb-1.0.32-listing.html`.

---

## What this mod is

Distant Vistas is a client-side level-of-detail mod for **Vintage Story 1.22.5–1.22.7**.
It extends how far you can see terrain beyond the vanilla view-distance slider by building
and drawing a persistent far-terrain cache from chunk data your client already receives.

The mod works on singleplayer worlds and on multiplayer servers **without requiring a
server install**. An optional server component can share cache work when both sides run
matching versions.

Distant Vistas is a fork of **Vintage Horizons** (AliasFactory, MIT). Rendering techniques
draw on **Farseer** (Badgerson, MIT). Author: **IllLeroySquad**.
[GitHub](https://github.com/leroysquad/DistantVistas)

---

## Codebase size (exact, counted from source)

| Component | Lines | Files |
| --- | ---: | ---: |
| Mod C# (`DistantVistas/src/`) | 30,917 | 89 |
| Automated checks (`tests/`) | 8,846 | 34 |
| **Total C#** | **39,763** | **123** |
| GLSL shaders | 1,282 | 10 |
| Build, bench, and test scripts | 2,732 | 16 |

Counts use `wc -l` on each source file. Authorship uses `git blame` on surviving lines.
Git records AI-assisted commits under `Cursor Agent`; those count as **IllLeroySquad**
directed work (22,850 lines across the repo). Largest single file: `LodTerrainRenderer.cs`
at 3,458 lines.

### Authorship — full repository graph

| Category | Files | Lines | **IllLeroySquad** | **AliasFactory** |
| --- | ---: | ---: | ---: | ---: |
| Mod C# | 89 | 30,920 | 21,786 | 9,134 |
| Tests (C#) | 34 | 8,846 | 4,612 | 4,234 |
| Shaders (GLSL) | 10 | 1,285 | 535 | 750 |
| Scripts | 16 | 2,732 | 6 | 2,726 |
| Documentation | 16 | 3,195 | 1,535 | 1,660 |
| Mod metadata (json, csproj) | 4 | 148 | 77 | 71 |
| Canvases | 2 | 301 | 301 | 0 |
| **Total** | **171** | **47,427** | **28,852** | **18,575** |

**Plain summary**

| | Lines |
| --- | ---: |
| **You (IllLeroySquad, including all Cursor Agent commits)** | **28,852** |
| **Original creator (AliasFactory)** | **18,575** |
| **Total repository** | **47,427** |

AliasFactory's Horizons foundation in mod source alone: **9,134 lines of C#** (~9,000).

**IllLeroySquad breakdown (direct vs Cursor Agent in git history)**

| Git author | Lines |
| --- | ---: |
| IllLeroySquad / leroysquad / Private Citizen (direct) | 6,002 |
| Cursor Agent (directed — counted as yours) | 22,850 |
| **IllLeroySquad total** | **28,852** |

**Shaders by file (surviving lines)**

| File | Lines | IllLeroySquad | AliasFactory |
| --- | ---: | ---: | ---: |
| `lodterrain.fsh` / `.vsh` | 417 | 153 | 264 |
| `farseer-region.fsh` / `.vsh` | 191 | 191 | 0 |
| `region.fsh` / `.vsh` (Farseer) | 191 | 191 | 0 |
| Vanilla chunk shader overrides (`chunk*.vsh`) | 486 | 0 | 486 |
| **Shader total** | **1,285** | **535** | **750** |

**Documentation by file (surviving lines)**

| File | Lines | IllLeroySquad | AliasFactory |
| --- | ---: | ---: | ---: |
| `CHANGELOG.md` | 1,050 | 690 | 360 |
| `DESIGN.md` | 938 | 0 | 938 |
| `README.md` | 266 | 47 | 219 |
| `docs/OFFICIAL-DESCRIPTION.md` | 148 | 148 | 0 |
| `docs/RELEASING.md` | 129 | 7 | 122 |
| ModDB / community docs (`docs/community/`, plans, audits) | 664 | 643 | 21 |
| `LICENSE` | 21 | 0 | 21 |
| **Documentation total** | **3,195** | **1,535** | **1,660** |

Blame line counts may differ from `wc -l` by a few lines per file; category totals above
use blame for authorship and `wc -l` for file/line inventory where noted in the first table.

---

## What it does

**Extended render distance.** Terrain continues past the vanilla view ring. Use
`.dvfar 0` for unlimited LOD draw distance, or set a cap in blocks. Detail resolution
steps down with distance; near the player, geometry stays at one-block fidelity.

**Real three-dimensional far terrain.** Mountains, overhangs, cave mouths, forests, and
player-built structures appear at distance as meshed geometry, with translucent water drawn
over lake and sea floors.

**Persistent per-world cache.** As you explore, the mod captures chunk columns into a
SQLite cache stored under your Vintage Story data folder. Cached land remains available on
later sessions. Join time and memory use scale with active draw range, not with the full
size of everything you have ever visited.

**Live seasonal appearance.** Vegetation tint follows the calendar month. Frost appears on
canopy tops in winter. Rock, snow, and water use appropriate material treatment. Season
refresh applies on join after calendar changes.

**Join overlay.** When you enter a world that runs Distant Vistas (singleplayer or a server
with the mod), a loading overlay runs while you remain at your spawn position. Sixteen
parallel scout viewers stream, capture, and bake far land around you. Graphics view distance
is held at 750 blocks during the overlay, then restored to your saved setting. On vanilla
public servers without the mod, the overlay is skipped and capture proceeds as you travel.

**Visited land remains visible.** Terrain you have already generated stays drawn when you
move away. The camera can show continuous hills and ridgelines rather than empty sky where
land was previously meshed.

**Farseer skyline.** Where Farseer is installed, both systems draw together: Farseer provides
the distant skyline band; Distant Vistas tiles sit on top where cached geometry exists. The
join between baked land and the skyline reads as a gray atmospheric tent with dark ridge
tips.

**Compatibility deferral.** By default, Distant Vistas yields draw priority to ChunkLOD and
TopoHorizon when those mods are active. Use `.dvdefer off` and restart if you want this
mod to draw alongside them.

---

## What it is capable of

| Capability | Detail |
| --- | --- |
| Render range | Unlimited with `.dvfar 0`; configurable cap up to 262,144 blocks |
| Detail control | `.dvdetail [blocks]` sets where coarser LOD begins (default 512) |
| Login bake area | 4,075-block radius disk around spawn (see math appendix for end-to-end and block² figures) |
| Login bake throughput | Up to 16 parallel scouts; up to 1,680 visit stops per overlay |
| Spawn solid zone | Immediate ground underfoot meshed within 1,024 blocks of pickup |
| Server assist | Optional shared cache and admin tools when server and client both run the mod |
| Savegame sweep | Loads previously generated columns so horizons rebuild over explored territory |
| Admin generation | `/dvgen` builds LOD over unvisited terrain transiently (server privilege required) |
| Game versions | Vintage Story 1.22.5, 1.22.6, 1.22.7 |

---

## Installation

1. Download `distantvistas_1.0.32.zip`.
2. Place the zip in your Vintage Story `Mods` folder. Do not extract it.
3. Fully quit Vintage Story, then start it again.

Recommended companion mod: [Client MeshRef Leak Fix](https://mods.vintagestory.at/vsvaogc)
(`vsvaogc`), client only.

---

## Commands

| Command | Purpose |
| --- | --- |
| `.dvistas` | Status: cache sections, meshes, far edge, settings |
| `.dvdetail [blocks]` | Distance before detail halving (default 512) |
| `.dvfar <blocks>` | Cap LOD render distance (`0` = unlimited) |
| `.dvdefer [on\|off]` | Defer to ChunkLOD / TopoHorizon (restart to apply) |
| `/dvserver` | Server assist status (controlserver privilege) |
| `/dvgen start [radius] [x z]` | Build LOD over unvisited terrain (controlserver privilege) |

Settings persist in `VintagestoryData/ModConfig/distantvistas.json`. Per-world cache files
live in `VintagestoryData/ModData/distantvistas/`.

---

## Summary

Distant Vistas brings persistent, full three-dimensional far terrain to Vintage Story on
any server, with live seasonal colour, a spawn-centered login bake, and configurable
render range. Official release **1.0.32** — **47,427 lines** across **171 files**
(**28,852** IllLeroySquad, **18,575** AliasFactory; **9,134** AliasFactory mod C# foundation).
