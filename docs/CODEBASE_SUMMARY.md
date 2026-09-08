# Distant Vistas — Codebase Summary

**Bot-oriented map of the repository.** Read this before crawling. For architecture deep-dive see [DESIGN.md](../DESIGN.md). For fork rationale see [docs/WHAT-WE-DO.md](WHAT-WE-DO.md).

---

## Repo state (this checkout)

| Field | Value |
|-------|-------|
| **Product name** | Distant Vistas |
| **Mod ID** | `distantvistas` |
| **Version** | **1.0.0** (`DistantVistas/modinfo.json`, `DistantVistas/DistantVistas.csproj`) |
| **Game dependency** | Vintage Story **1.22.5+** (declared minimum; README also lists 1.22.6/1.22.7) |
| **Runtime** | .NET **10** |
| **Git tip (this branch)** | `cursor/codebase-knowledge-base-9e50` — docs from **1.0.0** `main` |
| **Active login-bake work** | Branch `cursor/login-bake-chunkdb-autosave-1045-dba0` @ **1.0.45** (`c8ff709`) — **not merged to `main` yet** |
| **Lineage** | Fork of [Vintage Horizons](https://github.com/AliasFactory/Vintage-Horizons); tests/bench still use `VintageHorizons.*` namespace paths |

**Version lineage (releases):**

| Era | Versions | Notes |
|-----|----------|-------|
| Initial | 0.1.x | Client LOD + SQLite persistence |
| Server assist | 0.2.x | `/dvgen` (was `/vhgen`), wire protocol, deferral |
| Core rendering | 0.7.x | ModDB-stable line; ocean seal, visited-keep, settings UI |
| Login / season | 0.8.x | Join overlay, visit sweep, season bake, GL/FBO fixes |
| Official | **1.0.0** | Declared stable on `main` |
| Login-bake scouts (WIP) | **1.0.33–1.0.45** | Parallel scouts, stream cliffs, chunkdb backpressure — see [login bake stream cliffs](#login-bake-stream-cliffs--chunkdb-autosave-1033) |

Canonical **play** builds: ModDB 0.7.x history; **`main` is 1.0.0**. Login-bake stream fixes live on feature branch through **1.0.45** (still fails ~708–734 in playtests; target pass is past 734→1680). Local trees may lag GitHub — always read `modinfo.json` on the branch you have.

---

## Quick “where is X?”

| Question | Go here |
|----------|---------|
| Client entry / config | `DistantVistas/src/DistantVistasModSystem.cs` |
| Capture + mip + persistence | `DistantVistas/src/Lod/LodPipeline.cs` |
| Section data model | `DistantVistas/src/Lod/LodSection.cs`, `LodWorld.cs` |
| Background capture/mesh | `DistantVistas/src/Lod/LodWorker.cs`, `LodMesher.cs` |
| SQLite cache | `DistantVistas/src/Storage/LodStore.cs` |
| Draw / eviction / scheduling | `DistantVistas/src/Render/LodTerrainRenderer.cs` |
| Cake plates / coverage rules | `DistantVistas/src/Render/LodCoveragePolicy.cs` |
| Mesh pressure / keep ring | `DistantVistas/src/Render/LodMemoryBudget.cs` |
| Login sweep orchestration | `DistantVistas/src/Render/LodLoginBake.cs` |
| Overlay stream / VD grow | `LodLoginBakeViewBoost.cs` (1.0.44+) — `OverlayStreamBlocks`, stepped grow |
| Chunk visible budget | `LodLoginChunkRequestBudget.cs` (1.0.45+) |
| Scout fill / WaitChunks | `LodLoginScoutFill.cs` (1.0.33+) |
| Scout host KeepLoaded | `Net/LodScoutHostSystem.cs` (1.0.33+) |
| Hop-unlock pump | `LodLoginHopUnlock.cs` (1.0.39+) |
| Run vs skip sweep | `DistantVistas/src/Render/LodLoginSweepGate.cs` |
| Season / visit bake | `DistantVistas/src/Render/LodSeasonBake.cs` |
| Walk-time bake | `DistantVistas/src/Render/LodExploreBake.cs` |
| Live climate tints | `DistantVistas/src/Render/LodTintRegistry.cs` |
| Splash / join GL safety | `LodLoginBakeInputGuard.cs`, `LodJoinQuiet.cs`, `RestorePresentFramebuffer()` in ModSystem |
| Server capture + `/dvgen` | `DistantVistas/src/Net/LodServerCaptureSystem.cs` |
| Network assist | `DistantVistas/src/Net/LodAssistClient.cs`, `LodAssistServerSystem.cs` |
| Other LOD mod deferral | `DistantVistas/src/OtherLodMods.cs` |
| Telemetry JSON | `DistantVistas/src/SessionTelemetry.cs` → `Logs/distantvistas.json` |
| Fast invariant tests | `tests/VintageHorizons.Checks/` |

There is **no** class named `FrameGuard`. Join/present safety uses `LoginBakeBlocked`, `LodJoinQuiet.SuppressVaoDrain`, `RestorePresentFramebuffer()`, and deferred renderer registration — see [Join / splash / GL footguns](#join--splash--gl-footguns).

---

## ModSystem entry points

Vintage Story auto-discovers `ModSystem` subclasses. No separate `Mod` class.

| Class | File | Side | `ExecuteOrder` | Role |
|-------|------|------|----------------|------|
| `FarseerOverlayEarlyHook` | `Render/FarseerShaderOverlay.cs` | Client | `0.05` | Re-apply Farseer shader bytes on `ReloadShader` when overlay active |
| **`DistantVistasModSystem`** | `DistantVistasModSystem.cs` | Client | `0.6` | **Primary client**: config, pipeline, renderer, login sweep, assist, commands |
| `LodServerCaptureSystem` | `Net/LodServerCaptureSystem.cs` | Server | default | Server LOD cache, savegame sweep, `/dvgen`, auto-join pregen |
| `LodAssistServerSystem` | `Net/LodAssistServerSystem.cs` | Server | default | Assist handshake, manifest, section serving, `/dvserver` |

---

## Module / file map

### `DistantVistas/src/` (root)

| File | Owns |
|------|------|
| `DistantVistasModSystem.cs` | `DistantVistasConfig`, client lifecycle, `ChunkDirty` → pipeline, login sweep deferral, chat commands (`.dvistas`, `.dvfar`, `.dvdetail`, `.dvdefer`, `.dvfarseer`), atlas clamp, FBO restore |
| `OtherLodMods.cs` | Defer to ChunkLOD/TopoHorizon; Farseer as companion (draw together since 0.7.55) |
| `SessionTelemetry.cs` | ~1 Hz JSON telemetry for agents/benchmarks |

### `Lod/` — data model & pipeline

| File | Owns |
|------|------|
| `LodPipeline.cs` | Main-thread coordinator: capture queue, apply results, mip propagation, persistence, remote keys, explore bake hooks |
| `LodWorld.cs` | Section pyramid L0–L6, packed keys, dirty sets (`RenderDirty`, `MipDirty`, `SaveDirty`), LOD distance ladder |
| `LodSection.cs` | 64×64 column grid, vertical RLE, `LodPaletteEntry` flags |
| `LodWorker.cs` | Background capture + mesh threads (`CaptureJob`, `MeshJob`, `SectionSnapshot`) |
| `LodMesher.cs` | Greedy meshing, underwater seal, tint bands in vertex alpha |
| `LodMip.cs` | Child→parent downsampling |
| `LodBlockPolicy.cs` | Per-block capture flags (water, thin, skip, climate-untinted) |
| `LodPaletteRepair.cs` | Color repair, stable refresh, unknown-block grey, TrueScale `unknown.png` handling |
| `LodLocalOfferSource.cs` | Read-only SQLite from sibling `*-server.db` (singleplayer) |
| `LodRemoteKeySet.cs` | Remote-only section tracking |
| `LodWorldKey.cs` | Per-savegame ID for cache paths |

### `Storage/`

| File | Owns |
|------|------|
| `LodStore.cs` | SQLite cache (`Section` table, schema v6/v8), deflate blobs, block **codes** not IDs |
| `LodStorageThread.cs` | Async deflate/write + demand loads off main thread |
| `LodSaveSnapshot.cs` | Serializable section snapshot for writer queue |

### `Render/` — drawing & join UX

**Core renderer**

| File | Owns |
|------|------|
| `LodTerrainRenderer.cs` | `IRenderer` @ Opaque (~0.36): quadtree walk, mesh schedule/upload, frustum cull, fog, pressure eviction, climate uniforms |
| `LodFrustum.cs` | Frustum + lead-cone tests |
| `LodHeightfieldOcclusion.cs` | Optional FOV heightfield occlusion (L0/L1), per-frame test budget |
| `LodCoveragePolicy.cs` | Vanilla ownership, visited-keep, cake-plate bans, gap fill, lead cone |
| `LodMemoryBudget.cs` | RAM-probed keep scale; FPS/memory pressure thresholds (**not** mesh-count caps) |
| `LodTintRegistry.cs` | Live climate/season tint slots; topsoil untinted-share |
| `LodClimateField.cs` | 40-block climate cells, bilinear upload for vegetation tint |
| `LodPhaseCost.cs` | Per-frame render phase timing |
| `LodSnow.cs`, `LodSurfaceMix.cs`, `LodCanopyGray.cs` | Season/surface color helpers |
| `LodSeasonBake.cs` | Visit bake via `GetColor` → `FlagBaked` |
| `LodExploreBake.cs` | Walk-time budgeted visit bake (1–2 L0/tick) |
| `LodJoinQuiet.cs` | `SuppressVaoDrain` during join/sweep |
| `FarseerShaderOverlay.cs` | Farseer region shader injection (`OverlayActive = false` at tip) |

**Login visit sweep cluster** (30+ files; grouped by role)

| Files | Role |
|-------|------|
| `LodLoginBake.cs` | State machine: `OverlayWarmup` → `Sweeping` → `Done` |
| `LodLoginSweepGate.cs` | Run vs skip decision |
| `LodLoginSweep.cs` | L0 key enumeration, visit positions |
| `LodLoginSweepBootstrap.cs` | Bootstrap disk (~36 km), revisit budgets |
| `LodLoginSweepWindow.cs` | 30-day window (in-game + wall), paint revision |
| `LodLoginSweepComplete.cs`, `LodLoginSweepResume.cs` | Per-world JSON in `ModData/distantvistas/` |
| `LodLoginBakeAudit.cs` | Post-sweep miss detection |
| `LodLoginSweepOceanFill.cs` | Ocean stamp after visits |
| `LodLoginBakeInputGuard.cs` | Cairo HUD splash — **no OrthoMode** |
| `LodLoginBakeOverlay.cs`, `LodLoginBakePulse.cs` | Overlay coordinator + 50 ms tick |
| `LodLoginBakeViewBoost.cs`, `LodLoginBakeViewHoldStore.cs` | Hold view at **750**, restore real slider |
| `LodLoginBakeTimeFreeze.cs`, `LodLoginBakePlayerHide.cs`, `LodLoginBakePlayerMove.cs` | Player/time isolation during sweep |
| `LodLoginBakeAudioMute.cs`, `LodLoginBakeInputLock.cs`, `LodLoginBakeMouseDelta.cs` | Audio/input safety |
| `LodLoginBakeCharacterWait.cs` | Wait for character-creation UI before GL |
| `LodLoginSplashLayout.cs` | Cover-fit splash math |

### `Net/` — server assist & capture

| File | Owns |
|------|------|
| `LodAssistProtocol.cs` | Channel `distantvistas`, protobuf messages, rate limits |
| `LodAssistClient.cs` | Client handshake, manifest, section requests |
| `LodAssistServerSystem.cs` | Server handshake, serve loop, `/dvserver` |
| `LodServerCaptureSystem.cs` | Server `LodPipeline`, sweep, pregen, `/dvgen` |
| `LodServerConfig.cs` | `distantvistas-server.json` |
| `LodSavegameSweep.cs` | Load existing savegame columns into cache |
| `LodPlayerPregen.cs` | `/dvgen` and auto-join peek/worldgen capture |
| `LodPeekDiff.cs`, `LodAbsenceVerifier.cs` | Peek vs full-gen verification |
| `LodColumnMap.cs`, `ManifestLedger.cs` | Column indexing, manifest deltas |

### Static assets

| Path | Purpose |
|------|---------|
| `assets/game/shaders/chunk{opaque,transparent,liquid,chunktopsoil}.vsh` | Disable vanilla view-distance edge alpha fade |
| `assets/distantvistas/shaders/lodterrain.{vsh,fsh}` | LOD terrain GLSL |
| `assets/farseer/shaders/region.{vsh,fsh}` | Farseer region shader overlays (inject currently off) |

### Harmony patches

**This mod ships no Harmony patches.** No `HarmonyLib` reference. Patching is via:

- Asset overrides (vanilla chunk shaders)
- Runtime shader byte injection (`FarseerShaderOverlay`, currently disabled)
- `HudElement` overlay (`LodLoginBakeInputGuard`) — replaced Harmony `loginbake` patches in 0.8.39

---

## Runtime pipeline

```
JOIN
  LevelFinalize → defer login sweep (character UI, atlas compose)
  LoginBakeBlocked=true, renderer not registered until safe
  LodLoginSweepGate.Decide → run or skip
  │
  ├─ SKIP → play immediately (canvas complete within 30-day window)
  │
  └─ RUN → LodLoginBake overlay
         OverlayWarmup (Cairo splash, view=750)
         For each visit stop:
           Teleport → settle → stream chunks
           pipeline.SweepLoadedColumns / capture
           LodSeasonBake.BakeSectionFromVisit (block.GetColor)
           Set FlagBaked, persist
         Ocean fill, audit, mip drain
         Restore view slider, pose, audio
         DiscoverOnly=true, ExploreBake.Clear()

PLAY
  ChunkDirty → LodPipeline capture queue → LodWorker
            → section install → mip propagate → LodStore persist
  ExploreBake: 1–2 L0/tick live visit bake (same GetColor path)
  LodTerrainRenderer each frame:
    quadtree walk → frustum + occlusion + coverage policy
    schedule mesh jobs (12/frame) → upload (8/frame, 2ms budget)
    draw opaque + water
  OnGameTick: pipeline tick, assist pump, explore drain
  Under MeshPressureActive only: EvictStaleMeshes (outside 2× VD)

SERVER (optional)
  ChunkColumnLoaded → server LodPipeline → *-server.db
  Assist serves sections to clients; /dvgen peek worldgen (transient, not savegame)
```

---

## Palette flags (`LodPaletteEntry`)

| Flag | Bit | Meaning |
|------|-----|---------|
| `FlagWater` | 1 | Water/lava/lake ice |
| `FlagFrost` | 2 | Frosted canopy; mesher mixes UP toward frost white |
| `FlagSkip` | 8 | Fire/meta — dropped at capture |
| `FlagThin` | 16 | Flowers/ferns — drawn see-through |
| `FlagBaked` | 32 | RGB from visit bake; shader must **not** re-tint |

`VisitKeepMask = FlagBaked | FlagFrost` — reclassify must preserve these.

---

## Config / ModConfig relationship

### Client — `DistantVistasConfig`

- **File:** `VintagestoryData/ModConfig/distantvistas.json`
- **Load/save:** `DistantVistasModSystem.StartClientSide` / `SaveConfig()` (chat commands)

| Field | Default | Effect |
|-------|---------|--------|
| `FarViewDistanceCap` | 0 | 0 = unlimited LOD draw distance |
| `DetailDistance` | 320 | Where detail halving starts (`LodWorld.DetailDistance`) |
| `FidelityStep` | 1.0 | LOD ladder aggressiveness |
| `MaxVisualLodLevel` | max | Coarsest visible level (0 = L0 everywhere) |
| `IgnoreOtherLodMods` | false | `.dvdefer off` equivalent — read at startup only |
| `DisableLodFog` | true | Skip extra past-view haze on LOD |
| `OverdrawStart` | 0.55 | LOD draw starts at `viewDistance * OverdrawStart` |
| `PatchVanillaEdgeFade` | true | Ships patched chunk shaders |
| `FovOcclusion` | true | Heightfield occlusion for L0/L1 |
| `LoginVisitSweepEnabled` | true | Join overlay; auto-skipped when gate says skip |

### Server — `LodServerConfig`

- **File:** `ModConfig/distantvistas-server.json`
- **Class:** `Net/LodServerConfig.cs`
- Controls capture, serving, sweep, `/dvgen`, rate limits. Parse failure → defaults in memory, file not overwritten.

### External configs read (not owned)

| File | Reader | Purpose |
|------|--------|---------|
| `farseer-client.json` | `OtherLodMods`, `FarseerShaderOverlay` | Farseer on/off |
| `distantvistas-server.json` | Both server systems | Shared admin settings |

### Persistence surfaces

| Path | Content |
|------|---------|
| `ModData/distantvistas/<world>.db` | Client LOD cache |
| `ModData/distantvistas/<world>-server.db` | Server LOD cache |
| `ModConfig/distantvistas.json` | Client settings |
| `ModConfig/distantvistas-server.json` | Server settings |
| `ModData/distantvistas/login-sweep-complete-<worldId>.json` | Sweep success stamp |
| `ModData/distantvistas/login-sweep-resume-<worldId>.json` | Esc-pause checkpoint |
| `ModData/distantvistas/login-view-hold.json` | Saved view before 750 hold |
| `Logs/distantvistas.json` | Live telemetry |

---

## Build / pack / install paths

| Context | Path |
|---------|------|
| **Debug build output** | `DistantVistas/bin/Debug/net10.0/Mods/distantvistas/` |
| **Release build output** | `DistantVistas/bin/Release/net10.0/Mods/distantvistas/` |
| **Release zip** | `dist/distantvistas_<version>.zip` via `scripts/package.sh` |
| **Dev deploy** | `scripts/deploy-sandbox.sh` → `.testdata/Mods/` |
| **Player install** | `%AppData%/VintagestoryData/Mods/` — drop zip, **do not extract** |
| **ClientMods** | Not the intended install location; double `modPaths` (Mods + ClientMods) causes silent double-load failure (0.8.11) |

`scripts/package.sh`: Release build → zip contents at archive root (no wrapper folder).

`VINTAGE_STORY` env var points at game install for DLL references (not shipped in mod).

---

## Invariants / never-do list

Each item includes **why** — these are enforced by fast-tier checks and/or painful production history.

### Mesh eviction & memory

| Never | Why |
|-------|-----|
| Drop GPU meshes for **distance alone** or mesh count alone | 0.7.21 hard cap (6000/8000) dropped visited L0 → “land disappears” mid-session. `MaxResidentMeshes` is telemetry hint only (`LodMemoryBudget.cs:20-21`) |
| Evict inside **2× view distance** keep ring | Visited land must stay drawn when player turns back. `PressureKeepScale = 2.0f` |
| Evict when `MeshPressureActive` is false | Pressure requires sustained bad FPS (~40ms p95) and/or high managed memory + hitch |
| Touch disk cache during eviction | Eviction frees GPU `MeshRef` only; `LodStore` retains data for demand reload |

### Coverage / cake plates vs sky

| Never | Why |
|-------|-----|
| Submit **L3+ whole footprint** in player FOV lead cone | “Cake plates” — giant stacked squares on horizon (0.7.72 regression). `LeadConeMaxCoverLevel = 2`; L3+ uses `AddGap` + clipped ancestor fill |
| Use parent box as stand-in for **incomplete L0** | Punches sky holes or sheer walls. `PreferParentCoverage` gates completeness, not plate drawing |
| Use horizontal-only disc for vanilla ownership | At altitude, ground is outside sphere; need 3D + look-down rules (`LodCoveragePolicy.InsideVanillaCoverage`) |
| Shrink vanilla disc when looking at feet | Causes LOD/vanilla flicker on loaded ice |

**Terminology:** Maintainer “skybite” = **sky beyond the unvisited frontier** — expected until capture/cache fills that region. Not a code identifier in this repo.

### Login / explore bake

| Never | Why |
|-------|-----|
| Recolor at finalize without streaming chunks | Must teleport + load real columns; `LodLoginBake` comments |
| Bake >8 L0 sections per tick during sweep | 256 GetColor/tick froze Windows → `MaxBakePerTick = 8` |
| Skip explore bake for `FlagBaked` sections | Walk into winter must overwrite stored May colors (`LodExploreBake`) |
| Apply live tint in shader to `FlagBaked` pixels | Purple snap-back / double-tint — shader uses `baked ? vertexColor : vertexColor * tint` |
| Mid-session `/time` retint | Season bake is join/walk capture; calendar month change triggers recapture on rejoin |
| Use `DesiredViewDistance` alone for sweep hold | `RequestMode` overwrites — must write `ClientSettings.viewDistance` |

### Join / splash / GL

| Never | Why |
|-------|-----|
| OrthoMode / present-path GL for splash | Matrix stack overflow, SwapBuffers `GL_INVALID_OPERATION` (0.8.22–0.8.37). Use Cairo `HudElement` only |
| Splash GL before atlas `StageC` complete | TrueScale HD 16k atlas + mipmap race → AV at `GL.BindTexture` (0.8.43) |
| Leave cloudmap FBO bound at LevelFinalize | SwapBuffers “required buffer missing” during char-create (0.8.42) |
| Dispose MeshRef during present / VAO drain | Hangs SwapBuffers — `LodJoinQuiet.SuppressVaoDrain` |
| `ReloadShaders` after Farseer overlay inject | Wipes injected GLSL |

### Capture / storage

| Never | Why |
|-------|-----|
| Read block registry off main thread | `GetBlock(int)` mutates dictionary; storage thread keeps block codes |
| Use UTF-8 in shader source comments | OpenTK truncates GL source by byte/char delta |
| Peek worldgen on existing columns | Would cache pre-edit terrain |
| Server capture in singleplayer unless sweeping | Duplicate cache with client |

### Tint / TrueScale

| Never | Why |
|-------|-----|
| Resample climate tint at camera every frame | Painted entire horizon with local biome (0.8.x fix — resample when keep origin moves 384 blocks) |
| Treat TrueScale `unknown.png` (~0.26,0.29,0.45) as white cap | Walk-away white caps on remote servers (0.7.18–0.7.19) |
| Per-channel local/keep dilute on grass | Lavender skew (0.8.36) — luminance scale in shader instead |

---

## Known bug classes & diagnosis shortcuts

| Symptom | Likely cause | Check first |
|---------|--------------|-------------|
| Purple / wrong snap-back on far land | Live tint applied to `FlagBaked` palette, or `FlagBaked` missing after bake | `LodSeasonBake`, shader `baked` branch in `lodterrain.fsh`; audit `LodLoginBakeAudit` misses |
| Sky rectangles through visited hills | Hard mesh cap or distance eviction (bug if pre-0.8 pressure fix) | `meshPressure` in telemetry; `MeshPressureActive` must be true for eviction; inside 2× VD should block |
| Giant square “cake plates” in FOV | L3+ whole submit in lead cone | `LodCoveragePolicy.MayLeadConeCoarseCover`, `LeadConeMaxCoverLevel` |
| Black screen on join | Splash on wrong render path, or `SuppressRunningGameRender` without present | `LoginSweepChecks`, CHANGELOG 0.8.24–0.8.26 |
| Crash at SwapBuffers on new world | FBO still bound, or terrain GL during deferral | `RestorePresentFramebuffer`, `LoginBakeBlocked`, `LodLoginBakeCharacterWait` |
| AV during TrueScale atlas compose | 16k atlas mipmap | `ClampJoinAtlasSize` (8192 cap) in ModSystem |
| Far LOD grey / manila grass | Climate tint skipped on olive grass, or valuenoise on near L0 | `LodPaletteRepair`, `LodTintRegistry`, coarse-only plate breakup (`columnBlocks >= 8`) |
| Mod silently not loading | Duplicate modPaths (Mods + ClientMods) | Install only in AppData `Mods/` |
| No distant terrain at all | Deferring to ChunkLOD/TopoHorizon | `.dvistas` status, `.dvdefer off` + restart |
| Cross-world sweep skip | Global complete marker (fixed 0.8.24) | Per-world `login-sweep-complete-<worldId>.json` |
| Land beyond walked disk is sky | Expected client-side limit | Bootstrap disk ~36 km; beyond = unvisited frontier until explore/`/dvgen` |
| White cliffs after walking away | Parent mip bright-cap + no L0 mesh at handoff | `VisitedKeepChecks`, hold L0 at vanilla ring |
| Season wrong until rejoin | By design | `/time` does not retint; 30-day window or month change on rejoin |
| Overlay stalls at ~358 / ~639 / ~708 | Stream cliff — vanilla VD culls cold L0 past stream radius | [Login bake stream cliffs](#login-bake-stream-cliffs--chunkdb-autosave-1033) — **not Farseer** |
| SP dies after overlay climbs | `chunkdbthread` won't suspend; autosave ×20; FIFO overflow | Stepped stream + request budget — **not** queue-size-only bump |

---

## Login sweep & explore bake (detail)

### Gate (`LodLoginSweepGate.Decide`)

Runs sweep when:
- Resume checkpoint exists (Esc-pause)
- Empty canvas (bootstrap)
- No per-world complete marker
- 30-day window expired (in-game days **or** wall days)
- Paint revision bump (`LodSurfaceMix.PaintRevision`)
- Calendar month changed
- Audit misses or canvas grew since last sweep

Skips when complete marker matches world, visited count stable, within window.

### Sweep bake path (locked)

```
teleport → stream chunks → SweepLoadedColumns
→ QueueL0SectionForce → wait capture idle
→ LodSeasonBake.BakeSectionFromVisit (block.GetColor at real X/Y/Z)
→ FlagBaked + persist
```

Max **8 L0 bakes/tick** on **1.0.0** (`MaxBakePerTick`). **1.0.33+** uses 16 parallel scouts + time-budgeted chunked paint instead of per-stop teleports — see [login bake stream cliffs](#login-bake-stream-cliffs--chunkdb-autosave-1033).

View: **1.0.0** holds **750** (`LodLoginBakeViewBoost`). **1.0.43+** uses frontier-relative overlay stream (1024→2048 stepped), not a constant 750 hold.

### Explore bake

- **1 L0/tick** (2 when capture backlog busy)
- Same `BakeSectionFromVisit` / `GetColor` — **not** shader repro
- Re-queues `FlagBaked` so walking into loaded winter overwrites stored colors
- GPU mesh swap only after new mesh uploads (`RequestGpuSwap`)

### 30-day window (`LodLoginSweepWindow`)

- 30 in-game days **or** 30 wall-clock days since sweep stamp
- Later calendar month forces recapture even inside window (0.8.87)
- `PaintRevision` bump forces one-time recapture

---

## Login bake stream cliffs / chunkdb autosave (1.0.33+)

**Read this before touching login overlay stream code.** A local agent (~6h, runId 1044/1045) rediscovered these traps on branch `cursor/login-bake-chunkdb-autosave-1045-dba0` @ **1.0.45** (`c8ff709`). Files below exist on that branch, **not on `main` (1.0.0)**.

### Authoritative plan docs (on login-bake branch tip)

| Doc | Status @ `c8ff709` | Contents |
|-----|-------------------|----------|
| [`docs/plans/login-bake-walltime-1033.md`](plans/login-bake-walltime-1033.md) | **Present** | Full playtest history §1.0.33–§1.0.45, scout FIFO, cliffs, chunkdb death |
| `docs/plans/login-bake-cliff-forecast-1045.md` | **Not on tip** | Folded into walltime doc §1.0.44–§1.0.45 |
| `docs/plans/login-bake-chunkdb-autosave-lessons.md` | **Not on tip** | Folded into walltime doc §1.0.45 + CHANGELOG 1.0.45 |

Fetch the branch to read plans: `git fetch origin cursor/login-bake-chunkdb-autosave-1045-dba0`

### Two different radii (do not conflate)

| Radius | Blocks | Purpose |
|--------|--------|---------|
| **Visit / Farseer disk** | **4075** (`4.5 × 750 + 700`) | Scout visit targets, FlagBaked bake, Farseer onset — **not** vanilla tessellation |
| **Overlay vanilla stream** | **1024 → 2048** (clamped) | `ClientSettings.viewDistance` during overlay — what the client **streams** from server |
| **Sweep baseline** | **750** | Farseer/visit math onset; **not** the overlay stream floor after 1.0.43 |

Disk visit target **~4075 L0 keys** ≠ overlay stream. Stream is clamped **≤ 2048** (VS client ceiling). Full **~1680** revisit stops fill a disk of radius ~1478 blocks → stream target ~1760, still under 2048.

### Lineage cliffs (geometry, not random)

Each cliff is **filledRadius / overlayVD ≈ 0.89** — cold pending sits just past the vanilla view-cull edge:

| Cliff | Overlay VD | Filled radius | `finished` | Mechanism |
|-------|------------|---------------|------------|-----------|
| **358** | **750** constant | ~683 blocks | ~358 | Client view-culls map chunks past 750; scouts park in WaitChunks |
| **639** | **1024** constant (1.0.43) | ~913 blocks | ~639 | 1024 fixed stream ceiling; same 0.89 ratio |
| **708–734** | Growing stream (1.0.44–45) | ~630–650 blocks past stream | ~708–734 | Still failing in 1.0.45 playtests — target is through to **1680** |

**Wrong fix:** cap vanilla stream at **750** “login hold” — that **causes** the 358 cliff (view-cull kills cold L0 annulus).

**Right fix (1.0.44+):** frontier-relative `OverlayStreamBlocks(finished)`:

```
max(1024, finishedRadius + 256, finishedRadius / 0.88)
→ snap to chunk size → clamp 1024..2048
```

**Right fix (1.0.45+):** do **not** apply that target in one write. Step applied VD: **+32 blocks / 5 s** toward desired; skip grow when overlay hitch ≥ **800 ms** (`StreamGrowHitchPressureMs`).

Key file: `LodLoginBakeViewBoost.cs` — `StreamGrowStepBlocks`, `StreamGrowDwellMs`, `StepAppliedStream()`, `OverlayStreamBlocks()`.

### Symptom: SP dies after overlay climbs (runId 1044)

**Not Farseer.** Do not plan “remove Farseer during bake.”

After `finished` passes **639** with `streamViewBlocks` 1184→1216 and Capture alive, integrated server shuts down:

```
Unable to autosave, was not able to pause the server
threads were not suspended: chunkdbthread  (×20)
Server autosave failure
Indexed Fifo Queue overflow  (RequestChunkColumnsQueue / SupplyChunks / loadChunkAreaBlocking)
```

Server ticks hit **~3.2 s**. Autosave must pause `chunkdbthread`; a blocking FIFO means 20 failed suspends → kill (world may still save on shutdown).

### What does **not** fix it

| Approach | Why it fails |
|----------|--------------|
| Only bump `RequestChunkColumnsQueueSize` in `servermagicnumbers.json` | 2000 still overflowed — flood is the problem, not just headroom |
| One-shot VD jump 1024→1184 | Re-requests entire new disk at once (`EnsureBoosted` 1.0.44) |
| Hop resets `spawnRevealRadius` + full `RequestChunkColumnsVisible` (~40 chunks) | Re-dumps thousands of `SetChunkColumnVisible` |
| Scout `RequestUp` retry re-queuing whole KeepLoaded rings | 16 × (2r+1)² FIFO entries per HoldAnchor release |
| Constant **750** overlay VD | 358 cliff |
| Constant **1024** without grow | 639 cliff |

Optional **safety net only** (after mod backpressure): double `RequestChunkColumnsQueueSize` in `servermagicnumbers.json` while baking.

### What **does** fix it (1.0.45 shipped)

| Mechanism | File / constant |
|-----------|-----------------|
| Stepped stream **+32 / 5 s** + hitch hold | `LodLoginBakeViewBoost` |
| `SetChunkColumnVisible` budget **96/tick** | `LodLoginChunkRequestBudget.MaxVisiblePerTick` |
| Spawn-solid reveal cap — **do not** reset reveal on hop | `LodLoginBake`, `SpawnSolidStreamChunks()` |
| Hop visible = L0 neighbourhood only (radius **2**) | `LodLoginHopUnlock` |
| Idempotent `HoldAnchor` (same key/cx/cz/radius) | `LodScoutHostSystem` |
| Staggered priority loads **24/tick**, queue cap **512** | `LodScoutHostSystem.MaxPriorityLoadsPerTick` |
| Player stays at **pickup**; stream grows around pickup | No hop CameraPos follow (fights server WorldManager) |
| **16 scouts primary**; hop = `RequestUp` KeepLoaded pump only | `LodLoginScoutFill`, `LodScoutHostSystem.RequestUp` |

### After overlay ends

1. **Restore VD slider** — never restore overlay stream values (1184/1536/2048) as the player's graphics setting.
2. **Background past 2048** — remaining sparse visits on the **4075 disk** use throttled scout `KeepLoaded` / `PlayModeBakeBudget` + `LodExploreBake`, same backpressure discipline as overlay.

### Pass bar (playtest)

- `finished` climbs **past 734 → toward 1680** with server still up
- No `Server autosave failure` in `server-main.log`
- `stream-grow` telemetry: **+32** steps, `dwellMs: 5000` (not one-shot 1024→1184)
- `hitchPressure` true during long ticks, then clears
- `captureLive > 0`, `paintReadyQueued > 0` while scouts live
- Graphics view slider restores to pre-overlay value

### Telemetry filters

| Filter | Meaning |
|--------|---------|
| `H-SCOUT-SEQ` / `scout-budget` | `streamViewBlocks`, `paintReadyQueued`, `waitChunksLive`, `captureLive`, `paintStarveTicks` |
| `stream-grow` (runId **1045**) | `desiredStreamBlocks`, `stepBlocks`, `dwellMs`, `hitchMs`, `hitchPressure` |
| `hop-unlock` / `hop-residency-probe` | `distFromPickup`, `residencyLoaded` — should track filled radius, not stuck 836–846 |
| `server-main.log` | `chunkdbthread`, `RequestChunkColumnsQueueSize`, autosave suspend failures |

### Scout architecture (1.0.33+, replaces teleport sweep)

On **1.0.0 `main`**, login sweep **teleports** the player. From **1.0.33** on the feature branch:

- **16 parallel scout entities** (`LodScoutFill`, `LodScoutViewerEntity`) bake L0 via capture→paint pipeline
- Player stays at exact pickup XYZ; look locked
- `BakeSectionFromVisitChunked` — time-budgeted GetColor (120 ms/tick), not 4096×16 sync bake
- Post-overlay: `PlayModeBakeBudget` continues horizon fill during play

Do **not** revert to teleport-per-stop or raise `MaxBakePerTick` without chunked paint — 256 GetColor/tick froze Windows.

---

## Mesh scheduling & eviction (detail)

### Scheduling (`LodTerrainRenderer`)

| Constant | Value |
|----------|-------|
| Mesh schedules/frame | 12 |
| Load requests | 32 |
| Upload/frame | 8 |
| Upload wall budget | 2 ms |
| Visited-keep farthest slots | 4 |

### Three distance rings

1. **Full detail (1.0× VD)** — submit L0/L1 for drawing
2. **Keep circle (1.25–2.0× VD, RAM-based)** — GPU residency + skip frustum cull for visited L0/L1
3. **Pressure eviction (2.0× VD)** — only under `MeshPressureActive`; drop oldest L0/L1 outside ring

### Pressure (`LodMemoryBudget` + `UpdateMeshPressure`)

Enter: p95 ≥ 40 ms or avg ≥ 37 ms (sustained 1.5s), or managed memory + hitch storm.

Clear: p95 < 25 ms and avg < 28 ms (sustained 2.5s).

Eviction: max 2/frame, 20-frame cooldown, never inside 2× VD, never for count alone.

Telemetry fields: `meshPressure`, `evictOutside2x`, `evictBlockedInside2x`.

---

## Cake plates vs sky (coverage model)

**Cake plate** = coarse parent mesh drawn as a whole square footprint in the view cone — reads as a shelf on the horizon.

**Prevention:**
- `LeadConeMaxDrawLevel = 1` — L0/L1 fine in cone
- `LeadConeMaxCoverLevel = 2` — L2 land-like cover allowed; **L3+ hard banned** whole in cone
- Holes with only L3+ ancestor: `AddGap` + clipped mip fill, wait for L1 remesh
- `HorizonLeadCone` + `lookDown01` — looking down allows more coarse fill on ground, not sky shelf

**Sky beyond frontier** = unvisited / uncached regions. Client-only mod cannot see unstreamed terrain. Join bootstrap paints ~36 km disk; beyond that is sky until explore or `/dvgen`.

---

## TrueScale interactions

| Issue | Mitigation | File |
|-------|------------|------|
| 16k atlas AV during mipmap compose | Clamp block/item/entity atlas to **8192** before vanilla compose | `ClampJoinAtlasSize()` in ModSystem |
| `unknown.png` slate on isolated worlds | `LodPaletteRepair.IsMissingTextureSky` — not white cap | `LodPaletteRepair.cs` |
| Join deferred until atlas ready | `joinAtlasResolved`, character UI wait | `DeferLoginVisitSweep` |

---

## Purple LOD tint

In `lodterrain.fsh`, far terrain goes **purple/dark at night** via `rgbaAmbientIn` — not a debug LOD level color. Not related to `FlagBaked` mismatch (that causes live-tint snap-back on baked pixels).

CHANGELOG “purple explore FlagBaked” (0.8.43) referred to bake/tint interaction fixes, not this ambient night effect.

---

## Farseer / other LOD mods

| Mod | Behavior |
|-----|----------|
| **Farseer** | Companion since 0.7.55 — both draw; DV tiles on top where present. Shader overlay inject **off** at tip (`OverlayActive = false`). `YieldFootprintToCompanion` always false (0.8.x — yielded holes). |
| **ChunkLOD / TopoHorizon** | Defer by default — `.dvdefer off` + restart to draw anyway |
| **Komet** | Not a terrain drawer — compatible; does not fill DV view-ring holes |

---

## Testing tiers

```sh
scripts/check.sh fast    # ~30s — logic, wire format, invariants (no game)
scripts/check.sh smoke   # ~5 min — E2E sandbox + warm cache readback
scripts/check.sh matrix  # ~20 min — server/client/mod combos, deferral, admin switches
```

**No CI** — VS assemblies not redistributable. Fast tier is the daily gate.

Key check files mapping to systems:

| Check file | System |
|------------|--------|
| `LoginSweepChecks.cs` | Join overlay, gate, splash path |
| `ExploreBakeChecks.cs` | Walk bake, GPU swap |
| `SeasonBakeChecks.cs` | FlagBaked, frost, snow vote |
| `ResidencyChecks.cs` | Disk vs live section safety |
| `VisitedKeepChecks.cs` | Trail retention, frustum bypass |
| `OcclusionChecks.cs` | Heightfield cache |
| `PolicyChecks.cs` | Block classification |
| `MipChecks.cs` | Parent downsampling |
| `ConfigChecks.cs` | Server config clamps |
| `TintClampChecks.cs` | Climate tint safety |
| `SplashResolutionChecks.cs` | Cover-fit layout |

---

## Commands reference

| Command | Side | Purpose |
|---------|------|---------|
| `.dvistas` | Client | Status: sections, meshes, deferral, pressure |
| `.dvdetail [blocks]` | Client | Detail distance (default 512 in README, 320 in config default) |
| `.dvfar <blocks>` | Client | Far cap; 0 = unlimited |
| `.dvdefer [on\|off]` | Client | Other LOD mod deferral (startup only) |
| `/dvserver` | Server | Assist status |
| `/dvgen start\|stop\|status` | Server | Transient worldgen capture (not savegame) |

---

## Related docs

- [DESIGN.md](../DESIGN.md) — full architecture, wire protocol, test philosophy
- [WHAT-WE-DO.md](WHAT-WE-DO.md) — fork deltas from Horizons
- [RELEASING.md](RELEASING.md) — release checklist
- [CHANGELOG.md](../CHANGELOG.md) — version history
- [AGENTS.md](../AGENTS.md) — agent front door
