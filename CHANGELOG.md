## 0.7.17
- Fixes the clear square that followed the camera while flying high. Distant Vistas was deciding that vanilla owned nearby terrain from horizontal distance alone, even when the ground was far below the camera and vanilla was no longer drawing it. The handoff now includes the terrain's surface height, so LOD stays under high-altitude views until vanilla can actually cover it.

## 0.7.16
- Stuff I was hitting after using Vintage Horizons for a while: far terrain staying detailed when you walk away instead of turning into giant slabs, half-loaded bits staying hidden instead of drawing weird shelves and skinny pillars, old brown grass not crawling toward you as you get closer. Still has the join download / white cliff fixes from 0.7.15, and the colour / winter / fog seam work from 0.7.12-0.7.14. Personal fork. Not taking credit for Horizons or anyone else. What works for me might not work for you.

## 0.7.15
- **Server join and white cliffs.** A server install now declares Distant Vistas required on clients, so Vintage Story downloads the mod before joining instead of allowing a version mismatch to fail later. Client-only installs can still join servers without it. Missing neighbour snapshots no longer become full-height terrain walls; real cliffs against loaded shorter terrain still render. Starts from the stable 0.7.14 renderer and does not restore the reverted 0.8.x walk changes. Isolated test first; public update after in-game signoff.

## 0.7.14
- **Seam contrast.** Vanilla chunks run live `getFogLevel`/`applyFog`; LOD was uploading fogMin=99999 so the overdraw ring stayed crisp against the fogged foreground. 0.7.14 always feeds `BlendedFog*` and applyFog. DisableLodFog only skips extra pastViewHaze. Flat tops use vanilla-like up-face light so grass/snow plates are not a darker band. No spheres/height fog. Isolated 0.7.14; public listing stays 0.7.11.

## 0.7.13
- **Foreground grass + winter snow.** Near LOD was tiling random grass pixels against vanilla, and in winter a freeze-line overlay painted every LOD top plastic white while the foreground kept real snow layers on mixed grass/dirt. Overlay is alpine-only now; valley snow is captured snow plus seasonal tint. Isolated 0.7.13; public listing stays 0.7.11.

## 0.7.12
- **Winter / distant colour match.** Isolated 0.8.11–0.8.15 LOD-walk experiments are reverted; engine is 0.7.11 again. The ModDB report was far landscape not matching near terrain, especially in winter: seasonal maps are 2D and the engine picks the row from a hash of each block, so one sample painted every distant field with a single texel (green vs dead-grass brown). Grass overlay is colour-mapped in vanilla while the dirt showing through is not; multiplying the whole composite by the winter tint browned the dirt. 0.7.12 averages a lattice of colour-map samples per tint slot and dilutes the live tint by the untinted dirt share, same as vanilla `chunktopsoil`. Public listing stays 0.7.11 until this proves in-game.

## 0.8.15
- *Reverted* with 0.8.11–0.8.14. Isolated LOD-walk experiments; 0.7.12 restored 0.7.11 and only changed seasonal tint.

## 0.8.14
- **Restore parent coverage for open-side frontier.** 0.8.13 drew 0.8.12 surface-only tiles. In-game that was gray rectangular pillars after leaving an area (vanilla unloaded, pancake/stub LOD stayed) and sliced gray mountain faces; far walk also barely selected. 0.8.14 hides incomplete L0 and open-side sections behind parent/grandparent again. 0.8.12 mesher still does not build missing-neighbour cake walls. Isolated 0.8.14; public listing stays 0.7.11.

## 0.8.13
- **Draw surface-only frontier.** 0.8.12 stopped the mesher from building cake walls when a neighbour was not in RAM; the renderer still hid those meshes (and refused to enqueue them) until the neighbour ring filled. 0.8.13 lets complete open-side sections mesh and draw as surfaces. Incomplete/sparse L0 still PreferParentCoverage. OnSectionBecameResident still remeshes when a neighbour lands so real cliffs appear. Isolated 0.8.13; public listing stays 0.7.11.

