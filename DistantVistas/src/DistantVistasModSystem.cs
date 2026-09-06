using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using DistantVistas.Net;

namespace DistantVistas;

public class DistantVistasConfig
{
    /// <summary>0 = unlimited draw coverage (coarse far stays on screen). GPU L0/L1 still pages by keep-circle.</summary>
    public int FarViewDistanceCap = 0;

    /// <summary>Distance at which detail starts halving; see LodWorld.DetailDistance.</summary>
    public int DetailDistance = 320;

    /// <summary>0 = 0.7.9 ladder aggressiveness; 1 = one fidelity step up (default).</summary>
    public float FidelityStep = 1.0f;

    /// <summary>
    /// Coarsest visible LOD. Default is the full pyramid so the horizon can
    /// draw L1/L2/L3. 0 forces L0 everywhere (the 0.7.31 4621-L0 path).
    /// </summary>
    public int MaxVisualLodLevel = LodWorld.MaxLevel;

    /// <summary>
    /// Draw even when another LOD mod is installed and switched on. The escape hatch for
    /// the mods whose own switch we cannot read; see <see cref="OtherLodMods"/>. Read at
    /// startup only, because turning the mod on mid-session is not something the startup
    /// path supports.
    /// </summary>
    public bool IgnoreOtherLodMods = false;

    /// <summary>When true, skip extra past-view haze on LOD. Live vanilla fog still applies so the overdraw ring matches chunks in front.</summary>
    public bool DisableLodFog = true;

    /// <summary>Multiply ambient fog density on LOD.</summary>
    public float FogDensityScale = 1.0f;

    /// <summary>Where LOD starts dissolving into sky (0..1 of LOD band).</summary>
    public float SkyFadeStart = 0.88f;

    /// <summary>Extra haze past vanilla view distance when fog is enabled.</summary>
    public float PastViewHaze = 0.22f;

    /// <summary>
    /// DH-style overdraw: LOD may draw from liveViewDistance * OverdrawStart outward.
    /// Lower = more overlap under vanilla/fog; 1.0 = start at cut (seams). Default 0.35.
    /// </summary>
    public float OverdrawStart = 0.55f;
    /// <summary>
    /// When true (default), this mod ships patched <c>assets/game/shaders/</c> chunk vertex
    /// shaders that disable vanilla's view-distance edge alpha fade (TopoHorizon/Defog).
    /// The override is always packaged for 0.7.5; turning this false cannot unload the
    /// asset override without a client restart / removing the mod files. Reserved for a
    /// future toggle that can skip shipping or hot-swap.
    /// </summary>
    public bool PatchVanillaEdgeFade = true;
    /// <summary>Potato FOV heightfield occlusion at draw-submit (L0/L1). Default on.</summary>
    public bool FovOcclusion = true;

    /// <summary>Samples along camera-to-tile XZ ray for FOV occlusion (4..16). Default 6 for turn cost.</summary>
    public int FovOcclusionSamples = 6;

    /// <summary>Height slack (blocks) so peaks/towers that clear a ridge still draw.</summary>
    public int FovOcclusionPeekMargin = 32;

    /// <summary>Fresh occlusion ray tests per frame (cached results free). Default 48.</summary>
    public int FovOcclusionMaxTestsPerFrame = 48;

    /// <summary>
    /// Login visit sweep on join (overlay + teleports + season bake). ON by default —
    /// skipped automatically when the visited canvas is complete within the season window.
    /// Set false in distantvistas.json for immediate 0.7.78-style play without overlay.
    /// </summary>
    public bool LoginVisitSweepEnabled = true;
}

/// <summary>
/// Client entry point: wires the shared <see cref="LodPipeline"/> to client events and
/// owns everything the server has no equivalent of - the renderer, tint registry, chat
/// commands and telemetry. Chunk columns arrive from `ChunkDirty` and go straight to the
/// pipeline, which does the capture, mip and persistence work.
/// See DESIGN.md at the repo root.
/// </summary>
public class DistantVistasModSystem : ModSystem
{

    ICoreClientAPI capi = null!;
    LodPipeline pipeline = null!;
    LodTerrainRenderer renderer = null!;

    /// <summary>Block -> live tint slot; shared by capture, cache loads and the renderer.</summary>
    readonly LodTintRegistry tints = new();
    long tickListenerId;
    SessionTelemetry? sessionTelemetry;

    readonly BlockPos paletteSamplePos = new(0, 0, 0);
    readonly BlockPos colorProbePos = new(0, 0, 0);
    readonly Dictionary<int, int> stableColorByBlockId = new();
    const int StableColorSamples = 64;

    // Dev auto-explore (unattended stress testing): teleport along an expanding
    // spiral so fresh chunks stream through the pipeline continuously.
    bool autoExplore;
    int exploreLeg;      // spiral leg counter
    int exploreStep;     // steps taken on current leg
    int exploreDirX = 1, exploreDirZ;
    double exploreX, exploreZ;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    /// <summary>
    /// After Farseer's default 0.1 so AssetsLoaded can patch region.vsh before they
    /// compile, and StartClientSide can log the marker after that compile.
    /// </summary>
    public override double ExecuteOrder() => 0.6;

    /// <summary>Everything the config file holds, so a partial save cannot drop a setting.</summary>
    DistantVistasConfig config = new();

    /// <summary>Set when another LOD mod is drawing; we then stay out of its way.</summary>
    string? deferringTo;

    /// <summary>Farseer is loaded and switched on: unvisited land is their silhouette.</summary>
    bool farseerCompanion;

    /// <summary>
    /// Optional server assist (DESIGN.md Ã‚Â§10). Created even while deferring, because the
    /// channel has to be registered before the handshake either way; it simply never
    /// greets, so it stays silent.
    /// </summary>
    LodAssistClient? assist;

    LodLoginBakeVanillaLoadingHold? loginVanillaLoading;
    LodLoginBakeOverlay? loginBakeOverlay;
    LodLoginBakePulse? loginBakePulse;
    LodLoginBake? loginBake;
    long? loginSweepDeferListenerId;
    bool loginSweepDeferred;

