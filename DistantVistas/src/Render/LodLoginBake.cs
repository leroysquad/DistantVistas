using System.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Login visit sweep — gather live season truth by being at each visited square.
///
/// Purpose (locked): teleport the player (hidden behind the vanilla loading screen) to every
/// previously visited L0 canvas cell so vanilla streams real chunks. The mod re-captures
/// voxel columns from that loaded terrain (snow blocks, leaf species, grass tops) and
/// season-bakes palette colours per column top. Those canvases persist to SQLite and
/// stay painted in near and far LOD until the next login. This is NOT a finalize-time
/// recolor of unloaded cache rows.
/// </summary>
public sealed class LodLoginBake
{
    public enum Phase { OverlayWarmup, WaitingForWorld, Sweeping, Auditing, Draining, Stabilizing, Done }

    enum StopPhase { Teleport, TeleportSettle, WaitChunks, Capture, Bake, BakeSettle, Done }

    const int OverlayWarmupMinTicks = 1;
    const int OverlayWarmupMaxTicks = 20;
    /// <summary>~0.1s — chunk column request fires on teleport; no long pose settle needed.</summary>
    const int TeleportSettleTicks = 2;
    /// <summary>~0.1s — season bake is synchronous; brief gap before next teleport.</summary>
    const int BakeSettleTicks = 2;
    /// <summary>L0 cells around each stop to batch-bake when capture is already idle.</summary>
    const int BatchBakeL0Radius = 8;
    const int MaxBatchBakePerStop = 24;

    const int StabilizeWindowFrames = 90;
    const int StabilizeWindowsRequired = 4;
    const double StabilizeMaxMs = 28.0;
    const double StabilizeTimeoutSec = 12.0;
    const int MaxDrainTicks = 600;
    const int AuditSettleTicks = 4;

    readonly ICoreClientAPI capi;
    readonly LodPipeline pipeline;
    readonly LodTerrainRenderer renderer;
    readonly LodLoginBakeOverlay overlay;
    readonly LodLoginBakeAudioMute audioMute;
    readonly LodLoginBakeTimeFreeze timeFreeze;
    readonly LodLoginBakeGameMode gameMode;
    readonly LodLoginBakeHudHide hudHide;
    readonly LodLoginBakePlayerHide playerHide;
    readonly LodLoginBakeWorldHide worldHide;
    readonly LodLoginBakeViewBoost viewBoost;
    readonly LodSeasonSampleExporter seasonSamples;
    readonly LodLoginSweepTiming sweepTiming = new();
    readonly LodLoginSweepStatusWriter statusWriter;
    readonly LodLoginBakeProgressUi progressUi = new();
    readonly Block? plantTintFallback;
    readonly System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf;
    readonly Queue<long> pending = new();
    readonly HashSet<long> completedKeys = new();
    readonly HashSet<long> plannedKeys = new();
    readonly List<double> stabilizeWindow = new(128);
    readonly List<double> windowMedians = new(8);
    readonly EntityPos restorePos = new();
    readonly Vec3d restoreCameraPos = new();
    readonly Stopwatch stabilizeClock = new();

    int total;
    int finished;
    int worldWaitTicks;
    int overlayWarmupTicks;
    int drainTicks;
    int auditTicks;
    int resweepRound;
    bool retryingMisses;
    bool releaseSuccess;
    bool releaseKeepResume;
    LodLoginSweepPlanMode sweepMode = LodLoginSweepPlanMode.RevisitVisited;
    string sweepModeLabel = "Revisiting visited land";
    Phase phase = Phase.WaitingForWorld;
    bool released;
    long? currentKey;
    StopPhase stopPhase;
    int stopTicks;
    bool restoreCaptured;
    bool resuming;
    bool loggedTeleportBegin;
    bool loggedWarmupComplete;
    bool worldHideApplied;
    bool escWasDown;
    readonly List<long> oceanSampleKeys = new();
    readonly List<long> openOceanFillKeys = new();

    public Phase CurrentPhase => phase;
    public bool Active => phase != Phase.Done;
    public float Progress
    {
        get
        {
            if (phase == Phase.OverlayWarmup) return 0.01f;
            if (phase == Phase.WaitingForWorld) return 0.03f;
            if (phase == Phase.Sweeping)
                return total <= 0 ? 0.05f : 0.05f + (float)finished / total * 0.70f;
            if (phase == Phase.Auditing)
                return 0.76f + Math.Min(0.04f, auditTicks / (float)AuditSettleTicks * 0.04f);
            if (phase == Phase.Draining)
                return 0.80f + Math.Min(0.05f, drainTicks / (float)MaxDrainTicks * 0.05f);
            if (phase == Phase.Stabilizing)
            {
                float settle = Math.Min(1f, (float)windowMedians.Count / StabilizeWindowsRequired);
                return 0.85f + settle * 0.15f;
            }
            return 1f;
        }
    }