## 0.8.12
- **Mesher: do not build cake walls.** If a neighbour section is not in RAM, CollectSide used to treat that edge as the end of the world and emit a full-height dirt face (green top + tan sides). 0.8.11 hid those meshes after the fact; 0.8.12 never builds them. Real cliffs against a loaded shorter neighbour still emit. Missing sides are tracked so a later neighbour load remeshes. Isolated 0.8.12; public listing stays 0.7.11.

## 0.8.11
- **Fix live-travel cake boxes (NEW world / cold-join).** Gen trails the player; incomplete L0 and true missing-neighbour frontier sections were meshed/drawn as full-height green-top slabs with tan sides and stayed on screen after flying away. 0.8.11 never meshes or draws frontier (open HasDataSet side) or incomplete L0 — PreferParentCoverage keeps parent/grandparent silhouette until columns and the neighbour ring cover. CollectDrawNodes also hides AssumedCoveredSides meshes behind a parent until seam repair lands, so temporary cliffs cannot persist mid-far. IncompleteFillPerTick 24. Cold-join, crash-safe no PauseGame/ApplyZFar-before-matrices, and ASCII shaders unchanged.

## 0.8.10
- **Fix load-screen crash (NullReferenceException in ChunkRenderer.GlLoadMatrix / Mat4d.Copy).** 0.8.9 PrefetchOnLoad called PauseGame(true) during LevelFinalize and ApplyZFar ran Reset3DProjection before ClientMain projection/view matrices existed. Vanilla terrain still rendered on GuiScreenRunningGame and pushed a null matrix. Fix: never PauseGame; defer CPU-only prefetch until MatricesReady; skip ApplyZFar/draw until matrices non-null; remove LevelFinalize ApplyZFar. PrefetchOnLoad default **false** (cold-join only); safe deferred prefetch remains available in config. Cold-join (LoadPersistedCache false) and mountain depth unchanged. ASCII shaders retained.

## 0.8.9
- Load prefetch gate (`PrefetchOnLoad` default true): after join, pause briefly and aggressively mesh/peek-fill around the player until `PrefetchMinMeshes` or `PrefetchTimeoutSeconds` (always best-effort timeout). Makes cold-join spawn usable instead of empty far. Knobs: PrefetchRadiusChunks, PrefetchMinMeshes, PrefetchTimeoutSeconds.
- Cold join default: `LoadPersistedCache` defaults to **false**. On world join, SQLite keys are not installed into the live quadtree for meshing/drawing, so quit+rejoin cannot paint old far cake monoliths. Capture+save this session still writes the DB. Set `LoadPersistedCache: true` in ModConfig/distantvistas.json (Advanced settings) and rejoin to restore warm cache later.
- Peek / unvisited mashed strata: LodMip keeps minority height shelves on taller high-relief slices so coarse parents keep slope depth instead of flat horizontal color bands; WantedLevel pushed outward; stronger mountain relief delay.
- Back-away / incomplete: L0 with any open side no longer unlocks parent descent (parent keeps continuous silhouette); incomplete/sparse L0 still never drawn; AssumedCoveredSides skips solid temporary walls too (G51 harden) and kicks neighbour loads so seams heal.
- Soft seam: gentle mid-band open-edge dissolve on coarse sections so visited detail vs unvisited landform is less of a hard cliff.
- Visual: lighter atmos hue wash so far mountain side shading survives.

## 0.8.8
- Fix: lodterrain shader failed to compile on NVIDIA (`unexpected $end`) because UTF-8 em-dashes in comments broke GLSL; LOD rendering was fully disabled (0 meshes). Replaced with ASCII hyphens in lodterrain.fsh/vsh.
# Changelog

Written when a version is released, not when a commit lands - see
[docs/RELEASING.md](docs/RELEASING.md). Newest first.

## [Unreleased]

