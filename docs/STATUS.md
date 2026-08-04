# M4/M5 status notes

## Optimisation pass (2026-08-03)

A second performance pass, after 0.2.0. The rule was the same as last time and it
earned its keep twice: nothing is claimed without a before and an after.

### The benchmark could not have answered the question

Two runs of the SAME build, same world, same route disagreed by up to 29.7%. Every
renderer and shader item worth chasing is smaller than that. The harness would have
answered anyway, with numbers that looked like findings, so it was fixed first.

Three separate causes, each found by measuring rather than by reading the code:

- **`bench.sh` never reset anything.** The savegame and the LOD cache both persist and
  grow while the route is walked, so each run started warmer than the last. Now
  `scripts/bench-ab.sh` snapshots and restores both before every measured run.
- **Settling was a fixed sleep.** Waypoints actually need 12s to over 75s depending on
  what is cached, so a 20s timer was measuring load bursts at three of five waypoints.
  Settling now waits for the frame times to stop having a TREND, which is a different
  question from whether they are quiet. Quiet was the first attempt, and `ridge-east`
  could never satisfy it: at 13-19ms with 75ms worst frames its window medians never
  agree, so it timed out at every attempt while its neighbours settled in 12s. It
  settles in 18s now. Two further defects in that criterion were caught by testing it
  against traces with known answers rather than by reasoning about it, and those traces
  live in `bench/settle-criterion-check.py`.
- **The page cache favoured whichever run went second.** Run A read a 171 MB world off
  cold disk and managed 82 fps on its first lap; run B found the same restored files in
  RAM and managed 369 fps at the same waypoint. The restore now reads the files back so
  every run starts equally warm.

Laps are also not independent samples. At every waypoint the world kept getting faster
through lap 3. Lap 1 pays for the chunk streaming and capture that later laps find
already done. Early laps are therefore walked and discarded.

The spread across laps now goes into the CSV, beside the numbers it qualifies.
`scripts/bench-ab-compare.py` reads it, and refuses to call any difference smaller than
that spread a result.

### Measured wins

- **The greedy merge was keyed on a field the merged plane never reads.** A horizontal
  face carried the bottom of the run beneath it, and the plane group tested it, so a
  flat surface broke apart along the contour of whatever was underneath. Ocean is the
  exact case the mesher exists to collapse. Vertices per section, same fixtures: ocean
  over a sloping seabed 8848 -> 6580, over a stepped seabed 1132 -> 112, a plateau on
  uneven bedrock 66048 -> 49668. Water quads alone 602 -> 35 and 266 -> 11.
- **The join-time key scan had no index.** It selects four small columns and no blob,
  but the table's leaf pages are spread through a file that is mostly section blobs.
  On ext4 with the page cache dropped: 5581 sections 173.4ms -> 4.6ms, 15000 sections
  931.1ms -> 12.6ms. The index costs 0.03% of the cache. The same test on tmpfs reports
  2.1x and makes it look not worth doing, which is why it had to be measured on a disk.
- **The sibling cache was scanned once per tick.** A full table scan of the server
  side's sections, 20 times a second, for the whole session: 12ms/s at 2158 sections,
  105ms/s at 20000. Now once a second.
- **Mesher output buffers were allocated per job.** 240 KB for any section at all,
  including one that emits five quads. Per build: flat plain 241.0KB -> 0.6KB, ocean
  over a stepped seabed 243.0KB -> 2.6KB, plateau 4395.9KB -> 1067.2KB. Their lists also
  crossed the 85 KB large-object threshold and back on every dense job.
- **Mip downsampling copied every child column** to satisfy a signature: four
  allocations per parent column, 1024 per call, three calls per tick, on the game
  thread. 232.1KB -> 72.1KB per call.

### Fixed, with the measurement that found them

- **Capture stalled the tick on an evicted section.** `ApplyCaptureResults` called
  `GetOrCreateSection` with no residency check: measured at 10.60ms average and
  112.98ms worst, in a 50ms tick. `EnsureResident` already existed for mip propagation
  and states the hazard exactly; capture was simply never routed through it. The
  residency rule now has a test suite, which it did not before despite three callers
  depending on it.
