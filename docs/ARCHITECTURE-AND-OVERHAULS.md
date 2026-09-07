# Distant Vistas — Architecture and Overhauls (1.0.32)

How the mod is built, what had to exist for it to work on Vintage Story at all, and what
was overhauled from the Vintage Horizons foundation to reach official 1.0.32 quality.

Author: **IllLeroySquad**. Foundation: **Vintage Horizons** (AliasFactory, MIT).
Line counts: see [CODEBASE-AUTHORSHIP.md](CODEBASE-AUTHORSHIP.md).

---

## Executive summary

Distant Vistas is a **client-side level-of-detail engine** for Vintage Story. It captures
chunk columns the server already sent, compresses them into a persistent mip pyramid,
meshes them as real 3D geometry, and draws them past the vanilla view-distance ring.

That sounds like one feature. In practice it required:

- A full **ingest → store → mip → mesh → upload → draw** pipeline with crash-safe SQLite
  persistence
- A **render stack** matched to Vintage Story fog, curvature, seasons, night ambient, and
  vanilla chunk handoff
- A **login bake system** with sixteen parallel scout streamers, colour sampling, mesh
  gates, and Farseer skyline join
- An optional **server assist** layer (capture, manifest, rate-limited transfer)
- **8,846 lines of automated checks** because the game assemblies cannot run in CI

Roughly **28,852 lines** of directed development (including Cursor Agent commits) sit on
**~9,000 lines** of Horizons mod C# foundation.

---

## Why this is hard on Vintage Story

Vintage Story differs from Minecraft in ways that block a straight port of Distant
Horizons or Voxy:

| Constraint | What it forced |
| --- | --- |
| Client can join servers without a server mod | All LOD data must be built from chunks the client already received |
| Seasons, snow, and calendar change appearance daily | Block identity is stored; colour and tint resolve at render time |
| No client chunk-unload event | Snapshot on arrival; cache outlives live chunks |
| OpenGL 3.3 baseline (macOS ceiling 4.1) | VAO + per-section draws; no compute/MDI requirement |
| Chunk = 32³; sections = 64×64 columns | Custom key packing, column RLE, mip downsampling |
| Server has no texture atlas | Server assist ships colour-unresolved blobs; client resolves on receipt |
| Vanilla chunk shaders fade alpha at view ring | Patched chunk vertex shaders for seamless LOD join |

The mod accepts the Distant Horizons trade: **coverage grows as you explore**, cached
per world on disk, and it works on any server.

---

## Architecture overview

End-to-end data flow:

```text
ChunkDirty (NewlyLoaded / MarkedDirty)
  └─ snapshot column (read-only unpack; never touch live chunk after)
      └─ priority queue, hash-gated (persisted content hash per chunk)
          └─ ChunkToLod: 32×32 scan → palette + vertical RLE columns
              └─ LodStore: merge leaf section → SQLite WAL → applyToParent flag
                  ├─ MipPropagator: child→parent downsample (DB-flag driven, crash-safe)
                  └─ dirty → quadtree reload
                      └─ RenderSectionBuilder (worker): neighbor edge strips → greedy quads
                          └─ frame-budgeted GL upload queue
                              └─ LodTerrainRenderer (IRenderer @ Opaque 0.36):
                                 frustum cull, seasonal tint uniforms, fog-matched shader,
                                 dithered near fade, Farseer compositing
```

### Layer map (89 mod C# files)