## [0.8.7] - 2026-08-23
**Horizons G51 warm-join cake walls + matching fog + post-vanilla draw.** Ported DodenGruva/Horizons 0.3.23 seam repair: `AssumedCoveredSides` from `HasDataSet` (not RAM), water leaves assumed sides open, `SectionBecameResident` remeshes only neighbours that guessed (`meshedWithoutNeighbor`) â€” does not MarkChanged every InstallLoaded. Fixes quit+relog tall rectangular monoliths from warm SQLite cache. Fog: always upload live BlendedFogDensity/Min/cloud and always `getFogLevel`?`applyFog`/`applySpheresFog` so LOD matches vanilla haze in cloud; `DisableLodFog` only skips extra pastViewHaze. G50 flat-top light (`normal.y * 0.95`). Default opaque render order 0.38 after vanilla (toggle `PostVanillaDepthCulling`). Empty `CapturedColumns` children no longer pin parent coarse. Ocean seabed seal and settings UI unchanged. Credits: DodenGruva / Horizons adaptations.

## [0.8.6] - 2026-08-23
**Fixed: cake-slice gaps after backing away + dusty LOD hue pop.** Exploring up close looked correct (vanilla), then moving back opened vertical missing slices in mountains and a grey-lower / textured-upper break. Root cause: AllChildrenCovered skipped missing child slots, so a 3/4 L0 set unlocked descent and left holes once vanilla no longer covered them; ScheduleMeshJobs could still upload incomplete L0 thin1quad meshes. 0.8.6 requires all four child slots before descent, refuses/disposes incomplete L0 meshes (parent volume covers), pins complete near L0 against cold RAM eviction, and widens mid-band mesh demand + incomplete fill. Also: soft distance-based desaturate + fog/ambient hue wash on LOD albedo even when DisableLodFog kills density fog; when the camera is in thick fog/cloud, wash strengthens so ALL terrain ahead goes hazy (no crisp LOD popping through). Dusty mid/far greens match washed atmosphere instead of raw saturated atlas colors. Ocean seabed seal and settings UI unchanged.

## [0.8.5] - 2026-08-23
**Fixed: far flat cliff cards + mid-band voids together.** 0.8.4 skipped incomplete L0 leaves (good vs cake-slice pillars) but often left empty sky when the parent mesh was missing -- mid terrain/water in front of ridges became dark holes while far mountains stayed thin vertical cards. 0.8.5 always skips incomplete L0 draw, demands volumetric parent+grandparent remesh/fill (IncompleteFillPerTick 12), and widens CollectDrawNodes mesh demand so mid-band seals instead of falling through to void. Far mountain depth: WantedLevel ladder pushed outward further, relief-aware coarsen delays mountains up to 3 rungs, LodMip keeps minority height shelves from FidelityStep>=0.5 with wider canopy/relief slice bands. Trees/vegetation mid-far: softer mip floater gaps + mesher anti-floater so leaf crowns are not stripped before mountains show; thin mats retained through L3. Ocean seabed seal and settings UI unchanged. Success test: far view has volume + continuous mid coverage; walking closer still matches vanilla (data was always fine).

## [0.8.4] - 2026-08-23

**Fixed: far mountain cake-slice pillars / vertical cliffs.** Incomplete L0 sections (pre-0.7.7 thin1quad and partial capture fill) were meshed and drawn as isolated residual columns â€” missing slices left "towers" in the ridge. 0.8.4 refuses to mesh/draw incomplete L0 leaves, keeps the parent surface covering until columns fill, sweeps resident incomplete L0 for recapture when mapchunks are loaded, and reclassifies after capture. Softer mid/far WantedLevel ladder + stronger relief-aware mountain delay so ridges keep readable depth instead of Lego stairs. Far mesh demand widened so mid peaks are not a fog-only void. High-fidelity mip keeps minority height shelves on short solid slices. Ocean seabed seal and settings UI unchanged.
## [0.8.3] - 2026-08-23