- **A client could not tell "not written yet" from "never".** Both arrive as the same
  empty blob, and the client treated both as never, so a player joining a sweeping
  server lost those sections for the session. `AssistSection.Retryable` separates them.

### The renderer's own frame cost, and why it is timed rather than benchmarked

Each phase of the render frame is now timed directly and reported in the 15s stats line.
The reason is arithmetic. These phases are tens to hundreds of microseconds, against a
frame of ten thousand. The benchmark's own run-to-run spread is wider than any of them.
A frame-rate comparison therefore cannot resolve them. It can only appear to.

That measurement re-ranked the work three times, and each ranking was wrong in a way
reading the code could not have shown:

- One early sample said mesh scheduling cost 0.6us, so it was not worth touching.
- Twelve samples from a served world said 16.7-18.1us, the largest of the phases.
- A world with 951 resident sections said the walk cost 387us and the draw 360us.
  Scheduling sat at 7us there, with a 2.6ms spike.

All three are true. The costs depend on state. Scheduling dominates while the dirty set
is large, which is during fill-in. The walk and the draw dominate once meshes exist, and
both scale with the resident section count. A measurement on a small world under-reports
the walk by roughly 40x.

The lesson for anyone measuring this again: quote the resident section count beside any
phase number, or the number means nothing.

Prune and schedule were first timed together, which was a mistake. They are different
shapes, and a spike in the pair reads as a spike in scheduling. Separated, pruning turns
out to cost 0.4us and scheduling 0.2us once the dirty set has drained.

**Read the averages, not the maxima.** The per-phase maximum does not measure the phase.
The far-distance scan averages 2us over a few hundred meshes and has been seen to report
a 1624us maximum, which is 800x its own average for a loop that only multiplies and
compares. Nothing in it can take that long. What the maximum records is whatever
interrupted the frame, a garbage collection or the scheduler, charged to whichever phase
happened to be running at the time. Every phase shows millisecond maxima for the same
reason, and they should not be read as evidence about the code inside them.

### Known and not fixed

- **Draw submission and the quadtree walk are the two largest phases.** Measured at 951
  sections they were 360us and 387us a frame; at 358 sections, 136us and 89us. Both
  scale with the resident section count, and together they are nearly all of the
  renderer's own frame cost.
  Draw submission runs `SetupSectionTransform` per section per pass, twice over for
  anything with water, and each call makes four `HasDataSet` probes and four uniform
  uploads. The open-edge flags change only when a neighbour gains data, and
  `MarkChanged` already dirties the four neighbours, so they can be cached with the mesh.
- **Mesh eviction is counted in frames, not time.** `EvictAfterFrames = 3600` is two
  minutes at 30 fps and eight seconds at the 450 fps some waypoints reach, so how long
  the mod keeps a mesh swings by 15x with frame rate.
- **`LodSection.Captured` is `bool[4096]`,** 4 KB per section whatever it holds, cloned
  five times per mesh job. A `ulong[64]` bitset is eight times smaller and would take
  about 11 MB off a 3000-section world. The store already packs it to bits on the way to
  disk, so only the in-memory type would change.

## Performance pass (2026-07-24)

Three changes, each measured rather than assumed:

- **Frustum culling.** The quadtree walk selects in every direction, so sections
  behind the camera were still issuing draw calls. Planes are extracted from the
  same projection*view matrices the LOD shader gets, and tested at DRAW time only
  - culling inside the selection walk would evict off-screen meshes and re-mesh
  them the instant you turned around. Cull share runs 12%->59% depending on how
  much captured data surrounds the camera. Plane math is covered by the frustum
  suite in `scripts/check.sh fast` (behind/left/right/above/beyond-far reject;
  in-view cases pass; matrices built with the game's own `Mat4f` so the extraction's
  column-major assumption is tested rather than assumed).
