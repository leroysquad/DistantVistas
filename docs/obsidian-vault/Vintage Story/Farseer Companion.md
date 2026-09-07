---
tags: [vintage-story, distant-vistas, farseer]
aliases: [Farseer, Farseer onset, gray tent]
---

# Farseer Companion

How **Distant Vistas** coexists with the MIT **Farseer** mod: shared horizon, visit-aware shaders, and the land–sky join.

Related: [[Distant Vistas]] · [[Login Bake & Scouts]] · [[Season Colors & Canopy]] · [[ValksFuzzyClouds Rain Backlog]]

## Visual target (1.0.23+)

The liked silhouette is **not** empty black or sky fill:

- **Gray tent / smoke body** — pale gray near ground, slight darkening aloft (1.0.23/1.0.25 look).
- **Black mountain tips** — ridge ink so ridges read against sky (1.0.28+).
- **Lighter, thinner distance mist** — less sky-blue wash (1.0.28; kept in 1.0.29–1.0.32).
- **Quieter low-ground smoke** — avoids washing desert into white blob (1.0.30).

Pinned in `tests/VintageHorizons.Checks/StaticAssetChecks.FarseerOverlay`.

## Onset math

```
HorizonDrawDistance(VD) = 4.5 × viewDistance + 700
At VD 750 → 4.5 × 750 + 700 = 4075 blocks
```

- `LodCoveragePolicy.HorizonDrawDistance` / `LodLoginBakeViewBoost.SweepVisitRadiusBlocks`
- Login FlagBaked disk sized to meet Farseer silhouette, not a thin 750 ring.
- Scouts stream **local** rings at visit cells — they do not tessellate 4 km around the player.

## Shader overlay

`FarseerShaderOverlay` copies `distantvistas/shaders/farseer-region.*` over `farseer/shaders/region.*` after assets load.

- Marker: `DV_FARSEER_OVERLAY`
- **Do not** `ReloadShaders()` after inject — Farseer zip would wipe overlay.
- `FarseerVisitOnset` binds visit mask each frame; unvisited vs visited onset scales.
- `FarseerVisitedHeightEnrich` sharpens visited midground.

Files: `DistantVistas/assets/distantvistas/shaders/farseer-region.{vsh,fsh}`, `assets/farseer/shaders/region.*`.

## Sky gap

**Partial mitigation** — narrow white band can remain when:

- Capture envelope or meshed radius lags Farseer onset (~4075).
- Post-login `DiscoverOnly` + frontier drip leaves outer ring unpainted ([[Neglected Issues Audit]]).
- Missing-tex white at join — scouts skip unloaded map chunks (1.0.29+).

Diagnostics: `FarseerVisitOnset.LogHandoffGap` (H-A, H-E), `FarCoverageDiag` H-S3.

**Back-burner:** four cardinal-box Farseer rim ([[User-Mentioned Unfixed#2. Four cardinal-box Farseer rim plan]]). `LodCloudHorizon` stretches vanilla / FluffyClouds tiles toward horizon instead.

## Height enrich & visit mask (1.0.32)

- Farseer visit-mask rebuild debounced 500 ms on `HasDataSet` churn.
- Stamps L0 keys + envelope disk — not full 256×256 scan every tick.
- Height enrich waits until overlay ends.

## Version notes

| Version | Farseer change |
|---------|----------------|
| 1.0.25 | Onset ~700 farther; grayish smoke |
| 1.0.28 | Ridge ink, thinner mist — **aesthetic baseline** |
| 1.0.29 | Coverage to 4075; shaders **unchanged** from 1.0.28 |
| 1.0.30 | Thinner low Farseer smoke near ground |

Verify: after overlay, FlagBaked land meets gray/black silhouette; no big empty/white gap (`CHANGELOG.md` 1.0.29).