    public LodLoginBake(
        ICoreClientAPI capi,
        LodPipeline pipeline,
        LodTerrainRenderer renderer,
        LodLoginBakeOverlay overlay,
        Block? plantTintFallback,
        System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf)
    {
        this.capi = capi;
        this.pipeline = pipeline;
        this.renderer = renderer;
        this.overlay = overlay;
        this.plantTintFallback = plantTintFallback;
        this.untintedOf = untintedOf;
        audioMute = new LodLoginBakeAudioMute(capi);
        timeFreeze = new LodLoginBakeTimeFreeze(capi);
        gameMode = new LodLoginBakeGameMode(capi);
        hudHide = new LodLoginBakeHudHide(capi);
        playerHide = new LodLoginBakePlayerHide(capi);
        worldHide = new LodLoginBakeWorldHide(capi);
        viewBoost = new LodLoginBakeViewBoost(capi, renderer);
        seasonSamples = new LodSeasonSampleExporter(capi);
        statusWriter = new LodLoginSweepStatusWriter(capi);
        overlay.OnCancelRequested = CancelAndSave;
    }

    /// <summary>Escape during the overlay — save remaining queue and release controls.</summary>
    public void CancelAndSave()
    {
        if (phase == Phase.Done || released) return;
        int left = pending.Count + (currentKey != null ? 1 : 0);
        if (left <= 0
            && phase is not Phase.Sweeping
            and not Phase.WaitingForWorld
            and not Phase.OverlayWarmup)
        {
            Teardown(success: false);
            return;
        }

        if (left > 0)
        {
            SaveResumeSnapshot();
            capi.Logger.Notification(
                "[DistantVistas] Login visit sweep paused — {0} region(s) remaining. Relog to resume (same season or within {1} days).",
                left, (int)LodLoginSweepResume.MaxResumeDayGap);
            UpdateProgress(Progress,
                $"Paused — {left} region(s) saved. Relog to resume.", force: true);
            Teardown(success: false, keepResume: true);
            return;
        }

        capi.Logger.Notification("[DistantVistas] Login visit sweep cancelled — entering play.");
        UpdateProgress(1f, "Entering play (sweep cancelled)…", force: true);
        Teardown(success: false);
    }

    /// <summary>
    /// Poll Esc from the render loop — game-tick HudElement handlers do not run while the
    /// vanilla loading screen is held.
    /// </summary>
    public void PollCancelFromRender()
    {
        if (phase == Phase.Done || released) return;
        try
        {
            bool down = capi.Input.KeyboardKeyState[(int)GlKeys.Escape];
            if (down)
            {
                if (!escWasDown)
                {
                    escWasDown = true;
                    capi.Logger.Notification("[DistantVistas] Login visit sweep: Esc cancel requested.");
                    CancelAndSave();
                }
            }
            else escWasDown = false;
        }
        catch
        {
            // Input may not be ready on the first render pulse after LevelFinalize.
        }
    }

    public void Begin()
    {
        pending.Clear();
        completedKeys.Clear();

        overlay.Show();
        renderer.LoginBakeOverlayActive = true;
        LodLoginBakeHarmony.ResetPaintDiagnostics();
        LodLoginBakeSweepGate.Arm();
        LodLoginBakeSweepGate.EnsureRunningGameRenderPath(capi);

        LodLoginSweepResume? resume = LodLoginSweepResume.TryLoad(capi);
        if (resume != null && resume.IsEligible(capi.World))
        {
            if (resume.IsOversizedForCurrentBudget())
            {
                LodLoginSweepResume.Delete(capi);
                capi.Logger.Notification(
                    "[DistantVistas] Saved login sweep had {0} pending regions (budget {1}) — replanning with spatial subsample.",
                    resume.Pending.Count, LodLoginSweepBootstrap.RevisitMaxVisitStops);
                resuming = false;
                PlanSweepQueue();
            }
            else
            {
                ApplyResume(resume);
                resuming = true;
            }
        }
        else
        {
            resuming = false;
            if (resume != null)
            {
                LodLoginSweepResume.Delete(capi);
                capi.Logger.Notification(
                    "[DistantVistas] Saved login sweep expired (season/day limit) — planning a fresh sweep.");
            }

            PlanSweepQueue();
        }

        plannedKeys.Clear();
        foreach (long key in pending)
            plannedKeys.Add(key);
        foreach (long key in completedKeys)
            plannedKeys.Add(key);

        sweepTiming.Begin();
        finished = 0;
        worldWaitTicks = 0;
        drainTicks = 0;
        auditTicks = 0;
        resweepRound = 0;
        retryingMisses = false;
        phase = Phase.OverlayWarmup;
        released = false;
        currentKey = null;
        restoreCaptured = false;
        overlayWarmupTicks = 0;
        loggedTeleportBegin = false;
        loggedWarmupComplete = false;
        worldHideApplied = false;
        escWasDown = false;
        progressUi.Reset();
        stabilizeWindow.Clear();
        windowMedians.Clear();

        pipeline.DeferLegacyHeal = true;
        viewBoost.EnsureBoosted();
        audioMute.EnsureMuted();
        timeFreeze.EnsureFrozen();
        gameMode.EnsureCreative();
        hudHide.EnsureHidden();
        playerHide.EnsureHidden();

        capi.Logger.Notification(
            "[DistantVistas] LOGIN VISIT SWEEP ARMED — mode={0}, regions={1}, view={2} blocks, overlay warming up.",
            sweepModeLabel, total, viewBoost.BoostedViewDistanceBlocks);

        if (resuming)
        {
            capi.Logger.Notification(
                "[DistantVistas] Login visit sweep resuming: {0} — {1} left ({2}/{3} done).",
                sweepModeLabel, pending.Count, finished, total);
        }
        else
        {
            capi.Logger.Notification(
                "[DistantVistas] Login visit sweep: {0} — {1} L0 region{2}.",
                sweepModeLabel, total, total == 1 ? "" : "s");
        }
        seasonSamples.BeginSession(sweepMode, sweepModeLabel, total);
        string startMsg = resuming
            ? $"{sweepModeLabel} — resuming ({finished}/{total} done)…"
            : $"{sweepModeLabel} — preparing loading screen…";
        UpdateProgress(Progress, StatusWithEta($"{startMsg} (Esc to pause & save)"), force: true);
        statusWriter.TouchAdvance("armed");
        statusWriter.WriteNow(phase, sweepModeLabel, total, finished, force: true);

        CaptureRestorePose();
    }

