# What Distant Vistas does that the others don't

This is the code difference, not a slogan. Distant Vistas is a Vintage Horizons fork. Same family: a persistent LOD cache from chunks you already received. The fork is the draw and lighting path.

## Visited L0/L1 skip the frustum

`LodCoveragePolicy.ShouldKeepVisitedDraw` keeps captured near tiles in the draw set even when `LodFrustum.BoxInView` would reject them (behind or beside the camera). A wanted-level camera window plus frustum cull is how the trail turned into sky on fly-past. We do not drop that mesh because you turned.

## No parent plate as coverage

A missing child is not replaced by a coarse square parent mesh. That parent-as-stand-in is the Vintage Horizons cake-square look. Incomplete L0 stays off screen until it is real land. 0.7.20 meshing is the look lock.

## Vanilla-owns is 3D plus pitch

Handoff is not a horizontal disc. `InsideVanillaCoverage` uses surface height and look-down amount. Straight down from altitude still draws LOD where vanilla has no chunks.

## Live ambient, not disc color

Stored noon albedo is multiplied by the same live ambient the chunk shaders use (`BlendedAmbientColor` / `rgbaAmbientIn`). `Calendar.SunColor * daylight` is the sun disc and stays orange after vanilla has gone dark. Fog still uses `BlendedFog*`. `DisableLodFog` only skips extra pastViewHaze.

## Vanilla edge alpha is patched

Vanilla chunk shaders fade alpha at the live view-distance ring. Override those vertex shaders so LOD joins behind at full alpha instead of a white/fog slice.

## What this is not

- Not Farseer: server-side silhouettes.
- Not ChunkLOD: server-required Farseer fork / grid.
- Not TopoHorizon: a prebuilt clipmap pyramid you download.
- Not Vistas Beyond: that is worldgen, not LOD.
- Not Horizons parent plates: we forked Horizons and then refused that coverage lie.

Client cache first. Optional server assist shares cache. Fast fly is a stress test. Visited land stays.
