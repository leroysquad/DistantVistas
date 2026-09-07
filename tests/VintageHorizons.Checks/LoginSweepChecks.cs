using DistantVistas;
using Vintagestory.API.Common;

namespace DistantVistas.Checks;

public static class LoginSweepChecks
{
    public static void Run(Check c)
    {
        L0ChunkColumns(c);
        VisitedL0Only(c);
        BootstrapRevisitPlan(c);
        BackdropHook(c);
        AudioMuteKeys(c);
        TimeFreezeKey(c);
        TeardownHook(c);
        AuditMisses(c);
        VisitPriority(c);
        OverlayGuard(c);
        QuietTeleports(c);
        SeasonSampleExport(c);
        SweepResume(c);
        SweepSkipGate(c);
        SweepTiming(c);
        VisitOnsetEnvelope(c);
        CreativeMode(c);
        HudHide(c);
        CharacterWait(c);
    }

    static void L0ChunkColumns(Check c)
    {
        long key = LodWorld.SectionKey(0, 3, 5);
        var cols = LodLoginSweep.ChunkColumnsForL0(key).ToArray();
        c.Eq(4, cols.Length, "L0 section covers four chunk columns");
        c.Eq(6, cols[0].Cx, "sx=3 starts at chunk cx 6");
        c.Eq(10, cols[0].Cz, "sz=5 starts at chunk cz 10");
    }

    static void VisitedL0Only(Check c)
    {
        var world = new LodWorld();
        world.InstallStoredKey(0, 1, 2, applyToParent: true, provisional: false);
        world.InstallStoredKey(1, 0, 0, applyToParent: true, provisional: false);
        world.InstallStoredKey(0, 9, 9, applyToParent: true, provisional: false);
        var keys = LodLoginSweep.VisitedL0Keys(world).ToArray();
        c.Eq(2, keys.Length, "only level-0 keys are swept");
        c.True(keys.All(k => LodWorld.KeyLevel(k) == 0), "every sweep key is L0");
    }

    static void BootstrapRevisitPlan(Check c)
    {
        LodLoginSweepTiming.SetMachineSecPerStop(LodLoginSweepTiming.InitialSecPerStop);
        var world = new LodWorld();
        world.InstallStoredKey(0, 4, 7, applyToParent: true, provisional: false);
        world.InstallStoredKey(0, 8, 1, applyToParent: true, provisional: false);

        var visited = LodLoginSweep.VisitedL0Keys(world).ToList();
        visited.Sort();
        var plan = new LodLoginSweepPlan(
            LodLoginSweepPlanMode.RevisitVisited, visited, "Refreshing visited land (season)");

        c.Eq(LodLoginSweepPlanMode.RevisitVisited, plan.Mode, "visited canvas uses revisit mode");
        c.True(plan.ModeLabel.Contains("Refreshing visited land"), "revisit mode label");
        c.Eq(2, plan.Keys.Count, "small revisit plan includes every visited L0 key");

        long bad = LodWorld.SectionKey(0, 2, 3);
        var misses = new List<LodLoginBakeAudit.Miss>
        {
            new(bad, LodLoginBakeAudit.MissReason.EmptyCapture),
            new(bad, LodLoginBakeAudit.MissReason.BakeIncomplete),
        };
        var targeted = LodLoginSweepBootstrap.PlanIncomplete(misses);
        c.Eq(LodLoginSweepPlanMode.RevisitIncomplete, targeted.Mode, "incomplete plan mode");
        c.Eq(1, targeted.Keys.Count, "incomplete plan dedupes keys");
        c.True(targeted.ModeLabel.Contains("incomplete"), "incomplete plan label");

        var manyMisses = new List<LodLoginBakeAudit.Miss>();
        for (int i = 0; i < 300; i++)
            manyMisses.Add(new(LodWorld.SectionKey(0, i, 0), LodLoginBakeAudit.MissReason.BakeIncomplete));
        var budgeted = LodLoginSweepBootstrap.PlanIncomplete(manyMisses);
        c.Eq(LodLoginSweepBootstrap.RevisitMaxVisitStops, budgeted.Keys.Count,
            "incomplete plan stays inside the revisit stop budget");
        c.True(budgeted.ModeLabel.Contains("of 300"), "incomplete plan names the leftover gaps");

        c.Eq(180, LodLoginSweepBootstrap.RevisitMaxVisitStops,
            "revisit cap targets ~180s at fallback 1s/stop");
        c.Eq(180, LodLoginSweepBootstrap.BootstrapMaxVisitStops,
            "bootstrap land cap matches revisit (~180s at fallback 1s/stop)");
        c.Eq(48, LodLoginSweepBootstrap.RetryMaxVisitStops,
            "retry hop matches MaxRetryStops at fallback 1s/stop");
        c.True(LodLoginSweepBootstrap.RevisitMaxVisitStops >= LodLoginSweepBootstrap.BootstrapMaxVisitStops,
            "revisit budget is at least bootstrap budget");
        c.True(LodLoginSweepBootstrap.RetryMaxVisitStops <= LodLoginSweepBootstrap.RevisitMaxVisitStops,
            "retry hop is shorter than the first pass");
    }