**Settings UI spacing (aggressive).** SliderRow 96: label at y, slider at y+40 height 28, >=20px before the next label. Extra gap after Preset before the first Quality slider. BodyHeight 480 / FooterGap 20 so hint + Cancel/Apply/OK never collide. Same AddLabeledSlider rhythm on Quality / Generation / Visuals.
## [0.8.2] - 2026-08-23

**Settings UI spacing (again).** Generous DH-like gaps: ContentTop ~45 under title bar, SwitchRow 52, labeled sliders with >=10px under label + 26px track and SliderRow 76, TabHeight 36 / TabToBodyGap 24, BodyHeight 340, FooterGap 18. Same on all tabs. Fixes 0.8.1 still-cramped labels-on-tracks / Enabled / tabs / footer.
## [0.8.1] - 2026-08-23

**Settings UI spacing fix.** Clear top padding under the dialog title bar (no colliding title content row), taller horizontal tabs (~34px) with body below and >=16px gap so Preset never overlaps Quality, aligned Preset label+dropdown row, roomier switch (~42px) and labeled slider (~58px) rows, footer (hint + Cancel/Apply/OK) packed to content height. Same spacing on Generation / Visuals / Advanced. Behavior and presenter wiring unchanged.
## [0.8.0] - 2026-08-23

**Gen 1 settings UI.** In-game Distant Vistas dialog with Enabled master switch, quality presets (Performance / Balanced / Quality / Custom), and Quality / Generation / Visuals / Advanced tabs. Live-applies safe knobs (fidelity, detail, overdraw, fog, Enabled); persists `distantvistas.json` and, in singleplayer, `distantvistas-server.json`. Multiplayer clients see Generation as read-only.

**How to open:** `.distantvistas settings` or `.dvsettings`, or **Ctrl+F8**. Escape ? Settings native injection was not wired (VS has no first-party mod settings hook without ConfigLib/GuiCompositeSettingsEx); chat + hotkey are the reliable Gen 1 entry points.

**No engine regress:** 0.7.11 ocean seabed seal, horizon-first, checkerboard fix, white-tex harden, OverdrawStart default 0.55, DisableLodFog default true unchanged. Config keys extended only (`Enabled`, `QualityPreset`).

## [0.7.11] - 2026-08-23

**Fixed: ocean "cake slice" â€” cave ceilings/walls/floors visible through transparent water.** Root cause: `LodMesher` only culls solid faces by solid neighbours, so terrain (including hollows) stayed meshed under translucent ocean and read as cutaway seabed + caves, with mid-water overdraw amplifying it. 0.7.11 seals an underwater hull per column before face collect: detect the top contiguous `FlagWater` stack, take `waterBottom`, keep opaque meshing only for the contiguous solid seabed skin attached to that interface down to the first air gap, and drop opaque runs below that gap. Still emits water surfaces/walls, seabed tops under water, and walls that open into water (trenches/cliffs). Remesh-only â€” existing caches apply on the next mesh pass; no recapture required.

**Note (later):** optional Capture hardening to avoid storing deep under-ocean hollows is still useful for cache size / overdraw, but is not required for this ship.
## [0.7.10] - 2026-08-23

**Horizon-first peeks:** auto-gen no longer waits for a full-disk probe before painting far LOD. Peeks start at ring ~14 (just past live VD) and expand outward so continuous terrain appears in minutes without exploring; full radius still fills in background. Near-horizon peeks are not idle-gated (exploration is not required).

**White / missing-tex wash:** never paint near-white unknown.png / bad atlas samples (0xFCFCFC and HD-pack lookalikes). LOD falls back to grass/terrain colour and logs it. Sparse pre-0.7.7 one-quadrant L0 caches re-queue for recapture instead of drawing holes.

**Fidelity +1 (tunable FidelityStep / DetailDistance 320):** softer far coarsen, slightly higher mid-far leaf/veg retention, mountains delay one ladder rung. Anti-floater drops unsupported mid-air leaf/veg scraps after mip so ridges stay grounded.

