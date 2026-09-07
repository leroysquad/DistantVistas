# Distant Vistas — own ModDB page (community-facing claims)

- **URL:** https://mods.vintagestory.at/distantvistas (`asset` ~65544)
- **Author:** IllLeroySquad; fork of Vintage Horizons (AliasFactory, MIT); Farseer techniques (Badgerson, MIT)
- **Latest public:** 0.7.56 (look-down squares wait until ~67 deg / off the monitor). 0.7.55 was Farseer companion draw, spawn plates locked, spatial tint. 0.7.49 was visited land / sky circle / keep-origin seasons. Winter colour: first pass 0.7.14, walking autumn snap 0.7.49, mountain leaf/grass 0.7.55.
- **Tags:** `lod` `fog` `bug` `horizons`
- **Comments:** 14 (mostly packaging/screenshot at scrape time)

## Public problem statements that match research keywords

- “without the **fog wall** or **white slice** at the edge”
- View distance maxed but horizon cuts off mid-mountain
- Distant Horizons-style vistas + optional server cache share

## Changelog ↔ bug dictionary (author-confirmed)

| Version | Claim | Research alias |
| --- | --- | --- |
| 0.7.5 | White fog slice / mid-mountain cut; patched vanilla chunk shaders force full alpha | Edge-fade seam (ChunkLOD formula / TopoHorizon LodPatchTerrainEdgeFade) |
| 0.7.7 | Checkerboard / 32-block cliff grid of missing LOD; QueueColumn skip bug on L0 2×2 | Holes / incomplete capture |
| 0.7.11 | Ocean “**cake slice**”: cave interiors through transparent water; sealed seabed skin | Cake walls / underwater cutaways (related to G51 water walls) |

## Install constraints stated publicly

- SP client-only OK; MP server assist optional but versions should match
- **Server view distance is the ceiling** for streamed terrain / LOD fill
- Commands: `.dvistas`, `.dvdetail`, `.dvfar`, `.dvdefer`

## Cross-links

- Upstream: [vintagehorizons-moddb.md](vintagehorizons-moddb.md), [github-vh-horizons.md](github-vh-horizons.md)
- Cake/seam internals: [horizons-gotchas-crosslink.md](horizons-gotchas-crosslink.md)