    /// <summary>
    /// Pin the player's pre-sweep pose (spawn / relog location) before any visit teleports.
    /// </summary>
    void CaptureRestorePose()
    {
        if (restoreCaptured) return;

        EntityPlayer entity = capi.World.Player.Entity;
        restorePos.SetFrom(entity.Pos);
        restoreCameraPos.Set(entity.CameraPos);
        restoreCaptured = true;
    }

    void TickOverlayWarmup()
    {
        overlayWarmupTicks++;
        string detail = total > 0
            ? $"{sweepModeLabel} — preparing ({overlayWarmupTicks})…"
            : $"Starting visit sweep… ({overlayWarmupTicks})";
        UpdateProgress(Progress, StatusWithEta($"{detail} (Esc to pause & save)"), force: overlayWarmupTicks <= 1);

        if (overlayWarmupTicks < OverlayWarmupMinTicks) return;

        if (!loggedWarmupComplete)
        {
            loggedWarmupComplete = true;
            statusWriter.TouchAdvance("warmup-complete");
            if (overlay.HasRendered)
            {
                capi.Logger.Notification(
                    "[DistantVistas] Login visit sweep: Distant Vistas loading splash active.");
            }
            else
            {
                NoteLoadingCoverUnpainted();
            }

            capi.Logger.Notification(
                "[DistantVistas] Login visit sweep: warmup complete — entering visit teleports.");
        }

        if (!worldHideApplied && overlay.HasRendered)
        {
            worldHideApplied = true;
            worldHide.HideAllLoaded();
        }

        if (total == 0)
        {
            BeginAuditing();
            return;
        }

        phase = Phase.WaitingForWorld;
        worldWaitTicks = 0;
    }

    void AbortSweep(string reason)
    {
        capi.Logger.Error("[DistantVistas] Login visit sweep aborted: {0}", reason);
        UpdateProgress(1f, "Entering play (sweep aborted)…", force: true);
        renderer.LoginBakeComplete = true;
        Teardown(success: false, handoverReason: "abort");
    }

    /// <summary>Never abort into play solely because the loading cover failed to paint.</summary>
    void NoteLoadingCoverUnpainted()
    {
        capi.Logger.Warning(
            "[DistantVistas] Login visit sweep: loading cover not painted yet — keeping sweep active (no abort).");
    }

    void PlanSweepQueue()
    {
        LodWorld world = pipeline.World;
        int visitedCount = LodLoginSweep.VisitedL0Keys(world).Count();

        // First successful sweep for this world: always bootstrap the ~6 km spawn disk
        // (coast guard / radius), even if the player already walked some land. Revisit/
        // refresh of VisitedL0Keys would only re-cover the tiny walked frontier and leave
        // vistas beyond it as sky (0.8.26).
        if (LodLoginSweepComplete.TryLoad(capi) == null)
        {
            ApplyBootstrapPlan(PlanBootstrap(), visitedCount, "first sweep → bootstrap");
            return;
        }

        List<LodLoginBakeAudit.Miss> misses = LodLoginBakeAudit.FindMisses(
            world, pipeline, capi.World.Blocks, plantTintFallback, untintedOf);

        if (misses.Count > 0)
        {
            LodLoginSweepPlan plan = LodLoginSweepBootstrap.PlanIncomplete(misses);
            sweepMode = plan.Mode;
            sweepModeLabel = plan.ModeLabel;
            oceanSampleKeys.Clear();
            openOceanFillKeys.Clear();
            foreach (long key in plan.Keys)
                pending.Enqueue(key);
            total = pending.Count;
            capi.Logger.Notification(
                "[DistantVistas] Login visit sweep: {0} ({1} visited in cache).",
                sweepModeLabel, visitedCount);
            return;
        }

        if (visitedCount > 0)
        {
            LodLoginSweepPlan plan = LodLoginSweepBootstrap.PlanRevisitVisited(
                world, capi.World, pipeline, capi.World.Blocks, plantTintFallback, untintedOf, capi);
            sweepMode = plan.Mode;
            sweepModeLabel = plan.ModeLabel;
            oceanSampleKeys.Clear();
            openOceanFillKeys.Clear();
            foreach (long key in plan.Keys)
                pending.Enqueue(key);
            total = pending.Count;
            return;
        }

        ApplyBootstrapPlan(PlanBootstrap(), visitedCount, "empty cache fallback");
    }