## [0.7.9] - 2026-08-23

0.7.8 overdraw 0.35 / near-band sky soften made white mid-ground wash worse; restore OverdrawStart 0.55 and prior LOD fog/sky behavior. Checkerboard fix from 0.7.7 retained.

## [0.7.8] - 2026-08-23

**Fixed: severe white fog / sky-wash banding at the vanilla->LOD mid-ground seam.** Lowered
default `OverdrawStart` from 0.55 to **0.35** so LOD terrain starts drawing well under the
fogged vanilla edge instead of leaving a white mid-band where only height fog owns the
ground. Confirmed game shader overrides still force full alpha (`chunkopaque` /
`chunktransparent` / `chunkliquid`); `chunktopsoil` now also sets `rgba.a = 1.0`.
LOD `DisableLodFog` path still keeps `fogAmount=0` / no `pastViewHaze`; near-band sky
fade is softened so edge/sky dissolve cannot wash the overdraw ring white.

## [0.7.7] - 2026-08-23

**Fixed: regular checkerboard / 32-block cliff grid of missing LOD past the vanilla cut.**
Root cause was not CollectDrawNodes floater skip (0.7.6). LodPipeline.QueueColumn treated
"section key already in HasDataSet" as "this chunk is captured". An L0 section is 64 blocks
(2x2 vanilla chunks); the first ChunkDirty for any quadrant added the section key, and the
other three chunks of the same section were skipped forever. Live caches showed median
CapturedColumns=1024 (one 32x32 quadrant) â€” meshed as filled rectangles with vertical
cliffs into grey void in a regular grid. Isolated pregen used CaptureColumn (peek path),
which never applied that skip, so it looked continuous. 0.7.7 skips only when **this**
chunk's quadrant is already marked Captured on a resident section. Stats now log drawn L0
(sx,sz) parity and resident capture-fill (full/partial/thin1quad).

## [0.7.6] - 2026-08-23

**Fixed: empty mid-band + striped / checkerboard L0 pillars past the vanilla cliff.** Two
walk bugs stacked. (1) A meshed parent whose nearest edge was inside the vanilla bubble
(`insideVanilla`) but whose children were incomplete (remote-only / not all meshed) did
not descend â€” then hit `if (insideVanilla) return false` and drew nothing, so the ring
just past OverdrawStart stayed empty while scattered L0 floaters appeared further out.
`CollectDrawNodes` now always descends into `HasDataSet` children when
`insideVanilla && level > 0`, requests missing gate meshes via `AllChildrenCovered`,
and never draws the inside-vanilla parent. (2) L0 floater suppression skipped leaves with
`open >= 2` when a parent mesh existed. Incomplete coverage is often a regular stripe or
checkerboard of missing neighbour keys; that skip removed the drawn neighbours too and
read as vertical slabs / isolated pillars. 0.7.6 never skips drawing for open sides â€”
it only requests parent/neighbour fill-in. Mid-band meshing also marks transitional levels
just past overdraw so the walk can fill continuously when data exists.
## [0.7.5] - 2026-08-23

**Fixed: white fog slice / mid-mountain cut at the vanilla view-distance edge.** Vanilla
chunk shaders fade terrain alpha to the sky at the live view-distance horizon
(`chunkopaque`, `chunkliquid`, `chunktopsoil`, `chunktransparent`). That edge fade left a
bright fog band and severed mid-mountain silhouettes before Distant Vistas LOD could join
behind. 0.7.5 ships patched copies under `assets/game/shaders/` that force full alpha
(liquid keeps fresnel but drops the viewDistance clamp wrap). Config adds
`PatchVanillaEdgeFade` (default true); for this release the override is always packaged Ã¢â‚¬â€
unloading it still needs a restart / removing the mod. Does not touch fogandlight includes
or ambient `getFogLevel`.
## [0.2.1] - 2026-08-15

