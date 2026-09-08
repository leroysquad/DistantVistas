# Agent front door — Distant Vistas

**Read [`docs/CODEBASE_SUMMARY.md`](docs/CODEBASE_SUMMARY.md) first.** It is the definitive bot-oriented map: module ownership, runtime pipeline, invariants, bug shortcuts, and config paths. Do not re-crawl the tree for questions already answered there.

## Repo identity

- **Product:** Distant Vistas (fork of Vintage Horizons)
- **Tip version:** 1.0.0 (`DistantVistas/modinfo.json`)
- **Game:** Vintage Story 1.22.5+, .NET 10
- **Tests namespace:** `VintageHorizons.Checks` (historical name)

## Ownership

| Area | Primary files |
|------|---------------|
| Client orchestration | `DistantVistas/src/DistantVistasModSystem.cs` |
| Capture / persistence | `DistantVistas/src/Lod/LodPipeline.cs`, `Storage/LodStore.cs` |
| Rendering / eviction | `DistantVistas/src/Render/LodTerrainRenderer.cs` |
| Coverage / cake plates | `DistantVistas/src/Render/LodCoveragePolicy.cs` |
| Login sweep + bake | `DistantVistas/src/Render/LodLoginBake.cs`, `LodSeasonBake.cs` |
| Walk bake | `DistantVistas/src/Render/LodExploreBake.cs` |
| Server / assist | `DistantVistas/src/Net/` |

There is **no Harmony** in this mod. There is **no `FrameGuard` class** — join GL safety uses `LoginBakeBlocked`, `LodJoinQuiet`, and `RestorePresentFramebuffer()`.

## Do-not-thrash warnings

1. **Documentation-only changes preferred** unless fixing a verified bug. Do not refactor gameplay systems while documenting.
2. **Never add hard mesh-count eviction** — only `MeshPressureActive` (FPS/RAM) may evict, and only outside 2× view distance.
3. **Never reintroduce present-path splash GL** (OrthoMode / `IRenderer` splash) — use `LodLoginBakeInputGuard` Cairo HUD.
4. **Do not bake season colors without streaming chunks** — login and explore bake both require live `GetColor`.
5. **Run `scripts/check.sh fast`** after C# changes; full three tiers before release.
6. **Shaders must be pure ASCII** — UTF-8 comments silently truncate GLSL.
7. **Block registry reads are main-thread only.**

## Quick diagnosis

| User report | See CODEBASE_SUMMARY section |
|-------------|------------------------------|
| Purple / wrong far colors | Known bug classes → FlagBaked / live tint |
| Sky holes in visited land | Mesh eviction invariants |
| Giant horizon squares | Cake plates vs sky |
| Join black screen / GL crash | Join / splash / GL footguns |
| TrueScale crash on join | TrueScale interactions |
| Mod not loading | Build paths → double modPaths |

## Verify before claiming

- **skybite** — not a code symbol; means sky beyond unvisited frontier (expected until cached).
- **Purple LOD at night** — ambient shader effect in `lodterrain.fsh`, not a debug mode.
- **Local tree vs GitHub tip** — document the `modinfo.json` version on the branch you have.

## Build & test

```sh
export VINTAGE_STORY="$HOME/Games/vintagestory1.22.5"
dotnet build DistantVistas
scripts/check.sh fast
```

Install path for players: `VintagestoryData/Mods/` (zip, not extracted). **Not** `ClientMods/`.