    LodLoginSweepPlan PlanBootstrap() =>
        LodLoginSweepBootstrap.PlanBootstrap(
            pipeline.World, capi.World, pipeline, capi.World.Blocks, plantTintFallback, untintedOf, capi);

    void ApplyBootstrapPlan(LodLoginSweepPlan plan, int visitedCount, string reason)
    {
        sweepMode = plan.Mode;
        sweepModeLabel = plan.ModeLabel;
        oceanSampleKeys.Clear();
        oceanSampleKeys.AddRange(plan.OceanSampleKeys);
        openOceanFillKeys.Clear();
        openOceanFillKeys.AddRange(plan.OpenOceanFillKeys);
        pending.Clear();
        foreach (long key in plan.Keys)
            pending.Enqueue(key);
        total = pending.Count;
        capi.Logger.Notification(
            "[DistantVistas] Login visit sweep: {0} ({1} visited in cache; {2}).",
            sweepModeLabel, visitedCount, reason);
    }

    public void Tick(float dt)
    {
        if (phase == Phase.Done) return;

        HoldPlayerControls();

        switch (phase)
        {
            case Phase.OverlayWarmup:
                TickOverlayWarmup();
                break;
            case Phase.WaitingForWorld:
                TickWaitingForWorld();
                break;
            case Phase.Sweeping:
                TickSweeping();
                break;
            case Phase.Auditing:
                TickAuditing();
                break;
            case Phase.Draining:
                TickDraining();
                break;
            case Phase.Stabilizing:
                TickStabilizing(dt);
                break;
        }

        statusWriter.WriteNow(phase, sweepModeLabel, total, finished);
    }

    void TickWaitingForWorld()
    {
        worldWaitTicks++;
        if (!LodLoginSweep.IsWorldReady(capi.World))
        {
            UpdateProgress(Progress,
                StatusWithEta(worldWaitTicks > LodLoginSweep.MaxWorldReadyTicks
                    ? $"{sweepModeLabel} — world slow to load, continuing anyway…"
                    : $"{sweepModeLabel} — waiting for world and map to load…"));
            if (worldWaitTicks < LodLoginSweep.MaxWorldReadyTicks) return;
        }

        if (total == 0)
        {
            BeginAuditing();
            return;
        }

        phase = Phase.Sweeping;
        LogTeleportBegin();
        statusWriter.TouchAdvance("teleports-begin");
        BeginNextStop();
    }

    void LogTeleportBegin()
    {
        if (loggedTeleportBegin) return;
        loggedTeleportBegin = true;
        capi.Logger.Notification(
            "[DistantVistas] Login visit sweep: quiet teleports begin — {0} L0 region{1}.",
            total, total == 1 ? "" : "s");
    }

    void TickSweeping()
    {
        if (currentKey == null)
        {
            if (pending.Count == 0)
            {
                RestorePlayerPose();
                BeginAuditing();
                return;
            }
            LogTeleportBegin();
            BeginNextStop();
            return;
        }

        long key = currentKey.Value;
        switch (stopPhase)
        {
            case StopPhase.Teleport:
                stopPhase = StopPhase.TeleportSettle;
                stopTicks = 0;
                break;

            case StopPhase.TeleportSettle:
                stopTicks++;
                UpdateProgress(Progress,
                    StatusWithEta($"{VisitPrefix()}settling after move… ({stopTicks}/{TeleportSettleTicks})"));
                if (stopTicks < TeleportSettleTicks) return;
                stopPhase = StopPhase.WaitChunks;
                stopTicks = 0;
                break;

            case StopPhase.WaitChunks:
                stopTicks++;
                if (stopTicks == 1)
                    worldHide.RevealL0(key);
                if (stopTicks % 2 == 0)
                    SweepColumnsAround(key);
                if (!LodLoginSweep.AllMapChunksLoaded(capi.World.BlockAccessor, key)
                    && stopTicks < LodLoginSweep.MaxChunkWaitTicks)
                {
                    UpdateProgress(Progress,
                        StatusWithEta($"{VisitPrefix()}loading terrain… ({stopTicks})"));
                    return;
                }
                SweepColumnsAround(key);
                pipeline.QueueL0SectionForce(key);
                stopPhase = StopPhase.Capture;
                stopTicks = 0;
                break;

            case StopPhase.Capture:
                stopTicks++;
                if (!pipeline.IsL0SectionCaptureIdle(key)
                    && stopTicks < LodLoginSweep.MaxCaptureWaitTicks)
                {
                    UpdateProgress(Progress,
                        StatusWithEta($"{VisitPrefix()}capturing live terrain… ({Pct(finished, total)})"));
                    return;
                }
                stopPhase = StopPhase.Bake;
                break;

            case StopPhase.Bake:
            {
                int bakedAtStop = BakeBatchAtStop(key);
                finished += bakedAtStop;
                SaveResumeSnapshot();
                sweepTiming.NoteFinished(finished);
                statusWriter.TouchAdvance($"region-{finished}-of-{total}");
                stopPhase = StopPhase.BakeSettle;
                stopTicks = 0;
                UpdateProgress(Progress,
                    StatusWithEta($"{VisitPrefix()}{Pct(finished, total)} — settling…"));
                break;
            }

            case StopPhase.BakeSettle:
                stopTicks++;
                if (stopTicks < BakeSettleTicks) return;
                stopPhase = StopPhase.Done;
                currentKey = null;
                UpdateProgress(Progress,
                    StatusWithEta($"{VisitPrefix()}{Pct(finished, total)}"));
                break;
        }
    }