**Fixed: chiselled blocks drew as one flat wrong colour in the distance.** Reported as
purple. A chiselled block's colour lives in its block entity, and only a probe at the
block's exact position finds it - the world map colours chisel work the same way. The
LOD colour probe asked at a stand-in position, the centre of the chunk column, found no
block entity there, and fell through to the placeholder texture. So every chisel in a
section took the placeholder's colour. The probe now uses the block's own position, and
distant chisel work takes the materials it is made of. Chiselled terrain that came from
a server or from transient generation has no block entity to read. That now draws a
neutral grey instead of the placeholder colour. Ground already cached with the wrong
colour corrects itself when you visit it again.

**Fixed: reloading a singleplayer world crashed with "cache file not writable".** When
you left a world, the mod parked an open handle to the server-side cache in a connection
pool. The handle lived as long as the game process. On the next load of the same world,
the integrated server failed to open its own cache file. On platforms whose file sharing
blocks a writer while any handle is open, this failed every time. 0.1.0 had no
server-side cache, so this fault did not exist there. The mod now closes the handle,
and a check watches the process's handle table so that this stays true.

**Fixed: Vistas Beyond no longer switches this mod off.** Vistas Beyond was on the list
of LOD mods that this one defers to, a guess made from its name. It does not belong
there: it is a server-side worldgen mod that adjusts terrain generation and draws
nothing, so there is no conflict to avoid. The two together now give exactly what that
pairing promises: more dramatic terrain, visible from further away. Reported from the
field.

**Fixed: patches of distant terrain were solid black, and stayed black.** A server has
no texture atlas, so it stores no colour, and the client adds the colour on arrival. The
client also saves what it receives. So anything that stopped the colour step went to the
cache without colour and stayed there. That ground drew as pure black for as long as
that world existed.

What stopped it was a block code that failed to resolve. The mod kept that answer for
the rest of the session, and the lookup runs while a world still starts. So when one
common block lost that race, every section saved after it had no colour at all. The
measurement on a real world found 7 sections with no colour anywhere and 59 more in
patches, on ground as ordinary as soil, slate and tall grass.

The mod no longer keeps a failed lookup, and tries the code again instead. Sections that
were saved without colour get their colour from the texture atlas as they load, and the
mod writes them back. So a cache repairs itself as you play, and nothing is discarded. A
block that this game does not have now draws as plain grey, and the log names both the
count and the block codes. A black patch with no explanation cannot occur again.

**Fixed: joining a server before its cache existed switched the assist off for the whole
session.** The server reported the assist as "off" whenever its cache was empty at the
instant you joined. An empty cache is the ordinary state of a fresh server, and of any
server just before an admin runs `/vhgen`. The client took that answer as final and
ignored everything that the server sent afterwards. No amount of generation helped until
you relogged. The answer now says whether the server *will* serve, not whether it holds
anything at that second, and a server with nothing yet says so plainly.

**Fixed: a server cache that grew while you were online never reached you.** The server
sent its list of sections once, when you joined, and never again. A client only requests
sections that the server offered. So an admin who ran `/vhgen` while people played built
terrain that none of them had a way to request. `.vhwhy` reported `no-data` for ground
that the server held for hours, and a relog was the only cure. A sweep that finished
late did the same, and so did other players who explored. The server now offers what it
gained every few seconds, and `/vhserver` reports how many of those follow-up offers it
sent.

**Fixed: a section requested too early was lost for the rest of the session.** The
server answers every request, but it refused with the same empty packet both when it
will never have that section and when it simply did not write it yet. The client read
both answers as "never" and stopped asking. A server that sweeps or runs `/vhgen` is in
the "not yet" state all the time, so a player who joined in the middle of a run gave up
on sections whose data arrived seconds later. The two answers are now distinct, and the
client retries "not yet" a bounded number of times. An older client ignores the new
field and keeps the behaviour it had.

**Fixed: short freezes while exploring with a cold cache.** A capture that landed on a
section no longer in memory read and decompressed that section inline, on the game
tick. The worst measured case took 113 ms, which is more than two whole game ticks.
Capture now waits for the section to load in the background, and results apply in
order when it arrives.