- **Writes off the render thread.** Save batches measured 10-22ms average and
  49ms peak on the main thread - a whole 50ms tick - because serializing deflates
  ~100-300KB inline. Sections are frozen on the main thread (copies: the live
  section keeps mutating and SetColumn edits Runs in place) and compressed +
  written by a storage thread, with deflate outside the DB lock. After: 0.3ms
  average, ~3ms peak on an equivalent batch (~32x).
- **Reads off the render thread.** 302 inline loads at join (4.3ms avg, 29.5ms
  peak) -> 47, with 600 served in the background. Capture and mip propagation
  stay synchronous on purpose: they must merge into stored data before creating a
  section, which is exactly how stored rows got shadowed and overwritten before.

Two hazards found and closed while doing it: the storage thread must not touch
the block registry (the sibling `GetBlock(int)` lazily mutates a dictionary), so
off-thread loads keep palette CODES and resolve ids at install on the main
thread; and a reload that comes back empty is remembered, or the walk
re-requests that key every frame forever.

Verification beyond the counters: restart reloaded 958 sections with zero
unreadable rows, and decompressing freshly written blobs showed 0 empty block
codes across L0/L1/L2 (a failed id resolution would have written empty codes).

Remaining known costs: 47 inline loads per join from the capture/propagation
paths (8ms avg, 33ms peak) - making propagation defer safely is the next step.

## Multiplayer verified (2026-07-16 evening)

The headline claim - client-side-only install working on an unmodded server -
is now tested: a strictly vanilla dedicated server (`scripts/test-server.sh`,
fresh dataPath, zero mods) with the release zip as the client's only mod.
Full pipeline flowed from server-streamed chunks: 3,183 columns captured,
311 sections across all 7 levels, meshing/draw/persistence all live, fresh
per-world cache db keyed by SavegameIdentifier (works in MP), 343 sections
persisted. Capture errors accumulate faster in MP than SP (50 in ~5 min,
suspected chunk-disposed-mid-read after teleport hops); worker now records
the first swallowed exception and logs it with the next stats line.

### Test isolation (hard rules - a violation crashed the user's game once)

- The VS client is single-instance via a global named pipe in `$TMPDIR`
  (`CoreFxPipe_SingleInstanceVintageStoryWithUriScheme`). A `-c host:port`
  launch FORWARDS the connect into any already-running instance (even with
  `--dataPath`!) and exits silently. `scripts/test-client.sh` isolates via a
  sandbox-private TMPDIR.
- Start test instances only via `scripts/test-client.sh` / `test-server.sh`;
  stop them only via `scripts/test-stop.sh` (pidfiles from `$!`). Never
  locate game processes by name/args - the user plays concurrently.
- Sandbox mods go in `.testdata/Mods` and load via `--addModPath` (a relative
  `Mods` entry in clientsettings resolves against the game install dir).

## M5 progress (2026-07-15 morning)

