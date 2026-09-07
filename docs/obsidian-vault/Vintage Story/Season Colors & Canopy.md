---
tags: [vintage-story, distant-vistas, season, color]
aliases: [Season colors, Canopy GetColor, FlagFrost, PaintRevision]
---

# Season Colors & Canopy

How Distant Vistas bakes and refreshes seasonal colour on LOD terrain — ground, canopy, frost, and the walk-away green flip.

Related: [[Distant Vistas]] · [[Login Bake & Scouts]] · [[Farseer Companion]] · [[User-Mentioned Unfixed]]

Full audit matrix: [[Sources/season-color-audit-1.0.19]]

## Core model

| Layer | Storage | Runtime |
|-------|---------|---------|
| Ground / soil | Visit bake RGB + tint class | Shader seasonal bands |
| Leaves / bushes | **Pure GetColor** at crown Y + `FlagFrost` bit | Mesher frost on walls/crown, not baked into RGB (rev 6+) |
| Water / ice | Tint class | Live shader |
| FlagBaked band | Stored RGB | Shader **band 3** bypasses live green tint |

`PaintRevision = 9` — bumps alone no longer force login teleport (1.0.24+); expire uses `PlanSeasonExpired`.

## GetColor path

- **Canopy:** `LodCanopyGray.ApplyTop` reverted to **identity** — leaves keep live GetColor + frost (`LodCanopyGray.cs:151–158`).
- **Foliage detection:** `LodSurfaceMix.IsSeasonFoliage` — leaves + bush; flower exclude skips `*-flowering` berrybush.
- **Crown walls:** `LodMesher.CrownSideBlocks` + `FrostFaceColor` — frost on walls/crown, not mixed into stored RGB.
- **Ground:** `SampleTextureMean` / 8-sample mean per BlockId during login bake ([[Login Bake Efficiency]]).

## Frost

- `FrostSeasonMin = 0.75` — frost only late autumn+ (`LodSeasonBake.cs`).
- Visit bake **does not** mix frost into stored RGB (PaintRevision 6+); `FlagFrost` on run flags.
- Mesher applies frost wash on walls + UP; thaw remesh clears mesher wash on `FrostSeasonMin` edge.

### Audit bands (1.0.19)

From [[Sources/season-color-audit-1.0.19]]:

| Band | Pre-1.0.19 failure | Fix |
|------|-------------------|-----|
| Mid-autumn | Frost at 0.20 mid-ramp; bushes excluded | Gate 0.75; `IsSeasonFoliage` |
| Late autumn frost | Side frost baked into RGB | Pure GetColor + FlagFrost |
| Deep winter | Mostly OK | Mesher side+crown wash |
| Early spring | Wall frost until rebake | Remesh on FrostSeasonMin edge |
| Stale months | PaintRev 5 | PaintRev 6 forces overlay once |

Pass = far LOD reads like near vanilla for calendar band after **full quit + rejoin**.

## 30-day refresh

Triggers (`LodLoginSweepWindow` / `LodLoginSweepBootstrap.PlanSeasonExpired`):

- 30 **in-game** days since last sweep
- 30 **wall-clock** days
- Calendar month change

On expire: spatial subsample of stored L0 for recapture. **Not** a guaranteed full LOD-rung rebake in one session — live tint refresh is incremental (`LodTerrainRenderer.RefreshSeasonalState`, ~30 s lattice).

User liked **1.0.23 gray-smoke Farseer look** across LODs — shader pinned separately ([[Farseer Companion]]).

🟡 Partial per [[User-Mentioned Unfixed#4. ~30-day season refresh on all LODs]].

## Green flip (walk-away)

**Fixed 1.0.27+**

- Child L0 has `FlagBaked`; parent L1+ still live-tint → visible green shift when walking away.
- `RemeshStaleLiveTintParent` remeshes coarse parent when child FlagBaked (`LodTerrainRenderer.cs`).
- Mip preserves FlagBaked canopy RGB (`MipChecks.FlagBakedCanopySurvivesMip`).
- Shader: `band == 3` bypasses live tint (`StaticAssetChecks`).

**Residual:** queue parent remesh on overlay success, not only during draw descent.

## Expire recapture white strip

🟡 Exception: `AllowExpireNoMapSample` during month expire recapture (`LodSeasonBake.cs`) — can paint near-white when map chunk missing. Scouts skip unloaded maps on login path (1.0.29); expire path should mirror `IsMissingTextureWhite` guard.

## Palette repair

Unresolved block codes → black ground. `LodPaletteRepair` + load-time `RefreshStoredPalette`. First-load storms (~3419 entries) — see [[Neglected Issues Audit#🟡 P1 — Palette "no colour" black repair storms]].

Tool: `scripts/scan-cache-palettes.py`.

## Key files

| File | Role |
|------|------|
| `LodSeasonBake.cs` | Frost gate, expire recapture, FlagFrost |
| `LodSurfaceMix.cs` | `PaintRevision`, foliage paths |
| `LodCanopyGray.cs` | Canopy top (identity) |
| `LodMesher.cs` | CrownSideBlocks, frost faces |
| `LodTerrainRenderer.cs` | Live tint refresh, stale parent remesh |
| `lodterrain.{vsh,fsh}` | Tint bands, baked band 3 |

Tests: `SeasonBakeChecks.cs`, `MesherChecks.cs`, `TopSoilColorChecks.cs`.

## Research export

`LodSeasonSampleExporter` — JSONL under `ModData/distantvistas/season-samples/` during login sweep (four-season research). See `CHANGELOG.md` season research samples entry.
