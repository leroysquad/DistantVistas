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
        BootstrapSpawnFirst(c);
        VisitOnsetEnvelope(c);
        CreativeMode(c);
        HudHide(c);
        CharacterWait(c);
        PostGetColorSimd(c);
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
        for (int i = 0; i < 2000; i++)
            manyMisses.Add(new(LodWorld.SectionKey(0, i, 0), LodLoginBakeAudit.MissReason.BakeIncomplete));
        var budgeted = LodLoginSweepBootstrap.PlanIncomplete(manyMisses);
        c.Eq(LodLoginSweepBootstrap.RevisitMaxVisitStops, budgeted.Keys.Count,
            "incomplete plan stays inside the revisit stop budget");
        c.True(budgeted.ModeLabel.Contains("of 2000"), "incomplete plan names the leftover gaps");

        c.Eq(1680, LodLoginSweepBootstrap.RevisitMaxVisitStops,
            "revisit cap targets ~7 min at fallback 0.25s/stop");
        c.Eq(1680, LodLoginSweepBootstrap.BootstrapMaxVisitStops,
            "bootstrap land cap matches revisit (~7 min at fallback 0.25s/stop)");
        c.Eq(96, LodLoginSweepBootstrap.RetryMaxVisitStops,
            "retry hop hits MaxRetryStops at fallback 0.5s/stop");
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
        string progressUi = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakeProgressUi.cs"));
        c.True(progressUi.Contains("HeartbeatMs = 3000"),
            "overlay status refreshes at least every 3s so % does not sit still");
        c.True(progressUi.Contains("detail != lastDetail"),
            "overlay status refreshes when scout count / paint text changes");
        c.True(bake.Contains("if (released) return"),
            "login bake teardown is idempotent");
        c.True(bake.Contains("Teardown(success: false, keepResume: true)"),
            "dispose routes through Teardown");
        c.True(bake.Contains("LOGIN VISIT SWEEP ARMED"),
            "login bake logs loudly when sweep arms");
        c.True(bake.Contains("scout viewer entities stream visit cells"),
            "login bake logs scout coverage, not player teleports");
        c.True(!bake.Contains("quiet teleports begin"),
            "login bake no longer claims quiet teleports");
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
        c.True(season.Contains("block.GetColor(capi, LodBakeScratch.Pos(x, y, z))"),
            "visit bake samples vanilla GetColor at column top");
        c.True(season.Contains("FinishColumnPaint"),
            "visit bake uses the shared season-ground mix for overlay and walk");
        c.True(bake.Contains("BakeSectionFromVisitChunked"),
            "login overlay uses time-budgeted visit bake (BlurRadius 0 — no halo pass)");
        c.True(bake.Contains("MaxPaintWallMsPerTick"),
            "login overlay caps GetColor wall time per tick");
        c.True(bake.Contains("paintResumeCol"),
            "partial L0 bakes resume next tick instead of freezing the client");
        c.True(bake.Contains("PrioritizePaintQueue"),
            "paint queue prefers partial + spawn-near L0 before far rim");
        c.True(bake.Contains("MaybeSaveResumeSnapshot"),
            "resume snapshot is throttled so paint batches do not allocate every tick");
        c.True(bake.Contains("DeferLegacyHeal = true"),
            "legacy heal is deferred during visit sweep");

        string pipeline = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Lod", "LodPipeline.cs"));
        c.True(pipeline.Contains("DeferLegacyHeal"),
            "pipeline can defer approximate legacy heal");
        c.True(pipeline.Contains("OverlayInstallsPerTick"),
            "inline storage installs are throttled during overlay DeferLegacyHeal");
        c.True(pipeline.Contains("DiscoverOnly"),
            "pipeline can restrict post-sweep capture to new and nearby land");
        c.True(bake.Contains("DiscoverOnly = true"),
            "successful sweep enables discover-only capture, not a total freeze");
        c.True(bake.Contains("DeferLegacyHeal = false"),
            "successful sweep re-enables explore bake for newly discovered land");
        c.False(bake.Contains("FreezeCapture = true"),
            "successful sweep must not lock all capture until relog");
        string terrain = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodTerrainRenderer.cs"));
        int clearAt = terrain.IndexOf("public void ClearMeshes()", StringComparison.Ordinal);
        c.True(clearAt >= 0, "renderer has ClearMeshes");
        string clear = terrain.Substring(clearAt, Math.Min(900, terrain.Length - clearAt));
        c.True(clear.Contains("MeshPressureActive = false"),
            "leave-world ClearMeshes drops the mesh-pressure latch");
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
        c.True(bootstrap.Contains("Repairing {keys.Count} of {gapCount} incomplete regions"),
            "incomplete plan label uses the real miss count, not a hardcoded 500");

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
            "terrain renderer knows the login overlay is up");
        c.True(!renderer.Contains("if (LoginBakeOverlayActive || LoginBakeBlocked) return"),
            "overlay no longer skips the whole render frame (meshes must upload under the splash)");
        c.True(renderer.Contains("if (LoginBakeBlocked && !LoginBakeOverlayActive) return"),
            "character-wait still skips GL before the overlay arms");
        int schedAt = renderer.IndexOf("ScheduleMeshJobs()", StringComparison.Ordinal);
        int overlayHoldAt = renderer.IndexOf("if (LoginBakeOverlayActive) return", StringComparison.Ordinal);
        c.True(schedAt >= 0 && overlayHoldAt > schedAt,
            "overlay hold skips GPU draw only after mesh schedule/upload");
        c.True(renderer.Contains("LodPauseOnStartCompat.KeepUnpaused"),
            "renderer force-unpauses while overlay/blocked so Pause-on-Start cannot freeze bake");
        c.True(renderer.Contains("RemeshStaleLiveTintParent"),
            "walk-away remeshes live-tint parents once FlagBaked children exist");
        c.True(renderer.Contains("if (world.ForceRemesh.Contains(key)) return;"),
            "stale-parent remesh does not requeue every frame while ForceRemesh is already set");
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
        c.True(guard.Contains("OnRenderPin?.Invoke()"),
            "overlay render frames freeze pickup pose so the player never hops");
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
        c.True(driver.Contains("bake.FreezePickupPose()"),
            "pulse re-pins pickup pose every overlay frame, not only the 50ms tick");
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
        c.True(mod.Contains("else if (!renderer.LoginBakeOverlayActive)"),
            "Farseer height enrich does not rebuild on HasDataSet churn during overlay");
        c.True(mod.Contains("LodPauseOnStartCompat.RestoreAfterLoginBake"),
            "skip/not-allowed join restores Pause-on-Start after overlay-time unpause");
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
        c.True(bake.Contains("BatchBakeL0Radius = 2"),
            "overlay visit paint is the visit cell, not a 750-block neighbour disk");
        c.True(bake.Contains("MaxBakePerTick = 32"),
            "login bake paints many captured scouts per overlay tick");
        c.True(bake.Contains("MaxLeftoverBakePerTick = 16"),
            "expire leftover GetColor is 16/tick (research 12→16)");
        c.True(bake.Contains("CollectExpireLeftovers"),
            "expire leftovers are queued, not baked in one tick");
        c.True(bake.Contains("MaxExpireLeftoverKeys"),
            "expire leftover queue is capped on large caches");
        c.Eq(256, LodLoginBake.MaxExpireLeftoverKeys,
            "expire leftover cap is 256 nearest L0");
        c.True(bake.Contains("RequestChunkColumnRing"),
            "login bake grows the streamed ring instead of requesting the full disk at teleport");
        c.True(bake.Contains("SweepRowsPerCall"),
            "login bake sweeps loaded columns a few rows per tick");
        c.True(bake.Contains("MaxBatchBakePerStop = 32"),
            "leftover neighbour bake is small; overlay scouts paint the visit cell");
        c.True(bake.Contains("BakeBatchAtStop"),
            "leftover hop-era neighbour bake helper remains for expire leftovers");
        c.True(bake.Contains("PaintReadyScouts"),
            "login overlay paints every captured scout each tick, not one serial currentKey");
        c.True(bake.Contains("scoutFill.Tick"),
            "login overlay drives staggered scout entities instead of player hops");
        c.True(bake.Contains("PinPickupPose();"),
            "login bake re-pins pickup pose after scout SetChunkColumnVisible");
        c.True(bake.Contains("LodLoginScoutFill"),
            "login bake owns the concurrent scout fill");
        c.True(bake.Contains("GrowRevealAroundSpawn()"),
            "login bake grows a spawn-centered vanilla stream for spawn-solid land");
        c.True(bake.Contains("viewBoost.SpawnStreamRadiusChunks"),
            "spawn vanilla stream is the spawn-solid disk, not a 4 km tessellation");
        c.True(bake.Contains("SweepColumnsAroundSpawn()"),
            "login bake captures loaded columns across the onset disk, not only the current stop");
        c.True(bake.Contains("SpawnSweepEveryTicks"),
            "spawn-disk capture is not every overlay tick (GC)");
        c.True(bake.Contains("SpawnRevealEveryTicks"),
            "spawn reveal ring is throttled so it does not fight 16 scout streams");
        c.True(bake.Contains("BatchBakeRadiusFor"),
            "leftover neighbour bake shrinks past spawn-solid instead of a 12-cell disk");
        c.True(bake.Contains("CollectBatchBakeKeys(primaryKey, stopBakeKeys)"),
            "leftover batch keys fill stopBakeKeys in place");
        c.True(!bake.Contains("batchBakeResult"),
            "in-place CollectBatchBakeKeys does not copy through a second list");
        c.True(bake.Contains("scoutFill.LiveCount}/{LodLoginScoutFill.MaxConcurrent} scouts"),
            "overlay reports live/max scouts so 1/16 stuck is visible");
        int releaseAt = bake.IndexOf("void ReleaseResources(bool success, bool keepResume = false)", StringComparison.Ordinal);
        int nextAt = bake.IndexOf("void LogMayFlagBakedDump()", releaseAt, StringComparison.Ordinal);
        c.True(releaseAt >= 0 && nextAt > releaseAt, "ReleaseResources bounds");
        string release = bake.Substring(releaseAt, nextAt - releaseAt);
        int saveAt = release.IndexOf("SaveResumeSnapshot()", StringComparison.Ordinal);
        int resetAt = release.IndexOf("scoutFill.Reset(capi)", StringComparison.Ordinal);
        int releasedAt = release.IndexOf("released = true", StringComparison.Ordinal);
        c.True(saveAt >= 0 && resetAt >= 0 && saveAt < resetAt,
            "Esc/world-leave resume snapshot includes live scout keys before Reset");
        c.True(saveAt >= 0 && releasedAt >= 0 && saveAt < releasedAt,
            "resume snapshot runs before teardown marks released/Done");
        c.True(bake.Contains("LodPauseOnStartCompat.KeepUnpaused"),
            "login bake force-unpauses while the overlay is up (Pause-on-Start compat)");
        c.True(bake.Contains("LodPauseOnStartCompat.RestoreAfterLoginBake"),
            "login bake restores Pause-on-Start after a successful overlay");
        c.True(!bake.Contains("LodLoginBakePlayerMove.HoldQuiet(entity"),
            "login bake does not HoldQuiet the player onto visit cells");
        c.True(!bake.Contains("void TeleportPlayer"),
            "login bake has no visit-cell TeleportPlayer helper");
        c.True(!bake.Contains("ApplyQuiet(capi"),
            "login bake never ApplyQuiet-hops to a visit cell");
        c.True(bake.Contains("entity.Pos.SetFrom(restorePos)"),
            "login bake pins the exact pickup pose, not visit cells");
        c.True(bake.Contains("PinPickupPose"),
            "login bake re-pins pickup XYZ after scout chunk requests");
        c.True(bake.Contains("pickupX"),
            "login bake stores exact pickup X as a double");
        c.True(bake.Contains("pickupY"),
            "login bake stores exact pickup Y as a double");
        c.True(bake.Contains("pickupZ"),
            "login bake stores exact pickup Z as a double");
        c.True(bake.Contains("pickupYaw") && bake.Contains("pickupPitch"),
            "login bake snapshots pickup yaw/pitch");
        c.True(bake.Contains("ApplyExactPickup"),
            "login bake restores exact pickup doubles if anything hopped");
        c.True(bake.Contains("pickupX, pickupY, pickupZ"),
            "overlay end/Esc/fail writes the original pickup XYZ, not spawn or a chunk origin");

        string scoutFill = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginScoutFill.cs"));
        c.True(scoutFill.Contains("RequestChunkColumnRing"),
            "scouts grow streamed rings without moving the player");
        c.True(!scoutFill.Contains("forceRecapture: true"),
            "scouts do not force-recapture every tick (remesh storm)");
        c.True(scoutFill.Contains("LodPipeline.SweepLaneScout"),
            "scout neighbourhood sweep does not share the spawn-disk row cursor");
        c.True(scoutFill.Contains("RevealGrowPerTick"),
            "scout ring growth is staggered");
        c.True(scoutFill.Contains("Do not bake missing-tex white"),
            "scouts skip GetColor bake when map chunks never arrived");
        c.True(bake.Contains("AllMapChunksLoaded(capi.World.BlockAccessor, primaryKey)"),
            "login bake does not force-paint a stop with missing-tex white when maps are absent");
        c.True(bake.Contains("LodLoginScoutFill.LocalVisitRevealChunks"),
            "visit/scout rings stay local; they do not grow past the silhouette into empty sky");
        c.True(scoutFill.Contains("LocalVisitRevealChunks"),
            "scout grow cap is the local visit neighbourhood");
        c.True(LodLoginScoutFill.LocalVisitRevealChunks < 40,
            "local scout reveal is a neighbourhood, not the ~130-chunk onset disk");
        c.True(bake.Contains("FreezePickupPose"),
            "login bake exposes a public pose freeze for overlay frames");
        c.True(bake.Contains("overlay.OnRenderPin = FreezePickupPose"),
            "login overlay render path pins pickup XYZ every GUI frame");
        c.True(!bake.Contains("void BeginNextStop"),
            "login bake has no leftover hop-era BeginNextStop (visit-cell player stream)");
        c.True(bake.Contains("LodScoutViewerEntity"),
            "login bake comments/uses real scout viewer entities, not tokens");
        c.True(scoutFill.Contains("LodScoutViewerEntity.SpawnAt"),
            "scouts spawn a real viewer entity at the visit cell");
        c.True(scoutFill.Contains("DespawnOne") && scoutFill.Contains("DespawnAll"),
            "scouts despawn each viewer and wipe leftovers on reset");
        c.True(scoutFill.Contains("TryHandoffPaint"),
            "capture hands off to scoutReady paint queue before slot release");
        c.True(scoutFill.Contains("RunSpawnDiskSweep"),
            "spawn-disk column sweep is optional; mesh gate is overlay end only");
        c.True(scoutFill.Contains("WaitForMesh"),
            "near/far band kept for telemetry only");
        c.True(scoutFill.Contains("PartitioningEveryTicks"),
            "pinned scouts re-partition every 8 ticks, not every overlay tick");
        c.Eq(8, LodLoginScoutFill.PartitioningEveryTicks,
            "partition throttle matches the research-branch cadence");
        c.True(scoutFill.Contains("LodVsCompat.TryUpdatePartitioning(viewer)"),
            "HoldViewer partitions through the 1.22.7 reflection helper");
        int meshAt = scoutFill.IndexOf("Phase.Mesh", StringComparison.Ordinal);
        int countMixAt = scoutFill.IndexOf("void CountLiveMix", meshAt, StringComparison.Ordinal);
        c.True(meshAt >= 0 && countMixAt > meshAt, "Mesh phase bounds");
        string meshPhase = scoutFill.Substring(meshAt, countMixAt - meshAt);
        c.True(!meshPhase.Contains("SweepLoadedColumns"),
            "Mesh phase does not recapture neighbourhoods");
        c.True(!meshPhase.Contains("forceRecapture"),
            "Mesh phase has no forceRecapture");
        int paintAt = scoutFill.IndexOf("Phase.Paint", StringComparison.Ordinal);
        c.True(paintAt >= 0 && paintAt < meshAt, "Paint precedes Mesh");
        string paintPhase = scoutFill.Substring(paintAt, meshAt - paintAt);
        c.True(!paintPhase.Contains("SweepLoadedColumns"),
            "Paint phase does not recapture neighbourhoods");
        c.True(scoutFill.Contains("Phase.Paint"),
            "scouts stay until GetColor paint, then near waits mesh / far despawns");
        c.True(scoutFill.Contains("NotifyPainted"),
            "paint completion unblocks the scout slot so the next pending cell can start");
        c.True(scoutFill.Contains("readyScratch"),
            "scout fill reuses the ready-key list each tick");
        c.True(bake.Contains("batchBakeCandidates"),
            "expire leftover candidate list is reused, not allocated per stop");
        c.True(File.ReadAllText(Path.Combine(
                GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodSeasonBake.cs"))
                .Contains("LodBakeScratch.RentColumnMeta"),
            "visit bake pools per-column scratch arrays");
        string bakeScratch = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodBakeScratch.cs"));
        c.True(bakeScratch.Contains("ArrayPool"),
            "column meta arrays come from ArrayPool, not new T[4096] each stop");
        c.True(bakeScratch.Contains("ThreadStatic"),
            "BlockPos scratch is thread-local so GetColor does not allocate per column");
        c.True(bakeScratch.Contains("BeginSectionTextureMeans"),
            "per-section texture-mean cache avoids 8× GetColorWithoutTint per ground layer");
        c.True(bakeScratch.Contains("TryGetSectionGetColor"),
            "per-section GetColor cache reuses climate-tile samples across columns");
        c.True(bakeScratch.Contains("TryGetBlockIdGetColor"),
            "climate-untinted blocks reuse one GetColor per BlockId per section");
        c.True(bakeScratch.Contains("TryGetSeasonTile"),
            "season rel is cached per 16×16 tile during section bake");
        c.True(bakeScratch.Contains("x >> 4"),
            "GetColor cache tile is 16×16 blocks for higher reuse on flat terrain");
        c.True(File.ReadAllText(Path.Combine(
                GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodSeasonBake.cs"))
                .Contains("TryGetSectionTextureMean"),
            "SampleTextureMean hits the per-section BlockId cache");
        c.True(File.ReadAllText(Path.Combine(
                GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodSurfaceMix.cs"))
                .Contains("StackDeterminedByTopOnly"),
            "stack sampler stops after the top when FinishColumnPaint keeps topRgb");
        c.True(File.ReadAllText(Path.Combine(
                GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodSurfaceMix.cs"))
                .Contains("NeedsTextureMean"),
            "texture mean is skipped outside deep-winter camouflage");
        c.True(File.Exists(Path.Combine(
                GameAssemblies.RepoRoot, "docs", "plans", "login-bake-efficiency.md")),
            "efficiency plan (citations + A/B tiers) ships in-repo");
        c.True(scoutFill.Contains("TakeNextPending"),
            "overlay scouts fill pending FIFO without heldNear starvation");
        c.True(scoutFill.Contains("FlushHeldToPending"),
            "legacy heldNear/heldFar queues drain into pending each tick");
        c.True(scoutFill.Contains("WaitChunksForceCaptureTicks"),
            "WaitChunks enters Capture on a hard deadline, not only when all map chunks load");
        c.True(scoutFill.Contains("WaitChunksRotateTicks"),
            "WaitChunks pile-up rotates scouts into paint handoff");
        c.True(scoutFill.Contains("MaxFarWaitChunksLive"),
            "far-ring WaitChunks capped so near paint is not starved");
        c.True(scoutFill.Contains("WaitChunksHandoffTicks"),
            "WaitChunks tries paint handoff before forcing Capture");
        c.True(scoutFill.Contains("WaitChunksRotateMinLive"),
            "WaitChunks rotation triggers at partial grid load, not all 16");
        c.True(scoutFill.Contains("captureStall"),
            "Capture timeout releases slot instead of parking until maxWait");
        c.Eq(4, LodLoginScoutFill.MaxNearWaitChunksLive,
            "at most 4 spawn-disk scouts park in WaitChunks concurrently");
        c.Eq(6, LodLoginScoutFill.MaxFarWaitChunksLive,
            "at most 6 far scouts park in WaitChunks while paint queue drains");
        c.Eq(8, LodLoginScoutFill.WaitChunksForceCaptureTicks,
            "every scout enters Capture at ~400ms even without full map");
        c.Eq(16, LodLoginScoutFill.WaitChunksRotateTicks,
            "WaitChunks pile-up rotates into paint at ~800ms");
        c.Eq(24, LodLoginScoutFill.MaxWaitTicks,
            "chunk wait safety cap ~1.2s at 50ms pulse");
        c.Eq(6, LodLoginScoutFill.MaxCaptureWaitTicks,
            "capture hands off or stalls out at ~300ms");
        c.True(scoutFill.Contains("TryResidentPaintHandoff"),
            "HasDataSet revisits hand off to paint without long WaitChunks");
        c.True(scoutFill.Contains("SetPaintStarving"),
            "paint queue starvation tightens scout wait budgets");
        c.Eq(64, LodLoginScoutFill.ResidentFastHandoffCols,
            "resident sections with full footprint skip WaitChunks");
        c.Eq(4, LodLoginScoutFill.PaintStarveForceCaptureTicks,
            "starving scouts force Capture at ~200ms");
        c.Eq(4, LodLoginScoutFill.MaxWaitHotKeyCooldown,
            "maxWait hot keys defer respawn for 4 ticks");
        c.Eq(1, LodLoginScoutFill.ChunkPressureMinLive,
            "chunkPressure engages on any paint starve with live scouts");
        c.True(scoutFill.Contains("IsColdNearVisit"),
            "cold-near annulus keys detected outside stream hold inside spawn disk");
        c.True(scoutFill.Contains("CanEnterCapture"),
            "Capture gated on map/resident readiness while paint starves");
        c.True(scoutFill.Contains("WarmRingL0CellEstimate"),
            "warm-ring L0 estimate from stream-hold radius");
        c.True(scoutFill.Contains("CaptureStallCooldownTicks"),
            "captureStall uses escalating cooldown to break hot-loop");
        c.Eq(8, LodLoginScoutFill.MaxColdNearLiveWhenStarving(16),
            "at most half the scout fleet on cold-near keys while paint starves");
        c.True(scoutFill.Contains("OrderVisitKeysByResidency"),
            "visit plan prefers resident map chunks (A4)");
        c.True(File.ReadAllText(Path.Combine(
                GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodScoutSeqDiag.cs"))
                .Contains("warm-ring-probe"),
            "warm-ring telemetry proves cliff geometry near finished~358");
        c.True(File.ReadAllText(Path.Combine(
                GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodScoutSeqDiag.cs"))
                .Contains("stall-forensics"),
            "stall forensics logs distance, loaded chunks, reveal, host on captureStall/maxWait");
        c.True(File.ReadAllText(Path.Combine(
                GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodScoutSeqDiag.cs"))
                .Contains("stalled-live-probe"),
            "stalled-live-probe aggregates live scouts in cliff band when paint starves");
        c.True(File.ReadAllText(Path.Combine(
                GameAssemblies.RepoRoot, "docs", "plans", "login-bake-walltime-1033.md"))
                .Contains("can't enter the next huge square"),
            "walltime plan documents user hypothesis verdict");
        c.True(File.ReadAllText(Path.Combine(
                GameAssemblies.RepoRoot, "docs", "plans", "login-bake-walltime-1033.md"))
                .Contains("Scouts primary; hop-unlock only if proven"),
            "walltime plan locks scouts as primary bake workers");
        c.True(File.ReadAllText(Path.Combine(
                GameAssemblies.RepoRoot, "docs", "plans", "login-bake-walltime-1033.md"))
                .Contains("hop-unlock pump"),
            "Plan C is stream unlock pump only, not hop-sweep replacement");
        c.True(File.ReadAllText(Path.Combine(
                GameAssemblies.RepoRoot, "docs", "plans", "login-bake-walltime-1033.md"))
                .Contains("1.0.39 hop-unlock pump"),
            "walltime plan documents shipped hop-unlock in 1.0.39");
        c.True(bake.Contains("LodLoginHopUnlock"),
            "overlay integrates hop-unlock pump for cold annulus");
        c.True(bake.Contains("hopUnlock.TryFirstHop"),
            "first hop-unlock on all-WaitChunks stall signature");
        c.True(File.ReadAllText(Path.Combine(
                GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginHopUnlock.cs"))
                .Contains("TryPickNearAnnulusTarget"),
            "hop-unlock prefers near-cliff annulus keys not outer disk");
        c.True(File.ReadAllText(Path.Combine(
                GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginHopUnlock.cs"))
                .Contains("failedKeyBan"),
            "hop-unlock bans keys that stay loaded=0 after dwell");
        c.True(File.ReadAllText(Path.Combine(
                GameAssemblies.RepoRoot, "docs", "plans", "login-bake-walltime-1033.md"))
                .Contains("1.0.41 near-cliff annulus"),
            "walltime plan documents 1.0.41 annulus fix");
        c.True(File.ReadAllText(Path.Combine(
                GameAssemblies.RepoRoot, "docs", "plans", "login-bake-walltime-1033.md"))
                .Contains("1.0.43 stream spawn-solid"),
            "walltime plan documents 1.0.43 stream-1024 residency fix");
        c.Eq(1024, LodLoginBakeViewBoost.SweepStreamViewDistanceBlocks,
            "overlay vanilla stream covers spawn-solid so cliff L0s stay resident");
        c.True(LodLoginBakeViewBoost.IsSweepHoldValue(1024),
            "1024 stream hold is never restored as the player slider");
        c.True(bake.Contains("hopCameraPos"),
            "hop CameraPos follows unlock so client stream is not pinned at pickup");
        c.True(File.ReadAllText(Path.Combine(
                GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginHopUnlock.cs"))
                .Contains("priority: true"),
            "unlock RequestUp is priority so it is not the refused 17th hold");
        c.Eq(48, LodLoginHopUnlock.FailedKeyDwellTicks,
            "failed hop targets banned after dwell ticks");
        c.True(File.ReadAllText(Path.Combine(
                GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginHopUnlock.cs"))
                .Contains("PumpUnlockResidency"),
            "hop-unlock forces map-chunk residency via scout-host path");
        c.True(File.ReadAllText(Path.Combine(
                GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakePlayerMove.cs"))
                .Contains("RequestL0MapChunksVisible"),
            "unlock marks all four L0 map columns visible");
        c.True(bake.Contains("PumpUnlockResidency"),
            "overlay pumps forced residency each tick while hop active");
        c.Eq(128, LodLoginHopUnlock.MaxResidencyForceTicks,
            "hop holds unlock up to 128 ticks while forcing residency");
        c.True(File.ReadAllText(Path.Combine(
                GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodScoutSeqDiag.cs"))
                .Contains("streamViewBlocks"),
            "hop residency probe logs overlay stream VD vs player/camera");
        c.True(File.ReadAllText(Path.Combine(
                GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodScoutSeqDiag.cs"))
                .Contains("hop-unlock"),
            "hop-unlock telemetry for runId 1039 playtest");
        c.Eq(4, LodLoginScoutFill.MaxColdNearWaitChunksWhenStarving,
            "several cold-near WaitChunks streamers now that spawn-solid is inside vanilla VD");
        c.True(scoutFill.Contains("PendingPickScore"),
            "pending pick scores chunk residency before cold rim keys");
        c.True(scoutFill.Contains("spawnCooldown"),
            "maxWait keys enter hot-key cooldown instead of immediate respawn");
        c.True(bakeScratch.Contains("BeginOverlayGetColorCache"),
            "overlay-wide GetColor dedup cache spans L0 sections");
        c.True(bakeScratch.Contains("TryGetOverlayGetColor"),
            "SampleVanillaColor checks overlay cache before section cache");
        c.True(bake.Contains("resumePendingScratch"),
            "resume snapshot reuses pending scratch instead of new List each save");
        c.True(bake.Contains("paintStarveTicks"),
            "overlay detects empty paint queue while scouts live");
        c.True(File.ReadAllText(Path.Combine(
                GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodScoutSeqDiag.cs"))
                .Contains("paintStarveTicks"),
            "scout-budget telemetry includes paint starvation watchdog");
        c.True(scoutFill.Contains("RequestUpRetryTicks"),
            "scouts retry KeepLoaded if the server refused an Up at the hold cap");
        c.True(bake.Contains("scoutFill.HeldCount"),
            "overlay does not finish while far/near keys sit in the mixed-fill hold queues");
        c.True(bake.Contains("scoutFill.IsLive(key)"),
            "failed GetColor paint retries only while the scout is still live");
        string terrain = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodTerrainRenderer.cs"));
        c.True(terrain.Contains("public bool HasDrawableMesh"),
            "LOD renderer exposes drawable-mesh wait so scouts do not despawn on a hole");
        c.True(terrain.Contains("public bool HasEmptyMeshClaim"),
            "LOD renderer exposes sticky empty claims so Mesh wait can release the slot");
        c.True(terrain.Contains("bool parentHasMesh = HasDrawableMesh(parentKey)"),
            "parent coverage uses drawable meshes, not sticky emptyMeshKeys");
        c.True(terrain.Contains("HasDrawableMesh(parent), AllChildrenCovered(parent)"),
            "eviction PreferParentCoverage ignores empty-mesh claims");
        c.True(terrain.Contains("!HasDrawableMesh(nk)"),
            "neighbour mesh request retries past sticky empty claims");
        int pruneAt = terrain.IndexOf("void PruneRenderDirty()", StringComparison.Ordinal);
        c.True(pruneAt >= 0, "renderer prunes RenderDirty");
        string prune = terrain.Substring(pruneAt, Math.Min(1800, terrain.Length - pruneAt));
        c.True(prune.Contains("!HasAnyMesh(key)"),
            "RenderDirty prune still treats empty claims as in-flight so it does not drop their jobs");
        c.True(scoutFill.Contains("RequestUp(key, scout.Cx, scout.Cz, radius, dim, x, y, z)"),
            "client sends visit-cell XYZ so the server spawns the viewer on that column");
        c.Eq(16, LodLoginScoutFill.MaxConcurrent, "all 16 scout slots must work in parallel");
        c.Eq(8, LodLoginScoutFill.MaxNearConcurrent,
            "half the slots stay on spawn-solid mesh wait");
        c.Eq(8, LodLoginScoutFill.MaxFarConcurrent,
            "half the slots paint the far ring so overlay % moves during near mesh waits");
        c.Eq(LodLoginScoutFill.MaxNearConcurrent + LodLoginScoutFill.MaxFarConcurrent,
            LodLoginScoutFill.MaxConcurrent,
            "near+far caps fill all 16 slots");
        c.Eq(4, LodLoginScoutFill.LocalVisitRevealChunks,
            "scouts stream a local neighbourhood around visit cells");
        c.Eq(2, LodLoginScoutFill.FarRevealChunks,
            "far KeepLoaded is the L0 footprint, not an 8-chunk tessellation disk");
        c.Eq(LodLoginScoutFill.NearRevealChunks, LodScoutHostSystem.MaxHoldRadiusChunks,
            "server KeepLoaded radius matches the near scout neighbourhood");
        c.Eq(18, LodScoutHostSystem.MaxConcurrentHolds,
            "server hold cap is 16 scouts plus residency pump plus spare");
        c.Eq(48, LodScoutHostSystem.MaxForceSendPerTick,
            "ForceSend is budgeted so 16 KeepLoaded rings do not dump in one tick");
        c.Eq(32, LodScoutHostSystem.MaxPendingUps,
            "refused KeepLoaded Ups queue instead of going silent");

        string scoutViewer = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodScoutViewerEntity.cs"));
        c.True(scoutViewer.Contains("class LodScoutViewerEntity : Entity"),
            "scout viewer is a real Vintage Story Entity, not a token");
        c.True(!scoutViewer.Contains("class LodScoutViewerEntity : EntityPlayer"),
            "scout viewer is not EntityPlayer (that type is tied to IPlayer; VS has no dummy player)");
        c.True(scoutViewer.Contains("AllowOutsideLoadedRange => true"),
            "scout viewers may sit on far columns before those columns are loaded");
        c.True(scoutViewer.Contains("StoreWithChunk => false"),
            "scout viewers are not saved with the chunk (players are stored separately)");
        c.True(scoutViewer.Contains("AlwaysActive"),
            "scout viewers stay Active far from the real player");
        c.True(scoutViewer.Contains("ShouldDespawn => !Alive"),
            "Die() can actually remove scout viewers; they do not stick after teardown");
        c.True(scoutViewer.Contains("LodVsCompat.TryUpdatePartitioning"),
            "1.22.7: UpdatePartitioning is invoked through reflection");
        c.True(scoutViewer.Contains("LodVsCompat.TryIndexLoadedEntity"),
            "1.22.7: LoadedEntities is not assumed on IWorldAccessor");
        c.True(!scoutViewer.Contains("api.World.LoadedEntities"),
            "scout spawn does not touch IWorldAccessor.LoadedEntities (missing on 1.22.7)");
        c.True(!scoutViewer.Contains("viewer.UpdatePartitioning()"),
            "scout spawn does not call Entity.UpdatePartitioning directly (missing on 1.22.7)");
        c.True(scoutViewer.Contains("SpawnPriorityEntity") && scoutViewer.Contains("SpawnEntity"),
            "viewers spawn through the world entity APIs");
        c.True(scoutViewer.Contains("IServerWorldAccessor") && scoutViewer.Contains("DespawnEntity"),
            "server teardown uses DespawnEntity, not only a client LoadedEntities.Remove");
        c.True(scoutViewer.Contains("despawnScratch"),
            "teardown reuses the scout despawn list");
        c.True(scoutFill.Contains("LodScoutViewerEntity.SpawnAt(capi"),
            "client fill spawns a viewer at the visit cell");

        string scoutHost = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Net", "LodScoutHostSystem.cs"));
        c.True(scoutHost.Contains("RegisterEntity(LodScoutViewerEntity.ClassName"),
            "scout viewer class is registered on client and server");
        c.True(scoutHost.Contains("LodScoutViewerEntity.SpawnAt(sapi"),
            "server host spawns a viewer entity at the visit cell, not only KeepLoaded");
        c.True(scoutHost.Contains("KeepLoaded = true"),
            "server keeps scout columns loaded like a player standing there");
        c.True(scoutHost.Contains("ForceSendChunkColumn"),
            "server force-sends scout columns to the real player (out of range)");
        c.True(scoutHost.Contains("UnloadChunkColumn"),
            "server unloads scout columns on despawn so far centers do not linger");
        c.True(scoutHost.Contains("LodScoutViewerEntity.DespawnAll"),
            "teardown despawns every scout viewer entity");
        c.True(scoutHost.Contains("DespawnEntity"),
            "server teardown DespawnEntity-s leftover viewers");
        c.True(scoutHost.Contains("> 160"),
            "server rejects KeepLoaded anchors beyond the onset disk");
        c.True(scoutHost.Contains("holds.Count >= MaxConcurrentHolds"),
            "server caps concurrent scout holds so a client cannot pin the world");
        c.True(scoutHost.Contains("TryEvictFarthestHold"),
            "priority RequestUp evicts a far hold instead of queueing forever at cap");
        c.True(scoutHost.Contains("EnqueuePendingUp"),
            "server queues extra Ups instead of dropping them at the hold cap");
        c.True(scoutHost.Contains("MaxForceSendPerTick"),
            "server ForceSend is per-tick budgeted");
        c.True(scoutHost.Contains("Math.Clamp(msg.Radius, 1, MaxHoldRadiusChunks)"),
            "server KeepLoaded radius is the local neighbourhood, not the onset disk");
        c.True(scoutHost.Contains("LodVsCompat.TryGetLoadedEntities"),
            "server teardown walks LoadedEntities through the 1.22.7-safe helper");
        string vsCompat = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "LodVsCompat.cs"));
        c.True(vsCompat.Contains("GetMethod(\"UpdatePartitioning\""),
            "UpdatePartitioning is resolved by reflection for 1.22.7");
        c.True(vsCompat.Contains("IServerWorldAccessor"),
            "LoadedEntities is read from IServerWorldAccessor, not IWorldAccessor");
        string scoutJson = Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "assets", "distantvistas", "entities", "scoutviewer.json");
        c.True(File.Exists(scoutJson), "scout viewer entity json is packaged");
        c.True(File.ReadAllText(scoutJson).Contains("\"class\": \"LodScoutViewer\""),
            "entity json class matches RegisterEntity");

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
        c.True(bake.Contains("LodLoginBakePlayerMove.ApplyExactPickup"),
            "leftover hops snap back to exact pickup doubles");
        c.True(bake.Contains("SpawnRestoreRadius"),
            "login bake re-requests spawn columns at real view radius");
        c.True(bake.Contains("finally") && bake.Contains("RestorePlayerPose(requestChunks: success)"),
            "success, Esc, fail, and world-leave all restore pickup pose");

        string playerMove = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakePlayerMove.cs"));
        c.True(playerMove.Contains("void ApplyExactPickup"),
            "player move has an exact-pickup restore");
        c.True(playerMove.Contains("void WriteExactPickup"),
            "exact pickup writes Pos and ServerPos");
        c.True(playerMove.Contains("ServerPos.SetPos(x, y, z)"),
            "ServerPos gets the same exact doubles as Pos (engine copy-back cannot keep a hop)");
        c.True(playerMove.Contains("entity.Pos.SetPos(x, y, z)"),
            "Pos is written with the original doubles");
        int writeAt = playerMove.IndexOf("void WriteExactPickup", StringComparison.Ordinal);
        int holdAt = playerMove.IndexOf("void HoldQuiet", writeAt, StringComparison.Ordinal);
        c.True(writeAt >= 0 && holdAt > writeAt, "WriteExactPickup bounds");
        string writeExact = playerMove.Substring(writeAt, holdAt - writeAt);
        c.True(!writeExact.Contains("Math.Floor"),
            "exact pickup does not Floor XYZ to a chunk origin");
        c.True(!writeExact.Contains("VisitPosition"),
            "exact pickup does not snap to a visit-cell column");
        c.True(!writeExact.Contains("(int)x") && !writeExact.Contains("(int)y") && !writeExact.Contains("(int)z"),
            "exact pickup does not round XYZ to ints");

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
        c.Eq(9, LodSurfaceMix.PaintRevision,
            "paint revision 9: canopy GetColor at crown Y, low-ground mist / scout-fill era");
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
        c.True(gate.Contains("resumeMisses > 0 || unfilledGaps > 0"),
            "dropped Esc resume with leftover holes names deferred counts, not complete");
        c.True(gate.Contains("empty canvas needs bootstrap sweep"),
            "gate runs bootstrap on empty visited canvas");
        c.True(gate.Contains("still incomplete"),
            "gate runs when audit finds misses and no in-window complete");
        c.True(gate.Contains("skip re-canvas"),
            "in-window complete marker skips full re-canvas when leftovers are small");
        c.True(gate.Contains("in-window skip blocked"),
            "large FindMisses / unfilled gaps force a scout fill instead of claiming complete");
        c.True(gate.Contains("frontier drip"),
            "small leftover skip names deferred regions instead of implying zero gaps");
        c.True(LodLoginSweepGate.AllowsInWindowSkip(0, 0),
            "zero misses and gaps may skip");
        c.True(LodLoginSweepGate.AllowsInWindowSkip(
                LodLoginSweepGate.MaxSkipMisses, LodLoginSweepGate.MaxSkipUnfilledGaps),
            "at-threshold leftovers stay frontier drip");
        c.False(LodLoginSweepGate.AllowsInWindowSkip(LodLoginSweepGate.MaxSkipMisses + 1, 0),
            "33 FindMisses blocks in-window skip");
        c.False(LodLoginSweepGate.AllowsInWindowSkip(0, LodLoginSweepGate.MaxSkipUnfilledGaps + 1),
            "33 unfilled gaps block in-window skip");
        c.Eq(32, LodLoginSweepGate.MaxSkipMisses, "skip miss threshold is 32");
        c.Eq(32, LodLoginSweepGate.MaxSkipUnfilledGaps, "skip gap threshold is 32");
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
        c.True(mod.Contains("explore pending"),
            "skip path logs explorePending so DiscoverOnly stall is visible");
        c.True(mod.Contains("Do not ExploreBake.Clear()"),
            "in-window skip does not wipe load-queued explore bakes");
        c.True(mod.Contains("LastUnfilledGaps"),
            "gate sees renderer unfilled-gap count");
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
            "visit-cell neighbourhood recaptures streamed columns");
        c.True(bake.Contains("forceRecapture: false"),
            "spawn-disk sweep does not force-recapture every loaded column");
        c.True(bake.Contains("LodPipeline.SweepLaneSpawn"),
            "spawn-disk capture keeps its own row cursor so scout rings cannot skip spawn-local rows");
        c.True(bake.Contains("LodPipeline.SweepLaneVisit"),
            "visit-cell recapture is a third lane, not a reset of the spawn disk");
        c.True(!bake.Contains("InvalidateGpuMesh"),
            "login bake does not drop GPU meshes before swap-in");
        c.True(bake.Contains("InvalidateMipAncestors(l0Key)"),
            "visit paint remeshes parents without disposing the live mesh");
        c.True(bake.Contains("visitBakeChanged"),
            "visit bake treats FlagBaked RGB deltas as a completed stop");

        string season = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodSeasonBake.cs"));
        c.True(season.Contains("entry.Color = baked"),
            "visit bake overwrites FlagBaked palette RGB in place");
        c.True(season.Contains("block.GetColor(capi, LodBakeScratch.Pos(x, y, z))"),
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
            "login visit Farseer/visit baseline stays 750 blocks");
        c.True(view.Contains("SweepStreamViewDistanceBlocks = 1024"),
            "login overlay vanilla stream is spawn-solid 1024");

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
        c.Eq(420.0, LodLoginSweepTiming.TargetMaxSec, "sweep target max seconds (~7 min)");
        c.Eq(420.0, LodLoginSweepTiming.BootstrapTargetMaxSec, "bootstrap target max seconds");
        c.Eq(90.0, LodLoginSweepTiming.RetryTargetSec, "retry pass wall seconds");
        c.Eq(0.25, LodLoginSweepTiming.InitialSecPerStop, "fallback per-stop with viewer scouts (no hop cost)");
        c.Eq(0.02, LodLoginSweepTiming.MeasuredMinSecPerStop,
            "measured parallel rate may be faster than hop-era 0.25");
        LodLoginSweepTiming.SetMachineSecPerStop(0.04);
        c.Near(0.04, LodLoginSweepTiming.MachineSecPerStop, 1e-9,
            "do not clamp live scout rate up to 0.25s/stop");
        LodLoginSweepTiming.SetMachineSecPerStop(LodLoginSweepTiming.InitialSecPerStop);
        c.Eq(1680, LodLoginSweepTiming.VisitStopBudget(0.04, LodLoginSweepTiming.TargetMaxSec),
            "faster than fallback still plans the 1680 ceiling");
        var batch = new LodLoginSweepTiming();
        batch.BeginSession(0.25);
        c.Near(0.25, batch.SecondsPerStop, 1e-9, "seeded ETA before any painted stop");
        batch.NoteFinished(24);
        c.Eq(24, batch.SampleCount, "a 24-scout PaintReadyScouts tick counts 24 stops, not 1");
        c.True(batch.SecondsPerStop < 0.05, "immediate batch finish is not 0.25s per stop");
        c.True(batch.EstimateRemainingSec(24, 1680) < 1680 * 0.1,
            "ETA after a parallel batch does not assume 0.25s/stop");
        c.Eq(LodLoginBakeViewBoost.SweepVisitRadiusBlocks, LodLoginSweepBootstrap.EmptyCanvasBootstrapRadiusBlocks,
            "bootstrap disk is Farseer onset at the 750 hold (4.5× + 700), not a 288 km sparse probe");
        c.Eq(
            (int)Math.Ceiling(LodLoginBakeViewBoost.SweepVisitRadiusBlocks / (double)LodSection.SectionBlocks),
            LodLoginSweepBootstrap.BootstrapCellRadius(),
            "bootstrap cell radius matches the onset disk");
        c.Eq(4075, LodLoginBakeViewBoost.SweepVisitRadiusBlocks,
            "750-block hold × 4.5 + 700 is 4075 blocks (Farseer onset, not a void band)");
        c.Eq(16384, LodLoginSweepBootstrap.MaxBootstrapClassifyCells,
            "classify ceiling covers the ~12k L0 onset disk");
        c.Eq(4, LodLoginScoutFill.LocalVisitRevealChunks,
            "scouts stream a local neighbourhood around visit cells");
        c.Eq(1024, LodLoginSweepBootstrap.SpawnPriorityRadiusBlocks,
            "bootstrap visits a 1024-block spawn neighbourhood before the rim");
        c.Eq(1024.0, LodLoginBake.SpawnSolidRadiusBlocks,
            "overlay waits for drawable meshes inside 1024 blocks of spawn");
        c.Eq(90.0, LodLoginBake.SpawnReadyTimeoutSec,
            "spawn-solid wait is 90s, not a 3s frame-time timeout");
        c.Eq(0.75f, LodLoginBake.FarReadyHorizonScale,
            "far canvas is ready at 75% of Farseer-onset radius");
        c.Eq(480, LodLoginSweepTiming.MinVisitStops, "first-pass floor densifies the disk");
        c.Eq(1680, LodLoginSweepTiming.MaxVisitStops, "first-pass ceiling for concurrent scouts in the 7 min wall");
        c.Eq(36, LodLoginSweepTiming.MinRetryStops, "retry floor stays shorter than first pass");
        c.Eq(96, LodLoginSweepTiming.MaxRetryStops, "retry ceiling matches retry wall at 1s/stop");
        c.Eq(1680, LodLoginSweepTiming.VisitStopBudget(0.25, LodLoginSweepTiming.TargetMaxSec),
            "0.25s/stop plans 1680 first-pass stops inside the 7 min wall");
        c.Eq(840, LodLoginSweepTiming.VisitStopBudget(0.5, LodLoginSweepTiming.TargetMaxSec),
            "0.5s/stop plans 840 first-pass stops inside the 7 min wall");
        c.Eq(420, LodLoginSweepTiming.VisitStopBudget(1.0, LodLoginSweepTiming.TargetMaxSec),
            "1s/stop plans 420 first-pass stops");
        c.Eq(480, LodLoginSweepTiming.VisitStopBudget(2.0, LodLoginSweepTiming.TargetMaxSec),
            "2s/stop clamps to MinVisitStops 480");
        c.Eq(480, LodLoginSweepTiming.VisitStopBudget(3.6, LodLoginSweepTiming.TargetMaxSec),
            "3.6s/stop clamps to MinVisitStops 480");
        c.Eq(36, LodLoginSweepTiming.RetryStopBudget(3.6),
            "3.6s/stop retry clamps to MinRetryStops 36");
        c.Eq(1680, LodLoginSweepBootstrap.BootstrapMaxVisitStops,
            "bootstrap visit cap targets ~7 min at fallback 0.25s/stop");
        c.Eq(1680, LodLoginSweepBootstrap.RevisitMaxVisitStops,
            "revisit visit cap targets ~7 min at fallback 0.25s/stop");
        c.Eq(96, LodLoginSweepBootstrap.RetryMaxVisitStops,
            "retry visit cap hits MaxRetryStops at fallback 0.5s/stop");
        c.Eq(96, LodLoginSweepTiming.MaxRetryStops,
            "retry ceiling is 96");
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
            "bootstrap spatially subsamples the onset disk");
        c.True(bootstrap.Contains("SpawnPriorityRadiusBlocks"),
            "bootstrap spends visit budget on spawn before the Farseer rim");
        c.True(bootstrap.Contains("(max * 2) / 3"),
            "bootstrap spends about two thirds of the visit budget near spawn");
        c.True(bootstrap.Contains("bands - b"),
            "outer visit bands prefer nearer cells over the silhouette");
        c.True(bootstrap.Contains("OrderVisitKeysFromCenter"),
            "bootstrap queues visit keys near-to-far from spawn, not raw key order");
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
        c.True(bake.Contains("CountMissingSpawnDrawable"),
            "login overlay waits until spawn-local L0 has drawable meshes");
        c.True(bake.Contains("SpawnReadyTimeoutSec"),
            "login overlay does not treat a 3s frame-time settle as complete");
        c.True(!bake.Contains("StabilizeTimeoutSec = 3.0"),
            "3s stabilize timeout is gone -- that was the early snapshot");
        int drainAt = bake.IndexOf("void BeginDraining()", StringComparison.Ordinal);
        int stabAt = bake.IndexOf("void BeginStabilizing()", drainAt, StringComparison.Ordinal);
        c.True(drainAt >= 0 && stabAt > drainAt, "BeginDraining bounds");
        c.True(!bake.Substring(drainAt, stabAt - drainAt).Contains("LoginBakeOverlayActive = false"),
            "drain keeps the splash up while meshes upload");
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

    static void BootstrapSpawnFirst(Check c)
    {
        const int cx = 50;
        const int cz = 50;
        var keys = new List<long>();
        for (int sz = 20; sz <= 80; sz += 2)
        {
            for (int sx = 20; sx <= 80; sx += 2)
                keys.Add(LodWorld.SectionKey(0, sx, sz));
        }

        List<long> picked = LodLoginSweepBootstrap.BudgetBootstrapVisitStops(keys, cx, cz, 80);
        c.Eq(80, picked.Count, "bootstrap still respects the visit-stop budget");
        c.True(picked.Contains(LodWorld.SectionKey(0, cx, cz)),
            "spawn L0 is in the visit list so coverage is centered on the player");

        long first = picked[0];
        long last = picked[picked.Count - 1];
        long firstD = DistSq(first, cx, cz);
        long lastD = DistSq(last, cx, cz);
        c.True(firstD <= lastD,
            "visit queue is near-to-far from spawn, not raw key order (that offset the disk)");

        int innerCells = (int)Math.Ceiling(
            LodLoginSweepBootstrap.SpawnPriorityRadiusBlocks / (double)LodSection.SectionBlocks);
        long innerRsq = (long)innerCells * innerCells;
        int innerPicked = 0;
        foreach (long key in picked)
        {
            if (DistSq(key, cx, cz) <= innerRsq) innerPicked++;
        }
        c.True(innerPicked >= 20,
            "a chunk of the visit budget stays in the spawn neighbourhood");
        c.True(innerPicked * 2 >= picked.Count,
            "at least half the subsampled stops stay in the spawn neighbourhood");
    }

    static long DistSq(long key, int centerSx, int centerSz)
    {
        int dx = LodWorld.KeySx(key) - centerSx;
        int dz = LodWorld.KeySz(key) - centerSz;
        return (long)dx * dx + (long)dz * dz;
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
        c.True(onset.Contains("MaskRebuildMinMs"),
            "visit-mask does not full-rebuild on every HasDataSet stamp during overlay");
        c.True(onset.Contains("FarseerOnsetScaleForMeshedRim"),
            "Farseer onset uniforms pull to the meshed rim when FlagBaked lags the silhouette");

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
            "visit/Farseer baseline stays 750 so the FlagBaked disk remains 4075");
        c.Eq(1024, LodLoginBakeViewBoost.SweepStreamViewDistanceBlocks,
            "overlay writes vanilla view to spawn-solid 1024 then restores");
        c.Eq(
            (int)Math.Ceiling(LodCoveragePolicy.HorizonDrawDistance(
                LodLoginBakeViewBoost.SweepBoostViewDistanceBlocks)),
            LodLoginBakeViewBoost.SweepVisitRadiusBlocks,
            "visit disk reaches Farseer onset (hold × HorizonDrawScale + pad)");
        c.True(LodLoginBakeViewBoost.SweepVisitRadiusBlocks > LodLoginBakeViewBoost.SweepBoostViewDistanceBlocks,
            "visit radius is wider than the thin graphics hold");
        c.True(viewBoost.Contains("SweepVisitRadiusBlocks"),
            "ChunkSweepRadiusChunks uses SweepVisitRadiusBlocks toward onset");
        c.True(viewBoost.Contains("SweepStreamViewDistanceBlocks"),
            "vanilla stream around the player is the spawn-solid 1024 disk");
        c.False(viewBoost.Contains("Math.Min(vd, SweepBoostViewDistanceBlocks)"),
            "visit/scout clamp is not a leftover 750 Math.Min");
        c.True(viewBoost.Contains("Stream to Farseer onset + 700"),
            "scout ring clamp still uses the FlagBaked onset disk");
        c.True(viewBoost.Contains("renderer.OverdrawStart = savedOverdrawStart"),
            "reassert restores overlay overdraw, not only the slider");
        c.Eq(1000, LodLoginBakeViewBoost.LegacySweepHoldBlocks,
            "old 1000-block hold is leftover, never a restore target");
        c.True(LodLoginBakeViewBoost.IsSweepHoldValue(750), "750 is the visit baseline hold");
        c.True(LodLoginBakeViewBoost.IsSweepHoldValue(1024), "1024 is the overlay stream hold");
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
        c.True(viewBoost.Contains("SweepStreamViewDistanceBlocks"),
            "boost resolve uses spawn-solid 1024 for vanilla stream, 750 for visit disk");
        c.True(viewBoost.Contains("FarViewDistanceCap"),
            "view boost clears DV far cap during sweep");
        c.True(viewBoost.Contains("ApplyZFar"),
            "view boost refreshes camera z-far after far-cap change");

        string frontier = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodFrontierScout.cs"));
        c.Eq(24, LodFrontierScout.MaxExplorePendingYield,
            "frontier yield matches 16-scout bake parallelism, not 4");
        c.False(frontier.Contains("farCap < maxR"),
            "frontier scout does not shrink the fill ring to EffectiveFarDistance");
        c.True(frontier.Contains("HorizonDrawDistance(vd)"),
            "frontier scout aims at Farseer onset + 700 after overlay");
        c.True(!frontier.Contains("ApplyQuiet"),
            "frontier scout never ApplyQuiet-hops the player");
        c.True(!frontier.Contains("HoldQuiet"),
            "frontier scout never HoldQuiet-snaps to visit cells");
        c.True(!frontier.Contains("HopHold"),
            "frontier scout has no hop-hold phase");
        c.True(!frontier.Contains("allowHop"),
            "frontier scout has no hop permission flag");
        string modFrontier = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "DistantVistasModSystem.cs"));
        c.True(modFrontier.Contains("frontierScout?.Tick(capi, pipeline, renderer);"),
            "post-login frontier ticks without a hop flag");
        c.True(!modFrontier.Contains("frontierScout?.Tick(capi, pipeline, renderer, LoginVisitSweepAllowedHere())"),
            "post-login frontier is not passed allowHop");
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

    static void PostGetColorSimd(Check c)
    {
        string simdPath = Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodRgbSimd.cs");
        c.True(File.Exists(simdPath), "LodRgbSimd.cs ships (post-GetColor SIMD)");
        string simd = File.ReadAllText(simdPath);
        c.True(simd.Contains("Vector256"), "SIMD uses Vector256");
        c.True(simd.Contains("Avx2"), "SIMD uses AVX2 when the CPU has it");
        c.True(simd.Contains("Vector128"), "SIMD falls back to portable Vector128 (SSE2/NEON)");
        c.True(simd.Contains("Vector.IsHardwareAccelerated"),
            "SIMD consults System.Numerics.Vector.IsHardwareAccelerated");
        c.True(simd.Contains("Vector.Divide") && simd.Contains("Vector<int>"),
            "QuantizeSpan uses System.Numerics.Vector<int> when AVX2 is absent");
        c.True(simd.Contains("BlurLandOnceScalar"),
            "scalar BlurLand kernel remains the bit-identical reference");
        c.True(simd.Contains("QuantizePacked") && simd.Contains("QuantizeSpan"),
            "quantize is vectorized on packed RGB buffers");
        c.True(simd.Contains("UnpackPlanes") && simd.Contains("PackPlanes"),
            "RGB pack/unpack runs over sampled color planes");
        c.True(!simd.Contains("GetColor("),
            "SIMD never calls Block.GetColor");
        c.True(!simd.Contains("GetColorWithoutTint"),
            "SIMD never calls GetColorWithoutTint");

        string mix = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodSurfaceMix.cs"));
        c.True(mix.Contains("LodRgbSimd.BlurLandOnce"),
            "BlurLand uses the SIMD kernel (radius 0 still runs the mask/alpha copy)");
        c.True(mix.Contains("LodRgbSimd.QuantizePacked"),
            "Quantize uses the SIMD packed kernel");
        c.True(mix.Contains("ArrayPool"),
            "mix / halo / blur scratch come from ArrayPool");
        c.Eq(0, LodSurfaceMix.BlurRadius, "production BlurRadius stays 0");

        string plan = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "docs", "plans", "login-bake-efficiency.md"));
        c.True(plan.Contains("SIMD after GetColor (done)"),
            "efficiency plan marks post-GetColor SIMD done, not a leftover TODO");
        c.True(plan.Contains("LodRgbSimd"),
            "efficiency plan names the live SIMD type");

        string simdPlanPath = Path.Combine(
            GameAssemblies.RepoRoot, "docs", "plans", "simd-after-getcolor.md");
        c.True(File.Exists(simdPlanPath), "simd-after-getcolor research note ships");
        string simdPlan = File.ReadAllText(simdPlanPath);
        c.True(simdPlan.Contains("BlurLandOnceRadiusZero"),
            "SIMD research documents radius-0 production blur path");
        c.True(simdPlan.Contains("ForceScalar"),
            "SIMD research documents scalar fallback / test hook");
        c.True(simdPlan.Contains("learn.microsoft.com"),
            "SIMD research cites Microsoft SIMD docs");
    }
}
