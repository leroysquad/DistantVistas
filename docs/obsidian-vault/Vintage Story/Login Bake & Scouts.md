---
tags: [vintage-story, distant-vistas, login-bake]
aliases: [Login bake, Scouts, LodScoutViewer]
---

# Login Bake & Scouts

Login overlay mechanics: how far land is painted before play without moving the player.

Related: [[Login Bake Efficiency]] · [[Farseer Companion]] · [[Distant Vistas]] · [[Playtest Log - 2026-09-07]]

## Why scouts exist

Vintage Story auto-loads and tessellates around **players**, not around bare `SetChunkColumnVisible` tokens. There is no dummy-player API.

**1.0.30+:** `LodScoutViewerEntity` — real `Entity` at each visit cell (`assets/distantvistas/entities/scoutviewer.json`). Server KeepLoaded + ForceSend neighbourhood to the baker.

**1.0.27:** `LodScoutEntity` — client-side workers with `SetChunkColumnVisible` (no VS Entity subclass).

See `LodScoutViewerEntity.cs` header comment.

## No player teleport

- Player stays at **pickup pose** for entire overlay.
- `CaptureRestorePose` / `FreezePickupPose` / `ApplyExactPickup` every GUI frame (`LodLoginBake.cs`).
- Success, Esc, fail, world-leave write same doubles to `Pos` and `ServerPos`.
- Hop-era `BeginNextStop` (visit-cell stream via player) is **gone** (1.0.30).

## Exact pickup XYZ

- Snapshot once: exact `Pos.X/Y/Z` + yaw/pitch — **not** spawn chunk origin.
- Warmup may replace unset (0,0) with live pickup, then never overwrite.
- `RestorePlayerPose` uses pickup doubles only at end/Esc.

## Bake flow (per visit cell)

1. Scout spawns at visit key (staggered across disk).
2. Stream neighbourhood (KeepLoaded + visible ring).
3. Capture columns → L0 section.
4. **GetColor** bake → `FlagBaked` paint.
5. Mesh build (near: wait for drawable; far: release after paint).
6. Scout despawn + server despawn + UnloadChunkColumn (never under player feet).

## Two-tier scout gate (1.0.31+)

| Zone | Distance | Gate |
|------|----------|------|
| **Near** | Inside 1024 (`SpawnSolidRadiusBlocks`) | `WaitForMesh=true` — dwell until `HasDrawableMesh` |
| **Far** | Toward 4075 onset | `WaitForMesh=false` — release after stream → capture → FlagBaked paint |

- 16 concurrent scouts: **8** near mesh-wait + **8** far paint-release.
- `HasEmptyMeshClaim` releases slot (sticky empty mesh must not block scouts).
- Far ring: 2-chunk reveal; near: 4-chunk.

Implementation: `LodLoginScoutFill.cs`, `LodScoutEntity.WaitForMesh`.

## Snapshot / stabilize rules (1.0.30)

Overlay stays until (`TickStabilizing`):

- Spawn-local L0 drawable count == 0 missing (`CountMissingSpawnDrawable`).
- Far meshes ≥ **75%** of 4075 (~3056 blocks).
- Mip drain queue empty.
- 4×90-frame frame time ≤ 28 ms.

**Escape hatch:** `SpawnReadyTimeoutSec` = 90 s can release with holes.

Removed: 3 s stabilize shortcut that released before land/smoke visible.

## Separate sweep lanes

- `SweepLaneSpawn` vs `SweepLaneScout` — sharing one cursor skipped spawn-local rows (holes underfoot).
- Spawn-first visit budget ~⅔ of inner 1024 m, then rim near-to-far.

## View boost

- Graphics slider held at **750** during overlay (`LodLoginBakeViewBoost`).
- `OverdrawStart` lowered for wider cover; restored after overlay.
- Visit disk = Farseer onset + 700 — not 4 km vanilla tessellation storm.

## Teardown

Full despawn on overlay end / Esc / fail / leave:

- `Die()` every scout viewer.
- Server `DespawnEntity`.
- Unload keep-loaded neighbourhoods.
- Restore graphics slider + OverdrawStart.

## Gates & skip

- `LodLoginSweepGate` — in-window complete stamp skips re-canvas (30-day window).
- Esc cancel snapshots in-flight scout keys (`LodLoginBake.cs`).
- `ShouldDropLeftoverResume` when in-window complete would skip.

See [[Neglected Issues Audit]] for skip UX with deferred holes.

## Key files

| File | Role |
|------|------|
| `LodLoginBake.cs` | Overlay phases, pose, progress |
| `LodLoginScoutFill.cs` | Scout tick, gates, despawn |
| `LodScoutViewerEntity.cs` | Player-style stream anchor |
| `LodLoginSweepBootstrap.cs` | Visit plan, spawn-first budget |
| `LodLoginBakeViewBoost.cs` | 750 hold, 4075 disk |
| `LodLoginSweepGate.cs` | Skip / resume decisions |
| `LodPauseOnStartCompat.cs` | Force-unpause during overlay |

Tests: `LoginSweepChecks.cs`.
