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

Counts use `wc -l` on each source file. Largest single file: `LodTerrainRenderer.cs` at
3,458 lines.

### Authorship (C#, current surviving lines via `git blame`)

| Contributor | Mod C# | Tests | **C# total** |
| --- | ---: | ---: | ---: |
| **AliasFactory** (Vintage Horizons foundation) | 9,134 | 4,234 | **13,368** |
| **IllLeroySquad** | 4,565 | 989 | **5,554** |
| **Cursor Agent** (AI-assisted development) | 17,221 | 3,623 | **20,841** |
| **Total** | **30,920** | **8,846** | **39,766** |

The original Horizons foundation is **~9,000 lines of mod C#** (9,134 above). The
remaining mod source and nearly all post-fork test code is Distant Vistas development.
IllLeroySquad identities in git: `IllLeroySquad`, `leroysquad`, and `Private Citizen`.

| | Lines |
| --- | ---: |
| You (IllLeroySquad) | **5,554** |
| Original creator (AliasFactory) | **13,368** C# (**9,134** in mod source) |
| Cursor Agent | **20,841** |
| **Total C#** | **39,766** |

Blame totals may differ from `wc -l` by a few lines when files have uncommitted edits;
file counts above use `wc -l` on the release tree.

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
render range. Official release **1.0.32** — **39,763 lines** of C# (**5,554** IllLeroySquad,
**9,134** AliasFactory mod foundation, **20,841** Cursor Agent).
