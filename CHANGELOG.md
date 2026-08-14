# Changelog

Written when a version is released, not when a commit lands - see
[docs/RELEASING.md](docs/RELEASING.md). Newest first.

## [Unreleased]

**Fixed: patches of distant terrain were solid black, and stayed black.** A server has no
texture atlas, so it stores no colour and the client fills it in on arrival. The client
also saves what it receives. So anything that stopped the fill-in was written to the cache
and stayed there, drawing as pure black ground for as long as that world existed.

What stopped it was a block code that failed to resolve. That answer used to be remembered
for the rest of the session, and the lookup runs while a world is still coming up, so one
common block losing that race left every section saved afterwards with no colour at all.
Measured on a real world: 7 sections with no colour anywhere and 59 more in patches, on
ground as ordinary as soil, slate and tall grass.

A failed lookup is no longer remembered, so it is simply tried again. Sections already
saved without colour are repaired as they load, from the texture atlas, and written back,
so a cache fixes itself as you play and nothing is discarded. A block this game genuinely
does not have now draws as plain grey, and both the count and the block codes are logged
rather than left as a black patch with no explanation.

**Fixed: joining a server before its cache existed switched the assist off for the whole
session.** The server reported the assist as "off" whenever its cache happened to be empty
at the instant you joined, which is the ordinary state of a fresh server and of any server
an admin is about to run `/vhgen` on. The client took that as final and ignored everything
the server sent afterwards, so no amount of generating helped until you relogged. The
answer now says whether the server *will* serve, not whether it holds anything that
second, and a server with nothing yet says so plainly.

**Fixed: a server cache that grew while you were online never reached you.** The list of
sections a server has was sent once, when you joined, and never again. So an admin running
`/vhgen` while people were playing built terrain none of them could ask for: a client only
requests sections it has been offered. `.vhwhy` reported `no-data` for ground the server
had been holding for hours, and relogging was the only cure. A sweep finishing late, or
other players exploring, did the same. The server now offers what it has gained every few
seconds, and `/vhserver` reports how many of those follow-up offers it has sent.

**Fixed: another LOD mod that is switched off no longer switches this one off too.**
0.2.0 went idle whenever Farseer, ChunkLOD, Vistas Beyond or TopoHorizon was loaded. On a
server that runs one of those, every client is made to load it and the game downloads it
for you, so "loaded" never meant "drawing". A player who had switched Farseer off in its
own dialog and used this mod instead was left with no distant terrain from either mod.

This mod now reads Farseer's own switch, in `farseer-client.json`, and draws when Farseer
is switched off. Nothing to configure: the setting is already on disk. The mods whose
switch cannot be read still get deference, and `IgnoreOtherLodMods` in
`vintagehorizons.json` is the escape hatch for those, with `.vhdefer on|off` to set it
from chat. It applies on the next start, never mid-session.

`.vhinfo` and the log line no longer say to remove the other mod, which is advice a
player on that server cannot follow.

**Fixed: an idle client made the game log a channel warning.** Deferring returned before
registering the network channel, so the game reported "Server sends me channel name
vintagehorizons, but no client side mod registered it" at every join.

**Fixed: a crashing client cleaned up almost nothing.** When the game went down its own
shutdown crash path, our teardown ran off the main thread, where the engine refuses these
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

Server admins get `ModConfig/vintagehorizons-server.json` and a `/vhserver` status
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