| Layer | Role | Key types |
| --- | --- | --- |
| **Entry** | Client wiring, config, commands, telemetry | `DistantVistasModSystem`, `DistantVistasConfig` |
| **Lod/** | Side-agnostic capture, mip, scheduling | `LodPipeline`, `LodWorker`, `LodMesher`, `LodWorld`, `LodSection` |
| **Storage/** | SQLite persistence, async I/O thread | `LodStore`, `LodStorageThread`, `LodSaveSnapshot` |
| **Render/** | Draw, login bake, season colour, Farseer | `LodTerrainRenderer`, `LodLoginBake*`, `FarseerShaderOverlay` |
| **Net/** | Server assist, scouts, savegame sweep, `/dvgen` | `LodAssistClient`, `LodAssistServerSystem`, `LodSavegameSweep` |

Design DNA: **Distant Horizons** pipeline shape (column RLE, mip pyramid, quadtree,
crash-safe dirty flags), **Voxy** encoding ideas (packed keys, early-out mip, weighted
worker pool), **Farseer** VS rendering (camera-relative matrices, ZFar extension,
fog-matched GLSL, MIT).

---

## Core data model

**Section** — 64×64 data columns at detail level D; each column covers 2^D × 2^D blocks.

**Section key** — one packed `long`: `detail(6) | x(29) | z(29)` through cache, DB,
quadtree, and render map.

**Column** — vertical RLE top-down, gap-free. Each run packs palette id, y bounds,
light, and material flags into 64 bits.

**Palette** — per-section block codes, self-contained in the blob. Merged and remapped on
section merge.

**Chunk → leaf** — walk 32×32 columns from heightmaps; read raw palette arrays (not
per-block accessors) for ingest performance.

**Mip rule** — 2×2 columns → 1: slice sweep, most-common block per slice, re-RLE;
early-out when a level produces no change.

**Storage** — SQLite WAL, one DB per server/world under `ModData/distantvistas/`.
`applyToParent` dirty flags persist in rows so mip propagation survives crashes.
Chunk-hash gating skips identical re-receives.

---

## Rendering stack

### Mesh and draw

- `LodTerrainRenderer` — 3,458 lines; quadtree walk, parent-until-children rule, water
  pass, memory budget, FOV heightfield occlusion
- Greedy quad merge on run-boxes with neighbor edge strips (no full neighbor deserialize)
- Frame-budgeted upload queue; GL work only on render thread
- Camera-relative model matrices for precision at extreme draw distances
- ZFar extension via `ClientMain.MainCamera.ZFar` (Farseer pattern)

### Appearance without rebaking geometry

- Tint classes: grass, foliage, rock, water, snow, etc.
- Vertex stores base colour; fragment shader applies live calendar/season multipliers
- Snow line from climate; frost on canopy tops
- `GetColor` / `FlagBaked` / `FlagFrost` paint path for login and walk capture
- `LodRgbSimd` — AVX2/SSE2/NEON paths for blur/quantize after GetColor (bit-identical to scalar)

### Vanilla handoff

- LOD draws just before real terrain (order 0.36) so depth occlusion is correct
- Bayer-dithered discard at the view ring
- Patched `chunkopaque.vsh`, `chunktransparent.vsh`, `chunkliquid.vsh`, `chunktopsoil.vsh`
  disable vanilla view-distance alpha fade so the join is full alpha, not a white fog slice

### Farseer compositing

- Both systems draw: Farseer skyline band behind; Distant Vistas tiles on top where cached
- `FarseerShaderOverlay` — region shader overlay without Harmony patches
- Visit-onset uniforms align baked land rim with gray atmospheric tent and dark ridge tips
- `HorizonDrawScale` (4.5×) + `FarseerOnsetExtraBlocks` (+700) define login bake disk radius

---

## Login bake system (major overhaul)

The largest single engineering investment after the core pipeline. Horizons hop-teleported
the player across visit cells. Distant Vistas replaced that entire approach.

### What login bake must do

1. Stream chunk columns around spawn without moving the player
2. Capture columns into the LOD cache
3. Sample vanilla `GetColor` for every column (season-accurate RGB)
4. Build and upload meshes before the overlay ends
5. Meet the Farseer skyline at the correct radius with no white sky strip
6. Restore graphics view distance, overdraw, pause state, and exact pickup XYZ

### How 1.0.32 does it

| Mechanism | Purpose |
| --- | --- |
| **16 `LodScoutViewerEntity` instances** | Real player-style stream centers; VS loads around players, not visibility tokens |
| **Two-tier gate** | Inside 1,024 blocks: wait for drawable mesh. Outer band: FlagBaked paint, mesh drains after |
| **4,075-block disk** | `4.5 × 750 + 700` — matches Farseer onset at login graphics hold |
| **1,680 visit stop budget** | Weighted near-to-far; spawn-first ordering |
| **Parallel bake** | `MaxBakePerTick` 24; scouts paint every tick, not one serial stop |
| **Texture-mean cache** | One 8-sample GetColor per BlockId per L0 section, not ×4096 columns |
| **ArrayPool scratch** | Column arrays, mix/halo/blur buffers rented, not allocated per tick |
| **Spawn solid 1,024** | Ground underfoot meshed before overlay ends |
| **75% far-ready gate** | Overlay waits until ~3,056 of 4,075 blocks have far meshes |
| **Player pin** | Pos/ServerPos/facing written every GUI frame; no hop teleport |

Supporting files span **`LodLoginBake.cs`**, **`LodLoginSweepBootstrap.cs`**, **`LodLoginScoutFill.cs`**, **`LodExploreBake.cs`**, **`LodSeasonBake.cs`**, overlay UI/input/audio/time-freeze, and server-side **`LodScoutHostSystem`** for KeepLoaded + ForceSend.

---

## Overhauls from the Horizons foundation

The fork kept the idea — persistent LOD from client chunk data — and replaced the draw
path and much of the runtime policy.

### 1. Parent plates removed

Horizons meshed coarse parent boxes as stand-ins when children were not ready (giant square
shelves from altitude). Distant Vistas waits for real L0 geometry. Incomplete sections stay
off screen.

### 2. Visited land retention

Horizons used a camera window that dropped captured tiles once you flew past. Distant
Vistas keeps captured L0/L1 in the draw set behind and beside the camera. Fill queues
cannot stall tiles the player already generated.

### 3. Login: scouts instead of teleports

Horizons hop-teleported the player to each visit cell. Distant Vistas spawns scout viewer
entities, pins the real player at pickup XYZ, and tears down scouts on overlay end. Esc
resume, Pause-on-Start compat, and exact facing restore were added on top.

### 4. Login disk sizing

Early public builds planned 36k–144k block probe radii (1.0.1–1.0.3). Vanilla cannot stream
land that far at login; the result was a thin ring and a frozen client. 1.0.29+ uses the
**4,075-block Farseer onset disk** with dense scout streaming inside it.

### 5. Season and night colour

- Live ambient multiply on stored albedo (night matches chunk shaders)
- Season as shader clock on vegetation only; rock/snow/water excluded
- `GetColor` bake path with paint revisions for stale FlagBaked RGB
- Frost gates tied to calendar month and climate, not a flat white wash
- Canopy crown colour at tree top Y; side bands inherit crown

### 6. Vanilla edge and ocean fixes

- Chunk shader overrides kill the white fog slice at view distance (0.7.5)
- Checkerboard capture fix — per-quadrant queue skip (0.7.7)
- Ocean sealed seabed under transparent water — cake-slice fix (0.7.11)
- Missing-texture white strip at Farseer join — skip GetColor when maps never arrived

### 7. Farseer integration rebuilt

- Draw with Farseer behind (0.7.55+), tiles on top where present
- Shader overlay replaces Harmony patches (0.7.57+)
- Gray tent + black ridge tips silhouette (1.0.23–1.0.28)
- Visit-onset uniform recede when meshes lag Farseer by >256 blocks (1.0.32)

### 8. Capture pipeline hardening

- Pending column ceiling raised from 200 → 16,384 (fast flight no longer leaves sky squares)
- Capture result backlog cap, apply time budget, worker backlog limits
- Chunk-hash gating and crash-safe mip flags
- Sticky empty-mesh remesh; `HasDrawableMesh` for coverage/eviction truth

### 9. Optional server assist (M7)

Universal mod (`requiredOnClient: true`, `requiredOnServer: false`):

- Server captures its own LOD DB from loaded columns
- Handshake sends key manifest; client requests missing sections
- Wire format = storage blob (no second serializer)
- Colour unresolved on server; client resolves on receipt
- Rate limits, serve radius, local-capture-wins precedence
- Savegame sweep loads previously generated columns for horizon rebuild
- `/dvgen` transient worldgen peek for unvisited terrain (bare landform, no structures)

Dedicated server only for server cache (singleplayer excluded — duplicate work).

---

## Threading and backpressure

- Unified worker pool weighted by pending × priority: ingest ≫ save ≫ mesh ≫ mip
- All queues bounded and player-centered; farthest work evicted under load
- Storage on dedicated thread; palette block ids resolved on main thread only
- GL uploads frame-budgeted (~half a frame max)
- Login bake respects mesh pressure, capture backlog, and frontier yield caps

---

## Quality infrastructure

Because Vintage Story assemblies are not redistributable, **there is no CI**. Safety net:

| Tier | Time | Proves |
| --- | --- | --- |
| `scripts/check.sh fast` | ~1 s | Key packing, RLE, mip, mesher, shader constant guards |
| `scripts/check.sh smoke` | ~5 min | End-to-end client + server sandbox, warm cache readback |
| `scripts/check.sh matrix` | ~20 min | Vanilla/modded server, deferral, Farseer on/off, admin commands |

**34 test files, 8,846 lines.** Includes login sweep checks, assist protocol checks,
static asset/version guards, and install-matrix scenarios.

---

## Milestone arc (foundation → 1.0.32)

| Milestone | Delivered |
| --- | --- |
| M0 | Client ModSystem skeleton, ChunkDirty subscription |
| M1 | First pixels — heightmap LOD past view distance |
| M2 | SQLite persistence, chunk-hash gating, reload on join |
| M3 | Full column-RLE pyramid, quadtree detail selection |
| M4 | True 3D meshing — overhangs, cliffs, greedy merge |
| M5 | Season tint classes, water, commands, public release |
| M6 | Optional GL 4.3 fast path (gated; 3.3 path complete) |
| M7 | Server assist — capture, manifest, transfer, admin config |
| 0.7.x | Fork fixes: edge fade, capture holes, ocean, season, Farseer, visited trail |
| 1.0.x | Official release, scout login bake, parallel bake, SIMD, audit follow-ups |

---

## What “high level” required in one paragraph

Making Distant Vistas workable at official quality meant rebuilding the Horizons draw path
(parent plates, camera window eviction, hop login), solving Vintage-Story-specific problems
(seasonal colour without geometry rebakes, vanilla fog slice, Farseer join, night ambient),
standing up a production login bake with sixteen parallel scouts and GetColor sampling at
scale, persisting a crash-safe mip pyramid in SQLite, adding optional server assist with
rate-limited transfer, patching vanilla chunk shaders, writing ten GLSL programs and a
3,458-line renderer, and maintaining an 8,846-line local test regimen because no hosted
runner can compile against the game. The Horizons foundation provided the first ~9,000 lines
of mod C#; the remaining ~22,000 lines of directed work are what turned a promising fork
into a shippable 1.0.32 release.