    public override void AssetsLoaded(ICoreAPI api)
    {
        if (api.Side != EnumAppSide.Client) return;
        if (!FarseerShaderOverlay.OverlayActive) return;
        if (!api.ModLoader.IsModEnabled("farseer")) return;
        if (FarseerShaderOverlay.ApplyBytes(api, Mod.Logger))
        {
            Mod.Logger.Notification("Farseer overlay bytes written. {0}",
                FarseerShaderOverlay.Describe(api));
        }
        else
        {
            Mod.Logger.Warning("Farseer overlay did not apply. {0}",
                FarseerShaderOverlay.Describe(api));
        }
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;

        LodLoginBakeHarmony.Apply(Mod);

        try
        {
            config = capi.LoadModConfig<DistantVistasConfig>("distantvistas.json") ?? new DistantVistasConfig();
        }
        catch
        {
            config = new DistantVistasConfig();
        }

        LodLoginBakeHarmony.IsLoginSweepEnabled = () => LoginVisitSweepEnabled();

        // Before anything that can return early, and before the connection handshake:
        // the server learns we speak this channel at handshake time and never again. A
        // client that skips the registration makes the game log "Server sends me channel
        // name distantvistas, but no client side mod registered it" on every join,
        // which reads like a broken install. The channel stays inert until Greet(), and
        // only the active path reaches that.
        assist = new LodAssistClient(capi, Mod.Logger, Mod.Info.Version);
        assist.Register();

        // Two LOD mods both extend the camera's far plane and both draw terrain out
        // there, so they z-fight over the same ground. Defer rather than fight. What we
        // must not do is defer to a mod that is installed but switched off, which leaves
        // the player with nothing at all.
        deferringTo = ChooseDeferralTarget();
        if (deferringTo != null)
        {
            Mod.Logger.Notification(
                "'{0}' is installed and switched on, so DistantVistas stays idle. Two LOD mods "
                + "draw over each other and fight for the camera far plane. Switch '{0}' off in its "
                + "own settings, then restart the game to use DistantVistas instead. The mod "
                + "decides this once at startup: if you switch the other mod off now, nothing "
                + "changes until the next start.",
                deferringTo);
            RegisterCommands();

            // The idle path reaches no LevelFinalize handler of its own, so the dev hook
            // is wired straight to the event here. Without it the matrix cannot drive
            // '.dvdefer off' from the state that command exists for.
            capi.Event.LevelFinalize += RegisterAutoCommand;
            FinishFarseerOverlay();
            return;
        }

        LodWorld.DetailDistance = GameMath.Clamp(config.DetailDistance,
            (int)LodWorld.MinDetailDistance, (int)LodWorld.MaxDetailDistance);
        LodWorld.FidelityStep = GameMath.Clamp(config.FidelityStep, 0f, 2f);
        LodWorld.MaxVisualLevel = GameMath.Clamp(config.MaxVisualLodLevel, 0, LodWorld.MaxLevel);

        pipeline = new LodPipeline(capi, Mod.Logger, DescribePalette, block => (byte)TintSlotOf(block));

        // Refreshes old stable colours as well as empty server colours. Client-side only:
        // this needs the texture atlas and topsoil textures; a server stores 0 on purpose.
        pipeline.RepairUncoloredPalette = RefreshStoredPalette;
        pipeline.HealLegacyPalette = HealLegacySection;
        renderer = new LodTerrainRenderer(capi, pipeline.World, pipeline.Worker, tints)
        {
            AutoUnpause = Environment.GetEnvironmentVariable("VINTAGEHORIZONS_AUTOUNPAUSE") == "1",
            FarViewDistanceCap = config.FarViewDistanceCap,
            DisableLodFog = config.DisableLodFog,
            FogDensityScale = config.FogDensityScale,
            SkyFadeStart = config.SkyFadeStart,
            PastViewHaze = config.PastViewHaze,
            OverdrawStart = config.OverdrawStart,
            DrawAfterCompanion = farseerCompanion,
        };
        renderer.HeightOcclusion.Enabled = config.FovOcclusion;
        renderer.HeightOcclusion.SampleCount = config.FovOcclusionSamples;
        renderer.HeightOcclusion.PeekMarginBlocks = config.FovOcclusionPeekMargin;
        renderer.HeightOcclusion.MaxTestsPerFrame = config.FovOcclusionMaxTestsPerFrame;
        pipeline.InvalidateGpuMesh = renderer.InvalidateGpuMesh;
        loginVanillaLoading = new LodLoginBakeVanillaLoadingHold(capi);
        capi.Event.RegisterRenderer(loginVanillaLoading, EnumRenderStage.Ortho, "distantvistas-login-vanilla");
        capi.Event.RegisterRenderer(loginVanillaLoading, EnumRenderStage.AfterFinalComposition,
            "distantvistas-login-vanilla-final");
        capi.Event.RegisterRenderer(loginVanillaLoading, EnumRenderStage.Done,
            "distantvistas-login-vanilla-done");
        loginBakePulse = new LodLoginBakePulse();
        loginVanillaLoading.OnRenderPulse = dt => loginBakePulse.Pulse(dt);
        LodLoginBakeHarmony.PaintSplashCover = () => loginVanillaLoading.PaintSweepFrame();
        LodLoginBakeHarmony.RenderPulse = dt =>
        {
            loginBakePulse.BeginFrame();
            loginBakePulse.Pulse(dt);
        };
        loginBakeOverlay = new LodLoginBakeOverlay(capi, loginVanillaLoading);
        // Real holes (captured land with no mesh at any rung) are reported with
        // the state of the keys involved, so a screenshot of sky has a log line.
        renderer.SetHoleLogger(msg => Mod.Logger.Notification(msg));

        capi.Event.ChunkDirty += OnChunkDirty;
        capi.Event.LevelFinalize += OnLevelFinalize;
        capi.Event.LeaveWorld += OnLeaveWorld;

        tickListenerId = capi.Event.RegisterGameTickListener(OnGameTick, 50);
        sessionTelemetry = new SessionTelemetry(capi);

        RegisterCommands();

        FinishFarseerOverlay();
        if (!FarseerShaderOverlay.OverlayActive)
        {
            Mod.Logger.Notification(
                "Farseer overlay is off. We draw the hills. Their heightmaps are the far silhouettes.");
        }
        Mod.Logger.Notification("DistantVistas {0} loaded (client-only)", Mod.Info.Version);
        try
        {
            Mod.Logger.Notification(
                "DistantVistas origin: {0} ({1})",
                Mod.FileName ?? "(unnamed)",
                Mod.SourcePath ?? "(unknown path)");
        }
        catch
        {
            // Diagnostic only.
        }
    }

    /// <summary>
    /// AssetsLoaded already wrote the bytes. Do not re-register Farseer's region
    /// program. ReloadShader re-apply is FarseerOverlayEarlyHook (0.05).
    /// </summary>
    void FinishFarseerOverlay()
    {
        if (!FarseerShaderOverlay.OverlayActive) return;
        if (!capi.ModLoader.IsModEnabled("farseer")) return;
        FarseerShaderOverlay.ApplyBytes(capi, Mod.Logger);
        if (FarseerShaderOverlay.MarkerPresent(capi))
        {
            Mod.Logger.Notification("Farseer overlay in place (no region re-register). {0}",
                FarseerShaderOverlay.Describe(capi));
        }
        else
        {
            Mod.Logger.Warning("Farseer overlay marker MISSING after ApplyBytes. {0}",
                FarseerShaderOverlay.Describe(capi));
        }
    }

    /// <summary>
    /// A dev hook for the matrix tier: send one chat command shortly after the world is
    /// up. The harness cannot reach a detached server's console, and a second entry point
    /// into a command would test something other than what players type. The delay lets
    /// the handshake and the privilege grant settle first.
    ///
    /// Reachable from the idle path too, and not for symmetry: '.dvdefer off' is the
    /// escape hatch we tell an idle player to use, and it writes the config file from a
    /// state where the renderer does not exist. That is the one command whose failure
    /// would strand exactly the player it is meant to rescue.
    /// </summary>
    void RegisterAutoCommand()
    {
        if (Environment.GetEnvironmentVariable("VINTAGEHORIZONS_AUTOCMD") is not { Length: > 0 } autoCmd)
        {
            return;
        }

        capi.Event.RegisterCallback(_ =>
        {
            Mod.Logger.Notification("Auto-command: {0}", autoCmd);

            // SendChatMessage sends to the SERVER, so it runs '/' commands and silently
            // does nothing with a client-side '.' one: the message goes out, no handler
            // on either side claims it, and the log looks like a success. Client commands
            // have to be dispatched locally.
            if (autoCmd.StartsWith('.'))
            {
                capi.ChatCommands.ExecuteUnparsed(autoCmd, new TextCommandCallingArgs
                {
                    Caller = new Caller
                    {
                        Player = capi.World.Player,
                        Pos = capi.World.Player.Entity.Pos.XYZ,
                        FromChatGroupId = GlobalConstants.GeneralChatGroup,

                        // The game fills these in when a person types the command; a
                        // caller built here starts with none and is refused by its own
                        // privilege test. Wildcard, because this hook exists only to
                        // reproduce what a player does, and a test that cannot reach the
                        // command tests nothing.
                        CallerPrivileges = new[] { "*" },
                    },
                }, result => Mod.Logger.Notification("Auto-command result: {0}", result.StatusMessage));
                return;
            }

            capi.SendChatMessage(autoCmd);
        }, 15000);
    }

    /// <summary>
    /// The mod to stay idle for, or null to draw. Reports what it found either way: a
    /// player who sees no distant terrain needs the log to say which mod stopped us, and
    /// a player whose other mod is switched off needs to know we noticed.
    /// </summary>
    string? ChooseDeferralTarget()
    {
        (string? drawing, string[] switchedOff, string[] companions) =
            OtherLodMods.Inspect(capi.ModLoader.IsModEnabled, ReadOtherModSwitch);
        farseerCompanion = false;

        if (switchedOff.Length > 0)
        {
            Mod.Logger.Notification(
                "Installed but switched off in its own settings, so DistantVistas is not "
                + "staying idle for it: {0}", string.Join(", ", switchedOff));
        }

        if (companions.Length > 0)
        {
            farseerCompanion = companions.Any(id =>
                string.Equals(id, "farseer", StringComparison.OrdinalIgnoreCase));
            Mod.Logger.Notification(
                "Drawing our land. Companion LOD sits behind for far silhouettes: {0}",
                string.Join(", ", companions));
        }

        if (drawing != null && config.IgnoreOtherLodMods)
        {
            Mod.Logger.Warning(
                "'{0}' is switched on, and IgnoreOtherLodMods is set, so DistantVistas is drawing "
                + "anyway. Switch '{0}' off in its own settings, or the two draw over the same ground.",
                drawing);
            return null;
        }

        return drawing;
    }

    /// <summary>Reads another mod's own switch. Null means "could not tell", which counts as on.</summary>
    bool? ReadOtherModSwitch(string file)
    {
        try
        {
            return capi.LoadModConfig<OtherLodModSwitch>(file)?.Enabled;
        }
        catch (Exception e)
        {
            // Another mod's config file is not ours to repair, and guessing "off" would
            // put us on the same ground as a mod that is still drawing.
            Mod.Logger.Warning("Could not read '{0}' to tell whether that mod is switched on: {1}",
                file, e.Message);
            return null;
        }
    }

    void OnChunkDirty(Vec3i chunkCoord, IWorldChunk chunk, EnumChunkDirtyReason reason)
    {
        pipeline.QueueColumn(chunkCoord.X, chunkCoord.Z);
    }

    void PumpLoginBakeWhileSweeping()
    {
        PumpServerAssist();
        PumpLocalOffers();
        pipeline.Tick();
    }

