## 0.7.90
- **Per-column captured colours (no whole-cell flat fill).** Palette rows are keyed by block id **and** stored colour, capture registers each column at its own position, and `BakeSectionFromVisit` samples `GetColor` per column top — splitting palette rows when neighbours differ. Fixes the hard-line 64-block checkerboard / manila plates from one averaged colour per block id across the L0 cell.
- **Exact visit-sweep priority unchanged (0.7.89).** Only live `GetColor` during the sweep; legacy heal deferred; baked band 3 displays stored RGB with no live tint wash; coarse plant-pull/noise only on unbaked far LOD.

## 0.7.89
- **Exact visit-sweep colours (priority).** Login bake now uses `BakeSectionFromVisit`: only vanilla `Block.GetColor` at each column top while teleported there. No shader-reproduced or greener-stable fallback during the sweep — rows stay unbaked until GetColor succeeds, and the miss audit re-queues those cells. Legacy palette heal is deferred for the sweep duration so approximate pre-heal cannot satisfy the audit early.
- **Baked band is display-only.** Fragment shader uses stored RGB directly on band 3 (`albedo = vertexColor.rgb`); live tint, snow overlay, valuenoise, and plant-pull never touch baked pixels. Greener topsoil bias applies only to legacy `StableColorOf` refresh, not visit capture or rebake untinted math.
- **0.8.46 coarse fallbacks (secondary).** Valuenoise plate-breakup and plant-pull remain gated to unbaked coarse LOD (`columnBlocks >= 8`) for live-tint / mip-parent paths that would otherwise invent manila plates.

## 0.7.88
- **Manila/tan grass fix (0.8.46 port).** Near L0/L1 keep flat greedy grass like 0.7.76: valuenoise plate-breakup and shader plant-pull run only on coarse sections (`columnBlocks >= 8`, L3+), not on near quads where they read as checkerboard micro-squares. Coarse grass tops get a far-only plant-pull toward live climate tint. Greener topsoil bake (`LodTopSoil.GreenerCoverage`) biases stable captures away from dirt-heavy manila. Climate field is 40-block cells with four-corner bilinear upload per section draw (unchanged wiring, denser lattice). On join/load, `RefreshStable` skips `FlagBaked` rows and `UpgradeLegacyEntries` heals old SQLite live-tint browns without a cache wipe.
- **Login sweep stack unchanged:** full-screen backdrop + gold arc title + loading panel, input lock, mute, freeze time, HUD hide (`.gui`), player invis + nametag hide, miss-list re-sweep before release/teardown.

## 0.7.87
- **Login sweep player hide (MP-aware).** During the visit sweep the local player entity is tinted fully transparent, first-person hands are hidden, and sneak is forced so vanilla suppresses the nameplate the same way crouching does. All state restores on teardown. Client-side only: other players on a dedicated server may still see a teleporting body because position is server-authoritative.

## 0.7.86
- **Login sweep overlay crash fix (critical).** `ComposeDialog()` no longer runs from `LevelFinalize` on fragile `WindowBounds`; visuals stay on the ortho render pass and a deferred `HudElement` input guard retries compose/open once the viewport is valid. Screen cover paints even if dialog open fails.
- **Creative during sweep.** Player switches to creative for fly teleports; prior gamemode (survival, etc.) restores on teardown/abort.
- **HUD hidden during sweep.** F4 / `.gui` hide-HUD path toggled for the sweep and restored exactly on release.
- **Player hidden during sweep (initial).** First-person camera forced so the local model stays out of view while teleports run.
- **Bootstrap timing + ETA.** Empty-cache bootstrap sizes a coast-biased L0 ring to ~1.5–2.5 min from measured capture rate; progress UI shows time remaining. Large existing visited canvases keep full sweep with honest ETA.

## 0.7.85
- **Login bake miss audit.** After the main visit sweep, audit every visited L0 cell for load failure, empty capture, pending capture, provisional capture, or incomplete bake. Re-queue and re-sweep only misses (overlay, mute, freeze, and input lock stay on) until the audit is clean, then drain mips, stabilize, and teardown. Progress UI names the retry pass.

## 0.7.84
- **Login bake colour match (critical).** Visit-sweep bake now samples vanilla `Block.GetColor` at each column top (with split climate + season fallback matching the live shader). Baked sections use alpha band 3 so the mesher and shaders display stored RGB exactly — no manila wash from live climate/season multiply, keep-origin ratio, snow overlay, or valuenoise.

## 0.7.83
- **Login bake teardown.** One idempotent `Teardown()` path on success, abort, error, and world leave — hides overlay, restores player pose, unlocks controls/mouse, unmutes audio, and unfreezes calendar time with no leftover state.

## 0.7.82
- **Login sweep overlay + input lock (critical fix).** Layout locked: (1) full-screen landscape photo `assets/distantvistas/textures/gui/login-backdrop.png`, (2) centered arched solid-gold title graphic `assets/distantvistas/textures/gui/login-title-rainbow.png` (rainbow shape, single gold — not multicolor letters), (3) loading panel below with progress %, bar and status. Paints every frame via `IRenderer` at `EnumRenderStage.Ortho`; solid dark fill and gold arched text only if assets are missing from the zip.
- **Input lock during bake.** Movement, look, jump, fly, sneak, sprint and mouse buttons cleared every tick; mouse grabbed; join camera pose held; sweep stops pin the player at each visit position so they cannot free-fly between teleports. Input-blocking dialog captures keyboard/chat/inventory shortcuts.
- **Audio muted during bake.** Client volume sliders (`masterSoundLevel`, `soundLevel`, `entitySoundLevel`, `ambientSoundLevel`, `weatherSoundLevel`, `musicLevel`) are saved, set to zero for the whole visit sweep, and restored exactly on release or dispose (including abort/leave world).
- **Time frozen during bake.** `CalendarSpeedMul` is saved and set to zero; a calendar speed modifier zeroes `SpeedOfTime` so day/season do not advance while teleports run. Prior calendar speed is restored on release or dispose.

## 0.7.81
- **Visit-sweep purpose locked.** Login pass exists to gather live season truth by being at each visited 64×64 canvas: re-capture loaded voxels (snow on ground, snowy/part-green trees, autumn tone) while teleported there under the full-screen overlay, season-bake per column top, persist to SQLite, propagate to parent mips so near and far LOD match until relog. Not finalize-time recolor of unloaded cache.
- **Drain phase.** After all visits, mips and SQLite flush behind the overlay before frame-time release.