    void TickDraining()
    {
        drainTicks++;
        pipeline.DrainLoginMip(64);
        pipeline.DrainLoginPersistence(24);

        int mips = pipeline.World.MipDirty.Count;
        UpdateProgress(Progress,
            mips > 0
                ? $"Updating distant land… ({mips} parent sections left)"
                : "Saving visited canvases…");

        if (!pipeline.HasPendingLoginMip && !pipeline.HasPendingLoginPersistence) 
        {
            BeginStabilizing();
            return;
        }

        if (drainTicks >= MaxDrainTicks)
            BeginStabilizing();
    }

    void BeginAuditing()
    {
        RestorePlayerPose();
        StampOpenOceanFromSamples();
        phase = Phase.Auditing;
        auditTicks = 0;
        currentKey = null;
        UpdateProgress(Progress, StatusWithEta("Checking visited regions for gaps…"), force: true);
    }

    void StampOpenOceanFromSamples()
    {
        EnsureOceanFillPlan();
        if (openOceanFillKeys.Count == 0) return;

        int stamped = LodLoginSweepOceanFill.StampOpenOcean(
            capi, pipeline, openOceanFillKeys, oceanSampleKeys, completedKeys);
        if (stamped <= 0) return;

        capi.Logger.Notification(
            "[DistantVistas] Bootstrap ocean: stamped {0} open-water L0 cell(s) from sample visit(s).",
            stamped);
    }

    void EnsureOceanFillPlan()
    {
        if (openOceanFillKeys.Count > 0) return;
        if (sweepMode is not LodLoginSweepPlanMode.BootstrapCoastGuard
            and not LodLoginSweepPlanMode.BootstrapRadius)
            return;

        LodLoginSweepPlan plan = PlanBootstrap();
        oceanSampleKeys.Clear();
        oceanSampleKeys.AddRange(plan.OceanSampleKeys);
        openOceanFillKeys.Clear();
        openOceanFillKeys.AddRange(plan.OpenOceanFillKeys);
    }

    void TickAuditing()
    {
        auditTicks++;
        pipeline.DrainLoginPersistence(8);

        if (auditTicks < AuditSettleTicks)
        {
            UpdateProgress(Progress, StatusWithEta("Checking visited regions for gaps…"), force: true);
            return;
        }

        List<LodLoginBakeAudit.Miss> misses = LodLoginBakeAudit.FindMisses(
            pipeline.World, pipeline, capi.World.Blocks, plantTintFallback, untintedOf);

        if (misses.Count == 0)
        {
            BeginDraining();
            return;
        }

        if (resweepRound >= LodLoginBakeAudit.MaxResweepRounds)
        {
            capi.Logger.Warning(
                "[DistantVistas] Login visit sweep: {0} regions still incomplete after {1} retry passes — continuing anyway.",
                misses.Count, resweepRound);
            BeginDraining();
            return;
        }

        resweepRound++;
        retryingMisses = true;
        pending.Clear();
        foreach (LodLoginBakeAudit.Miss miss in misses)
            pending.Enqueue(miss.Key);

        total = pending.Count;
        finished = 0;
        phase = Phase.Sweeping;
        capi.Logger.Notification(
            "[DistantVistas] Login visit sweep: retrying {0} missed regions (pass {1}).",
            total, resweepRound);
        sweepTiming.Begin();
        UpdateProgress(Progress,
            StatusWithEta($"Retrying {total} missed region{(total == 1 ? "" : "s")} (pass {resweepRound})…"),
            force: true);
        BeginNextStop();
    }

    string VisitPrefix()
    {
        if (retryingMisses)
            return $"Retrying missed regions (pass {resweepRound}) — {finished + 1}/{total} — ";
        return $"{sweepModeLabel} — {finished + 1}/{total} — ";
    }