**Faster.** Flat water broke into one rectangle per depth step of the seabed under it,
because the merge grouped surface quads by a depth they never draw. A flat sea now
collapses the way the mesher always intended: water quads over a sloping seabed went
from 602 to 35, with identical geometry on screen. Opening a world with a large cache
now reads only the keys it needs - 931 ms down to 13 ms on a 691 MB cache. The
singleplayer client now asks the server-side cache what it holds once a second, not
twice a frame, which cost up to 105 ms of main-thread time per second on a large
world. The renderer's per-frame walk drops a square root and a logarithm per node. The
sky colour is computed only for the horizon ring that uses it. And meshing a section
stops allocating 241 KB of scratch buffers - simple terrain now allocates less than
1 KB.

**Fixed: another LOD mod that is switched off no longer switches this one off too.**
0.2.0 went idle whenever Farseer, ChunkLOD, Vistas Beyond or TopoHorizon was loaded. On a server that
runs one of those, the game makes every client load it, and downloads it for you. So
"loaded" never meant "drawing". A player who switched Farseer off in its own dialog and
used this mod instead got no distant terrain from either mod.

This mod now reads Farseer's own switch, in `farseer-client.json`, and draws when
Farseer is off. There is nothing to configure: the setting is already on disk. For the
mods whose switch this mod cannot read, it still defers. `IgnoreOtherLodMods` in
`distantvistas.json` overrides that, and `.vhdefer on|off` sets it from chat. A change
applies at the next start, never in the middle of a session.

`.vhinfo` and the log line no longer tell you to remove the other mod. That is advice
that a player on such a server cannot obey.

**Fixed: an idle client made the game log a channel warning.** The deferral path
returned before it registered the network channel. The game then reported "Server sends
me channel name distantvistas, but no client side mod registered it" at every join.

**Fixed: a crashing client cleaned up almost nothing.** When the game crashed during its
own shutdown, our teardown ran off the main thread, where the engine refuses these
calls, and each refusal skipped every step behind it. That cost the storage writer's
shutdown, and then, once that was guarded, the release of every GPU mesh. Each step now
stands alone, and our own work runs before the engine calls that can refuse.

## [0.2.0] - 2026-08-03

**Chunk generation on request**, with `/vhgen start [radius] [x z]`. It builds the LOD
picture around a player, or around coordinates you give, for terrain nobody has visited.
It writes nothing to the savegame. Real worldgen runs transiently from the seed, through
the engine's `PeekChunkColumn`. A column that already exists loads normally instead, so
player builds stay correct.

The command needs the controlserver privilege, which every singleplayer host has. Config
ceilings and rate caps bound it. Generated terrain has no trees until a real visit
replaces it. Give both coordinates or neither: the command refuses one on its own, rather
than centring somewhere you did not ask for.

**The non-destructive promise is now measured, twice.** Every sweep and every generation
run re-probes sampled positions that did not exist before it. Each run then prints the
result, as "Verified 256/256 sampled absent positions still absent". So a worldgen mod
that breaks the promise is detected on the server where it happens, and not only in this
repo's test matrix. The check regimen also asserts, byte for byte, that an all-peek run
leaves the savegame's terrain tables identical.

The sample keeps clear of online players, because the engine generates terrain around a
player as ordinary play. A run centred on a player therefore still measures something,
instead of reporting "Verified 0/0".

**Fixed: a client can stop receiving terrain for the rest of a session.** A server
dropped queued section requests without answering them, in two places. The first was
when its cache was not open yet. The second was when a client asked for more than the
queue holds.

A client marks a key in flight when it asks, and forgets it only when a reply arrives. So
a dropped key was stranded. Sixteen of them filled the in-flight cap and blocked every
later request. The server now refuses out loud in both cases, and `/vhserver` counts the
refusals. This fits an intermittent stall seen in testing. It was never caught with
logging in place, so treat it as a defect fixed and not as a diagnosis confirmed.

