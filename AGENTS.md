# Agent front door — Distant Vistas

**Read [`docs/CODEBASE_SUMMARY.md`](docs/CODEBASE_SUMMARY.md) first.** It is the definitive bot-oriented map: module ownership, runtime pipeline, invariants, bug shortcuts, and config paths. Do not re-crawl the tree for questions already answered there.

## Structural freedom (user policy 2026-09-07)

Distant Vistas forked Vintage Horizons historically, but **VH code structure is not sacred**. You may streamline, relocate modules, rewrite pipelines, or redesign internals for clarity, fluency, or performance when it improves the product.

**Preserve product invariants, not VH layout.** Examples of what must stay true:

- Pressure-only mesh eviction (never count/distance alone); visited land inside 2× VD
- No cake plates in the player FOV cone
- Live `GetColor` visit bake (`FlagBaked`); no shader-repro season paint
- Login overlay: no autosave / `chunkdbthread` FIFO death; stepped stream backpressure
- Exact pickup XYZ + look restore after overlay
- No present-path splash GL (Cairo HUD only)

Do **not** block refactors with “Horizons did it this way” or “the file map says X owns Y.” Update docs when structure changes.

### Organization / contextualism (user policy 2026-09-07)

Prefer **co-location by concern**. If code is about one topic, keep it in one space — do not scatter parsing/logic for the same concern across hodgepodge files. Adjacent/relevant context must be quickly identifiable for both human coders and AI agents.

**Goal:** minimize search and recon time so cognition focuses on the work.

When refactoring under structural freedom, regroup modules for **locality of reference** — e.g. login bake together, mesh eviction together, splash/GL together — not VH historical layout.

## Repo identity

- **Product:** Distant Vistas (fork of Vintage Horizons)
- **Tip version:** 1.0.0 on `main`; login-bake work at **1.0.45** on `cursor/login-bake-chunkdb-autosave-1045-dba0` (`c8ff709`)
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
| Overlay stream / chunkdb | `LodLoginBakeViewBoost.cs`, `LodLoginChunkRequestBudget.cs`, `Net/LodScoutHostSystem.cs` (1.0.44+ branch only) |
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

## Login bake stream cliffs (read before touching overlay)

**Full detail:** [`docs/CODEBASE_SUMMARY.md` → Login bake stream cliffs](docs/CODEBASE_SUMMARY.md#login-bake-stream-cliffs--chunkdb-autosave-1033)

**Plans (on branch `cursor/login-bake-chunkdb-autosave-1045-dba0`):** `docs/plans/login-bake-walltime-1033.md` §1.0.44–§1.0.45. Cliff-forecast / chunkdb-lessons standalone files are **not** on tip — content is in walltime doc + CHANGELOG.

| Trap | Do not | Do instead |
|------|--------|------------|
| Overlay stalls ~358 / ~639 / ~708 | Hold VD at 750 or 1024 | Frontier `OverlayStreamBlocks` + **+32/5s** stepped grow |
| SP autosave death after climb | Remove Farseer; only bump `RequestChunkColumnsQueueSize` | `LodLoginChunkRequestBudget` (96/tick), idempotent `HoldAnchor`, 24 priority loads/tick |
| Visit disk vs stream | Tessellate 4075 via vanilla VD | Stream ≤2048; scouts + `PlayModeBakeBudget` for sparse rim |

**Pass bar:** `finished` past 734→1680, server up, no autosave failure, slider restored.

## Quick diagnosis

| User report | See CODEBASE_SUMMARY section |
|-------------|------------------------------|
| Purple / wrong far colors | Known bug classes → FlagBaked / live tint |
| Sky holes in visited land | Mesh eviction invariants |
| Giant horizon squares | Cake plates vs sky |
| Join black screen / GL crash | Join / splash / GL footguns |
| TrueScale crash on join | TrueScale interactions |
| Mod not loading | Build paths → double modPaths |
| Login overlay cliff / SP death | Login bake stream cliffs |

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