    void OnGameTick(float dt)
    {
        if (!pipeline.Active) return;

        if (loginBake?.Active == true)
        {
            // Sweep ticks run from LodLoginBakePulse on the render thread — game tick
            // listeners stall while GuiScreenLoadingGame is held during the visit bake.
            return;
        }

        sessionTelemetry?.Tick(pipeline, renderer, deferringTo, Mod.Info.Version);

        ReportFillIn();
        PumpServerAssist();
        PumpLocalOffers();
        pipeline.Tick();

        var pos = capi.World.Player.Entity.Pos;
        int chunkSize = GlobalConstants.ChunkSize;
        int sweepCx = (int)Math.Floor(pos.X / chunkSize);
        int sweepCz = (int)Math.Floor(pos.Z / chunkSize);
        int sweepRadius = Math.Max(4, (int)Math.Ceiling(renderer.LiveViewDistance / chunkSize) + 2);
        pipeline.SweepLoadedColumns(sweepCx, sweepCz, sweepRadius);
        // Cold RAM section spill only under mesh pressure — never distance-alone.
        if (renderer.MeshPressureActive && pipeline.MaybeEvictAround(pos.X, pos.Z))
        {
            LodWorld world = pipeline.World;
            Mod.Logger.Notification("Evict sweep at {0},{1}: checked {2}, pinned {3}, cold {4}, total evicted {5}",
                (int)pos.X, (int)pos.Z, world.LastSweepChecked, world.LastSweepPinned,
                world.LastSweepCold, world.EvictedSectionsTotal);
        }
    }

    /// <summary>
    /// Adopt whatever the server sent, then ask for what the render path now wants.
    /// Both on the game tick, because both mutate the LodWorld.
    /// </summary>
    void PumpServerAssist()
    {
        if (assist == null || !assist.Available) return;

        int before = pipeline.RemoteOnly.Count;
        assist.Pump((key, blob) =>
        {
            if (blob.Length > 0 && pipeline.InstallForeignBlob(key, blob, RecolorForeignSection)) return true;
            pipeline.MarkRemoteUnavailable(key);
            return false;
        });

        // Manifest keys become quadtree-visible here rather than in the packet handler:
        // HasDataSet belongs to this thread.
        if (assist.RemoteKeys.Count > 0) pipeline.AddRemoteKeys(assist.RemoteKeys);

        // Nearest first. The render path asks for exactly the sections it wants, but far
        // more than the in-flight cap allows at once, and an unordered set hands the
        // network whatever hashes first - so distant terrain arrived while ground in front
        // of the player stayed at its coarse parent, which is what the no-holes rule draws
        // until all four children land. Sorting here rather than in the pipeline keeps the
        // pipeline free of any notion of where the viewer is.
        long[] wanted = pipeline.RemoteWanted();
        if (wanted.Length > 1)
        {
            var at = capi.World.Player.Entity.Pos;
            double px = at.X, pz = at.Z;
            Array.Sort(wanted, (a, b) =>
                LodWorld.NearestDistanceSqTo(a, px, pz).CompareTo(LodWorld.NearestDistanceSqTo(b, px, pz)));
        }
        pipeline.MarkRemoteRequested(assist.Request(wanted));

        if (pipeline.RemoteOnly.Count != before && !loggedRemoteKeys)
        {
            loggedRemoteKeys = true;
            Mod.Logger.Notification(
                "Server assist: {0} sections offered that this client never captured. "
                + "The mod fetches them as the view needs them.", pipeline.RemoteOnly.Count);
        }
    }

    bool loggedRemoteKeys;

    /// <summary>
    /// The server side's cache for this same singleplayer world, when it has swept one.
    /// Null on a dedicated server (the network assist covers that) and on any world that
    /// has never swept.
    /// </summary>
    LodLocalOfferSource? localOffers;
    bool loggedLocalOffers;
    int localOfferProbeTicks;
    int localOfferScanTicks;

    /// <summary>
    /// About 5s at the 50ms tick. The retry usually answers with one failed File.Exists,
    /// so it stays cheap; anything shorter is pointless for a cache that grows for
    /// minutes once it appears.
    /// </summary>
    const int LocalOfferProbeIntervalTicks = 100;

    /// <summary>
    /// About 1s at the 50ms tick, for re-reading the offered key list.
    ///
    /// Unlike the probe above, this one is NOT cheap: it is a full scan of the server
    /// side's Section table, and it ran on every tick for the whole session. A swept
    /// world holds thousands of sections, so that was tens of thousands of row decodes
    /// and a fresh key array every 50ms, on the main thread, to learn about the handful
    /// of rows a sweep writes per second.
    /// </summary>
    const int LocalOfferScanIntervalTicks = 20;

    /// <summary>
    /// Adopt sections the server side swept out of the savegame.
    ///
    /// Identical in shape to PumpServerAssist and deliberately so - same remote-key
    /// bookkeeping, same recolour on install, same nearest-first ordering. Only the
    /// transport differs, and there is no in-flight cap because a local file read has no
    /// round trip to protect. The budget per tick is there so a sweep of ten thousand
    /// sections does not try to install all of them in one frame.
    /// </summary>
    void PumpLocalOffers()
    {
        if (localOffers == null)
        {
            // A null here means "not yet", not "never": the server side can open its
            // cache long after this client finalized - /vhgen creates one on demand,
            // and a sweep's file can appear after a slow start. Opening once at
            // LevelFinalize and never looking again left both invisible for the whole
            // session.
            if (++localOfferProbeTicks < LocalOfferProbeIntervalTicks) return;
            localOfferProbeTicks = 0;
            if (pipeline.DbPath is not string dbPath) return;
            localOffers = LodLocalOfferSource.TryOpen(dbPath, Mod.Logger);
            if (localOffers == null) return;
        }

        // The sweep writes continuously, so re-reading the key list picks up whatever has
        // landed since. AddRemoteKeys ignores anything already known, and anything local
        // disk already holds.
        //
        // A sweep writes a few sections a second, so a second of latency here costs
        // nothing. The install budget below still runs every tick: what is throttled is
        // asking the database what exists, not acting on the answer.
        if (++localOfferScanTicks >= LocalOfferScanIntervalTicks)
        {
            localOfferScanTicks = 0;
            long[] offered = localOffers.Keys();
            if (offered.Length > 0)
            {
                pipeline.AddRemoteKeys(offered);
                if (!loggedLocalOffers)
                {
                    loggedLocalOffers = true;
                    // Sweeps and /vhgen both fill the sibling cache; this line covers either.
                    Mod.Logger.Notification(
                        "Server-side cache offers {0} sections locally. The mod adopts them as "
                        + "the view needs them.", offered.Length);
                }
            }
        }

        long[] wanted = pipeline.RemoteWanted();
        if (wanted.Length == 0) return;

        if (wanted.Length > 1)
        {
            var at = capi.World.Player.Entity.Pos;
            double px = at.X, pz = at.Z;
            Array.Sort(wanted, (a, b) =>
                LodWorld.NearestDistanceSqTo(a, px, pz).CompareTo(LodWorld.NearestDistanceSqTo(b, px, pz)));
        }

        int budget = Math.Min(wanted.Length, LocalOffersPerTick);
        var taken = new long[budget];
        for (int i = 0; i < budget; i++)
        {
            long key = wanted[i];
            taken[i] = key;

            byte[]? blob = localOffers.Blob(key);
            // A miss is ordinary while the sweep is still running: the key was listed but
            // its row is not written yet. MarkRemoteUnavailable is permanent, so it must
            // not be used for "not yet" - leaving the key alone lets a later tick retry.
            if (blob == null || blob.Length == 0) continue;

            if (!pipeline.InstallForeignBlob(key, blob, RecolorForeignSection))
            {
                pipeline.MarkRemoteUnavailable(key);
            }
        }
        pipeline.MarkRemoteRequested(taken);
    }

    /// <summary>
    /// Sections installed from the local sweep per tick. Higher than the network's in-flight
    /// cap because there is no round trip to hide, but still bounded: each install
    /// decompresses a blob and recolours a palette on the main thread.
    /// </summary>
    const int LocalOffersPerTick = 4;