## 0.7.80
- **Login visit sweep (replaces cache bake at finalize).** Full-screen opaque Distant Vistas loading UI covers the viewport while the client waits for the world/map to stream, then teleports the player (invisibly) to every visited L0 region so vanilla loads real terrain and the mod re-captures + season-bakes what is actually there now. Progress shows N/total and status; original pose is restored before release. No mid-play season remap after sweep (unchanged from 0.7.79).
- **Discarded:** immediate SQLite palette bake on `LevelFinalize` — cached rows lack current-season truth until chunks load and the player visits.

## 0.7.79
- **Login-time season bake.** On join, every cached visited section is painted once through Vintage Story's own `ApplyColorMapOnRgba` climate + season maps at each block's real X/Y/Z. A loading overlay with progress bar holds the player until the bake queue finishes and frame time settles; only then is play released. Baked palettes carry `FlagBaked` and tint slot 0 so the shader does not re-tint until the next relog.
- **No mid-play season remap.** After login bake, live `RefreshSeason` / gradual month repaint is off for visited land. Newly discovered chunks still use the live shader path until the next login. Alpine snow-line overlay is disabled once baked; ground snow on a section follows a majority vote over snow-eligible surface columns (grass/topsoil, not bushes).
- **Discarded approach.** Discover-bake at capture plus budgeted month repaint (0.7.79 draft line) was dropped in favour of one login sweep — simpler truth model, no idle remesh storms when the calendar changes.

## 0.7.78
- **Turn hitch frame budgets.** Look-only (no 64-block origin shift) no longer walks or mesh-requests tiles entirely behind the camera and outside the 15° lead cone; GPU meshes stay pinned. Per-frame memo for lead cone, frustum-in-view, land-like, and captured-beyond probes cuts repeated sqrt/frustum work during the quadtree walk. RenderDirty prune spreads to every 4th frame while idle so prune + walk + schedule do not stack on every yaw tick. FOV occlusion cache is lazy per tile (~10° yaw slack per entry) instead of wiping the whole table on small turns. Lead-cone L0 promote cap 2/frame (was 3). Cake ban, occlusion fail-open, pressure-only eviction, and 0.7.77 hysteresis unchanged.

## 0.7.77
- **Pressure thrash hysteresis.** Enter mesh pressure only after ~1.5s of sustained bad frames (enter at ~40ms p95 / ~37ms avg), and clear only after ~2.5s under a lower bar (~25ms p95 and ~28ms avg). Stops the 0.7.75/0.7.76 feedback loop where whole-client ~33ms deltaTime permanently latched pressure, eviction/remesh made frames worse, and FPS bounced 15–48. Eviction under pressure capped at 2 meshes/frame with a short cooldown so eviction itself cannot spike walk/draw. Mesh count alone still never opens pressure. Telemetry: `pressureEnter`, `pressureClear`, `pressureActiveMs`. Cake ban, FOV occlusion, fail-open unchanged.
- **Linux zip paths.** Packaging still forces forward-slash (`/`) entry names in the mod zip (from 0.7.76). Windows pack must not leave `\\` in nested asset paths or Linux clients/servers miss shaders and assets.