**Fixed: a bad config file destroyed your settings.** A file that failed to parse was
overwritten with defaults, which deleted every hand-edited setting over one stray comma.
The file is now left untouched, the error names the problem, and defaults apply for that
session only.

**Also fixed.** The client now notices a server-side cache that appears mid-session, from
a sweep after a slow start or from a `/vhgen` run. Before, it looked once at join and
never again. The cache-format purge also reports how many sections it discards, instead
of deleting them in silence.

**Stays out of the way of other LOD mods.** With Farseer, ChunkLOD, Vistas Beyond or
TopoHorizon loaded, this mod now goes idle. Two LOD mods would fight for the camera far
plane and draw over each other. A server that runs one of those forces it onto every
client, while this mod stays optional, so this is the one that yields. `.vhinfo`
reports the idle state and names the mod it defers to.

**Optional server-side assist.** The mod is now Universal, with both
`requiredOnClient` and `requiredOnServer` false. Install it only on your client and it
works exactly as before, on any server, vanilla included.

Install it on the server too and it builds its own LOD cache from everyone's travels. It
then shares that cache with connecting clients on request. A fresh join, or a fresh area,
can therefore already be far. Before, it showed only what that one player had explored.

Server admins get `ModConfig/distantvistas-server.json` and a `/vhserver` status
command. The settings cover capture on and off, serving on and off, a serve radius per
player, and rate caps. Serving defaults on, at an 8192-block radius per player.

**Savegame sweeping**, on by default. A server now loads terrain the world generated in
past sessions, so the cache can be built from it at once. Before, the cache grew only
from terrain a player walked past again. Sweeping never generates new terrain. This
covers a dedicated server and singleplayer's own integrated server.

**Pre-generation**, separate and off by default. `PregenRadiusChunks` builds the cache
around spawn at startup, over terrain nobody has visited yet. A server can therefore offer
a horizon on the first join, instead of one that appears over weeks of play.

It now runs the same transient generation `/vhgen` uses. It costs worldgen time but **no
disk**, because the terrain is captured and thrown away. Earlier in this release it loaded
columns instead, which cost a few hundred MB at radius 64. It stays opt-in because it
still reveals map nobody has explored.

**Faster terrain fill-in.** Meshing runs on a thread pool. Before, one thread did capture
and meshing in lockstep, and exploring new terrain starved the mesher. Measured 2-3.5x
faster fill-in at the same load.

**Fixed:**
- LOD regions got permanently stuck coarse, with a hard, unmoving edge between detailed
  and blocky terrain. A wrong "does the server have this" check misfired on ancestor keys.
- LOD colour sometimes resolved to the wrong texture, on a block whose first texture has
  no baked colour. Confirmed on vanilla `fruitingbush-wild-blackberry`, and reported
  against a modded world.
- A false "discarding your cache" message appeared on every new install.
- Singleplayer ran two redundant copies of the whole pipeline in one process.
- Remote terrain now arrives nearest-to-you first, instead of in an arbitrary order.

**Internally**, a repeatable check suite (`scripts/check.sh`) now backs the correctness
claims. Before, each one rested on a hand-run sandbox session.

## [0.1.1] - 2026-07-25

0.1.0 shipped without the LICENSE in the zip. A review afterwards found three defects.
Ground-cover mats floated on mip-merged runs. Thin plants hid water, so shorelines
showed through. And an unbounded scheduler loop turned one frame into a six-figure scan.

## [0.1.0] - 2026-07-25

Initial release. Unlimited render distance, decoupled from the vanilla view-distance
slider. Real 3D terrain, not a heightmap: mountains, overhangs, cave mouths, forests, and
player builds all appear at distance. Translucent water over lake and sea floors. Live
seasonal colour and a derived snow line. Persistent per-world cache that keeps growing as
you play. Fully client-side, works on any server.