    /// <summary>
    /// Fill in palette colours for a section captured by a server, which had no texture
    /// atlas and stored 0 for every one of them (DESIGN.md Ã‚Â§10.4). Block ids are already
    /// resolved from codes by the deserializer, so this only needs the atlas.
    /// </summary>
    void RecolorForeignSection(LodSection section)
    {
        for (int i = 0; i < section.Palette.Count; i++)
        {
            LodPaletteEntry entry = section.Palette[i];

            // A code the block registry could not answer for. It still carries the flags
            // the capturing side worked out, so it is still drawn as terrain - and a
            // server stores 0 for every colour, so leaving this one alone drew it at
            // exactly RGB 0,0,0. Black ground, correctly shaped, with nothing anywhere
            // saying why. The shader cannot save it either: shade bottoms out at 0.55 and
            // daylight is clamped to 0.02, but zero times anything is still zero.
            //
            // Neutral grey instead, and counted. Wrong-but-plausible stone beats a hole
            // in the world, and the count is what turns the next report into a log line.
            if (entry.BlockId <= 0)
            {
                entry.Color = LodPaletteRepair.UnknownBlockColor;
                section.Palette[i] = entry;
                uncoloredForeignEntries++;
                continue;
            }

            Block block = capi.World.Blocks[entry.BlockId];
            // Server captures have no atlas. Use the same stable topsoil composite as
            // local capture, or remote grass stays brown until vanilla replaces it.
            entry.Color = block.EntityClass == null ? StableColorOf(block) : AtlasColorOf(entry.BlockId);
            section.Palette[i] = entry;
        }
    }

    /// <summary>
    /// Palette colour semantics changed without invalidating the player's captured
    /// terrain. Refresh stable blocks as cached sections load; preserve chisels and
    /// other position-dependent entries whose stored colour is the only exact answer.
    /// </summary>
    int RefreshStoredPalette(LodSection section)
    {
        int refreshed = LodPaletteRepair.RefreshStable(section, blockId =>
        {
            if (blockId <= 0) return null;
            Block block = capi.World.Blocks[blockId];
            return block.EntityClass == null ? StableColorOf(block) : null;
        });

        return refreshed + LodPaletteRepair.Fill(section, AtlasColorOf);
    }

    int HealLegacySection(LodSection section, long sectionKey) =>
        LodSeasonBake.UpgradeLegacyEntries(
            capi, section, sectionKey, tints.PlantTintFallback, UntintedForRebake);

    /// <summary>
    /// Client palette registration for newly captured blocks: stable untinted atlas mean
    /// plus a live tint slot. Login bake handles visited cache separately on join.
    /// A server has no atlas and cannot answer this at all (DESIGN.md §10.4).
    /// </summary>
    (int Color, byte TintSlot, bool Baked) DescribePalette(int blockId, int blockX, int blockY, int blockZ)
    {
        Block block = capi.World.Blocks[blockId];

        int color;
        if (block.EntityClass != null)
        {
            paletteSamplePos.Set(blockX, blockY, blockZ);
            int sampled = block.GetColorWithoutTint(capi, paletteSamplePos);
            color = LodPaletteRepair.Sanitize(sampled, sampled);
            if (!IsUsableAtlasTexture(block.TextureSubIdForBlockColor)
                || (unknownTextureColor != 0 && color == unknownTextureColor)
                || LodPaletteRepair.NeedsColor(color))
            {
                color = sampled;
            }
        }
        else
        {
            color = StableColorOf(block);
        }

        color = LodPaletteRepair.KeepCapturedColor(
            color, terrainFallbackColor, LodBlockPolicy.IsClimateUntinted(block));
        byte slot = LodPaletteRepair.IsRockLikeAlbedo(color) || LodPaletteRepair.IsSnowOrIceAlbedo(color)
            ? (byte)LodTintRegistry.SlotNone
            : (byte)TintSlotOf(block);
        // Newly discovered land keeps the live shader path until the next relog bake.
        return (color, slot, false);
    }

    /// <summary>
    /// One colour per block id so neighbouring sections agree. GetColorWithoutTint on
    /// grass-covered ground is GetRandomColor, so each section used to pick a different
    /// pixel and the near LOD ring tiled against vanilla in the foreground.
    /// </summary>
    int StableColorOf(Block block)
    {
        if (stableColorByBlockId.TryGetValue(block.BlockId, out int cached)) return cached;

        if (TryTopSoilColor(block, out int composite, out _, legacyGreenerBias: true))
        {
            stableColorByBlockId[block.BlockId] = composite;
            return composite;
        }

        colorProbePos.Set(0, capi.World.BlockAccessor.MapSizeY - 1, 0);
        int color = block.GetColorWithoutTint(capi, colorProbePos);

        if (color >= 0)
        {
            long r = 0, g = 0, b = 0;
            for (int i = 0; i < StableColorSamples; i++)
            {
                int sample = block.GetColorWithoutTint(capi, colorProbePos);
                r += sample & 0xFF;
                g += (sample >> 8) & 0xFF;
                b += (sample >> 16) & 0xFF;
            }
            color = unchecked((int)0xFF000000)
                | (int)(b / StableColorSamples) << 16
                | (int)(g / StableColorSamples) << 8
                | (int)(r / StableColorSamples);
        }

        if (!IsUsableAtlasTexture(block.TextureSubIdForBlockColor)
            || (unknownTextureColor != 0 && color == unknownTextureColor)
            || LodPaletteRepair.NeedsColor(color)
            || color < 0)
        {
            bool keepSnow = LodBlockPolicy.IsClimateUntinted(block) && color > 0
                && !LodPaletteRepair.IsMissingTextureSky(color);
            if (!keepSnow)
            {
                color = ColorFromAnyTexture(block, terrainFallbackColor);
                color = LodPaletteRepair.Sanitize(color, terrainFallbackColor);
                LogMissingTextureFallback(block);
            }
        }

        color = LodPaletteRepair.KeepCapturedColor(
            color, terrainFallbackColor, LodBlockPolicy.IsClimateUntinted(block));
        stableColorByBlockId[block.BlockId] = color;
        return color;
    }

    int TintSlotOf(Block block) =>
        LodBlockPolicy.IsClimateUntinted(block)
            ? LodTintRegistry.SlotNone
            : tints.SlotFor(block, TryTopSoilColor(block, out _, out LodUntintedShare share)
                ? share
                : LodUntintedShare.None);

    /// <summary>
    /// Vanilla chunktopsoil composites brown soil with a colour-mapped grass overlay.
    /// LOD has one vertex colour, so store that composite and dilute the live tint by
    /// the dirt share that must stay untinted (winter brown grass must not brown the dirt).
    /// </summary>
    bool TryTopSoilColor(Block block, out int composite, out LodUntintedShare share,
        bool legacyGreenerBias = false)
    {
        composite = 0;
        share = LodUntintedShare.None;

        if (block.RenderPass != EnumChunkRenderPass.TopSoil) return false;
        if (block.Textures == null
            || !block.Textures.TryGetValue("specialSecondTexture", out CompositeTexture? overlay)) return false;

        int overlayId = overlay?.Baked?.TextureSubId ?? -1;
        if (!IsUsableAtlasTexture(overlayId) || !IsUsableAtlasTexture(block.TextureSubIdForBlockColor)) return false;

        TextureMean soil = MeanOf(TextureFor(block, block.TextureSubIdForBlockColor),
            block.TextureSubIdForBlockColor);
        TextureMean grass = MeanOf(overlay, overlayId);
        if (grass.Coverage <= 0f) return false;

        float a = legacyGreenerBias
            ? LodTopSoil.GreenerCoverage(grass.Coverage)
            : grass.Coverage;
        composite = unchecked((int)0xFF000000)
            | Channel(soil.B, grass.B, a) << 16 | Channel(soil.G, grass.G, a) << 8 | Channel(soil.R, grass.R, a);
        share = new LodUntintedShare(
            LodTopSoil.UntintedShare(soil.R, grass.R, a),
            LodTopSoil.UntintedShare(soil.G, grass.G, a),
            LodTopSoil.UntintedShare(soil.B, grass.B, a));
        return true;
    }

    static int Channel(float soil, float grass, float a) =>
        Math.Clamp((int)(LodTopSoil.Composite(soil, grass, a) + 0.5f), 0, 255);

    static CompositeTexture? TextureFor(Block block, int subId)
    {
        if (block.Textures == null) return null;
        foreach (CompositeTexture tex in block.Textures.Values)
        {
            if ((tex?.Baked?.TextureSubId ?? -1) == subId) return tex;
        }
        return null;
    }

    readonly record struct TextureMean(float R, float G, float B, float Coverage);

    readonly Dictionary<AssetLocation, TextureMean> textureMeans = new();

    TextureMean MeanOf(CompositeTexture? tex, int atlasSubId)
    {
        AssetLocation? loc = tex?.Base?.Clone().WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png");
        if (loc != null && textureMeans.TryGetValue(loc, out TextureMean cached)) return cached;

        TextureMean mean = loc != null && TryReadTextureMean(loc, out TextureMean read)
            ? read
            : AtlasMean(atlasSubId);

        if (loc != null) textureMeans[loc] = mean;
        return mean;
    }

    TextureMean AtlasMean(int subId)
    {
        int c = capi.BlockTextureAtlas.GetAverageColor(subId);
        return new TextureMean(c & 0xFF, (c >> 8) & 0xFF, (c >> 16) & 0xFF, ((c >> 24) & 0xFF) / 255f);
    }