## 0.7.76
- **Linux zip paths.** Packaging now forces forward-slash (/) entry names in the mod zip. Windows pack used to leave `\` in nested asset paths, so Linux clients/servers could miss shaders and assets. Same game build; packaging fix only.
## 0.7.75
- **Pressure-only mesh eviction.** GPU meshes are never dropped just because there are "too many" or they sit past some distance. Eviction runs only when you are actually hurting: sustained bad frame time (~33ms+ p95/avg), and/or truly high managed memory (with hitch spikes). When pressure is on, only the oldest L0/L1 outside **2× view distance** may go; inside that ring visited land stays drawn. Disk cache stays. Soft mesh-count hint may reinforce pressure only after frame time is already bad. Telemetry: `meshPressure`, `evictOutside2x`, `evictBlockedInside2x`.
- **Turn hitch soften.** FOV occlusion (0.7.74) now uses a temporal cache, 6 samples by default, and a hard per-frame ray budget (48). Small yaw no longer re-rays every L0/L1 every frame; out of budget fails open (draw). Lead-cone L0 promote is capped to a few requests per frame so turning does not remesh-storm plates into full L0 (keeps the 0.7.69 lesson). Cake-plate ban (0.7.73) and occlusion fail-open stay.

## 0.7.74
- **Potato FOV occlusion.** At draw-submit, L0/L1 tiles cast a cheap heightfield ray (default 12 samples) against resident LodSection SurfaceYMax. Land truly behind a nearer ridge skips Submit this frame; disk/RAM cache stays. Peek margin default 32 blocks so peaks/towers that clear a ridge still draw; missing height data fails open (draw). L2 temporary cover and the 0.7.73 L3+ cake-plate ban are unchanged. Telemetry: `occCull`. Config: `FovOcclusion`, `FovOcclusionSamples`, `FovOcclusionPeekMargin`.
## 0.7.73
- **Cake plates from 0.7.72 parent cover - L3+ never whole in the view cone again; L1/L2 land-like cover only.** PreferParentCoverage still keeps hole coverage, but MayLeadConeCoarseCover / FillGaps / SubmitMeshedGaps hard-cap whole-plate draw at L2 in the lead cone. L3+ holes use AddGap + clipped ancestor fill (or wait for L1 remesh), not giant stacked squares.

## 0.7.72
- **Yaw no longer opens a sky hole in the lead cone.** Refusing L2+ without parent cover left nothing to draw and nothing to gap-fill. PreferParentCoverage now keeps a land-like parent until its children can replace it; refuse paths always AddGap so ancestors can clip-fill. Land-like L2 past 1.5x view distance may cover while L0/L1 remesh; flat plates stay banned.
- **Incomplete / thin L0 is not submitted alone** when a land-like parent can cover; remesh is requested and the parent fills.
- **Gap budget** raised from 384 to 1024 so a busy turn does not leave hundreds of captured footprints as sky for a frame.
- **Past 3x view distance** we only empty-stop when Farseer is actually drawing that band. With Farseer off, Distant Vistas keeps land-like cover instead of handing the skyline to nothing.
- **Eviction pins** L0/L1 that must cover intervening land, or whose parent is not drawable yet, so keep-circle pressure cannot punch the same holes.

## 0.7.70
- **Hills, then Farseer shadows, not fog and not giant squares.** Skyline in front is L1 (128-block tiles) out to 3x view distance. L2-L6 never submit as a whole plate in that cone. Past 3x we stop so Farseer's heightmaps can sit there as dark silhouettes. SkyTint 4.3 was a yellow wall; it is 0.4 so those shadows actually show. L0 stays inside 1.5x so turning does not remesh every 64x64. Overlay still off. Auto-join still 24 chunks.

## 0.7.69
- **Turning hitch.** Behind you we keep a cheap L2 plate. The 15 degree lead cone used to promote that plate to every 64x64 L0 the moment you looked at it. Log: opaque 20-38ms, tick 50-117ms, 1200 meshes, 5 GB GC in 30s. Fog cut still L0/L1. Past 1.5x view distance the plate stays a plate. Farseer stays out: overlay off, zip in Mods-disabled. Their SkyTint 4.3 was a yellow wall, not hills.

## 0.7.68
- **No Farseer.** Overlay inject is off. Their SkyTint 4.3 was a yellow sky wall, not hills, and yielding for them punched holes in us. Distant Vistas draws the far land. Auto-join peeks at 8/s / 8 in flight after the 15s spawn delay so the cube rim fills without them. Farseer zip is out of MAIN Mods.

## 0.7.67
- **0.7.66 punched holes in Distant Vistas.** With Farseer on we stopped drawing past 1.5x view distance (33 tiles handed off, 31 of them walked land). Farseer did not fill that band. We draw our land again. Overlay inner start is stock `0.785` so their spawn region can rasterize behind us. Auto-join no longer marks the player done before the 15s delay, so the 70k savegame sweep cannot jump the queue. Horizon peeks start at 7 chunks (224 blocks) instead of 14 (448), so the fog cut fills first.

## 0.7.66
- **Last shot at Farseer behind us, not instead of us.** Fog cut stays ours (walked, peek, cubes). Past 1.5x vanilla view distance we stop submitting so their heightmap can occupy that band. Overlay inner start is `viewDistance * 1.5`. SkyTint still clamped. No ReloadShaders, no re-register, no required Farseer dependency, no 256-chunk join job, no eviction under 1600 meshes. If the far band is still sky with `marker=present`, overlay is done.

## 0.7.65
- **New-world join was wedging vanilla.** Auto-join used the json 256-chunk / 32-per-second / 24-in-flight job (263k columns, ~190 minutes) on the same tick as spawn chunk gen, so the land in front of you sat unloaded. GPU eviction then dumped 394 peek meshes while only 55 were live, which is the cube ring and white void. Auto-join waits 15s, caps at 24 chunks / 4 per second / 4 in flight. Meshes stay until we are actually over the 1600 GPU budget. /dvgen can still go to 256.

## 0.7.64
- **New worlds were vanilla fog.** 0.7.63 turned off auto-join peek whenever Farseer was on, then handed every unvisited tile and every walked tile past view distance to Farseer. Their hills still do not show. A fresh world has no cache, so that left vanilla chunks and a sky wall. Auto-join peek is back. We draw our land. Farseer stays required and still sits behind.

## 0.7.63
- **Farseer is required.** Walked land next to you is this mod. Past vanilla view distance is Farseer's silhouette, always, so we stop submitting a thousand L0 meshes. GPU cap is 1600 meshes (was 4000 and never fired). Over-budget eviction is 32 a frame. Last session: 3132 meshes, 1187 L0 drawn, opaque 10-30ms with TrueScale HD. Farseer on or off did not matter because we were still drawing all of it.

## 0.7.62
- **Farseer hills in the fog cut.** 0.7.61 re-registered their `region` program after they compiled; the log said overlay stuck and the gap was still sky. We only write overlay bytes now. Inner start is stock `0.785` of view distance so that haze line has silhouettes. No `-512` far clip, no sphere fog, SkyTint still clamped. You do not walk there: heightmaps come from worldgen. Over 4000 GPU meshes, walked land past vanilla view distance yields to Farseer so the frame rate recovers. `.dvistas` reports pressure yield.

## 0.7.61
- **Farseer overlay stays on the GPU.** 0.7.60 wrote the overlay, then called `ReloadShaders()`, which reloaded Farseer's zip. Log went `marker=present` then `marker=MISSING`. Ceiling gone was SkyTint 0.4 on stock Farseer, not our GLSL. We recompile only pass `region` in place. No full reload. Overlay no longer sinks or overhead-discards the heightmap. Hills are darkened so they read against sky.

## 0.7.60
- **Farseer overlay actually applies.** `optionaldependencies` is not a Vintage Story field, so Farseer loaded after us and overwrote our region shaders. That was the sky-wall ceiling and the hole over your head: stock Farseer, not missing heightmaps. We now write the overlay into `farseer:shaders/region.*` after assets load and reload the program. `.dvfarseer` prints whether the marker is in the live asset. Overhead fragments (steep above the camera) discard; sampler-high Y sinks.

## 0.7.59
- **Farseer silhouette mountains.** Two things were sitting on top of them. SkyTint 5 plus a 0.4 slate ColorTint painted their heightmap as sky. Our auto-join peek (this world's file was 256 chunks) then drew cube terrain over the same land. Overlay now starts their disc in the vanilla fog band, keeps the far rim instead of clipping 512 blocks early, clamps SkyTint and ColorTint, and still kills the cloud-cut sphere fog. Peek-only tiles yield to Farseer. Auto-join peek stays off while Farseer is on. Walked land is still ours.

## 0.7.58
- **Fat-cache FPS.** 32 GB+ used to keep 6000 fine GPU meshes. RAM can hold that. The GPU cannot draw it plus HD vanilla at the same time. Cap is 4000 meshes / 2.5x keep-circle, same as the 20-32 GB box. Pair with Komet for the vanilla visibility sweep. Not OptiTime: Komet lists that combo as unsupported.

## 0.7.57
- **Farseer sky ring and missing hills.** Their inner disc starts at 0.785 of view distance, so you see a cylinder cut through the sky. SkyTint cranked to 5-10 plus mix-to-sky paints their heightmap as sky, so the silhouette is there and you cannot see it. We overlay their region shaders (no Harmony): inner start is 24 blocks, sphere fog is off, sky mix only at the far rim, SkyTint clamped to 0.4. Heightmaps were already on disk. Optional dependency so we load after Farseer and the overlay sticks.

## 0.7.56
- **Look-down cubes wait for a real down pitch.** Coarse fill used to kick in at 0.55 (~33 deg). That still has most of the sky in frame, so the hills in front turned into blocks while you could see the skyline. It now waits until 0.92 (~67 deg). Same delay on the skip-disc shrink and the shader near-sink. Straight down still coarsens for the FPS win.

## 0.7.55
- **Farseer no longer shuts this mod off.** If Farseer is installed we still draw. Farseer stays in the background as a fog silhouette. Our tiles sit on top where we have them. No Harmony patch on Farseer, so a missing or different Farseer cannot crash the client. ChunkLOD and TopoHorizon still idle us. I am not sure the mix looks right for everyone. Tell me if it does.
- **Colour shading.** Mountain leaves match mountain grass. Far vegetation uses climate at that place, not one global sample.
- **Spawn plates.** Looking down at spawn no longer paints a low-poly plate over loaded ground. Far coarse land past vanilla still draws.
- **Winter and seasons.** I thought winter was done in 0.7.14. It was better, but far autumn still snapped green while you walked until 0.7.49. This drop keeps that and the colour shading. On my main world winter looks right.
- Still in from 0.7.49: visited land stays, the view-distance sky circle is gone, mountain ridges no longer chop into caves.

## 0.7.54
- **Mountain leaves match mountain grass, and the fade keeps vanilla hue.** Far vegetation no longer takes one climate from the keep origin. A coarse field samples GetClimateAt at that XZ; grass, leaves, and bushes on one hill share it. Season weight uses that temperature, not a global sea-level byte.

## 0.7.53
- **Spawn plates stay locked.** A coarse L1+ tile you are standing inside never paints its whole footprint. The walk descends and clip-fills only gaps vanilla does not own. Far L1/L2 past the coverage radius still draw whole. Peek cubes remesh while you stand still so spawn is not frozen terrain blocks.

## 0.7.49
- **Far autumn no longer snaps back to green while you walk.** Climate and seasonWeight were resampled at your feet. One warmer GetClimateAt, or the shader adding tree-top height onto worldgen temperature (vanilla only does that to undo lapse), zeroed the season mix on every distant canopy. Tints now sit on the keep origin and only move when you travel a few hundred blocks. Tree tops keep the calendar.
## 0.7.48
- **The view-distance sky circle is gone.** 0.7.47 hid every LOD pixel inside vanilla view distance. Raising that slider grew a perfect hole before the chunks existed, so you saw sky and caves in a ring that moved with the setting. A tile now goes to vanilla only when every map-chunk covering it is actually loaded and the whole tile sits inside view distance. Until then LOD stays. The shader no longer cuts a camera-locked sphere. Isolated 0.7.48; still not a public push.
## 0.7.47
- **LOD stays off vanilla.** 0.7.45 drew captured L0/L1 from 0.55Ã— view distance while vanilla still owns the ground out to 1.0Ã—. A straddling L1/L2 painted its near half over loaded chunks: brown cubes on the tree in your face, stair-step fight on the hill, flicker on ice. The fragment shader now drops any LOD pixel still inside view distance (unless you are high enough that vanilla dropped that column). A refused parent next to you is not submitted. Foam-white water (atlas sparkle / missing-tex) is forced back to lake blue so streams are not ice. Isolated 0.7.47; still not a public push.
## 0.7.46
- **Looking down no longer paints LOD on the ice under your feet.** Straight-down used to shrink the vanilla skip disc to nothing, so the walk treated loaded chunks as sky and drew the coarse mesh on top. Ice flickered (two translucent surfaces), trees next to you turned into brown cubes, and the polys went to hell at a steep pitch. The 3D sphere still owns the column you are standing in; look-down only uncovers when you are high enough that vanilla actually dropped that ground. The shader keeps the 5-block sink next to the camera when you look down, so any leftover overlap sits under vanilla instead of z-fighting. Isolated 0.7.46; do not push 0.7.45 until this is signed off.
## 0.7.45
Far land keeps the season you are in, and the sky holes are gone.

**Seasons.** Grass and trees at distance follow the same calendar as the ground under your feet. Walk out of vanilla range in autumn and it stays autumn. Rock, snow, and water do not get a fake seasonal wash.

**Holes.** Land you already walked does not turn into blue rectangles when you back away. New land you generate stays when you leave. Mountains do not get punched down into their caves. The moving sky ring at view distance is gone. If you were just standing on it, it stays on screen.

How we got there, in the versions that led here: capture no longer drops columns when you fly (0.7.43), a parent never paints a green cube over vanilla chunks next to you (0.7.43), cliff faces survive the L0â†’L1 merge (0.7.44), and the walk no longer hides a mesh you already have just because it wanted a coarser parent (0.7.45). A leftover gap that already has a GPU mesh is drawn instead of handed up to a parent that remip deleted. Missing parents rebuild from their children this tick instead of stalling on a read that will fail.
## 0.7.44
- **Sky slits in the spawn hills when you back away: the L1 mip was throwing the cliff face away.** Not missing capture and not something a kill or relog fixes. Up close you were on L0 (the real columns). At the L0â†’L1 ring the walk hides that L0 and draws the parent. The parent merge required 2 of the 4 child columns to share a height before it kept a slice. A cliff or ridge almost never does â€” one column is the face, the other three are the ground below â€” so the face was dropped and you looked through to sky and caves. The ring is camera-centred, so as you walk away from spawn the slits slide toward you across those hills and then stop (the rest of the world is not that cliff). 0.7.44 keeps any solid coverage (1-of-4); snow/missing-tex still needs 3-of-4; plant scraps still drop. Derived parents rebuild from the existing L0 (mip v8). Isolated only; MAIN stays 0.7.34 until you sign off.
## 0.7.43
- **New land you just generated stays when you leave, and the green cubes next to you are gone.** Two stacked failures, neither of them height. (1) Capture was dropping columns: `QueueColumn` silently refused anything past 200 pending, then applied one result a tick (20/s). Flying over fresh land at view distance streams three to four times that; the extras never captured, vanilla covered them while you stood there, and they became sky rectangles the moment you left. The cap is 16384 and counted (`dropped` in `.dvistas`); the worker takes 8 schedules / 32 in flight; applies go up to 8 a tick under a 4ms budget when results stack. A loaded-chunk sweep walks one row of the view-distance square each tick and re-queues any quadrant that is still missing (or only peeked), so a column that lost the race with unload or a half-read no longer stays a hole for the rest of the session. (2) The cubes were not z-fight. Gap-fill's parent, after walking children that sat inside the vanilla bubble and correctly returned "vanilla owns this", painted its whole L1 mip over those children: 2-block-wide max-height columns and solid tree pillars right next to you. A parent that touches the skip sphere now fills only the gaps, clipped, same as any other; `FillGaps` uses the same 3D inside-vanilla test as a drawn tile. Peeked / foreign L0 (worldgen stopped at Terrain, no trees) is marked provisional on disk and recaptured when you actually load the chunk, so a forest you fly through is not frozen as bare hills. Isolated only; MAIN stays 0.7.34 until you sign off.
## 0.7.42
- **Holes in visited land are gone as a class: a captured tile is never sky while any coarser mesh of it is on the GPU.** Every hole so far - the mid-band rectangles, the moving arc, the "holes places" in a fresh world - had the same shape underneath: the walk wanted a fine mesh that was not there yet (unmeshed, evicted outside the keep-circle, still loading, still coming from the server, half-captured) and a rule said the coarser parent may not stand in for it (L2+ in front, plates in front, the 0.7.20 no-parent-box rule, the frontier not being "sealed"). Each release fixed one rule and the next hole came through another. 0.7.42 stops arguing with the rules and fills the gap directly: as the walk unwinds, every captured footprint that nothing drew is handed up until an ancestor with a resident mesh paints it, clipped in the shader to exactly that footprint (`clipRect`). Siblings that did draw are untouched, so nothing collapses to a coarser rung and nothing pops when the child arrives - it lands on top and the clipped draw is dropped. Horizon cone, frontier, any rung: a coarse mip is the same captured land at lower resolution, and the only alternative was a hole. Vanilla-owned ground is still never painted. The whole-plate rules stay for fidelity: L2+ and plates in front still walk to L0/L1 and stand in only where those are missing. Incomplete L0 (2-3 of 4 quadrants) draws the quadrants it has instead of hiding them; the never-captured quadrants have no data at any rung. 0.7.40's sealed-interior takeover (retract siblings, draw the parent whole, L1/L2 only) is replaced by this. Two request leaks fixed on the way: an ancestor key whose mip row does not exist yet no longer sits in the mesh queue winning a nearest-first slot every frame and failing it (it asks the mip pipeline for the row instead), and the coarse-fill budget is charged only for a request that is new this frame, so one pending target cannot starve the rest of the walk. Sections whose mesh comes back with no geometry are remembered instead of rebuilt every frame. What is left after this is a real hole - captured land with no mesh at any rung above it - and the client log now says so every 10 seconds with the key states involved (`Unfilled gaps this frame: ...`); `.dvistas` shows `gap fills` and `unfilled gaps`. Isolated only; MAIN stays 0.7.34 until you sign off.
## 0.7.41
- **The mountain chop: ridges cut down to a wall from a certain distance, gone when you walk up.** Not a draw-walk hole and not the shader. It was the mip. Since 0.7.10 the child-to-parent anti-floater dropped every run above the first air gap in a merged column, meant for leaf pixels the 1-of-4 canopy vote left hanging. A cave is an air gap. Wherever two of the four children shared a cave room at the same height, the merged column sank to the cave floor: everything above it - rock, soil, grass, the ridge line - was thrown away as a "floater". Measured on the real cache: 16% of all L1 columns sat 12+ blocks under the L0 surface, 8% sat 40+ under it, every one of them over a cave. L1 starts at the 0.5x ring and L2 at the 1.0x ring, so from there out a mountain was a field of 2x2 / 4x4 / 8x8 shafts with the intact columns standing between them as pillars and one-colour walls - the "single tower" / "smear of one wall" - and a big cavern under a ridge took the whole ridge top with it, which is the cut-in you saw travel with the camera, and the sky through it. Up close L0 is exact, so it was never a hole from the air. 0.7.41 walks the merged column in stacks and only drops a floating stack that is plant matter through and through (leaves, grass tops, thin cover, with any snow on it); a floating stack with rock, soil, ore, or anything untinted in it is terrain over a cave, stays, and supports what sits above. The mesher's mid-far anti-floater is plant-only for the same reason: a one-block soil or ore run at a cave ceiling is the bottom of the terrain above it, not a scrap. Derived parents rebuild from the existing L0 cache on first load (mip v7) - L0 captures are untouched. Sealed-interior draw (0.7.40) and everything before it unchanged. Isolated only; MAIN stays 0.7.34 until you sign off.
## 0.7.40
- **The curved sky gap that moves with you, with mountains still drawn past it.** From the air: foreground fine, then a camera-centred arc of blue sky with sharp vertical cookie-cutter faces and barcode strips of missing 64-wide tiles, and far ridges still standing beyond the gap. Not vanilla view distance and not a world hole - the draw walk was drawing the far L2/L3 and submitting nothing in the mid band. Past the 1.0x ring the walk wants L2, the lead cone refuses to draw L2 in front (the 0.7.34 cake-shelf ban), and any visited L0 that was unmeshed, incomplete, or skipped was simply left out. 0.7.39 kept a sibling L0 when a parent yielded; it still returned sky for incomplete L0 and still had no parent fill. 0.7.40 seals the interior: captured land in the lead cone with captured land past it (probed along the camera ray, so a frontier tile does not count as interior because land is loaded in some other direction) must draw something. Complete L0/L1 when it exists; otherwise the resident L1 or L2 mip takes the whole footprint, retracting the partial siblings so two surfaces do not fight, and flipping to full detail once all four children are ready. Approaching a tile that has L1 keeps L1 until L0 exists instead of hiding it and waiting; backing away never burns an L0 remesh just to coarsen it. Incomplete L0 under the seal draws its captured quadrants until the parent mip is resident. Takeover is bounded to L1/L2 (256 blocks), never over vanilla-owned ground, never when MaxVisualLevel forces L0, and the in-front horizon still walks to L0/L1 - a standalone L2 shelf on unexplored land is still banned. Sealed L1/L2 that has no mesh yet gets one (request or mip from children). The shader far discard now sits on the real captured rim: `dist == 1` is `farViewDistance` itself (the extra `- 512` put the far cut one ring inside the land we hold), and the far edge is floored by the captured extent so evicted interior meshes cannot pull the discard cylinder inward. Isolated only; MAIN stays 0.7.34 until you sign off.
## 0.7.39
- **Mid-ground sky rectangles and the forest/bare stripe: land you already visited stays drawn when land past it is drawn.** Capture was never the problem - the row was on disk. The draw walk is a moving wanted-level window, and past the 1.0x ring it coarsens to a parent, stops remeshing visited L0, and skipped tiles per child. Two visited L0 under one L1 do not have to agree: relief bumps the flat one's wanted level, so it stepped aside for the L1 mesh while its hilly sibling drew L0. The L1 then never drew (a child drew, and 0.7.20 forbids the parent box), so the flat tile was a white 64x64 - and where the sibling was forest, the cut showed up as a hard wall of trees ending in bare ground. A tile whose far corner is nearer than the farthest mesh we hold now counts as *intervening*: in the lead cone it is visited, requested however far out it sits, and never skipped as too fine. Children that do step aside are parked until the parent actually draws, and submitted if it does not. A mesh drawn this frame is never evicted, and a key the walk asked for last frame is not pruned before it can reach the mesher. Incomplete L0 (2 or 3 of 4 quadrants, cold on disk) re-queues for capture instead of staying a permanent hole. No parent plates: holes fill with the real captured L0/L1, and the horizon L2+ shelf ban is unchanged.
## 0.7.38
- **Camera-locked chopped circle, square ring holes, sky outline on ridges, green ice caps, look-down 64x64 sky squares.** Two stacked skip-disc bugs punched a moving sky circle at vanilla view distance: the fragment shader discarded `dist < 0` (camera-relative XZ, so a disc that travels with the player), and the CPU treated a whole 64x64 as vanilla-owned as soon as the skip circle *touched* it. 0.7.38 only skips a tile whose farthest corner is still inside the 3D skip sphere, never discards the near side of `dist`, and keeps the 5-block sink so loaded vanilla floors still win depth (the 0.7.36 z-fight). Looking down still zeros the sink. Looking down also used to put the whole ground in the lead cone, which refuses L2+ so the horizon is not a cake shelf; any incomplete or unmeshed L0 then punched a white 64x64 through the plains. At a downward pitch the parent mesh is coverage, including flat land; vanilla-owned ground still does not get LOD on top. Snow, glacier ice, and lake ice no longer take a grass climate multiply; that was valley green copied onto a snow-row high tint, which painted high ridges as forest-green plates. Glacier ice is opaque ice, not a see-through lake. Farseer slate missing-tex is still repaired; cyan ice is not stolen into grass. Isolated 0.7.38 with Farseer drawing; leave MAIN Mods on 0.7.34 until you sign off. Komet (mods.vintagestory.at/komet) is a vanilla render-loop patch, not far terrain: it can help FPS and vanilla chunk-border holes while they stream in, and Distant Vistas still draws next to it. It does not replace this skip-disc or ice-cap fix.
## 0.7.26
- **Far land keeps the current season.** Stored LOD albedo has climate (or grey-green atlas) but not a live calendar clock, so walking out of vanilla range snapped autumn grass and trees to summer grey-green. 0.7.26 treats season the same way 0.7.24 treats night: climate stays in the slow tint table, and the shader mixes the season map from the current calendar every frame (`seasonRel`). Vegetation keeps autumn orange at distance; rock, snow, and water do not get fake autumn. Night lighting, visited L0/L1 trail, and no-parent-plate meshing stay. Isolated pending install; leave MAIN Mods on 0.7.25.
## 0.7.25
- **Visited land stays on screen when you fly past.** Captured L0/L1 stays drawn behind the camera instead of dropping to sky, and fill no longer stalls when you fly past already-generated land. Night lighting unchanged from 0.7.24. Isolated pending install; leave the live 0.7.24 session alone.
## 0.7.24
- **Night far land matches the near ground.** 0.7.23 already fed live fog, but the LOD shader still lit captured albedo with Calendar.SunColor * daylight. That disc color stays sunset orange after vanilla chunks have gone dark purple, so far sand was a glowing yellow band at night. 0.7.24 multiplies the stored albedo by the same live ambient the chunk shaders use, one clock for the whole horizon. DisableLodFog still only skips extra pastViewHaze. Daytime 0.7.23 look stays; visited-keep and 0.7.20 meshing stay. Isolated pending install; leave the live 0.7.23 session alone.## 0.7.23
- **Keep the trail. Wanted-level was a moving window.** 0.7.22 stopped dumping GPU meshes after 8000, but the walk still only meshed a ring around the camera (wanted-level plus a few tiles past view distance). Land you had already visited, including behind you, never got a mesh once that window moved, and the 1-minute unselected sweep cleaned up the rest. 0.7.23 draws every captured L0/L1 that is in view, same verts as when you were there. Just-left / RAM pin is tenfold. Recycle still spills far RAM to disk and pages the same-quality mesh back; it does not coarsen, and it does not punch sky in the trail. Look-down vanilla-owns and 0.7.20 meshing stay. Isolated pending install; leave the live 0.7.22 session alone.
## 0.7.22
- **No more Horizons cake squares, and no dumping land after a fill.** 0.7.21 kept parent tiles on screen until children meshed, and after about 8000 GPU meshes it started dropping the good L0/L1 onto those parents. From the air that looked like Vintage Horizons - giant flat plates with sheer walls. 0.7.22 goes back to 0.7.20 meshing: a missing child does not get a parent box as a stand-in, and there is no mesh-count cap that offloads visited land into sky. Looking straight down still uses 3D vanilla-owns plus pitch so already-meshed LOD does not vanish. Just-left ring still fills first. RAM can spill to disk; GPU meshes of land you can see, or just left, stay. 0.7.20 grass tint stays. Isolated and regular Mods.
## 0.7.21
- **Keep the visited picture, including look-down.** Two stacked failures: (1) vanilla-owns used a horizontal disc, so looking down from altitude hid already-meshed LOD (vanilla was not drawing that ground; pitching to the horizon filled it back in). (2) After a stretch of fill the walk stopped meshing and then dropped GPU meshes when RAM spilled to disk, so visited land unloaded into sky. 0.7.21 treats vanilla-owns as 3D plus pitch (straight down draws LOD; vanilla still occludes where it actually has chunks), draws parent/grandparent until children mesh, fills the just-left ring first, never disposes a mesh that is the only coverage, and under memory pressure coarsens the farthest fine tiles that already have a parent (spill stays on disk; LoadPersistedCache stays true). No hard tile cap that stops generation while the view has holes. 0.7.20 grass tint and 3-of-4 snow stay. Isolated and regular Mods.
## 0.7.20
- **SP far LOD grey.** 0.7.19 skipped live climate tint on any mid-luma top with chroma 24 or more, so TrueScale / vanilla grass (dull olive, not pure grey) drew as raw atlas grey. Walking into vanilla turned green; walking back went grey again. 0.7.20 keeps the climate slot on greyscale and olive grass, and only skips tint on stored colours that are already brown earth (red well ahead of green, chroma 48). The 0.65 luminance clamp that crushed greens toward grey is back to 0.7.18's 0.78, and only pulls bright (toward-white) samples. Snow-row high samples still copy the valley climate colour, but not if that valley sample is itself grey/identity. L0 hold at the vanilla ring and 3-of-4 bright-cap majority stay. Derived parents rebuild (mip v6). Isolated and regular Mods.

## 0.7.19
- **Walk-away white caps (visit then handoff).** On a remote server, walking the Wendell north mountains looked correct in vanilla, then those caps went white once vanilla unloaded and LOD took over. Single-player never did this. Two things stacked after the visit: WantedLevel coarsened onto a parent mesh (L0 was never meshed while vanilla covered it, and mesh jobs for that L0 were pruned), and live climate tint was still applied to captured grass/soil tops whose albedo was already rock or dirt. 0.7.18's 3-of-4 bright cap only treated unknown.png (0xF0 white), so TrueScale / atlas near-whites still elected a whole 64x64 cap, and sea+160 luminance clamp still left a snow-row multiplier on greyscale grass. 0.7.19 holds complete visited L0 one extra rung at the vanilla handoff, meshes that L0 in the outer vanilla ring, treats luma>=200 low-chroma as a bright cap (real 3-of-4 snow stays), copies the valley tint when the high sample is a snow row, and skips live tint on already-coloured rock/dirt tops. Neighbour-steal now runs when a parent is rebuilt from L0, not only on disk load. Derived parents rebuild from the existing L0 cache. Isolated and regular Mods.

## 0.7.18
- **Far white mountain caps on some map slices.** High peaks in one area went plastic white as soon as vanilla dropped them to LOD, while other hills at the same distance stayed green and brown. Two things stacked: the high climate tint was sampled 320 blocks above sea, where the colour maps go snow-white, so greyscale grass on those peaks multiplied to white (rock sides, which have no tint, stayed correct); and coarse mip could elect a lone snow or missing-tex column for a whole 64x64 cap. 0.7.18 samples the high tint lower, clamps a slot so it cannot bleach to white, lets a bright cap win mip only with a 3-of-4 majority, and paints unknown blocks with a neighbour rock/dirt/grass colour instead of white. Derived parent meshes rebuild from the existing L0 cache. Isolated and regular Mods.

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
- **Winter / distant colour match.** Isolated 0.8.11Ã¢â‚¬â€œ0.8.15 LOD-walk experiments are reverted; engine is 0.7.11 again. The ModDB report was far landscape not matching near terrain, especially in winter: seasonal maps are 2D and the engine picks the row from a hash of each block, so one sample painted every distant field with a single texel (green vs dead-grass brown). Grass overlay is colour-mapped in vanilla while the dirt showing through is not; multiplying the whole composite by the winter tint browned the dirt. 0.7.12 averages a lattice of colour-map samples per tint slot and dilutes the live tint by the untinted dirt share, same as vanilla `chunktopsoil`. Public listing stays 0.7.11 until this proves in-game.

## 0.8.15
- *Reverted* with 0.8.11Ã¢â‚¬â€œ0.8.14. Isolated LOD-walk experiments; 0.7.12 restored 0.7.11 and only changed seasonal tint.

## 0.8.14
- **Restore parent coverage for open-side frontier.** 0.8.13 drew 0.8.12 surface-only tiles. In-game that was gray rectangular pillars after leaving an area (vanilla unloaded, pancake/stub LOD stayed) and sliced gray mountain faces; far walk also barely selected. 0.8.14 hides incomplete L0 and open-side sections behind parent/grandparent again. 0.8.12 mesher still does not build missing-neighbour cake walls. Isolated 0.8.14; public listing stays 0.7.11.

## 0.8.13
- **Draw surface-only frontier.** 0.8.12 stopped the mesher from building cake walls when a neighbour was not in RAM; the renderer still hid those meshes (and refused to enqueue them) until the neighbour ring filled. 0.8.13 lets complete open-side sections mesh and draw as surfaces. Incomplete/sparse L0 still PreferParentCoverage. OnSectionBecameResident still remeshes when a neighbour lands so real cliffs appear. Isolated 0.8.13; public listing stays 0.7.11.

## 0.8.12
- **Mesher: do not build cake walls.** If a neighbour section is not in RAM, CollectSide used to treat that edge as the end of the world and emit a full-height dirt face (green top + tan sides). 0.8.11 hid those meshes after the fact; 0.8.12 never builds them. Real cliffs against a loaded shorter neighbour still emit. Missing sides are tracked so a later neighbour load remeshes. Isolated 0.8.12; public listing stays 0.7.11.

## 0.8.11
- **Fix live-travel cake boxes (NEW world / cold-join).** Gen trails the player; incomplete L0 and true missing-neighbour frontier sections were meshed/drawn as full-height green-top slabs with tan sides and stayed on screen after flying away. 0.8.11 never meshes or draws frontier (open HasDataSet side) or incomplete L0 Ã¢â‚¬â€ PreferParentCoverage keeps parent/grandparent silhouette until columns and the neighbour ring cover. CollectDrawNodes also hides AssumedCoveredSides meshes behind a parent until seam repair lands, so temporary cliffs cannot persist mid-far. IncompleteFillPerTick 24. Cold-join, crash-safe no PauseGame/ApplyZFar-before-matrices, and ASCII shaders unchanged.

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

## [0.7.45] - 2026-09-02
Far land keeps the current season. Visited land stays on screen: no mid-band sky rectangles when you back away, no disappearing newly generated ground, no cave punches through ridges, no camera-locked sky ring at view distance.

## [0.8.7] - 2026-08-23
**Horizons G51 warm-join cake walls + matching fog + post-vanilla draw.** Ported DodenGruva/Horizons 0.3.23 seam repair: `AssumedCoveredSides` from `HasDataSet` (not RAM), water leaves assumed sides open, `SectionBecameResident` remeshes only neighbours that guessed (`meshedWithoutNeighbor`) ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â does not MarkChanged every InstallLoaded. Fixes quit+relog tall rectangular monoliths from warm SQLite cache. Fog: always upload live BlendedFogDensity/Min/cloud and always `getFogLevel`?`applyFog`/`applySpheresFog` so LOD matches vanilla haze in cloud; `DisableLodFog` only skips extra pastViewHaze. G50 flat-top light (`normal.y * 0.95`). Default opaque render order 0.38 after vanilla (toggle `PostVanillaDepthCulling`). Empty `CapturedColumns` children no longer pin parent coarse. Ocean seabed seal and settings UI unchanged. Credits: DodenGruva / Horizons adaptations.

## [0.8.6] - 2026-08-23
**Fixed: cake-slice gaps after backing away + dusty LOD hue pop.** Exploring up close looked correct (vanilla), then moving back opened vertical missing slices in mountains and a grey-lower / textured-upper break. Root cause: AllChildrenCovered skipped missing child slots, so a 3/4 L0 set unlocked descent and left holes once vanilla no longer covered them; ScheduleMeshJobs could still upload incomplete L0 thin1quad meshes. 0.8.6 requires all four child slots before descent, refuses/disposes incomplete L0 meshes (parent volume covers), pins complete near L0 against cold RAM eviction, and widens mid-band mesh demand + incomplete fill. Also: soft distance-based desaturate + fog/ambient hue wash on LOD albedo even when DisableLodFog kills density fog; when the camera is in thick fog/cloud, wash strengthens so ALL terrain ahead goes hazy (no crisp LOD popping through). Dusty mid/far greens match washed atmosphere instead of raw saturated atlas colors. Ocean seabed seal and settings UI unchanged.

## [0.8.5] - 2026-08-23
**Fixed: far flat cliff cards + mid-band voids together.** 0.8.4 skipped incomplete L0 leaves (good vs cake-slice pillars) but often left empty sky when the parent mesh was missing -- mid terrain/water in front of ridges became dark holes while far mountains stayed thin vertical cards. 0.8.5 always skips incomplete L0 draw, demands volumetric parent+grandparent remesh/fill (IncompleteFillPerTick 12), and widens CollectDrawNodes mesh demand so mid-band seals instead of falling through to void. Far mountain depth: WantedLevel ladder pushed outward further, relief-aware coarsen delays mountains up to 3 rungs, LodMip keeps minority height shelves from FidelityStep>=0.5 with wider canopy/relief slice bands. Trees/vegetation mid-far: softer mip floater gaps + mesher anti-floater so leaf crowns are not stripped before mountains show; thin mats retained through L3. Ocean seabed seal and settings UI unchanged. Success test: far view has volume + continuous mid coverage; walking closer still matches vanilla (data was always fine).

## [0.8.4] - 2026-08-23

**Fixed: far mountain cake-slice pillars / vertical cliffs.** Incomplete L0 sections (pre-0.7.7 thin1quad and partial capture fill) were meshed and drawn as isolated residual columns ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â missing slices left "towers" in the ridge. 0.8.4 refuses to mesh/draw incomplete L0 leaves, keeps the parent surface covering until columns fill, sweeps resident incomplete L0 for recapture when mapchunks are loaded, and reclassifies after capture. Softer mid/far WantedLevel ladder + stronger relief-aware mountain delay so ridges keep readable depth instead of Lego stairs. Far mesh demand widened so mid peaks are not a fog-only void. High-fidelity mip keeps minority height shelves on short solid slices. Ocean seabed seal and settings UI unchanged.
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

**Fixed: ocean "cake slice" ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â cave ceilings/walls/floors visible through transparent water.** Root cause: `LodMesher` only culls solid faces by solid neighbours, so terrain (including hollows) stayed meshed under translucent ocean and read as cutaway seabed + caves, with mid-water overdraw amplifying it. 0.7.11 seals an underwater hull per column before face collect: detect the top contiguous `FlagWater` stack, take `waterBottom`, keep opaque meshing only for the contiguous solid seabed skin attached to that interface down to the first air gap, and drop opaque runs below that gap. Still emits water surfaces/walls, seabed tops under water, and walls that open into water (trenches/cliffs). Remesh-only ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â existing caches apply on the next mesh pass; no recapture required.

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
CapturedColumns=1024 (one 32x32 quadrant) ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â meshed as filled rectangles with vertical
cliffs into grey void in a regular grid. Isolated pregen used CaptureColumn (peek path),
which never applied that skip, so it looked continuous. 0.7.7 skips only when **this**
chunk's quadrant is already marked Captured on a resident section. Stats now log drawn L0
(sx,sz) parity and resident capture-fill (full/partial/thin1quad).

## [0.7.6] - 2026-08-23

**Fixed: empty mid-band + striped / checkerboard L0 pillars past the vanilla cliff.** Two
walk bugs stacked. (1) A meshed parent whose nearest edge was inside the vanilla bubble
(`insideVanilla`) but whose children were incomplete (remote-only / not all meshed) did
not descend ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â then hit `if (insideVanilla) return false` and drew nothing, so the ring
just past OverdrawStart stayed empty while scattered L0 floaters appeared further out.
`CollectDrawNodes` now always descends into `HasDataSet` children when
`insideVanilla && level > 0`, requests missing gate meshes via `AllChildrenCovered`,
and never draws the inside-vanilla parent. (2) L0 floater suppression skipped leaves with
`open >= 2` when a parent mesh existed. Incomplete coverage is often a regular stripe or
checkerboard of missing neighbour keys; that skip removed the drawn neighbours too and
read as vertical slabs / isolated pillars. 0.7.6 never skips drawing for open sides ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â
it only requests parent/neighbour fill-in. Mid-band meshing also marks transitional levels
just past overdraw so the walk can fill continuously when data exists.
## [0.7.5] - 2026-08-23

**Fixed: white fog slice / mid-mountain cut at the vanilla view-distance edge.** Vanilla
chunk shaders fade terrain alpha to the sky at the live view-distance horizon
(`chunkopaque`, `chunkliquid`, `chunktopsoil`, `chunktransparent`). That edge fade left a
bright fog band and severed mid-mountain silhouettes before Distant Vistas LOD could join
behind. 0.7.5 ships patched copies under `assets/game/shaders/` that force full alpha
(liquid keeps fresnel but drops the viewDistance clamp wrap). Config adds
`PatchVanillaEdgeFade` (default true); for this release the override is always packaged ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â
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