    static void BackdropHook(Check c)
    {
        string renderDir = Path.Combine(GameAssemblies.RepoRoot, "DistantVistas", "src", "Render");
        string[] gone =
        {
            "LodLoginBakeHarmony.cs",
            "LodLoginBakeVanillaLoadingHold.cs",
            "LodLoginBakeSweepGate.cs",
            "LodLoginBakeScreenRenderer.cs",
            "LodLoginBakeSplashOverlay.cs",
            "LodLoginBakeHudHide.cs",
            "LodLoginBakeWorldHide.cs",
        };
        foreach (string file in gone)
        {
            c.False(File.Exists(Path.Combine(renderDir, file)),
                $"{file} is removed — present-path splash must not ship");
        }

        string overlay = File.ReadAllText(Path.Combine(renderDir, "LodLoginBakeOverlay.cs"));
        c.True(overlay.Contains("LodLoginBakeInputGuard"),
            "login overlay coordinates the HUD input guard");
        c.True(!overlay.Contains("IRenderer"),
            "login overlay coordinator is not a present-path IRenderer");
        c.True(!overlay.Contains("OrthoMode"),
            "login overlay coordinator never calls OrthoMode");

        string guard = File.ReadAllText(Path.Combine(renderDir, "LodLoginBakeInputGuard.cs"));
        c.True(guard.Contains(": HudElement"),
            "overlay is a HudElement, not a present-path renderer");
        c.True(guard.Contains("EnumDialogType.HUD"),
            "overlay stays HUD so CloseBlockingDialogs and pause do not stall the sweep");
        c.True(guard.Contains("AddStatbar"),
            "overlay composes a progress bar");
        c.True(guard.Contains("CaptureAllInputs"),
            "overlay captures input during the sweep");
        c.True(guard.Contains("OnEscapePressed"),
            "overlay Escape maps to cancel");
        c.True(!guard.Contains("OrthoMode(") && !guard.Contains("OrthoMode "),
            "HUD overlay never calls OrthoMode");
        c.True(!guard.Contains("ClearFrameBuffer"),
            "HUD overlay never clears the framebuffer");
        c.True(!guard.Contains("RenderToDefaultFramebuffer"),
            "HUD overlay never paints on framebuffer present");
        c.True(!guard.Contains("Render2DTexture"),
            "HUD overlay does not use the crashy Render2DTexture splash path");
        c.True(guard.Contains("AddStaticCustomDraw"),
            "HUD overlay paints splash art through Cairo custom draw");
        c.True(guard.Contains("CoverFit"),
            "HUD overlay cover-fits login-backdrop.png");
        c.True(guard.Contains("login-backdrop"),
            "HUD overlay binds the login backdrop asset");

        string mod = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "DistantVistasModSystem.cs"));
        c.True(!mod.Contains("distantvistas-login-vanilla"),
            "mod does not register splash IRenderers on Ortho/AfterFinal/Done");
        c.True(!mod.Contains("LodLoginBakeHarmony"),
            "mod does not apply Harmony loginbake patches");
        c.True(!mod.Contains("handOverRenderingToRunningGame"),
            "mod does not defer running-game handover");

        string csproj = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "DistantVistas.csproj"));
        c.True(csproj.Contains("0Harmony"),
            "mod still references Harmony for Farseer visit-onset and cloud horizon");
    }

    static void AudioMuteKeys(Check c)
    {
        c.Eq(6, LodLoginBakeAudioMute.VolumeKeys.Length, "all client volume sliders are muted");
        c.True(LodLoginBakeAudioMute.VolumeKeys.Contains("masterSoundLevel"), "master volume key");
        c.True(LodLoginBakeAudioMute.VolumeKeys.Contains("musicLevel"), "music volume key");
    }

    static void TimeFreezeKey(Check c)
    {
        c.Eq("distantvistas-loginbake", LodLoginBakeTimeFreeze.SpeedModifierKey,
            "login sweep calendar speed modifier key");

        string timeFreeze = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakeTimeFreeze.cs"));
        c.True(timeFreeze.Contains("anchoredTotalHours"),
            "time freeze anchors TotalHours during sweep");
        c.True(timeFreeze.Contains("CalendarSpeedMul = 0f"),
            "time freeze zeroes calendar speed multiplier");
        c.True(timeFreeze.Contains("RemoveTimeSpeedModifier(SpeedModifierKey)"),
            "time freeze cancels SpeedOfTime modifier each tick");
        c.True(timeFreeze.Contains("time restored"),
            "time freeze logs when time is restored");
        c.True(timeFreeze.Contains("TryRemoveSpeedModifier"),
            "time restore removes modifier independently");

        string bake = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBake.cs"));
        c.True(bake.Contains("finally") && bake.Contains("timeFreeze.Restore()"),
            "login bake always restores time in ReleaseResources finally");
        c.True(bake.Contains("CaptureRestorePose"),
            "login bake captures pre-sweep pose at arm");
        c.True(bake.Contains("RestorePlayerPose(); } catch { }") || bake.Contains("RestorePlayerPose()"),
            "login bake restores player pose on release");
        c.True(bake.Contains("LockPlayerCamera(capi, player, restorePos, restoreCameraPos)"),
            "restore applies saved yaw/pitch and camera");
    }

    static void TeardownHook(Check c)
    {
        string bake = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBake.cs"));
        c.True(bake.Contains("ReleaseResources"),
            "login bake uses unified ReleaseResources teardown");
        c.True(!bake.Contains("CompleteHandoverAndRelease"),
            "login bake does not complete deferred handover on teardown");
        c.True(bake.Contains("LodLoginBakeProgressUi"),
            "login bake throttles loading-text updates");
        c.True(bake.Contains("if (released) return"),
            "login bake teardown is idempotent");
        c.True(bake.Contains("Teardown(success: false, keepResume: true)"),
            "dispose routes through Teardown");
        c.True(bake.Contains("LOGIN VISIT SWEEP ARMED"),
            "login bake logs loudly when sweep arms");
        c.True(bake.Contains("quiet teleports begin"),
            "login bake logs when teleports begin");
        c.True(!bake.Contains("never painted opaque frames — entering play"),
            "login bake does not abort solely on overlay paint counter");
        c.True(bake.Contains("restoreCameraPos"),
            "login bake pins camera while entity teleports for chunk load");
        c.True(bake.Contains("RestoreCameraX"),
            "resume snapshot persists camera for paused sweeps");
        c.True(bake.Contains("CameraPos.Set"),
            "login bake writes frozen camera position each tick");

        string season = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodSeasonBake.cs"));
        c.True(season.Contains("BakeSectionFromVisit"),
            "visit sweep uses exact GetColor bake path");
        c.True(season.Contains("for (int col = 0; col < cols; col++)"),
            "visit bake walks every captured column, not one colour per block id");
        c.True(season.Contains("TrySetTopRunPaletteId"),
            "visit bake splits palette rows per column when colours differ");
        c.True(season.Contains("block.GetColor(capi, pos)"),
            "visit bake samples vanilla GetColor at column top");
        c.True(season.Contains("FinishColumnPaint"),
            "visit bake uses the shared season-ground mix for overlay and walk");
        c.True(bake.Contains("BakeSectionFromVisit"),
            "login bake calls visit-only exact bake");
        c.True(bake.Contains("DeferLegacyHeal = true"),
            "legacy heal is deferred during visit sweep");

        string pipeline = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Lod", "LodPipeline.cs"));
        c.True(pipeline.Contains("DeferLegacyHeal"),
            "pipeline can defer approximate legacy heal");
        c.True(pipeline.Contains("DiscoverOnly"),
            "pipeline can restrict post-sweep capture to new and nearby land");
        c.True(bake.Contains("DiscoverOnly = true"),
            "successful sweep enables discover-only capture, not a total freeze");
        c.True(bake.Contains("DeferLegacyHeal = false"),
            "successful sweep re-enables explore bake for newly discovered land");
        c.False(bake.Contains("FreezeCapture = true"),
            "successful sweep must not lock all capture until relog");
    }

    static void AuditMisses(Check c)
    {
        string bake = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBake.cs"));
        c.True(bake.Contains("Phase.Auditing"), "login bake has an audit phase before release");
        c.True(bake.Contains("Retrying missed regions"),
            "login bake UI names the miss-resweep pass");
        c.Eq(1, LodLoginBakeAudit.MaxResweepRounds,
            "miss-resweep capped to one retry pass after main sweep");
        c.True(bake.Contains("BeginDraining"),
            "login bake drains into play when resweep limit hit");

        var world = new LodWorld();
        long key = LodWorld.SectionKey(0, 1, 2);
        world.InstallStoredKey(0, 1, 2, applyToParent: false, provisional: false);
        world.Sections[key] = new LodSection();
        long failed = LodWorld.SectionKey(0, 9, 9);
        world.LoadFailed.Add(failed);

        IList<Block> blocks = Array.Empty<Block>();
        System.Func<Block, (int Color, LodUntintedShare Share)> untinted =
            _ => (0, LodUntintedShare.None);

        c.Eq(LodLoginBakeAudit.MissReason.LoadFailed,
            LodLoginBakeAudit.Classify(world, null!, failed, blocks, null, untinted),
            "load-failed keys are misses");
        c.Eq(LodLoginBakeAudit.MissReason.EmptyCapture,
            LodLoginBakeAudit.Classify(world, null!, key, blocks, null, untinted),
            "zero captured columns is a miss");

        long thinKey = LodWorld.SectionKey(0, 3, 4);
        world.InstallStoredKey(0, 3, 4, applyToParent: false, provisional: false);
        var thin = new LodSection();
        thin.CapturedColumns = LodSection.GridSize * LodSection.GridSize / 2;
        world.Sections[thinKey] = thin;
        c.Eq(LodLoginBakeAudit.MissReason.ThinCapture,
            LodLoginBakeAudit.Classify(world, null!, thinKey, blocks, null, untinted),
            "partial capture is a miss");

        long missing = LodWorld.SectionKey(0, 5, 6);
        c.Eq(LodLoginBakeAudit.MissReason.MissingSection,
            LodLoginBakeAudit.Classify(world, null!, missing, blocks, null, untinted),
            "missing section is a miss");
        c.False(LodLoginBakeAudit.IsVisitComplete(world, null!, missing, blocks, null, untinted),
            "missing section needs visit");
    }

    static void VisitPriority(Check c)
    {
        string bootstrap = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginSweepBootstrap.cs"));
        c.True(bootstrap.Contains("FilterNeedsVisit"),
            "bootstrap filters already-baked L0 cells");
        c.True(bootstrap.Contains("LogSkipComplete"),
            "bootstrap logs skipped baked cells");
        c.True(bootstrap.Contains("PartitionVisitKeys"),
            "revisit partitions incomplete vs complete keys");

        string audit = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakeAudit.cs"));
        c.True(audit.Contains("IsVisitComplete"),
            "audit exposes visit-complete check");
        c.True(audit.Contains("ThinCapture"),
            "audit treats thin capture as incomplete");
    }

    static void OverlayGuard(Check c)
    {
        string overlay = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakeOverlay.cs"));
        c.True(!overlay.Contains(": GuiDialog"),
            "login overlay coordinator is not a fragile GuiDialog");
        c.True(overlay.Contains("LodLoginBakeInputGuard"),
            "login overlay uses deferred input guard");
        c.True(!overlay.Contains("LodLoginBakeVanillaLoadingHold"),
            "login overlay does not coordinate a loading-screen hold");
        c.True(!overlay.Contains("OrthoMode") && !overlay.Contains("ClearFrameBuffer"),
            "login overlay does not call OrthoMode or ClearFrameBuffer");

        string renderer = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodTerrainRenderer.cs"));
        c.True(renderer.Contains("LoginBakeOverlayActive"),
            "terrain renderer skips draw while login overlay active");
        c.True(renderer.Contains("bool loginBakeBlocked = true"),
            "terrain renderer starts with LoginBakeBlocked so load/char-create cannot ApplyZFar");
        c.True(renderer.Contains("LodJoinQuiet.Sync(loginBakeBlocked, loginBakeComplete)"),
            "terrain renderer keeps vsvaogc join-quiet in sync with bake flags");
        c.True(renderer.Contains("if (LoginBakeBlocked) return"),
            "ApplyZFar is a no-op while join GL is blocked");
        c.True(renderer.Contains("void EnsureJoinRenderer()"),
            "lodterrain compile and Opaque registration wait until after character UI");
        int ctorAt = renderer.IndexOf("public LodTerrainRenderer(", StringComparison.Ordinal);
        int afterCtor = renderer.IndexOf("bool joinRendererReady", ctorAt, StringComparison.Ordinal);
        c.True(ctorAt >= 0 && afterCtor > ctorAt, "LodTerrainRenderer ctor bounds");
        string ctor = renderer.Substring(ctorAt, afterCtor - ctorAt);
        c.True(!ctor.Contains("LoadShader()") && !ctor.Contains("RegisterRenderer"),
            "ctor does not compile lodterrain or join Opaque before character UI");

        string guard = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakeInputGuard.cs"));
        c.True(guard.Contains("TryEnsureOpen"),
            "input guard retries open when viewport is ready");
        c.True(guard.Contains("SafeBounds"),
            "input guard uses render/window bounds fallback");
        c.True(guard.Contains("AddShadedDialogBG"),
            "input guard composes an opaque Cairo backdrop");
        c.True(guard.Contains("dv-progress"),
            "input guard binds a progress statbar");

        string driver = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakePulse.cs"));
        c.True(driver.Contains("LodLoginBakePulse"),
            "game-tick pulse drives sweep while overlay is up");
        c.True(driver.Contains("PollCancelFromRender"),
            "pulse polls Esc each tick");
        c.True(!driver.Contains("BeginFrame"),
            "pulse does not coalesce OnNewFrame present-path pulses");
        c.True(!driver.Contains("while (accum"),
            "pulse does not catch-up multiple Ticks after a hitch");

        string status = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginSweepStatusWriter.cs"));
        c.True(status.Contains("ModData/Logs/login-sweep-status.json"),
            "sweep status heartbeat path");
        c.True(status.Contains("StuckHint"),
            "sweep status includes stuck hint");

        string mod = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "DistantVistasModSystem.cs"));
        c.True(mod.Contains("LodLoginBakePulse"),
            "mod wires login bake pulse");
        c.True(mod.Contains("loginBakePulse?.Pulse(dt)"),
            "mod pulses the sweep from OnGameTick");
        c.True(!mod.Contains("OnRenderPulse"),
            "mod does not connect a present-path render pulse");
        c.True(!mod.Contains("PaintSplashCover"),
            "mod does not wire splash paint on framebuffer present");
        c.True(!mod.Contains("LodLoginBakeHarmony"),
            "mod does not wire ScreenManager.OnNewFrame pulse");
        c.True(mod.Contains("LoginBakeBlocked"),
            "mod blocks terrain GL while login sweep waits on character UI");
        c.True(!mod.Contains("renderer.ApplyZFar();\n        pipeline.Open"),
            "level finalize does not call ApplyZFar before matrices/atlas are safe");

        int tickAt = mod.IndexOf("void OnGameTick(float dt)", StringComparison.Ordinal);
        int afterTick = mod.IndexOf("void PumpServerAssist()", tickAt, StringComparison.Ordinal);
        c.True(tickAt >= 0 && afterTick > tickAt, "OnGameTick bounds");
        string tick = mod.Substring(tickAt, afterTick - tickAt);
        c.True(tick.Contains("if (renderer.LoginBakeBlocked) return"),
            "OnGameTick does not Tick or upload while join GL is blocked");
    }

    static void QuietTeleports(Check c)
    {
        string bake = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBake.cs"));
        c.True(!bake.Contains("SendChatMessage"),
            "login bake never sends chat commands during sweep");
        c.True(!bake.Contains("/tp"),
            "login bake does not use /tp");
        c.True(bake.Contains("LodLoginBakeInputLock.Apply"),
            "login bake blocks input via safe action list");
        c.True(bake.Contains("PlanSweepQueue"),
            "login bake plans targeted incomplete queue");
        c.True(bake.Contains("PlanIncomplete"),
            "login bake uses incomplete-only plan when audit has misses");
        c.True(bake.Contains("LoginBakeOverlayActive"),
            "login bake suppresses LOD draws behind overlay");
        c.True(bake.Contains("TeleportSettle"),
            "login bake settles after each teleport");
        c.True(bake.Contains("BakeSettle"),
            "login bake settles after each bake");
        c.True(bake.Contains("BatchBakeL0Radius = 12"),
            "login bake batch-bakes neighbour disk inside the 750-block view");
        c.True(bake.Contains("MaxBakePerTick = 12"),
            "login bake spreads GetColor across overlay ticks");
        c.True(bake.Contains("CollectExpireLeftovers"),
            "expire leftovers are queued, not baked in one tick");
        c.True(bake.Contains("RequestChunkColumnRing"),
            "login bake grows the streamed ring instead of requesting the full disk at teleport");
        c.True(bake.Contains("SweepRowsPerCall"),
            "login bake sweeps loaded columns a few rows per tick");
        c.True(bake.Contains("MaxBatchBakePerStop = 256"),
            "login bake batch-bakes streamed neighbours per teleport");
        c.True(bake.Contains("BakeBatchAtStop"),
            "login bake batch-bakes streamed neighbours per stop");
        c.True(bake.Contains("stopBakeSkipIdle++") && bake.Contains("idleQueued"),
            "batch bake counts queued neighbours but still paints them");
        c.True(!bake.Contains("stopBakeSkipIdle++;\n                continue")
            && !bake.Contains("stopBakeSkipIdle++;\r\n                continue"),
            "batch bake does not continue past a queued neighbour");
        c.True(bake.Contains("OverlayWarmup"),
            "login bake warms overlay before teleports");
        c.True(bake.Contains("warmup complete — entering visit teleports"),
            "login bake logs loudly when warmup ends");
        c.True(bake.Contains("PollCancelFromRender"),
            "login bake polls Esc from render loop");
        c.True(bake.Contains("KeyboardKeyStateRaw"),
            "login bake reads Escape from raw keyboard state");
        c.True(bake.Contains("LodLoginBakeLeaveMenu.Request"),
            "Esc cancel leaves to the main menu");
        c.True(bake.Contains("allowOverwrite"),
            "login bake may recapture spawn during overlay warmup");
        c.True(bake.Contains("NoteLoadingCoverUnpainted"),
            "login bake never aborts solely on unpainted cover");
        c.True(!bake.Contains("cover never painted"),
            "login bake does not abort on cover paint timeout");
        c.True(bake.Contains("overlay.HasRendered"),
            "login bake waits for the HUD overlay before teleports");
        c.True(!bake.Contains("LodLoginBakeWorldHide"),
            "login bake does not hide vanilla chunk meshes during sweep");
        c.True(!bake.Contains("worldHideApplied"),
            "login bake does not delay a world-hide until splash paint");
        c.True(bake.Contains("ChunkSweepRadiusChunks"),
            "login bake scales column sweep to boosted view distance");
        c.True(bake.Contains("LodLoginBakePlayerMove.ApplyQuietFrom"),
            "login bake restores pose with quiet client moves");
        c.True(bake.Contains("SpawnRestoreRadius"),
            "login bake re-requests spawn columns at real view radius");

        string inputLock = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakeInputLock.cs"));
        c.True(inputLock.Contains("StopAllMovement"),
            "input lock calls StopAllMovement");
        c.True(inputLock.Contains("public static void Release"),
            "input lock has a full unlock, not just speed");
        c.True(bake.Contains("LodLoginBakeInputLock.Release"),
            "login bake unlocks NoClip/flying on release");
        c.True(!inputLock.Contains("for (int i = 0"),
            "input lock does not iterate raw enum ints");
    }

    static void SeasonSampleExport(Check c)
    {
        c.Eq("ModData/distantvistas/season-samples", LodSeasonSampleExporter.SamplesSubdir,
            "samples live under ModData/distantvistas");
        c.Eq(1, LodSeasonSampleExporter.ColumnStride, "default full column density");
        c.Eq(4096, LodSeasonSampleExporter.TotalColumnsPerStop, "dense 64x64 export per L0 stop");
        c.Eq(2, LodSeasonSampleExporter.SchemaVersion, "season sample schema v2");
        c.Eq("white", LodSeasonSampleExporter.ClassifyLeafTint(unchecked((int)0xFFEDEDED)), "bright neutral is white");
        c.Eq("green", LodSeasonSampleExporter.ClassifyLeafTint(unchecked((int)0xFF3A8A2A)), "strong green leaf");
        c.Eq("mixed", LodSeasonSampleExporter.ClassifyLeafTint(unchecked((int)0xFF8A6A30)), "autumn tone is mixed");

        string bake = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBake.cs"));
        c.True(bake.Contains("seasonSamples.RecordSection"),
            "login bake streams season samples after visit bake");
        c.True(bake.Contains("seasonSamples.BeginSession"),
            "login bake opens a sample session with sweep mode");

        string exporter = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodSeasonSampleExporter.cs"));
        c.True(exporter.Contains("FlushBatch"),
            "sample exporter batches disk writes");
        c.True(exporter.Contains("README.md"),
            "schema readme is written beside samples");
        c.True(exporter.Contains("WriteStopLine"),
            "each L0 stop writes a stop header before columns");
        c.True(exporter.Contains("WriteColumnRecord"),
            "every column in the L0 cell is exported");
        c.True(exporter.Contains("columnComplete"),
            "column rows carry coverage completeness flags");
        c.True(exporter.Contains("subsurfaceBlockId"),
            "column rows include subsurface block under top");
        c.True(!exporter.Contains("if (!section.Captured[col]) continue"),
            "uncaptured columns are not skipped in export");
    }

    static void SweepResume(Check c)
    {
        c.Eq(30.0, LodLoginSweepResume.MaxResumeDayGap, "resume within 30 in-game days");
        c.Eq("ModData/distantvistas/login-sweep-resume-<worldId>.json", LodLoginSweepResume.RelPath,
            "resume file under ModData/distantvistas");

        string bake = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBake.cs"));
        c.True(bake.Contains("CancelAndSave"),
            "login bake can cancel and save progress");
        c.True(bake.Contains("Phase.OverlayWarmup"),
            "cancel path includes overlay warmup phase");
        c.True(bake.Contains("LoginBakeComplete = true"),
            "failed teardown marks login bake complete for normal play");
        c.True(bake.Contains("SaveResumeSnapshot"),
            "login bake persists resume snapshots");
        c.True(bake.Contains("LodLoginSweepResume.TryLoad"),
            "login bake restores eligible resume on begin");
        c.True(bake.Contains("IsOversizedForCurrentBudget"),
            "login bake reclaims oversized resume checkpoints");
        c.True(bake.Contains("PlanRevisitVisited"),
            "login bake uses budgeted revisit plan");

        string guard = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakeInputGuard.cs"));
        c.True(guard.Contains("OnCancelRequested"),
            "escape routes to cancel/resume handler");
        c.True(guard.Contains("Esc to pause and return to the menu"),
            "overlay hint says Esc returns to the menu");

        string leave = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakeLeaveMenu.cs"));
        c.True(leave.Contains("DestroyGameSession"),
            "Esc leave uses the vanilla leave-world path");
        c.True(leave.Contains("SendLeave(0)"),
            "Esc leave notifies the server before destroying the session");
        c.True(leave.Contains("EnumExitMode.SoftExit"),
            "Esc leave uses SoftExit like the vanilla pause menu");
    }

    static void SweepSkipGate(Check c)
    {
        c.Eq("ModData/distantvistas/login-sweep-complete-<worldId>.json", LodLoginSweepComplete.RelPath,
            "completion record under ModData/distantvistas");
        string completeSrc = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginSweepComplete.cs"));
        c.True(completeSrc.Contains("WindowStartedUtcMs"),
            "completion marker stamps the wall-clock start of the 30-day window");
        c.True(completeSrc.Contains("LodLoginSweepWindow.NowUtcMs()"),
            "successful sweep writes the current UTC stamp");
        c.Eq(30.0, LodLoginSweepWindow.MaxDayGap, "skip window matches resume day gap");
        c.Eq(30L * 24 * 60 * 60 * 1000, LodLoginSweepWindow.MaxWallMs,
            "wall-clock window is 30 real days");
        c.True(LodLoginSweepWindow.IsWithin("spring", "spring", 10, 0),
            "same season inside 30 days stays in window");
        c.True(LodLoginSweepWindow.IsWithin("spring", "winter", 10, 0),
            "season slug mismatch no longer expires when the day gap is small");
        c.False(LodLoginSweepWindow.IsWithin("spring", "spring", 31, 0),
            "day gap over 30 expires even when the season matches");
        c.True(LodLoginSweepWindow.IsWithin("", "winter", 10, 0),
            "empty saved season falls back to the day gap");
        c.True(LodLoginSweepWindow.IsWallGapWithin(1000, 0),
            "legacy markers with no wall stamp stay on the day gap");
        c.True(LodLoginSweepWindow.IsWithin(10, 0, 1000, 1000),
            "fresh wall stamp stays in window");
        c.False(LodLoginSweepWindow.IsWithin(10, 0, LodLoginSweepWindow.MaxWallMs + 2, 1),
            "30 real days expire even when in-game days are small");
        c.Eq(LodLoginSweepWindow.OutsideDayWindowReason,
            LodLoginSweepWindow.ExpireReason("spring", "spring", 40, 0),
            "day-gap expire reason is outside 30-day window");
        c.Eq(LodLoginSweepWindow.OutsideWallWindowReason,
            LodLoginSweepWindow.ExpireReason(10, 0, LodLoginSweepWindow.MaxWallMs + 2, 1),
            "wall-clock expire reason is outside 30-day wall-clock window");
        c.True(LodLoginSweepWindow.ExpireReason("spring", "winter", 10, 0) == null,
            "season change alone has no expire reason");
        c.True(LodLoginSweepWindow.ExpireReason("spring", "spring", 10, 0) == null,
            "in-window same season has no expire reason");
        c.True(LodLoginSweepWindow.RecaptureReason("fall", "fall", 10, 0, 0) == null,
            "stale paint revision does not force login teleport inside the 30-day window");
        c.True(LodLoginSweepWindow.RecaptureReason("fall", "fall", 10, 0, 1) == null,
            "older paint markers stay skipped when the day window holds");
        c.True(LodLoginSweepWindow.RecaptureReason("fall", "fall", 10, 0, LodSurfaceMix.PaintRevision) == null,
            "current paint revision stays skipped when the window holds");
        c.True(LodLoginSweepWindow.RecaptureReason("fall", "fall", 10, 0, 2) == null,
            "paint revision 2 does not force login teleport");
        c.True(LodLoginSweepWindow.RecaptureReason("fall", "fall", 10, 0, 3) == null,
            "paint revision 3 does not force login teleport");
        c.Eq(8, LodSurfaceMix.PaintRevision,
            "paint revision 8: empty-mesh remesh, Leaves foliage, onset sweep radius");
        c.True(LodLoginSweepWindow.RecaptureReason("fall", "fall", 10, 0, 5) == null,
            "paint revision 5 does not force login teleport");
        c.True(LodLoginSweepWindow.RecaptureReason("fall", "winter", 10, 0, 0) == null,
            "stale paint + season slug change still does not expire (month/day window only)");
        c.True(LodLoginSweepWindow.StalePaintRevisionReason.Contains("paint revision"),
            "legacy stale-paint reason string retained for log compatibility");

        string window = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginSweepWindow.cs"));
        c.True(window.Contains("nowTotalDays - savedTotalDays <= MaxDayGap"),
            "skip window still measures the in-game day gap");
        c.True(window.Contains("WindowStartedUtcMs") || window.Contains("savedUtcMs"),
            "skip window also measures real time since the stamped first day");
        c.True(window.Contains("MaxWallMs"),
            "skip window has a 30-day wall-clock bound");
        c.False(window.Contains("SeasonChangedReason"),
            "season slug is not an expire trigger");
        c.True(window.Contains("MonthChangedReason"),
            "calendar month change recaptures even inside the 30-day window");
        c.True(LodLoginSweepWindow.TryReadSavedMonth("Y1M5D120H8.5_spring", out int savedMonth)
            && savedMonth == 5,
            "calendar token month is the M field");
        c.True(LodLoginSweepWindow.MonthChanged("Y1M5D1H0_spring", 12),
            "May stamp expires in December");
        c.False(LodLoginSweepWindow.MonthChanged("Y1M12D1H0_winter", 12),
            "same-month stamp stays skipped");

        string gate = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginSweepGate.cs"));
        c.True(gate.Contains("resuming cancelled mid-sweep checkpoint"),
            "gate runs when an eligible resume exists and skip would not apply");
        c.True(gate.Contains("ShouldDropLeftoverResume") || gate.Contains("dropped leftover mid-sweep resume"),
            "gate drops leftover Esc pause when in-window complete would skip");
        c.True(gate.Contains("empty canvas needs bootstrap sweep"),
            "gate runs bootstrap on empty visited canvas");
        c.True(gate.Contains("still incomplete"),
            "gate runs when audit finds misses and no in-window complete");
        c.True(gate.Contains("skip re-canvas"),
            "in-window complete marker skips full re-canvas even with leftovers");
        c.True(gate.Contains("no successful sweep recorded yet"),
            "gate runs when completion marker is missing");
        int idxNoComplete = gate.IndexOf("no successful sweep recorded yet for this world", StringComparison.Ordinal);
        int idxExpire = gate.IndexOf("LodLoginSweepWindow.RecaptureReason", StringComparison.Ordinal);
        int idxMiss = gate.IndexOf("visited region(s) still incomplete", StringComparison.Ordinal);
        c.True(idxNoComplete >= 0 && idxMiss >= 0 && idxNoComplete < idxMiss,
            "gate prefers no-complete bootstrap over miss-repair on first sweep");
        c.True(idxExpire >= 0 && idxExpire < idxMiss,
            "gate prefers 30-day expire over leftover miss-repair");
        c.True(window.Contains("outside 30-day window"),
            "gate runs when completion window expired");
        c.True(window.Contains("outside 30-day wall-clock window"),
            "gate runs when 30 real days have passed since the stamped first day");
        c.True(gate.Contains("VisitedKeyCount >= visited"),
            "in-window skip requires canvas not grown");
        c.True(gate.Contains("visited canvas complete within 30-day window"),
            "gate skips when canvas is complete and in window");
        c.True(gate.Contains("LodLoginSweepWindow.RecaptureReason"),
            "gate expires on day gap, wall-clock gap, or calendar month (not paint revision alone)");
        c.True(gate.Contains("WindowStartedUtcMs <= 0"),
            "in-window skip adopts a wall-clock stamp on legacy markers");

        string mod = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "DistantVistasModSystem.cs"));
        c.True(mod.Contains("LodLoginSweepGate.Decide"),
            "level finalize consults sweep gate before overlay");
        c.True(mod.Contains("Login visit sweep skipped"),
            "skipped sweep logs and drops into play");
        c.True(!mod.Contains("ClearHandoverDeferral"),
            "skipped sweep does not clear a handover deferral");
        c.False(mod.Contains("LodLoginSweepComplete.RecordSuccess"),
            "skip path must not stamp completion marker");
        c.True(mod.Contains("LoginVisitSweepEnabled"),
            "sweep gated by config flag");
        c.True(mod.Contains("LoginVisitSweepAllowedHere"),
            "overlay hop-scan is refused on vanilla multiplayer");
        c.True(mod.Contains("assist != null && assist.ServerHasMod"),
            "vanilla MP skip is assist-channel Connected, not Welcome");
        c.True(mod.Contains("capi.IsSinglePlayer"),
            "singleplayer still runs the overlay");
        c.True(mod.Contains("LodLoginBakeViewBoost.RecoverPlayerViewIfNeeded"),
            "join restores a leftover 750/1000 slider before play or overlay");
        c.True(new DistantVistasConfig().LoginVisitSweepEnabled,
            "login visit sweep enabled by default");

        string bake = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBake.cs"));
        c.True(bake.Contains("LodLoginSweepComplete.RecordSuccess"),
            "successful sweep records completion for future skips");
        c.True(bake.Contains("LodLoginSweepWindow.RecaptureReason"),
            "planner uses the same recapture helper as the gate");
        c.True(bake.Contains("PlanSeasonExpired"),
            "expired window plans a season revisit, not leftover hops only");
        c.True(bake.Contains("forceRecapture: true"),
            "season hops recapture streamed columns");
        c.True(bake.Contains("visitBakeChanged"),
            "visit bake treats FlagBaked RGB deltas as a completed stop");

        string season = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodSeasonBake.cs"));
        c.True(season.Contains("entry.Color = baked"),
            "visit bake overwrites FlagBaked palette RGB in place");
        c.True(season.Contains("block.GetColor(capi, pos)"),
            "visit bake samples vanilla GetColor at the column top");
        c.True(season.Contains("CanVisitBake"),
            "visit bake does not drop snow or climate-untinted tops");
        c.True(season.Contains("TryResolveLiveSurface"),
            "visit bake walks the loaded chunk for snow and extra canopy");

        string worker = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Lod", "LodWorker.cs"));
        c.True(worker.Contains("HighestSolidY"),
            "capture starts above the rain map so snow on canopy enters the mesh");

        c.True(mod.Contains("pipeline.QueueColumn(chunkCoord.X, chunkCoord.Z)"),
            "ChunkDirty uses NeedsCapture so FlagBaked walk-back snow stays");
        c.False(mod.Contains("pipeline.QueueColumnForce(chunkCoord.X, chunkCoord.Z)"),
            "walk-time ChunkDirty does not force recapture");

        string view = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakeViewBoost.cs"));
        c.True(view.Contains("SweepBoostViewDistanceBlocks = 750"),
            "login visit holds graphics view at 750 blocks");

        string label = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginSweepBootstrap.cs"));
        int idxSeason = label.IndexOf("if (seasonRefresh && gapCount > 0)", StringComparison.Ordinal);
        int idxGaps = label.IndexOf("Filling gaps", StringComparison.Ordinal);
        c.True(idxSeason >= 0 && idxGaps >= 0 && idxSeason < idxGaps,
            "season-refresh overlay text wins over Filling gaps");
        c.True(label.Contains("BudgetVisitStops"),
            "30-day expire spatially samples the stored disk");
        c.True(label.Contains("maxVisitStops = RevisitMaxVisitStops"),
            "expire hops stay inside the timed visit budget, never the whole disk");
        c.True(label.Contains("fullDiskRecapture") && label.Contains(":false"),
            "expire planner logs that it did not queue every stored cell");
        c.True(label.Contains("InteriorGapsBetweenStops"),
            "after the timed sample, expire fills holes between those stops");
        c.True(label.Contains("RetryMaxVisitStops"),
            "interior gap-fill stays on the short retry budget");
    }

    static void SweepTiming(Check c)
    {
        LodLoginSweepTiming.SetMachineSecPerStop(LodLoginSweepTiming.InitialSecPerStop);
        c.Eq(30.0, LodLoginSweepTiming.TargetMinSec, "sweep target min seconds");
        c.Eq(180.0, LodLoginSweepTiming.TargetMaxSec, "sweep target max seconds");
        c.Eq(180.0, LodLoginSweepTiming.BootstrapTargetMaxSec, "bootstrap target max seconds");
        c.Eq(48.0, LodLoginSweepTiming.RetryTargetSec, "retry pass wall seconds");
        c.Eq(1.0, LodLoginSweepTiming.InitialSecPerStop, "fallback per-stop when this PC has no samples");
        c.Eq(288000, LodLoginSweepBootstrap.EmptyCanvasBootstrapRadiusBlocks,
            "empty-canvas bootstrap probe radius default (~288 km, meets Farseer onset)");
        c.Eq(4500, LodLoginSweepBootstrap.BootstrapCellRadius(),
            "288000 blocks is 4500 L0 cells radius at 64-block footprint");
        c.Eq(9216, LodLoginSweepBootstrap.MaxBootstrapClassifyCells,
            "classify ceiling scales with the 1.5x radius (~2.25x area)");
        c.Eq(96, LodLoginSweepTiming.MinVisitStops, "first-pass floor densifies the 216 km disk");
        c.Eq(240, LodLoginSweepTiming.MaxVisitStops, "first-pass ceiling for fast machines");
        c.Eq(24, LodLoginSweepTiming.MinRetryStops, "retry floor stays shorter than first pass");
        c.Eq(48, LodLoginSweepTiming.MaxRetryStops, "retry ceiling matches retry wall at 1s/stop");
        c.Eq(180, LodLoginSweepTiming.VisitStopBudget(1.0, LodLoginSweepTiming.TargetMaxSec),
            "fallback 1s/stop plans 180 first-pass stops");
        c.Eq(96, LodLoginSweepTiming.VisitStopBudget(2.0, LodLoginSweepTiming.TargetMaxSec),
            "2s/stop clamps to MinVisitStops 96, not 90 from 180/2");
        c.Eq(96, LodLoginSweepTiming.VisitStopBudget(3.6, LodLoginSweepTiming.TargetMaxSec),
            "3.6s/stop clamps to MinVisitStops 96");
        c.Eq(24, LodLoginSweepTiming.RetryStopBudget(3.6),
            "3.6s/stop retry clamps to MinRetryStops 24");
        c.Eq(180, LodLoginSweepBootstrap.BootstrapMaxVisitStops,
            "bootstrap visit cap targets ~180s at fallback 1s/stop");
        c.Eq(180, LodLoginSweepBootstrap.RevisitMaxVisitStops,
            "revisit visit cap targets ~180s at fallback 1s/stop");
        c.Eq(48, LodLoginSweepBootstrap.RetryMaxVisitStops,
            "retry visit cap matches MaxRetryStops (48s wall at 1s/stop)");
        c.Eq(48, LodLoginSweepTiming.MaxRetryStops,
            "retry ceiling is 48");
        c.True(LodLoginSweepBootstrap.RetryMaxVisitStops <= LodLoginSweepTiming.MaxVisitStops,
            "retry hop is not longer than the first pass");

        var harvest = LodLoginSweepTimingStore.HarvestSecPerStop(new[]
        {
            "6.9.2026 02:03:40 [Notification] [DistantVistas] Login visit sweep: quiet teleports begin — 30 L0 regions.",
            "6.9.2026 02:04:23 [Notification] [DistantVistas] Login visit sweep: retrying 94 missed regions (pass 1).",
            "6.9.2026 02:34:22 [Notification] [DistantVistas] Login visit sweep: quiet teleports begin — 313 L0 regions.",
            "6.9.2026 02:50:19 [Notification] [DistantVistas] Login visit sweep: retrying 491 missed regions (pass 1).",
        });
        c.Eq(1, harvest.Count, "log harvest keeps budgeted passes and drops 300+ hole hops");
        c.True(Math.Abs(harvest[0] - (43.0 / 30.0)) < 0.01, "harvested rate is 43s / 30 stops");
        c.Eq(24, LodLoginSweep.MaxChunkWaitTicks, "chunk wait capped ~1.2s at 50ms pulse");
        c.Eq(16, LodLoginSweep.MaxCaptureWaitTicks, "capture wait capped ~0.8s at 50ms pulse");
        c.Eq(3, LodLoginSweepBootstrap.OpenOceanMaxSamples,
            "open-ocean full-bake samples capped for 1-min sweep");

        string bootstrap = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginSweepBootstrap.cs"));
        c.True(bootstrap.Contains("BudgetVisitStops"),
            "bootstrap applies hard visit stop budget");
        c.True(bootstrap.Contains("BudgetBootstrapVisitStops"),
            "bootstrap uses outer-weighted distance-band subsample");
        c.True(bootstrap.Contains("SelectLandVisitCells"),
            "bootstrap full-visits land and coastline ocean");
        c.True(bootstrap.Contains("PickOceanSampleCells"),
            "bootstrap picks representative open-ocean samples");
        c.True(bootstrap.Contains("FilterNeedsVisit"),
            "bootstrap skips cells already baked in cache");
        c.True(bootstrap.Contains("OpenOceanMaxSamples"),
            "bootstrap caps ocean sample visits");
        c.True(bootstrap.Contains("open-water L0 cells"),
            "bootstrap logs ocean sample/stamp plan");
        c.True(bootstrap.Contains("PlanRevisitKeys"),
            "revisit applies spatial subsample budget");
        c.True(bootstrap.Contains("BootstrapCoastGuard"),
            "bootstrap can plan coast-guard ocean sweeps");
        c.True(bootstrap.Contains("BootstrapRadius"),
            "bootstrap can plan radius disk sweeps");

        string bake = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBake.cs"));
        c.True(bake.Contains("PlanBootstrap"),
            "login bake plans first sweep with bootstrap spawn disk");
        c.True(bake.Contains("StampOpenOceanFromSamples"),
            "login bake stamps open ocean from samples after visit pass");
        c.True(bake.Contains("LodLoginSweepOceanFill.StampOpenOcean"),
            "login bake calls ocean stamp fill helper");
        c.True(bake.Contains("PlanRevisitVisited"),
            "login bake uses budgeted revisit plan after complete exists");
        c.True(bake.Contains("TryLoad(capi) == null"),
            "login bake bootstraps when no per-world complete marker");
        c.True(bootstrap.Contains("PlanBootstrap"),
            "bootstrap exposes PlanBootstrap for first-sweep path");
        c.True(bake.Contains("sweepModeLabel"),
            "login bake progress distinguishes bootstrap vs revisit");
        c.True(bake.Contains("StatusWithEta"),
            "login bake progress includes ETA suffix");
        c.True(bake.Contains("LodLoginSweepTimingStore.EnsureApplied"),
            "login bake seeds ETA from this PC before planning");
    }

    static void VisitOnsetEnvelope(Check c)
    {
        int sb = LodSection.SectionBlocks;
        var world = new LodWorld();
        world.InstallStoredKey(0, 0, 0, applyToParent: true, provisional: false);
        world.InstallStoredKey(0, 10, 0, applyToParent: true, provisional: false);

        double radius = FarseerVisitOnset.CaptureEnvelopeRadiusBlocks(world, 0, 0, padBlocks: 0);
        double expected = (10 + 0.5) * sb;
        c.True(Math.Abs(radius - expected) < 1.0,
            "envelope radius reaches farthest L0 centre from origin");

        double padded = FarseerVisitOnset.CaptureEnvelopeRadiusBlocks(
            world, 0, 0, FarseerVisitOnset.EnvelopePadBlocks);
        c.True(padded > radius, "envelope pad extends past farthest L0");

        c.True(FarseerVisitOnset.IsVisitedForOnset(
                5, 0, sb, 0, 0, padded, key => world.HasDataSet.Contains(key)),
            "gap L0 between hop cells is visited via envelope fill");
        c.False(FarseerVisitOnset.IsVisitedForOnset(
                200, 200, sb, 0, 0, padded, key => world.HasDataSet.Contains(key)),
            "far outside envelope stays unvisited for early silhouette");
        c.True(FarseerVisitOnset.IsVisitedForOnset(
                10, 0, sb, 0, 0, 0, key => world.HasDataSet.Contains(key)),
            "exact captured L0 is visited even with zero envelope");

        string onset = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "FarseerVisitOnset.cs"));
        c.True(onset.Contains("CaptureEnvelopeRadiusBlocks"),
            "visit-onset paints continuous capture envelope");
        c.True(onset.Contains("IsVisitedForOnset"),
            "visit-onset classifies envelope + exact L0");

        string complete = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginSweepComplete.cs"));
        c.True(complete.Contains("SweepRadiusBlocks"),
            "complete stamp stores sweep envelope radius");
        c.True(complete.Contains("SweepOriginX"),
            "complete stamp stores sweep envelope origin");
    }

    static void CreativeMode(Check c)
    {
        string bake = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBake.cs"));
        c.True(bake.Contains("gameMode.EnsureCreative()"),
            "login bake enables creative during sweep");
        c.True(bake.Contains("gameMode.Restore()"),
            "login bake restores prior gamemode on teardown");
        c.True(bake.Contains("LodLoginBakeViewBoost"),
            "login bake temporarily boosts view distance for sweep cover");
        c.True(bake.Contains("viewBoost.Restore"),
            "login bake restores view distance after sweep");
        c.True(bake.Contains("viewBoost.EnsureBoosted"),
            "login bake keeps view boost active during sweep");
        c.True(bake.Contains("viewBoost.ReassertPlayerView"),
            "login bake writes the player's slider again after pose restore");

        string viewBoost = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakeViewBoost.cs"));
        c.Eq(2048, LodLoginBakeViewBoost.MaxVanillaViewDistance, "engine view-distance ceiling");
        c.Eq(750, LodLoginBakeViewBoost.SweepBoostViewDistanceBlocks,
            "sweep holds vanilla view at 750 blocks then restores");
        c.Eq(
            (int)Math.Ceiling(750 * LodCoveragePolicy.HorizonDrawScale),
            LodLoginBakeViewBoost.SweepVisitRadiusBlocks,
            "visit disk reaches Farseer onset (hold × HorizonDrawScale)");
        c.True(LodLoginBakeViewBoost.SweepVisitRadiusBlocks > LodLoginBakeViewBoost.SweepBoostViewDistanceBlocks,
            "visit radius is wider than the thin graphics hold");
        c.True(viewBoost.Contains("SweepVisitRadiusBlocks"),
            "ChunkSweepRadiusChunks uses SweepVisitRadiusBlocks toward onset");
        c.Eq(1000, LodLoginBakeViewBoost.LegacySweepHoldBlocks,
            "old 1000-block hold is leftover, never a restore target");
        c.True(LodLoginBakeViewBoost.IsSweepHoldValue(750), "750 is the scan hold");
        c.True(LodLoginBakeViewBoost.IsSweepHoldValue(1000), "1000 is the old scan hold");
        c.False(LodLoginBakeViewBoost.IsSweepHoldValue(160), "160 is a player slider");
        c.False(LodLoginBakeViewBoost.IsSweepHoldValue(352), "352 is a player slider");
        c.True(LodLoginBakeViewBoost.TryPickPlayerView(750, 160, out int picked) && picked == 160,
            "restore prefers stored player slider over a live 750 hold");
        c.True(LodLoginBakeViewBoost.TryPickPlayerView(1000, 160, out picked) && picked == 160,
            "restore prefers stored player slider over a leftover 1000");
        c.True(LodLoginBakeViewBoost.TryPickPlayerView(352, 0, out picked) && picked == 352,
            "first hold snapshots a live player slider");
        c.True(LodLoginBakeViewBoost.TryPickPlayerView(1536, 320, out picked) && picked == 320,
            "restore refuses a maxed leftover and keeps the player's slider");
        c.False(LodLoginBakeViewBoost.TryPickPlayerView(1536, 0, out _),
            "1536/1500 is the graphics max leftover, not a player slider");
        c.False(LodLoginBakeViewBoost.IsPlayerViewDistance(1536),
            "maxed 1536 is never original");
        c.False(LodLoginBakeViewBoost.IsPlayerViewDistance(750),
            "scan hold is never original");
        c.True(LodLoginBakeViewBoost.IsPlayerViewDistance(320),
            "320 is a player slider");
        c.False(LodLoginBakeViewBoost.TryPickPlayerView(750, 1000, out _),
            "750 and 1000 are not player sliders");
        c.False(viewBoost.Contains("SweepBoostViewDistanceBlocks = 1000"),
            "scan hold is never 1000");
        c.True(viewBoost.Contains("LodLoginBakeViewHoldStore"),
            "player slider is persisted before the 750 write");
        string holdStore = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakeViewHoldStore.cs"));
        c.True(holdStore.Contains("login-view-hold.json"),
            "hold snapshot file is login-view-hold.json");
        c.True(viewBoost.Contains("RecoverPlayerViewIfNeeded"),
            "crash leftover 750/1000 restores from the persisted slider");
        c.True(viewBoost.Contains("ViewDistanceSettingKey"),
            "boost writes ClientSettings.viewDistance — DesiredViewDistance alone is overwritten");
        c.True(viewBoost.Contains("ints.Set(ViewDistanceSettingKey, blocks, true)"),
            "boost triggers the graphics viewDistance watcher");
        c.True(viewBoost.Contains("SweepBoostViewDistanceBlocks"),
            "boost resolve uses the fixed 750-block bake view");
        c.True(viewBoost.Contains("FarViewDistanceCap"),
            "view boost clears DV far cap during sweep");
        c.True(viewBoost.Contains("ApplyZFar"),
            "view boost refreshes camera z-far after far-cap change");
    }

    static void HudHide(Check c)
    {
        string bake = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBake.cs"));
        c.True(!bake.Contains("hudHide.EnsureHidden()"),
            "login bake does not hide vanilla HUD (HideGuis would hide the overlay)");
        c.True(bake.Contains("playerHide.EnsureHidden()"),
            "login bake hides local player during sweep");
        c.True(bake.Contains("overlay.Show()"),
            "login bake opens the HUD overlay before teleports");
        c.True(bake.Contains("overlay.Hide()"),
            "login bake closes the HUD overlay on teardown");

        string playerHide = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakePlayerHide.cs"));
        c.True(playerHide.Contains("ServerControls.Sneak"),
            "player hide suppresses nametag like crouch");
        c.True(playerHide.Contains("InvisibleRenderColor"),
            "player hide tints entity render alpha to zero");
        c.True(playerHide.Contains(LodLoginBakePlayerHide.HideFpHandsKey),
            "player hide saves and restores hideFpHands");
    }

    static void CharacterWait(Check c)
    {
        string wait = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakeCharacterWait.cs"));
        c.True(wait.Contains("HasProtectedDialogOpen"),
            "character wait IsPending is dialog-only (not !PlayerReadyFired)");
        c.True(!wait.Contains("return !capi.PlayerReadyFired"),
            "character wait must not gate IsPending on PlayerReadyFired (deadlock)");
        c.True(wait.Contains("GuiDialogCharacterBase"),
            "character wait recognizes character dialog base type");
        c.True(wait.Contains("IsProtectedDialog"),
            "character wait exposes protected dialog check");

        string mod = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "DistantVistasModSystem.cs"));
        c.True(mod.Contains("DeferLoginVisitSweep"),
            "level finalize defers sweep until after the first present");
        c.True(mod.Contains("LodLoginBakeCharacterWait.IsPending"),
            "deferred sweep polls character wait helper");
        c.True(!mod.Contains("AllowHandoverWhileCharacterPending"),
            "character deferral does not hijack running-game handover");
        c.True(mod.Contains("EnsureJoinAtlasColors"),
            "atlas colours resolve on the post-finalize tick");
        c.True(mod.Contains("EnsureJoinPipelineOpen"),
            "join opens the LOD cache through an idempotent helper");
        c.True(mod.Contains("OnLoginSweepDeferTick") && mod.Contains("StartLoginVisitSweepIfNeeded"),
            "sweep start runs from the post-finalize tick");

        int deferAt = mod.IndexOf("void OnLoginSweepDeferTick", StringComparison.Ordinal);
        int afterDefer = mod.IndexOf("void StopLoginSweepDeferListener", deferAt, StringComparison.Ordinal);
        c.True(deferAt >= 0 && afterDefer > deferAt, "OnLoginSweepDeferTick bounds");
        string defer = mod.Substring(deferAt, afterDefer - deferAt);
        int pendingAt = defer.IndexOf("LodLoginBakeCharacterWait.IsPending", StringComparison.Ordinal);
        int rendererAt = defer.IndexOf("EnsureJoinRenderer", StringComparison.Ordinal);
        int openAt = defer.IndexOf("EnsureJoinPipelineOpen", StringComparison.Ordinal);
        c.True(pendingAt >= 0 && rendererAt > pendingAt,
            "join renderer arms after the character-wait check");
        c.True(openAt > rendererAt,
            "LOD cache Open runs after join renderer arm");

        int finalizeAt = mod.IndexOf("void OnLevelFinalize()", StringComparison.Ordinal);
        int afterFinalize = mod.IndexOf("static readonly int ExploreHopBlocks", finalizeAt, StringComparison.Ordinal);
        c.True(finalizeAt >= 0 && afterFinalize > finalizeAt, "OnLevelFinalize bounds");
        string finalize = mod.Substring(finalizeAt, afterFinalize - finalizeAt);
        c.True(!finalize.Contains("GetAverageColor"),
            "OnLevelFinalize does not sample the block atlas");
        c.True(!finalize.Contains("UnknownTexturePosition"),
            "OnLevelFinalize does not read UnknownTexturePosition");
        c.True(!finalize.Contains("StartLoginVisitSweepIfNeeded"),
            "OnLevelFinalize does not start the sweep or Decide");
        c.True(!finalize.Contains("pipeline.Open"),
            "OnLevelFinalize does not open the LOD cache");
        c.True(!finalize.Contains("LodLocalOfferSource"),
            "OnLevelFinalize does not open local offers");
        c.True(finalize.Contains("DeferLoginVisitSweep()"),
            "OnLevelFinalize always defers sweep start off the first present");
        c.True(finalize.Contains("RestorePresentFramebuffer()"),
            "OnLevelFinalize unbinds leftover FBOs before the first SwapBuffers");
        c.True(mod.Contains("CurrentFrameBuffer = null"),
            "present restore binds the default window framebuffer, not Primary");
        c.True(!mod.Contains("OrthoMode") && !mod.Contains("ClearFrameBuffer"),
            "present restore does not call OrthoMode or ClearFrameBuffer");

        string bake = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBake.cs"));
        c.True(!bake.Contains("EnsureRunningGameRenderPath"),
            "login bake does not switch ScreenManager present path during sweep");
        c.True(bake.Contains("LodLoginBakeCharacterWait.IsProtectedDialog"),
            "close-blocking-dialogs skips character/class selection");
        c.True(bake.Contains("renderer.LoginBakeBlocked = false"),
            "sweep Begin unblocks LOD GL after the overlay is shown");

        string quiet = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodJoinQuiet.cs"));
        c.True(quiet.Contains("public static bool SuppressVaoDrain"),
            "join quiet exposes SuppressVaoDrain for vsvaogc (no project reference)");
        c.True(quiet.Contains("loginBakeBlocked || !loginBakeComplete"),
            "join quiet stays on while terrain is blocked or the sweep is unfinished");
        c.True(!quiet.Contains("Harmony"),
            "join quiet does not Harmony-patch vsvaogc");
        c.True(!mod.Contains("HarmonyLib") && !mod.Contains("PatchAll("),
            "Distant Vistas does not Harmony-patch vsvaogc or the present path");
    }
}