    bool TryReadTextureMean(AssetLocation loc, out TextureMean mean)
    {
        mean = default;
        try
        {
            IAsset? asset = capi.Assets.TryGet(loc);
            if (asset?.Data == null) return false;

            using BitmapExternal bmp = capi.Render.BitmapCreateFromPng(asset.Data);
            int[] argb = bmp.Pixels;
            if (argb == null || argb.Length == 0) return false;
            long pixels = argb.Length;

            double r = 0, g = 0, b = 0, alpha = 0;
            foreach (int p in argb)
            {
                double aPixel = ((p >> 24) & 0xFF) / 255.0;
                r += ((p >> 16) & 0xFF) * aPixel;
                g += ((p >> 8) & 0xFF) * aPixel;
                b += (p & 0xFF) * aPixel;
                alpha += aPixel;
            }

            if (alpha <= 0) return false;

            mean = new TextureMean((float)(r / alpha), (float)(g / alpha), (float)(b / alpha),
                (float)(alpha / pixels));
            return true;
        }
        catch (Exception e)
        {
            Mod.Logger.Notification(
                "Could not read texture '{0}' for its true average; using the atlas estimate instead. {1}",
                loc, e.Message);
            return false;
        }
    }

    /// <summary>Average colour of unknown.png (near-white, not magenta - measured).</summary>
    int unknownTextureColor;

    /// <summary>Grass/terrain stand-in when atlas resolve fails (never near-white).</summary>
    int terrainFallbackColor = LodPaletteRepair.TerrainFallbackColor;

    int missingTextureFallbackUses;
    bool loggedMissingTextureFallbackSummary;

    /// <summary>Foreign palette entries drawn as grey because no block matched.</summary>
    int uncoloredForeignEntries;

    /// <summary>
    /// Colour for one block id, for repairing a cache saved without any. Separate from
    /// DescribePalette because that one also answers the tint slot and needs a world
    /// position; here the block is all there is to go on.
    /// </summary>
    int AtlasColorOf(int blockId)
    {
        if (blockId <= 0) return LodPaletteRepair.UnknownBlockColor;

        Block block = capi.World.Blocks[blockId];
        int subId = block.TextureSubIdForBlockColor;
        int color = IsUsableAtlasTexture(subId)
            ? capi.BlockTextureAtlas.GetAverageColor(subId)
            : ColorFromAnyTexture(block, terrainFallbackColor);

        if (LodPaletteRepair.NeedsColor(color)
            || (unknownTextureColor != 0 && color == unknownTextureColor))
        {
            if (!(LodBlockPolicy.IsClimateUntinted(block)
                && color != 0
                && !LodPaletteRepair.IsMissingTextureSky(color)))
            {
                color = ColorFromAnyTexture(block, terrainFallbackColor);
                color = LodPaletteRepair.Sanitize(color, terrainFallbackColor);
                LogMissingTextureFallback(block);
            }
        }
        return LodPaletteRepair.KeepCapturedColor(
            color, terrainFallbackColor, LodBlockPolicy.IsClimateUntinted(block));
    }

    /// <summary>
    /// Whether an atlas sub-id actually names a texture. `GetAverageColor` on an unassigned
    /// or out-of-range sub-id reads whatever the atlas holds there, which is where a
    /// nonsense LOD colour comes from - and unlike the unknown.png case, that does not
    /// require knowing what the placeholder looks like to detect.
    /// </summary>
    bool IsUsableAtlasTexture(int subId)
    {
        if (subId < 0) return false;

        TextureAtlasPosition[] positions = capi.BlockTextureAtlas.Positions;
        return subId < positions.Length && positions[subId] != null;
    }

    readonly Dictionary<int, int> missingTextureColorFallback = new();
    int missingTextureBlocks;
    bool loggedMissingTexture;

    /// <summary>
    /// Salvage a colour for a block whose block-colour texture did not resolve, so it draws
    /// as itself instead of as a placeholder or as whatever the atlas holds at a bogus id.
    ///
    /// Vanilla picks that texture in Block.LoadTextureSubIdForBlockColor: the
    /// 'textureCodeForBlockColor' attribute, else "up", else `Textures.First()` - and that
    /// last step ends in `?? 0`, so a block whose first texture in dictionary order has no
    /// Baked entry silently resolves to atlas subid 0, which is unknown.png. The block's
    /// other faces are baked fine, which is why it looks correct up close and magenta only
    /// in LOD. Measured firing on vanilla 'fruitingbush-wild-blackberry-free', so this is
    /// not a modded-content problem -- content-heavy block packs just hit it more often.
    ///
    /// So: use any of the block's own baked textures instead of the first one. Cached per
    /// block id - the answer cannot change within a session, and a palette entry is
    /// registered once per section, which is thousands of times per world.
    /// </summary>
    int ColorFromAnyTexture(Block block, int fallback)
    {
        // The cache holds only the block's own answer, with 0 for "no usable texture"
        // (no real block averages to 0, the same invariant colour-0 repair rests on).
        // The fallback is the caller's and is applied per call: the capture path passes
        // the probe colour, the foreign path passes grey, and caching whichever caller
        // came first hands one caller's stand-in to the others.
        if (missingTextureColorFallback.TryGetValue(block.BlockId, out int cached))
        {
            return cached != 0 ? cached : fallback;
        }

        int found = 0;
        if (block.Textures != null)
        {
            foreach (CompositeTexture tex in block.Textures.Values)
            {
                int subId = tex?.Baked?.TextureSubId ?? -1;
                if (!IsUsableAtlasTexture(subId)) continue;

                int candidate = capi.BlockTextureAtlas.GetAverageColor(subId);
                if (unknownTextureColor != 0 && candidate == unknownTextureColor) continue;

                found = candidate;
                break;
            }
        }

        if (found != 0 && LodPaletteRepair.NeedsColor(found)
            && !LodBlockPolicy.IsClimateUntinted(block)) found = 0;
        missingTextureColorFallback[block.BlockId] = found;
        missingTextureBlocks++;
        if (!loggedMissingTexture)
        {
            loggedMissingTexture = true;
            Mod.Logger.Notification(
                "Block '{0}' has no usable block-colour texture (vanilla resolved it to unknown.png). "
                + "Using terrain/grass fallback colour instead of near-white missing tex.",
                block.Code);
        }
        int pick = found != 0 ? found : fallback;
        return LodPaletteRepair.KeepCapturedColor(
            pick, fallback, LodBlockPolicy.IsClimateUntinted(block));
    }

    void ResolveTerrainFallbackColor()
    {
        terrainFallbackColor = LodPaletteRepair.TerrainFallbackColor;
        foreach (string code in new[] { "game:soil-low-normal", "game:soil-medium-normal", "game:tallgrass-medium-free" })
        {
            Block? b = capi.World.GetBlock(new AssetLocation(code));
            if (b == null) continue;
            int subId = b.TextureSubIdForBlockColor;
            if (!IsUsableAtlasTexture(subId)) continue;
            int c = capi.BlockTextureAtlas.GetAverageColor(subId);
            if (LodPaletteRepair.NeedsColor(c)) continue;
            terrainFallbackColor = c;
            return;
        }
    }

    void LogMissingTextureFallback(Block block)
    {
        missingTextureFallbackUses++;
        if (loggedMissingTextureFallbackSummary) return;
        if (missingTextureFallbackUses < 8) return;
        loggedMissingTextureFallbackSummary = true;
        Mod.Logger.Notification(
            "LOD colour fallback used {0}+ times (missing/near-white atlas samples). "
            + "Example block '{1}'. Painting terrain fallback {2:X8} instead of white wash.",
            missingTextureFallbackUses, block.Code, terrainFallbackColor);
    }

    /// <summary>
    /// Find a block using the standard plant tint, so plants that declare no colour map
    /// (ferns) can borrow it instead of rendering as their greyscale texture.
    /// </summary>
    void ResolvePlantTintFallback()
    {
        foreach (Block block in capi.World.Blocks)
        {
            if (block?.Code == null) continue;
            if (block.SeasonColorMapResolved != null) continue;
            if (block.ClimateColorMapResolved == null) continue;
            if (block.ClimateColorMap != "climatePlantTint") continue;

            tints.PlantTintFallback = block;
            return;
        }
    }

    readonly System.Diagnostics.Stopwatch joinClock = new();
    static readonly int[] FillInMilestones = { 100, 300, 600, 1200 };
    int nextMilestone;

    void ReportFillIn()
    {
        while (nextMilestone < FillInMilestones.Length && renderer.MeshCount >= FillInMilestones[nextMilestone])
        {
            Mod.Logger.Notification("Fill-in: {0} meshes after {1:0.0}s",
                FillInMilestones[nextMilestone], joinClock.Elapsed.TotalSeconds);
            nextMilestone++;
        }
    }

    bool LoginVisitSweepEnabled() =>
        config.LoginVisitSweepEnabled
        || Environment.GetEnvironmentVariable("VINTAGEHORIZONS_LOGIN_SWEEP") == "1";