- **VRAM eviction + demand-driven re-meshing** (first M5 item): meshes the quadtree
  hasn't selected for ~60s are disposed; when the walk wants a missing mesh again it
  re-requests it via the render-dirty queue (the selection walk IS the load queue -
  Voxy's idea, CPU-side). No holes: a section only stops being selected when its
  parent renders instead, and re-requested nodes stay covered by the parent until
  their mesh uploads. Remaining M5 items: greedy quad merging, seasonal tint classes
  + snow line, config GUI/persisted settings, RAM-side section eviction, ModDB prep.

# M4 first pass - overnight status (branch `m4-blockdata`, merged to master)

## What this branch does

Replaces the M3 heightmap data model with the real Distant Horizons-style pipeline
(DESIGN.md §4, originally planned for M3 and deferred):

- **Block-data capture**: chunk columns are scanned block-by-block (FluidOrSolid
  layer) from the rain height down to y=1 on a **worker thread**, producing vertical
  RLE runs. Trees, overhangs, caves-from-outside, and player edits are all captured -
  no more worldgen-heightmap limitations.
- **`LodSection`**: 64×64 columns per section, 2-block columns at level 0 (finer than
  M3's 4-block), packed `ulong` runs (`paletteId | yTop | yBottom`) over a per-section
  palette. Palettes store block **codes** on disk (ids are savegame-local).
- **3D meshing** (worker thread): every run is a box - top faces at air gaps, bottom
  faces under overhangs, side walls where the neighbor column's runs don't cover the
  span (interval subtraction), cross-section culling via neighbor snapshots.
  Thread-safety by convention: section run arrays are immutable once created (writes
  swap whole arrays), so worker snapshots are race-free.
- **Mip pyramid**: 2×2 columns merge via y-boundary slice sweep, majority occupancy
  (≥2 of 4), most-common block per slice. Crash-safe ApplyToParent flags as before.
- **Storage v4**: `Section` table, blob = palette (codes+colors+flags) + run-count
  plane + packed runs + captured bitset, deflated. v3 caches are purged on open.
- **Dev auto-unpause**: `VINTAGEHORIZONS_AUTOUNPAUSE=1` keeps singleplayer ticking
  without window focus (renderer-driven, since tick callbacks stall while paused) -
  this is what made unattended overnight verification possible.

## Verified overnight (unattended, real survival world)

- Full pipeline flows without focus: capture → palette remap → apply → mip →
  worker meshing → GL upload. First 30s: 116 columns captured, 24 sections across
  all 6 levels, 15 meshes, zero exceptions, zero GL errors.
- "1 drawn" in early stats is the no-holes swap rule mid-buildup (root renders until
  its subtree is fully meshed), not a bug - per-level draw histograms in later stats
  lines should show the walk descending as meshes complete.
- (See git log on this branch for exact telemetry at each iteration.)

## Verified later in the night

- **v4 persistence round-trip**: 29 sections saved with block-code palettes reloaded
  cleanly on rejoin ("29 sections from cache", zero unreadable rows).
- **Quadtree draw histogram**: once the cached subtree was fully meshed, the walk
  descended to `16 drawn [L0:16]` - full leaf detail near the player. The earlier
  "1 drawn" was the no-holes swap rule during buildup, as suspected.
- **Water pass added** (commit b764052): water meshes into a separate buffer drawn
  alpha-blended (α=168) after the opaque pass, with phase-aware face culling -
  solid faces only culled by solid neighbors so lake/ocean floors render under
  translucent water. NEEDS EYEBALLING in-game.

## Known gaps / follow-ups (deliberate for a first pass)

1. **No greedy quad merging** - vertex counts are box-naive; fine at current scale.
2. **Cross-level seams**: section borders between different detail levels may show
   cracks (M3's skirts were removed with the heightmap mesher; box walls go deep so
   it's less severe - needs eyeballing).
3. **Initial-buildup coarseness**: until a subtree fully meshes, the coarse parent
   renders even close to the player (swap rule). Cosmetic; refine in M5.
4. **Mesh memory**: all levels stay in RAM and VRAM; eviction is M5 territory.
   Baseline RSS during soak: ~3.8GB (game itself is most of it; watch the trend,
   not the absolute).
5. Deep oceans: capture includes full water depth from the rain height - verify
   visuals and mesh sizes over large water bodies.
6. Water draws unsorted within the blended pass (fine for a single surface;
   revisit if stacked water layers show artifacts).

## How to run

```sh
scripts/check.sh                          # the standing regimen, before committing
scripts/check.sh fast                     # pure logic and static assets only (~30s)
scripts/dev-run.sh                        # normal
VINTAGEHORIZONS_AUTOUNPAUSE=1 scripts/dev-run.sh   # unattended testing
```

`.vhinfo` in chat for live stats, `.vhwhy` to explain coarse terrain. Stats also log
every 15s when either `VINTAGEHORIZONS_AUTOUNPAUSE=1` or `VINTAGEHORIZONS_STATS=1` is
set, and once unconditionally 30s after level finalize.
