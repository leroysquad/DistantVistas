# Why a fork, and what is different

I forked Vintage Horizons to fix problems in my own worlds. Not taking credit for Horizons or anyone else. I am open to working with them. Right now I am just doing my own thing. What works for me might not work for you.

Same starting point: a persistent LOD cache from chunks the client already received. After that the draw path is different.

## Parent plates

Horizons will mesh a coarse parent as coverage when children are not ready. From the air that is giant square plates with sheer walls. Distant Vistas does not use a parent box as a stand-in. Incomplete L0 stays off screen until it is real land. That is the 0.7.20 look lock.

## Trail behind the camera

A wanted-level camera window plus frustum cull drops captured near tiles once you fly past, so land you already generated turns into sky until you linger. Distant Vistas keeps captured L0/L1 in the draw set behind and beside the camera (`ShouldKeepVisitedDraw`). Fill is not allowed to stall those tiles just because the window moved.

## Vanilla handoff

Vanilla-owns is 3D plus look-down, not a horizontal disc. Looking down from altitude still draws LOD where vanilla has no chunks.

## Night color

Stored albedo is multiplied by the same live ambient the chunk shaders use. `SunColor * daylight` is the sun disc and stays orange after vanilla has gone dark.

## Vanilla edge fade

Vanilla chunk shaders fade alpha at the view-distance ring. Those vertex shaders are overridden so LOD joins behind at full alpha instead of a fog slice.

Farseer, ChunkLOD, and TopoHorizon are different designs (server silhouettes, a Farseer fork, a prebuilt pyramid). Vistas Beyond is worldgen, not LOD.