    void BeginNextStop()
    {
        while (pending.Count > 0)
        {
            long key = pending.Dequeue();
            if (completedKeys.Contains(key)) continue;

            worldHide.HideAllLoaded();
            currentKey = key;
            stopPhase = StopPhase.Teleport;
            stopTicks = 0;

            (double x, double y, double z) = LodLoginSweep.VisitPosition(capi.World, key);
            TeleportPlayer(x, y, z, requestChunks: false);
            int revealRadius = viewBoost.ChunkVisibleRadius;
            LodLoginBakePlayerMove.RequestChunkColumnsVisible(
                capi, x, z, capi.World.Player.Entity.Pos.Dimension, revealRadius);
            UpdateProgress(Progress,
                StatusWithEta($"{VisitPrefix()}moving to region… ({Pct(finished, total)})"));
            return;
        }
    }

    void SweepColumnsAround(long l0Key)
    {
        (double x, _, double z) = LodLoginSweep.VisitPosition(capi.World, l0Key);
        int cx = (int)Math.Floor(x / GlobalConstants.ChunkSize);
        int cz = (int)Math.Floor(z / GlobalConstants.ChunkSize);
        pipeline.SweepLoadedColumns(cx, cz, viewBoost.ChunkSweepRadiusChunks);
    }

    /// <summary>
    /// Lock season appearance from the freshly captured voxels: snow on columns,
    /// leaf hue per species/height, ground tone from live maps at each block top.
    /// </summary>
    void BakeAndPersist(long l0Key)
    {
        LodWorld world = pipeline.World;
        if (!world.Sections.TryGetValue(l0Key, out LodSection? section))
        {
            section = world.LoadFromStore?.Invoke(l0Key);
            if (section != null) world.InstallLoaded(l0Key, section);
        }
        if (section == null) return;

        LodSeasonBake.BakeSectionFromVisit(
            capi, section, l0Key, plantTintFallback, untintedOf);

        seasonSamples.RecordSection(l0Key, section);

        world.MarkChanged(l0Key);
        pipeline.InvalidateGpuMesh?.Invoke(l0Key);
        world.RenderDirty.Add(l0Key);
        pipeline.DrainLoginPersistence(1);
    }

    /// <summary>
    /// After capture idles at a stop, bake the target L0 and any planned neighbours that
    /// the boosted column sweep already loaded — avoids a full teleport-wait per cell.
    /// </summary>
    int BakeBatchAtStop(long primaryKey)
    {
        int baked = 0;
        foreach (long key in CollectBatchBakeKeys(primaryKey))
        {
            if (completedKeys.Contains(key)) continue;
            if (!plannedKeys.Contains(key)) continue;
            if (!pipeline.IsL0SectionCaptureIdle(key)) continue;
            if (!LodLoginSweep.AllMapChunksLoaded(capi.World.BlockAccessor, key)) continue;

            BakeAndPersist(key);
            completedKeys.Add(key);
            baked++;
        }

        if (baked == 0)
        {
            BakeAndPersist(primaryKey);
            if (completedKeys.Add(primaryKey))
                baked = 1;
        }

        return baked;
    }

    List<long> CollectBatchBakeKeys(long primaryKey)
    {
        int sx0 = LodWorld.KeySx(primaryKey);
        int sz0 = LodWorld.KeySz(primaryKey);
        var candidates = new List<(long DistSq, long Key)>();

        for (int dsz = -BatchBakeL0Radius; dsz <= BatchBakeL0Radius; dsz++)
        {
            for (int dsx = -BatchBakeL0Radius; dsx <= BatchBakeL0Radius; dsx++)
            {
                int sx = sx0 + dsx;
                int sz = sz0 + dsz;
                if (sx < 0 || sz < 0) continue;
                long key = LodWorld.SectionKey(0, sx, sz);
                if (!plannedKeys.Contains(key)) continue;
                long dist = (long)dsx * dsx + (long)dsz * dsz;
                candidates.Add((dist, key));
            }
        }

        candidates.Sort((a, b) => a.DistSq.CompareTo(b.DistSq));
        var result = new List<long>(Math.Min(MaxBatchBakePerStop, candidates.Count));
        for (int i = 0; i < candidates.Count && result.Count < MaxBatchBakePerStop; i++)
            result.Add(candidates[i].Key);
        return result;
    }

    void BeginDraining()
    {
        RestorePlayerPose();
        phase = Phase.Draining;
        drainTicks = 0;
        UpdateProgress(Progress, "Updating distant land…", force: true);
    }

    void BeginStabilizing()
    {
        RestorePlayerPose();
        phase = Phase.Stabilizing;
        stabilizeClock.Restart();
        stabilizeWindow.Clear();
        windowMedians.Clear();
        UpdateProgress(Progress, "Waiting for frame time to settle…", force: true);
    }

    void TickStabilizing(float dt)
    {
        double ms = dt > 0 ? dt * 1000.0 : 16.0;
        stabilizeWindow.Add(ms);
        if (stabilizeWindow.Count > StabilizeWindowFrames)
            stabilizeWindow.RemoveAt(0);

        double now = stabilizeClock.Elapsed.TotalSeconds;
        if (stabilizeWindow.Count >= StabilizeWindowFrames && windowMedians.Count < StabilizeWindowsRequired)
        {
            stabilizeWindow.Sort();
            windowMedians.Add(stabilizeWindow[stabilizeWindow.Count / 2]);
            stabilizeWindow.Clear();
        }

        UpdateProgress(Progress,
            $"Stabilizing frame time… {windowMedians.Count}/{StabilizeWindowsRequired}");

        bool stable = windowMedians.Count >= StabilizeWindowsRequired
            && windowMedians.All(m => m <= StabilizeMaxMs);
        bool timedOut = now >= StabilizeTimeoutSec;
        if (!stable && !timedOut) return;

        Finish();
    }

