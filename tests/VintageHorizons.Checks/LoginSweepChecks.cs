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
        OverlayGuard(c);
        QuietTeleports(c);
        SeasonSampleExport(c);
        SweepResume(c);
        SweepSkipGate(c);
        SweepTiming(c);
        CreativeMode(c);
        HudHide(c);
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
        var world = new LodWorld();
        world.InstallStoredKey(0, 4, 7, applyToParent: true, provisional: false);
        world.InstallStoredKey(0, 8, 1, applyToParent: true, provisional: false);

        var visited = LodLoginSweep.VisitedL0Keys(world).ToList();
        visited.Sort();
        var plan = new LodLoginSweepPlan(
            LodLoginSweepPlanMode.RevisitVisited, visited, "Revisiting visited land");

        c.Eq(LodLoginSweepPlanMode.RevisitVisited, plan.Mode, "visited canvas uses revisit mode");
        c.Eq("Revisiting visited land", plan.ModeLabel, "revisit mode label");
        c.Eq(2, plan.Keys.Count, "revisit plan includes every visited L0 key");

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
    }

    static void BackdropHook(Check c)
    {
        string hold = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakeVanillaLoadingHold.cs"));
        c.True(hold.Contains("GuiScreenLoadingGame"),
            "login sweep holds vanilla world loading screen");
        c.True(hold.Contains("loadingText"),
            "vanilla hold updates ScreenManager.loadingText");
        c.True(hold.Contains("LoadScreenNoLoadCall"),
            "vanilla hold re-opens loader via ScreenManager");
        c.True(hold.Contains("FormatLoadingText"),
            "vanilla hold appends DV status to loading lines");
        c.True(!hold.Contains("never construct"),
            "vanilla hold can create loading screen when cache is empty");

        string gate = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakeSweepGate.cs"));
        c.True(gate.Contains("SuppressRunningGameRender"),
            "sweep gate suppresses running-game world render");

        string harmony = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakeHarmony.cs"));
        c.True(harmony.Contains("RenderToPrimary"),
            "harmony skips running-game primary render during sweep");

        string screen = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakeScreenRenderer.cs"));
        c.True(screen.Contains("stockOnly"),
            "custom DV splash is fallback-only via stockOnly flag");
        c.True(screen.Contains("stock-looking"),
            "screen renderer documented as stock fallback, not primary UX");
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
    }

    static void TeardownHook(Check c)
    {
        string bake = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBake.cs"));
        c.True(bake.Contains("void Teardown(bool success"),
            "login bake uses a single Teardown path");
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
        c.True(bake.Contains("BakeSectionFromVisit"),
            "login bake calls visit-only exact bake");
        c.True(bake.Contains("DeferLegacyHeal = true"),
            "legacy heal is deferred during visit sweep");

        string pipeline = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Lod", "LodPipeline.cs"));
        c.True(pipeline.Contains("DeferLegacyHeal"),
            "pipeline can defer approximate legacy heal");
    }

    static void AuditMisses(Check c)
    {
        string bake = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBake.cs"));
        c.True(bake.Contains("Phase.Auditing"), "login bake has an audit phase before release");
        c.True(bake.Contains("Retrying missed regions"),
            "login bake UI names the miss-resweep pass");

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
    }

    static void OverlayGuard(Check c)
    {
        string overlay = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakeOverlay.cs"));
        c.True(!overlay.Contains(": GuiDialog"),
            "login overlay coordinator is not a fragile GuiDialog");
        c.True(overlay.Contains("LodLoginBakeVanillaLoadingHold"),
            "login overlay coordinates vanilla loading hold");
        c.True(overlay.Contains("LodLoginBakeInputGuard"),
            "login overlay uses deferred input guard");

        string hold = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakeVanillaLoadingHold.cs"));
        c.True(hold.Contains("GuiScreenLoadingGame"),
            "vanilla hold resolves native loading screen");
        c.True(hold.Contains("LodLoginBakeStockLoadingFallback"),
            "vanilla hold falls back to stock Loading… UI");
        c.True(hold.Contains("AfterFinalComposition"),
            "vanilla hold paints ortho and after-final passes");

        string fallback = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakeStockLoadingFallback.cs"));
        c.True(fallback.Contains("stockOnly: true"),
            "stock fallback uses minimal renderer, not DV splash");

        string screen = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakeScreenRenderer.cs"));
        c.True(screen.Contains("DrawStockLayout"),
            "fallback renderer draws stock Loading… layout");
        c.True(screen.Contains("TryDrawWithInternalQuad"),
            "fallback renderer uses ortho internal quad tint path");
        c.True(screen.Contains("TryDrawWithExplicitQuad"),
            "fallback renderer uses explicit MeshRef on after-final pass");
        c.True(screen.Contains("HasEverPaintedOpaque"),
            "fallback renderer tracks whether ortho ever painted");

        string renderer = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodTerrainRenderer.cs"));
        c.True(renderer.Contains("LoginBakeOverlayActive"),
            "terrain renderer skips draw while login overlay active");

        string guard = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakeInputGuard.cs"));
        c.True(guard.Contains("TryEnsureOpen"),
            "input guard retries open when viewport is ready");
        c.True(guard.Contains("SafeBounds"),
            "input guard uses render/window bounds fallback");
        c.True(hold.Contains("RenderToDefaultFramebuffer"),
            "vanilla hold re-renders native loading UI each frame");

        string driver = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakePulse.cs"));
        c.True(driver.Contains("LodLoginBakePulse"),
            "render pulse drives sweep while game ticks stall");
        c.True(driver.Contains("PollCancelFromRender"),
            "render pulse polls Esc each frame");

        string status = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginSweepStatusWriter.cs"));
        c.True(status.Contains("ModData/Logs/login-sweep-status.json"),
            "sweep status heartbeat path");
        c.True(status.Contains("StuckHint"),
            "sweep status includes stuck hint");

        string mod = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "DistantVistasModSystem.cs"));
        c.True(mod.Contains("LodLoginBakePulse"),
            "mod wires render pulse from vanilla hold");
        c.True(mod.Contains("OnRenderPulse"),
            "mod connects vanilla hold render pulse");
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
        c.True(bake.Contains("OverlayWarmup"),
            "login bake warms overlay before teleports");
        c.True(bake.Contains("warmup complete — entering visit teleports"),
            "login bake logs loudly when warmup ends");
        c.True(bake.Contains("PollCancelFromRender"),
            "login bake polls Esc from render loop");
        c.True(bake.Contains("KeyboardKeyState"),
            "login bake reads Escape from keyboard state");
        c.True(bake.Contains("NoteLoadingCoverUnpainted"),
            "login bake never aborts solely on unpainted cover");
        c.True(!bake.Contains("cover never painted"),
            "login bake does not abort on cover paint timeout");
        c.True(bake.Contains("LodLoginBakeWorldHide"),
            "login bake hides vanilla chunks during sweep");
        c.True(bake.Contains("LodLoginBakePlayerMove.ApplyQuiet"),
            "login bake uses quiet client entity moves");
        c.True(bake.Contains("LodLoginBakePlayerMove.ApplyQuietFrom"),
            "login bake restores pose with quiet client moves");

        string inputLock = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakeInputLock.cs"));
        c.True(inputLock.Contains("StopAllMovement"),
            "input lock calls StopAllMovement");
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
        c.Eq("ModData/distantvistas/login-sweep-resume.json", LodLoginSweepResume.RelPath,
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

        string guard = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakeInputGuard.cs"));
        c.True(guard.Contains("OnCancelRequested"),
            "escape routes to cancel/resume handler");
    }

    static void SweepSkipGate(Check c)
    {
        c.Eq("ModData/distantvistas/login-sweep-complete.json", LodLoginSweepComplete.RelPath,
            "completion record under ModData/distantvistas");
        c.Eq(30.0, LodLoginSweepWindow.MaxDayGap, "skip window matches resume day gap");

        string gate = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginSweepGate.cs"));
        c.True(gate.Contains("resuming cancelled mid-sweep checkpoint"),
            "gate always runs when an eligible resume exists");
        c.True(gate.Contains("empty canvas needs bootstrap sweep"),
            "gate runs bootstrap on empty visited canvas");
        c.True(gate.Contains("still incomplete"),
            "gate runs when audit finds misses");
        c.True(gate.Contains("outside same-season / 30-day window"),
            "gate runs when completion window expired");
        c.True(gate.Contains("visited canvas complete within season window"),
            "gate skips when canvas is complete and in window");

        string mod = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "DistantVistasModSystem.cs"));
        c.True(mod.Contains("LodLoginSweepGate.Decide"),
            "level finalize consults sweep gate before overlay");
        c.True(mod.Contains("Login visit sweep skipped"),
            "skipped sweep logs and drops into play");
        c.True(mod.Contains("LoginVisitSweepEnabled"),
            "sweep gated by config flag");
        c.True(new DistantVistasConfig().LoginVisitSweepEnabled,
            "login visit sweep enabled by default");

        string bake = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBake.cs"));
        c.True(bake.Contains("LodLoginSweepComplete.RecordSuccess"),
            "successful sweep records completion for future skips");
    }

    static void SweepTiming(Check c)
    {
        c.Eq(90.0, LodLoginSweepTiming.TargetMinSec, "sweep target min seconds");
        c.Eq(150.0, LodLoginSweepTiming.TargetMaxSec, "sweep target max seconds");
        c.Eq(6000, LodLoginSweepBootstrap.EmptyCanvasBootstrapRadiusBlocks,
            "empty-canvas bootstrap probe radius default");
        c.Eq(94, LodLoginSweepBootstrap.BootstrapCellRadius(),
            "6000 blocks is ~94 L0 cells radius at 64-block footprint");
        c.Eq(43, LodLoginSweepBootstrap.BootstrapMaxVisitStops,
            "bootstrap visit cap targets ~2.5 min at 3.5s/stop");

        string bootstrap = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginSweepBootstrap.cs"));
        c.True(bootstrap.Contains("BudgetVisitStops"),
            "bootstrap applies hard visit stop budget");
        c.True(bootstrap.Contains("BootstrapCoastGuard"),
            "bootstrap can plan coast-guard ocean sweeps");
        c.True(bootstrap.Contains("BootstrapRadius"),
            "bootstrap can plan radius disk sweeps");

        string bake = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBake.cs"));
        c.True(bake.Contains("LodLoginSweepBootstrap.Plan("),
            "login bake plans sweep with bootstrap vs revisit mode");
        c.True(bake.Contains("sweepModeLabel"),
            "login bake progress distinguishes bootstrap vs revisit");
        c.True(bake.Contains("StatusWithEta"),
            "login bake progress includes ETA suffix");
    }

    static void CreativeMode(Check c)
    {
        string bake = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBake.cs"));
        c.True(bake.Contains("gameMode.EnsureCreative()"),
            "login bake enables creative during sweep");
        c.True(bake.Contains("gameMode.Restore()"),
            "login bake restores prior gamemode on teardown");

        string hud = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakeHudHide.cs"));
        c.True(hud.Contains("HideGuis"),
            "HUD hide saves prior HideGuis state");
        c.True(hud.Contains(".gui"),
            "HUD hide uses the .gui client toggle");
    }

    static void HudHide(Check c)
    {
        string bake = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBake.cs"));
        c.True(bake.Contains("hudHide.EnsureHidden()"),
            "login bake hides HUD during sweep");
        c.True(bake.Contains("playerHide.EnsureHidden()"),
            "login bake hides local player during sweep");

        string playerHide = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakePlayerHide.cs"));
        c.True(playerHide.Contains("ServerControls.Sneak"),
            "player hide suppresses nametag like crouch");
        c.True(playerHide.Contains("InvisibleRenderColor"),
            "player hide tints entity render alpha to zero");
        c.True(playerHide.Contains(LodLoginBakePlayerHide.HideFpHandsKey),
            "player hide saves and restores hideFpHands");
    }
}