    void DeferLoginVisitSweep()
    {
        if (loginSweepDeferred) return;

        loginSweepDeferred = true;
        // Must allow handover immediately or Loading… never reaches RunningGame / char UI
        // (Harmony blocks handover while IsLoginSweepEnabled), and the defer tick never sees
        // dialogs close because the world never leaves the loader.
        LodLoginBakeSweepGate.AllowHandoverWhileCharacterPending(capi);
        Mod.Logger.Notification(
            "[DistantVistas] Login visit sweep deferred — waiting for character creation / class selection.");
        if (loginSweepDeferListenerId == null)
            loginSweepDeferListenerId = capi.Event.RegisterGameTickListener(OnLoginSweepDeferTick, 250);
    }

    void OnLoginSweepDeferTick(float dt)
    {
        if (!loginSweepDeferred) return;

        if (!LoginVisitSweepEnabled())
        {
            CancelLoginSweepDefer();
            renderer.LoginBakeComplete = true;
            LodLoginBakeSweepGate.ClearHandoverDeferral(capi, "disabled", force: true);
            Mod.Logger.Notification(
                "[DistantVistas] Login visit sweep disabled in config — entering play without overlay.");
            return;
        }

        if (LodLoginBakeCharacterWait.IsPending(capi)) return;

        CancelLoginSweepDefer();
        StartLoginVisitSweepIfNeeded();
    }

    void CancelLoginSweepDefer()
    {
        loginSweepDeferred = false;
        if (loginSweepDeferListenerId == null) return;

        capi.Event.UnregisterGameTickListener(loginSweepDeferListenerId.Value);
        loginSweepDeferListenerId = null;
    }

    void StartLoginVisitSweepIfNeeded()
    {
        LodLoginSweepGate.Result sweepGate = LodLoginSweepGate.Decide(
            capi, pipeline.World, pipeline, capi.World.Blocks,
            tints.PlantTintFallback, UntintedForRebake);

        if (!sweepGate.RunSweep)
        {
            renderer.LoginBakeComplete = true;
            LodLoginBakeSweepGate.ClearHandoverDeferral(capi, "skip", force: true);
            Mod.Logger.Notification(
                "[DistantVistas] Login visit sweep skipped — {0}. Entering play ({1} sections in cache).",
                sweepGate.Reason, pipeline.CachedSectionsLoaded);
            return;
        }

        loginBakeOverlay!.Show();
        loginBake = new LodLoginBake(
            capi, pipeline, renderer, loginBakeOverlay!,
            tints.PlantTintFallback, UntintedForRebake);
        loginBakePulse!.Bind(loginBake, PumpLoginBakeWhileSweeping);
        loginBake.Begin();
        capi.Event.RegisterCallback(_ => loginBakePulse.Pulse(0.05f), 0);

        Mod.Logger.Notification(
            "[DistantVistas] Level finalized — login visit sweep starting ({0} sections in cache). Reason: {1}",
            pipeline.CachedSectionsLoaded, sweepGate.Reason);
    }

    void OnLevelFinalize()
    {
        ResolvePlantTintFallback();
        unknownTextureColor = capi.BlockTextureAtlas.UnknownTexturePosition.AvgColor;
        ResolveTerrainFallbackColor();
        Mod.Logger.Notification(
            "Missing-texture colour is {0:X8}{1}; LOD terrain fallback is {2:X8} (never paints near-white missing tex)",
            unknownTextureColor,
            unknownTextureColor == 0 ? " (zero: exact-match salvage disabled)" : "",
            terrainFallbackColor);
        renderer.ApplyZFar();
        pipeline.Open("ModData/distantvistas");
        joinClock.Restart();
        nextMilestone = 0;

        // A singleplayer world whose server side has swept the savegame leaves its results
        // in a sibling cache. Nothing to open on a dedicated server, where the same
        // sections arrive over the network instead.
        if (pipeline.DbPath is string dbPath)
        {
            localOffers = LodLocalOfferSource.TryOpen(dbPath, Mod.Logger);
        }

        // Last, and after the pipeline is live. An exception in a LevelFinalize handler
        // skips everything the handler has left to do, so an optional extra must not sit
        // upstream of the mod's actual job -- it did, and it broke exactly the
        // vanilla-server case it exists to stay out of the way of.
        assist?.Greet();

        try
        {
            if (!LoginVisitSweepEnabled())
            {
                renderer.LoginBakeComplete = true;
                LodLoginBakeSweepGate.ClearHandoverDeferral(capi, "disabled", force: true);
                Mod.Logger.Notification(
                    "[DistantVistas] Login visit sweep disabled in config — entering play without overlay.");
            }
            else if (!LodLoginBakeCharacterWait.IsPending(capi))
            {
                // Prefer Decide/skip (or start) before any character deferral. IsPending is
                // dialog-only, so rejoins with a complete marker reach ClearHandoverDeferral
                // on the skip path instead of waiting forever on Loading….
                StartLoginVisitSweepIfNeeded();
            }
            else
            {
                DeferLoginVisitSweep();
            }
        }
        catch (Exception ex)
        {
            Mod.Logger.Error(
                "[DistantVistas] Login visit sweep setup failed ({0}) — forcing handover release.",
                ex);
            renderer.LoginBakeComplete = true;
            LodLoginBakeSweepGate.ClearHandoverDeferral(capi, "level-finalize-error", force: true);
        }

        capi.Event.RegisterCallback(_ => LogStats("Stats after 30s"), 30000);

        // Continuous telemetry. Was tied to AutoUnpause, which meant an *attended* session
        // - the only kind where someone can say "this looks wrong" - was the one case with
        // no ongoing numbers to explain it. Its own switch now, so watching and driving are
        // independent.
        if (renderer.AutoUnpause || Environment.GetEnvironmentVariable("VINTAGEHORIZONS_STATS") == "1")
        {
            capi.Event.RegisterGameTickListener(_ => LogStats("Stats"), 15000);
        }

        RegisterAutoCommand();

        autoExplore = Environment.GetEnvironmentVariable("VINTAGEHORIZONS_AUTOEXPLORE") == "1";

        // Creative for any unattended run, not only an exploring one.
        //
        // A survival player is moved by things the test did not ask for: knockback,
        // drowning, hunger, a mob, and at worst a death that respawns them somewhere
        // else entirely. Player position is an INPUT to most of what these scenarios
        // measure - which chunks stream in, where /vhgen centres, and which positions
        // the absence verifier excludes as explainable by a nearby player. A scenario
        // whose subject wanders is measuring the wander.
        //
        // Sent before the auto-command below fires, so a /vhgen run is already centred
        // on a player who will stay put.
        if (autoExplore || Environment.GetEnvironmentVariable("VINTAGEHORIZONS_CREATIVE") == "1")
        {
            capi.Event.RegisterCallback(_ =>
            {
                Mod.Logger.Notification("Test run: entering creative so the player cannot be moved");
                capi.SendChatMessage("/gamemode creative");
            }, 10000);
        }

        if (autoExplore)
        {
            exploreX = capi.World.Player.Entity.Pos.X;
            exploreZ = capi.World.Player.Entity.Pos.Z;
            capi.Event.RegisterGameTickListener(_ => ExploreHop(), 60000);
            Mod.Logger.Notification("Auto-explore active (spiral teleports every 60s)");
        }
    }

    static readonly int ExploreHopBlocks =
        int.TryParse(Environment.GetEnvironmentVariable("VINTAGEHORIZONS_EXPLORE_HOP"), out int h) && h > 0 ? h : 350;

    void ExploreHop()
    {
        int hop = ExploreHopBlocks;

        exploreX += exploreDirX * hop;
        exploreZ += exploreDirZ * hop;

        // Square spiral: legs lengthen every second turn.
        if (++exploreStep >= exploreLeg / 2 + 1)
        {
            exploreStep = 0;
            exploreLeg++;
            (exploreDirX, exploreDirZ) = (-exploreDirZ, exploreDirX);
        }

        int y = capi.World.SeaLevel + 140;
        capi.SendChatMessage($"/tp ={(int)exploreX} {y} ={(int)exploreZ}");
    }

    bool loggedFirstCaptureError, loggedFirstMeshError, loggedFirstSaveError;
    int gen0AtLastReport, gen1AtLastReport, gen2AtLastReport;