    void Finish()
    {
        UpdateProgress(1f, "Ready.", force: true);
        ReleaseResources(success: true);
        capi.Logger.Notification(
            "[DistantVistas] Login visit sweep finished: {0}/{1} regions captured and locked until relog.",
            finished, total);
    }

    void CompleteRelease(bool success, bool keepResume = false) =>
        ReleaseResources(success, keepResume);

    /// <summary>
    /// Single idempotent release for success, abort, cancel, and world leave.
    /// </summary>
    void ReleaseResources(bool success, bool keepResume = false, string? handoverReason = null)
    {
        if (released) return;

        releaseSuccess = success;
        releaseKeepResume = keepResume;
        released = true;
        phase = Phase.Done;
        pipeline.DeferLegacyHeal = false;
        renderer.LoginBakeOverlayActive = false;
        renderer.LoginBakeComplete = true;
        progressUi.Reset();

        if (success)
            LodLoginSweepComplete.RecordSuccess(capi, pipeline.World);

        if (keepResume && !success)
            SaveResumeSnapshot();

        try
        {
            try { overlay.Hide(); } catch { }
            try { ReleasePlayerControls(); } catch { }
            try { audioMute.Restore(); } catch { }
            try { gameMode.Restore(); } catch { }
            try { hudHide.Restore(); } catch { }
            try { playerHide.Restore(); } catch { }
            try { viewBoost.Restore(); } catch { }
            try { seasonSamples.Dispose(); } catch { }
        }
        finally
        {
            // Always return the player home (position + look) and unfreeze time — success,
            // Esc cancel, abort, error, or world leave — even when other teardown throws.
            try { RestorePlayerPose(); } catch { }
            try { worldHide.Restore(); } catch { }
            try { timeFreeze.Restore(); } catch { }
        }

        statusWriter.TouchAdvance(
            success ? "release-success" : keepResume ? "release-paused" : "release-cancel");
        statusWriter.WriteNow(Phase.Done, sweepModeLabel, total, finished, force: true);
        statusWriter.Clear();

        string resolvedReason = handoverReason ?? (success ? "success" : keepResume ? "pause" : "cancel");
        LodLoginBakeSweepGate.CompleteHandoverAndRelease(capi, resolvedReason);

        if (success || !keepResume)
            LodLoginSweepResume.Delete(capi);
    }

    /// <summary>
    /// Single idempotent teardown for abort, error, cancel, and world leave.
    /// </summary>
    void Teardown(bool success, bool keepResume = false, string? handoverReason = null) =>
        ReleaseResources(success, keepResume, handoverReason);

    void HoldPlayerControls()
    {
        overlay.EnsureInputBlocked();
        CloseBlockingDialogs();

        IClientPlayer player = capi.World.Player;
        EntityPlayer entity = player.Entity;
        EntityControls controls = entity.Controls;

        if (phase == Phase.Done) return;

        audioMute.EnsureMuted();
        timeFreeze.EnsureFrozen();
        gameMode.EnsureCreative();
        hudHide.EnsureHidden();
        viewBoost.EnsureBoosted();

        CaptureRestorePose();

        HoldPlayerPose(entity);
        LockPlayerCamera(capi, player, restorePos, restoreCameraPos);
        BlockPlayerInput(controls);
        playerHide.EnsureHidden();
    }

    void ApplyResume(LodLoginSweepResume resume)
    {
        sweepMode = resume.SweepMode;
        sweepModeLabel = resume.SweepModeLabel;
        total = resume.PlannedTotal;
        finished = resume.Finished;
        resweepRound = resume.ResweepRound;
        retryingMisses = resume.RetryingMisses;
        foreach (long key in resume.Completed)
            completedKeys.Add(key);
        pending.Clear();
        foreach (long key in resume.Pending)
            pending.Enqueue(key);
        restorePos.SetPos(resume.RestoreX, resume.RestoreY, resume.RestoreZ);
        restorePos.Yaw = resume.RestoreYaw;
        restorePos.Pitch = resume.RestorePitch;
        EntityPlayer entity = capi.World.Player.Entity;
        if (resume.RestoreCameraY != 0 || resume.RestoreCameraX != 0 || resume.RestoreCameraZ != 0)
            restoreCameraPos.Set(resume.RestoreCameraX, resume.RestoreCameraY, resume.RestoreCameraZ);
        else
            RebuildRestoreCameraFromPose(entity);
        entity.Pos.SetFrom(restorePos);
        LockPlayerCamera(capi, capi.World.Player, restorePos, restoreCameraPos);
        restoreCaptured = true;
    }