    void LogStats(string prefix)
    {
        LodWorld world = pipeline.World;
        LodWorker worker = pipeline.Worker;
        LodStorageThread? storageThread = pipeline.StorageThread;

        if (!loggedFirstCaptureError && worker.FirstCaptureError != null)
        {
            loggedFirstCaptureError = true;
            Mod.Logger.Warning("First capture error was: {0}", worker.FirstCaptureError);
        }
        if (!loggedFirstMeshError && worker.FirstMeshError != null)
        {
            loggedFirstMeshError = true;
            Mod.Logger.Warning("First mesh error was: {0}", worker.FirstMeshError);
        }

        Mod.Logger.Notification(
            "{0}: {1} sections resident [{2}] ({3} RAM-evicted, {4} from cache), {5} meshes ({6} evicted), " +
            "{7} selected [{8}] minus {9} frustum-culled, {25} occCull, {19} gap fills, {20} unfilled gaps, {10} columns captured, {11} pending, " +
            "{21} dropped, {22} swept, {23} peek-confirmed, {24} provisional L0, " +
            "worker: {12} captures / {13} meshes queued / {14}+{15} errors, {16} awaiting mip, {17} render-dirty, {18} unsaved",
            prefix, world.Sections.Count, world.DescribeLevels(), world.EvictedSectionsTotal, pipeline.CachedSectionsLoaded,
            renderer.MeshCount, renderer.EvictedTotal, renderer.LastDrawCount, renderer.DescribeDrawnLevels(),
            renderer.LastCulledCount, pipeline.ColumnsCaptured, pipeline.PendingColumns, worker.PendingCaptures, worker.PendingMeshes,
            worker.CaptureErrors, worker.MeshErrors, world.MipDirty.Count, world.RenderDirty.Count,
            world.SaveDirty.Count, renderer.LastGapDrawCount, renderer.LastUnfilledGaps,
            pipeline.ColumnsDropped, pipeline.ColumnsSwept, pipeline.ProvisionalQuadrantsConfirmed,
            world.ProvisionalL0Keys.Count, renderer.LastOccludedCount);

        if (renderer.DrawAfterCompanion)
        {
            Mod.Logger.Notification(
                "  farseer yield: meshes {0}/{1}{2}, handed off {3} ({4} far walked)",
                renderer.MeshCount, LodMemoryBudget.MaxResidentMeshes,
                renderer.MeshPressureActive ? ", mesh pressure on" : (renderer.PressureYieldActive ? ", pressure yield on" : ""),
                renderer.LastCompanionYieldCount, renderer.LastPressureYieldCount);
        }

        Mod.Logger.Notification("  L0 parity/fill: {0}", renderer.DescribeL0ParityAndFill());

        Mod.Logger.Notification(
            "  storage on main thread since last report: snapshot {0} calls, {1:0.00}ms avg, {2:0.00}ms max | " +
            "inline loads {3} calls, {4:0.00}ms avg, {5:0.00}ms max | storage thread: {6} write backlog, " +
            "{7} written, {8} write errors, {11} read, {9} async loads in flight, {10} read errors",
            pipeline.SaveCalls, pipeline.SaveCalls > 0 ? pipeline.SaveMsTotal / pipeline.SaveCalls : 0, pipeline.SaveMsMax,
            pipeline.LoadCalls, pipeline.LoadCalls > 0 ? pipeline.LoadMsTotal / pipeline.LoadCalls : 0, pipeline.LoadMsMax,
            storageThread?.Backlog ?? 0, storageThread?.SectionsWritten ?? 0, storageThread?.SaveErrors ?? 0,
            world.LoadsInFlight.Count, storageThread?.LoadErrors ?? 0, storageThread?.SectionsRead ?? 0);

        if (assist != null && assist.RemoteKeys.Count > 0)
        {
            Mod.Logger.Notification(
                "  server assist: {0} offered, {1} remote-only, {2} wanted by view, {3} requested, " +
                "{4} received, {5} installed, {6} in flight, {7} declined",
                assist.RemoteKeys.Count, pipeline.RemoteOnly.Count, pipeline.RemoteWanted().Length,
                assist.SectionsRequested, assist.SectionsReceived, pipeline.ForeignSectionsInstalled,
                assist.InFlight, assist.SectionsRefused);
        }

        // Repairing means the cache on disk was written without colours, which drew as
        // black ground. Worth saying out loud, and worth being able to watch go to zero.
        if (pipeline.PaletteEntriesRepaired > 0)
        {
            Mod.Logger.Notification(
                "  repaired {0} palette entries that were cached with no colour at all. "
                + "They drew as black terrain, and the repair is written back as they load.",
                pipeline.PaletteEntriesRepaired);
        }

        // Warning, not notification: this is terrain drawn as a guess. Naming the codes is
        // the whole point - the previous symptom was black ground and nothing to grep for.
        if (uncoloredForeignEntries > 0)
        {
            string[] codes = pipeline.UnresolvedBlockCodes();
            Mod.Logger.Warning(
                "  {0} palette entries came from blocks this game does not have, and are drawn as "
                + "plain grey. {1}",
                uncoloredForeignEntries,
                codes.Length > 0
                    ? "Codes: " + string.Join(", ", codes.Take(12))
                      + (codes.Length > 12 ? $" and {codes.Length - 12} more" : "")
                    : "No unresolved codes were recorded, so these arrived over the network rather "
                      + "than from the cache.");
        }

        if (renderer != null && renderer.WalkCost.Calls > 0)
        {
            // Microseconds, because that is the scale these are at, and because rounding
            // them to milliseconds would print four zeroes and teach nobody anything.
            // A frame-rate comparison cannot see any of these: the benchmark's own
            // run-to-run spread is wider than all four added together.
            Mod.Logger.Notification(
                "  render thread per frame over {0} frames: prune {9:0.0}us avg / {10:0.0}us max | " +
                "schedule {1:0.0}/{2:0.0} | " +
                "far distance {3:0.0}/{4:0.0} | quadtree walk {5:0.0}/{6:0.0} | draw submit {7:0.0}/{8:0.0}",
                renderer.WalkCost.Calls,
                renderer.ScheduleCost.AvgUs, renderer.ScheduleCost.MaxUs,
                renderer.FarDistanceCost.AvgUs, renderer.FarDistanceCost.MaxUs,
                renderer.WalkCost.AvgUs, renderer.WalkCost.MaxUs,
                renderer.DrawCost.AvgUs, renderer.DrawCost.MaxUs,
                renderer.PruneCost.AvgUs, renderer.PruneCost.MaxUs);

            // Collections since the last report, beside the phase maxima, because the
            // two are related and the relationship is easy to get backwards. A phase
            // maximum is not a measurement of that phase: the far-distance scan averages
            // 2us over a few hundred meshes and has reported a 1624us maximum, which
            // nothing inside a loop that multiplies and compares can account for. What a
            // maximum records is whatever interrupted the frame, charged to whichever
            // phase was running. These counters say how much of that was collection.
            Mod.Logger.Notification(
                "  gc since last report: {0} gen0, {1} gen1, {2} gen2, {3} MB managed",
                GC.CollectionCount(0) - gen0AtLastReport,
                GC.CollectionCount(1) - gen1AtLastReport,
                GC.CollectionCount(2) - gen2AtLastReport,
                GC.GetTotalMemory(false) / (1024 * 1024));

            gen0AtLastReport = GC.CollectionCount(0);
            gen1AtLastReport = GC.CollectionCount(1);
            gen2AtLastReport = GC.CollectionCount(2);
        }

        if (storageThread?.FirstSaveError != null && !loggedFirstSaveError)
        {
            loggedFirstSaveError = true;
            Mod.Logger.Warning("First storage-write error was: {0}", storageThread.FirstSaveError);
        }
        pipeline.ResetStorageStats();
        renderer?.ResetPhaseCosts();
    }

    (int Color, LodUntintedShare Share) UntintedForRebake(Block block)
    {
        if (TryTopSoilColor(block, out int composite, out LodUntintedShare share))
            return (composite, share);
        return (StableColorOf(block), LodUntintedShare.None);
    }

    void OnLeaveWorld()
    {
        CancelLoginSweepDefer();
        loginBake?.Dispose();
        loginBake = null;
        loginBakePulse?.Bind(null, PumpLoginBakeWhileSweeping);
        if (LodLoginBakeSweepGate.HandoverDeferred)
            LodLoginBakeSweepGate.ClearHandoverDeferral(capi, "leave", force: true);
        else
            LodLoginBakeSweepGate.Release();
        renderer.LoginBakeComplete = false;
        renderer.LoginBakeOverlayActive = false;
        assist?.Reset();
        // Belongs to the world being left: the next one is a different savegame with a
        // different sibling cache, and holding this open would keep a file handle on a
        // database the server side may want to delete or replace.
        localOffers?.Dispose();
        localOffers = null;
        loggedLocalOffers = false;
        pipeline.Close();
        while (pipeline.Worker.MeshResults.TryDequeue(out _)) { }
        renderer.ClearMeshes();
    }

    void RegisterCommands()
    {
        capi.ChatCommands.Create("dvistas")
            .WithDescription("DistantVistas status")
            .HandleWith(_ => deferringTo != null
                ? TextCommandResult.Success(
                    $"[distantvistas] idle: '{deferringTo}' is installed and switched on, so it is "
                    + "drawing the distant terrain. Switch it off in its own settings, or run "
                    + "'.dvdefer off' to draw beside it. Either way, restart the game afterwards: "
                    + "this is decided once at startup.")
                : TextCommandResult.Success(
                $"[distantvistas] sections: {pipeline.World.Sections.Count} [{pipeline.World.DescribeLevels()}] " +
                $"({pipeline.CachedSectionsLoaded} from cache), meshes: {renderer.MeshCount}, " +
                $"drawn: {renderer.LastDrawCount} [{renderer.DescribeDrawnLevels()}], " +
                $"occCull: {renderer.LastOccludedCount}, " +
                $"gap fills: {renderer.LastGapDrawCount}, unfilled gaps: {renderer.LastUnfilledGaps}, " +
                $"columns captured: {pipeline.ColumnsCaptured}, pending: {pipeline.PendingColumns}, " +
                $"dropped: {pipeline.ColumnsDropped}, swept: {pipeline.ColumnsSwept}, " +
                $"peek-confirmed: {pipeline.ProvisionalQuadrantsConfirmed}, " +
                $"provisional L0: {pipeline.World.ProvisionalL0Keys.Count}, " +
                $"worker: {pipeline.Worker.PendingCaptures}c/{pipeline.Worker.PendingMeshes}m, awaiting mip: {pipeline.World.MipDirty.Count}, " +
                $"unsaved: {pipeline.World.SaveDirty.Count}, persistence: {(pipeline.Persisting ? "on" : "off")}, " +
                $"render distance: {(renderer.FarViewDistanceCap > 0 ? renderer.FarViewDistanceCap + " (capped)" : "unlimited")}, " +
                $"current far edge: {(int)renderer.EffectiveFarDistance}, " +
                $"detail distance: {(int)LodWorld.DetailDistance} (.dvdetail to change), " +
                $"coarsest visible: L{LodWorld.MaxVisualLevel} ({LodWorld.ColumnStepBlocks(LodWorld.MaxVisualLevel)} blocks/column), " +
                $"farseer: meshes {renderer.MeshCount}/{LodMemoryBudget.MaxResidentMeshes}" +
                (renderer.DrawAfterCompanion
                    ? (renderer.MeshPressureActive ? ", mesh pressure on" : ", mesh pressure off")
                      + $", handed off {renderer.LastCompanionYieldCount} ({renderer.LastPressureYieldCount} far walked)"
                    : ", off") + ", " +
                $"server assist: {assist?.Status ?? "off"}" +
                (assist != null && assist.RemoteKeys.Count > 0
                    ? $", server offers {assist.RemoteKeys.Count} sections " +
                      $"({pipeline.RemoteOnly.Count} not held locally, {pipeline.ForeignSectionsInstalled} fetched, " +
                      $"{assist.InFlight} in flight, {assist.SectionsRefused} declined)" +
                      (assist.ManifestComplete ? "" : " (manifest still arriving)")
                    : "")));

        // Registered in both states on purpose: the player who most needs this one is the
        // player we are currently idle for.
        capi.ChatCommands.Create("dvfarseer")
            .WithDescription("Show whether Distant Vistas overwrote Farseer's region shaders")
            .HandleWith(_ => TextCommandResult.Success(
                "[distantvistas] " + FarseerShaderOverlay.Describe(capi)));

        capi.ChatCommands.Create("dvdefer")
            .WithDescription("Stay idle when another LOD mod draws. Default on. Off draws anyway.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (args.Parsers[0].IsMissing)
                {
                    return TextCommandResult.Success(
                        $"[distantvistas] defer to other LOD mods: {(config.IgnoreOtherLodMods ? "off" : "on")}"
                        + (deferringTo != null ? $" - idle now, because '{deferringTo}' is drawing" : ""));
                }

                config.IgnoreOtherLodMods = !(bool)args[0];
                SaveConfig();

                // Saved, not applied. Starting the mod mid-session would have to register a
                // network channel after the handshake, run a LevelFinalize that has already
                // fired, and capture chunks whose ChunkDirty events are long past.
                return TextCommandResult.Success(config.IgnoreOtherLodMods
                    ? "[distantvistas] will draw even when another LOD mod is switched on (saved). "
                      + "Restart the game to apply. Switch the other mod off as well, or the two "
                      + "draw over the same ground."
                    : "[distantvistas] will stay idle when another LOD mod is drawing (saved). "
                      + "Restart the game to apply.");
            });

        // The remaining commands drive the renderer, which does not exist when we are
        // deferring to another LOD mod.
        if (deferringTo != null) return;

        capi.ChatCommands.Create("dvwhy")
            .WithDescription("Explain why nearby LOD terrain draws coarser than the detail setting allows")
            .HandleWith(_ =>
            {
                var at = capi.World.Player.Entity.Pos;
                return TextCommandResult.Success(
                    "[distantvistas] coarse draws:" + renderer.ExplainCoarseDraws(at.X, at.Z));
            });

        capi.ChatCommands.Create("dvfar")
            .WithDescription("Cap DistantVistas render distance in blocks (0 = unlimited)")
            .WithArgs(capi.ChatCommands.Parsers.Int("blocks"))
            .HandleWith(args =>
            {
                int blocks = (int)args[0];
                renderer.FarViewDistanceCap = blocks <= 0 ? 0 : GameMath.Clamp(blocks, 1024, 262144);
                SaveConfig();
                return TextCommandResult.Success(renderer.FarViewDistanceCap > 0
                    ? $"[distantvistas] render distance capped at {renderer.FarViewDistanceCap} (saved)"
                    : "[distantvistas] render distance unlimited (saved)");
            });

        capi.ChatCommands.Create("dvdetail")
            .WithDescription("Distance in blocks before LOD detail starts to halve. Default 512. A higher value gives sharper far terrain and costs more VRAM and CPU.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalInt("blocks"))
            .HandleWith(args =>
            {
                if (args.Parsers[0].IsMissing)
                {
                    return TextCommandResult.Success(
                        $"[distantvistas] detail distance {(int)LodWorld.DetailDistance} " +
                        $"(full 1-block detail out to {(int)LodWorld.DetailDistance * 2} blocks). " +
                        $"Set between {(int)LodWorld.MinDetailDistance} and {(int)LodWorld.MaxDetailDistance}.");
                }

                LodWorld.DetailDistance = GameMath.Clamp((int)args[0],
                    (int)LodWorld.MinDetailDistance, (int)LodWorld.MaxDetailDistance);
                SaveConfig();
                return TextCommandResult.Success(
                    $"[distantvistas] detail distance {(int)LodWorld.DetailDistance} - full detail out to " +
                    $"{(int)LodWorld.DetailDistance * 2} blocks (saved). Terrain re-selects over the next few seconds.");
            });
    }

    /// <summary>Writes every setting: a partial write would silently reset the others.</summary>
    void SaveConfig()
    {
        // The renderer and the LodWorld statics do not exist while we defer, and what was
        // loaded from the file is still the right thing to write back.
        if (deferringTo == null)
        {
            config.FarViewDistanceCap = renderer.FarViewDistanceCap;
            config.DetailDistance = (int)LodWorld.DetailDistance;
        config.FidelityStep = (float)LodWorld.FidelityStep;
            config.OverdrawStart = renderer.OverdrawStart;
        }

        capi.StoreModConfig(config, "distantvistas.json");
    }

    public override void Dispose()
    {
        if (capi == null) return;

        // The engine runs this on whichever thread is tearing the game down, and on the
        // vanilla shutdown crash path ("Can't use a disposed shader" out of a render
        // stage) that is not the main thread. Every engine call below refuses to run off
        // it, and an exception that escapes one step skips every step behind it. Two
        // rounds of this were needed to learn the general rule: the first version lost
        // the storage writer's shutdown, and the second, which guarded only the events,
        // lost the renderer's. So each step stands alone and none of them propagates.
        Quietly(() =>
        {
            capi.Event.ChunkDirty -= OnChunkDirty;
            capi.Event.LevelFinalize -= OnLevelFinalize;
            capi.Event.LevelFinalize -= RegisterAutoCommand;
            capi.Event.LeaveWorld -= OnLeaveWorld;

            // Nothing to unregister while deferring: that path registers no listener.
            if (deferringTo == null) capi.Event.UnregisterGameTickListener(tickListenerId);
            if (loginSweepDeferListenerId != null)
                capi.Event.UnregisterGameTickListener(loginSweepDeferListenerId.Value);
        });

        // Stops the storage writer before the connection it writes through.
        Quietly(() => pipeline?.Dispose());
        Quietly(() => renderer?.Dispose());
        Quietly(() => loginBakeOverlay?.Dispose());
        Quietly(() => loginVanillaLoading?.Dispose());
    }

    /// <summary>
    /// One shutdown step, and whatever it throws stays here. Only ever called from
    /// <see cref="Dispose"/>, where the game is already going down and the alternative is
    /// abandoning the steps behind this one.
    /// </summary>
    void Quietly(Action step)
    {
        try
        {
            step();
        }
        catch (Exception e)
        {
            Mod.Logger.Debug("A shutdown step was refused, which changes nothing by now: {0}",
                e.Message);
        }
    }
}