    void SaveResumeSnapshot()
    {
        if (released || phase == Phase.Done) return;

        int left = pending.Count + (currentKey != null ? 1 : 0);
        if (left <= 0
            && phase is not Phase.Sweeping
            and not Phase.WaitingForWorld
            and not Phase.OverlayWarmup)
            return;

        LodLoginSweepResume snap = LodLoginSweepResume.CaptureCalendar(capi);
        snap.SweepMode = sweepMode;
        snap.SweepModeLabel = sweepModeLabel;
        snap.PlannedTotal = total;
        snap.Finished = finished;
        snap.ResweepRound = resweepRound;
        snap.RetryingMisses = retryingMisses;
        snap.Completed = completedKeys.ToList();

        var pendingList = new List<long>(pending);
        if (currentKey != null)
            pendingList.Insert(0, currentKey.Value);
        snap.Pending = pendingList;

        if (restoreCaptured)
        {
            snap.RestoreX = restorePos.X;
            snap.RestoreY = restorePos.Y;
            snap.RestoreZ = restorePos.Z;
            snap.RestoreYaw = restorePos.Yaw;
            snap.RestorePitch = restorePos.Pitch;
            snap.RestoreCameraX = restoreCameraPos.X;
            snap.RestoreCameraY = restoreCameraPos.Y;
            snap.RestoreCameraZ = restoreCameraPos.Z;
        }

        snap.Save(capi);
    }

    void HoldPlayerPose(EntityPlayer entity)
    {
        if (phase == Phase.Sweeping && currentKey != null)
        {
            (double x, double y, double z) = LodLoginSweep.VisitPosition(capi.World, currentKey.Value);
            LodLoginBakePlayerMove.HoldQuiet(entity, x, y, z);
        }
        else
        {
            entity.Pos.SetFrom(restorePos);
            entity.Pos.Motion.Set(0, 0, 0);
        }
    }

    static void LockPlayerCamera(
        ICoreClientAPI capi,
        IClientPlayer player,
        EntityPos pose,
        Vec3d cameraPos)
    {
        EntityPlayer entity = player.Entity;
        player.CameraYaw = pose.Yaw;
        player.CameraPitch = pose.Pitch;
        entity.Pos.Yaw = pose.Yaw;
        entity.Pos.Pitch = pose.Pitch;
        entity.CameraPos.Set(cameraPos);
        entity.CameraPosOffset.Set(0, 0, 0);
        capi.Input.MouseYaw = pose.Yaw;
        capi.Input.MousePitch = pose.Pitch;
    }

    static void BlockPlayerInput(EntityControls controls) =>
        LodLoginBakeInputLock.Apply(controls);

    void ReleasePlayerControls()
    {
        capi.World.Player.Entity.Controls.MovespeedMultiplier = 1f;
    }

    void CloseBlockingDialogs()
    {
        var open = capi.Gui.OpenedGuis;
        for (int i = open.Count - 1; i >= 0; i--)
        {
            GuiDialog dlg = open[i];
            if (dlg.DialogType == EnumDialogType.HUD) continue;
            if (LodLoginBakeCharacterWait.IsProtectedDialog(dlg)) continue;
            dlg.TryClose();
        }
    }

    void RestorePlayerPose()
    {
        if (!restoreCaptured) return;

        IClientPlayer player = capi.World.Player;
        EntityPlayer entity = player.Entity;
        LodLoginBakePlayerMove.ApplyQuietFrom(capi, entity, restorePos);
        LockPlayerCamera(capi, player, restorePos, restoreCameraPos);
    }

    void RebuildRestoreCameraFromPose(EntityPlayer entity)
    {
        restoreCameraPos.Set(
            restorePos.X,
            restorePos.Y + entity.LocalEyePos.Y,
            restorePos.Z);
    }

    void TeleportPlayer(double x, double y, double z, bool requestChunks = true) =>
        LodLoginBakePlayerMove.ApplyQuiet(capi, capi.World.Player.Entity, x, y, z, requestChunks);

    static string Pct(int done, int total) =>
        total <= 0 ? "0%" : $"{done * 100 / total}%";

    void UpdateProgress(float progress, string detail, bool force = false)
    {
        string phaseLabel = phase switch
        {
            Phase.OverlayWarmup or Phase.WaitingForWorld => "Preparing",
            Phase.Sweeping => "Visiting",
            Phase.Auditing or Phase.Draining or Phase.Stabilizing => "Finishing",
            _ => "Loading"
        };
        int pct = (int)Math.Round(Math.Clamp(progress, 0f, 1f) * 100);
        string lined = detail.StartsWith(phaseLabel, StringComparison.Ordinal)
            ? detail
            : $"{phaseLabel} {pct}% — {detail}";
        if (force || progressUi.ShouldUpdate(phase, finished, total, lined))
            overlay.UpdateProgress(progress, lined);
    }

    string StatusWithEta(string detail) =>
        phase == Phase.Sweeping && total > 0
            ? detail + sweepTiming.EtaSuffix(finished, total)
            : detail;

    public void Dispose()
    {
        if (Active)
            SaveResumeSnapshot();
        Teardown(success: false, keepResume: true);
    }
}
