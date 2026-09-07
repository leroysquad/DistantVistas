using System.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Login visit sweep — gather live season truth at each visited square.
///
/// Purpose (locked): during the HUD overlay, staggered <see cref="LodScoutEntity"/>
/// workers force-load distant columns (SetChunkColumnVisible) without moving the
/// player. The mod recaptures voxel columns from that streamed terrain and
/// season-bakes palette colours per column top. Those canvases persist to SQLite.
/// This is NOT a finalize-time recolor of unloaded cache rows.
/// </summary>
public sealed class LodLoginBake
{
    public enum Phase { OverlayWarmup, WaitingForWorld, Sweeping, Auditing, Draining, Stabilizing, Done }

    enum StopPhase { Teleport, TeleportSettle, WaitChunks, Capture, Bake, BakeSettle, Done }

    const int OverlayWarmupMinTicks = 1;
    const int OverlayWarmupMaxTicks = 20;
    /// <summary>~50 ms — chunk request fires on teleport; move on fast.</summary>
    const int TeleportSettleTicks = 1;
    /// <summary>~50 ms — season bake is synchronous; brief gap before next teleport.</summary>
    const int BakeSettleTicks = 1;
    /// <summary>L0 neighbour disk at a stop. 12 × 64-block cells ≈ 768, matching the 750-block bake view.</summary>
    const int BatchBakeL0Radius = 12;
    const int MaxBatchBakePerStop = 256;
    /// <summary>GetColor + persist per overlay tick. 12 keeps Windows responsive while hopping faster.</summary>
    const int MaxBakePerTick = 12;
    const int MaxLeftoverBakePerTick = 12;
    const int SweepRowsPerCall = 2;
    const int RevealGrowPerTick = 4;

    const int StabilizeWindowFrames = 90;
    const int StabilizeWindowsRequired = 4;
    const double StabilizeMaxMs = 28.0;
    const double StabilizeTimeoutSec = 3.0;
    const int MaxDrainTicks = 1800;
    const int DrainStallTicks = 240;
    const int AuditSettleTicks = 4;

    readonly ICoreClientAPI capi;
    readonly LodPipeline pipeline;
    readonly LodTerrainRenderer renderer;
    readonly LodLoginBakeOverlay overlay;
    readonly LodLoginBakeAudioMute audioMute;
    readonly LodLoginBakeTimeFreeze timeFreeze;
    readonly LodLoginBakeGameMode gameMode;
    readonly LodLoginBakePlayerHide playerHide;
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
    int drainStallTicks;
    int lastDrainDirty = -1;
    int lastDrainPending = -1;
    int visitBakeGetColor;
    int visitBakeChanged;
    int visitBakePale;
    int visitBakeZero;
    int visitBakeLeaves;
    int visitBakeSnow;
    int auditTicks;
    int resweepRound;
    bool retryingMisses;
    bool expireRecapture;
    bool expireLeftoversDone;
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
    bool escWasDown;
    int escGraceLeft;
    readonly List<long> oceanSampleKeys = new();
    readonly List<long> openOceanFillKeys = new();
    readonly List<long> stopBakeKeys = new();
    readonly List<long> leftoverKeys = new();
    readonly LodLoginScoutFill scoutFill = new();
    readonly Queue<long> scoutReady = new();
    int revealRadius;
    int stopBakeIndex;
    bool stopBakePrepared;
    int leftoverIndex;
    bool leftoverQueued;
    int leftoverHopDone;
    int leftoverTotal;
    int leftoverBaked;
    int leftoverNearLeft;
    int leftoverFarLeft;
    int leftoverNearBaked;
    int leftoverFarBaked;
    int spawnRevealRadius;
    int stopBakeSkipIdle;
    int stopBakeSkipMaps;
    int stopBakeBaked;

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
        playerHide = new LodLoginBakePlayerHide(capi);
        viewBoost = new LodLoginBakeViewBoost(capi, renderer);
        seasonSamples = new LodSeasonSampleExporter(capi);
        statusWriter = new LodLoginSweepStatusWriter(capi);
        overlay.OnCancelRequested = CancelAndSave;
    }

    /// <summary>Escape during the overlay — save remaining queue, put the player home, leave to the menu.</summary>
    public void CancelAndSave()
    {
        if (phase == Phase.Done || released) return;
        int left = pending.Count + scoutFill.LiveCount + scoutReady.Count
            + (currentKey != null ? 1 : 0);
        capi.Logger.Notification(
            "[DistantVistas] Login visit sweep paused — {0} region(s) remaining. Returning to the menu (relog to resume).",
            left);
        // #region agent log
        AgentEscLog("esc-cancel", "H-G-ABORT",
            "\"runId\":\"glitch-1\",\"left\":" + left
            + ",\"phase\":\"" + phase + "\""
            + ",\"sections\":" + pipeline.World.Sections.Count
            + ",\"dirty\":" + pipeline.World.RenderDirty.Count
            + ",\"gaps\":" + renderer.LastUnfilledGaps
            + ",\"drawn\":" + renderer.LastDrawCount
            + ",\"complete\":" + (renderer.LoginBakeComplete ? "true" : "false")
            + ",\"restoreX\":" + restorePos.X.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"restoreY\":" + restorePos.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"restoreZ\":" + restorePos.Z.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"captured\":" + (restoreCaptured ? "true" : "false"));
        // #endregion
        if (left > 0
            || phase is Phase.Sweeping or Phase.WaitingForWorld or Phase.OverlayWarmup)
        {
            SaveResumeSnapshot();
            UpdateProgress(Progress,
                $"Paused — {left} region(s) saved. Returning to menu…", force: true);
            Teardown(success: false, keepResume: true);
        }
        else
        {
            Teardown(success: false);
        }

        capi.Event.EnqueueMainThreadTask(() => LodLoginBakeLeaveMenu.Request(capi), "dv-esc-leave-menu");
    }

    /// <summary>
    /// Poll Esc from the sweep pulse. Overlay capture swallows the filtered keyboard
    /// state, so this reads the raw key — same reason Alt has to.
    /// </summary>
    public void PollCancelFromRender()
    {
        if (phase == Phase.Done || released) return;
        if (escGraceLeft > 0)
        {
            escGraceLeft--;
            escWasDown = true;
            return;
        }
        try
        {
            bool raw = capi.Input.KeyboardKeyStateRaw[(int)GlKeys.Escape];
            bool filtered = capi.Input.KeyboardKeyState[(int)GlKeys.Escape];
            if (raw)
            {
                if (!escWasDown)
                {
                    escWasDown = true;
                    capi.Logger.Notification("[DistantVistas] Login visit sweep: Esc cancel requested.");
                    // #region agent log
                    AgentEscLog("esc-poll", "H-E-raw",
                        "\"raw\":true,\"filtered\":" + (filtered ? "true" : "false")
                        + ",\"phase\":\"" + phase + "\"");
                    // #endregion
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
        LodLoginSweepTimingStore.EnsureApplied(capi, sweepTiming);
        pending.Clear();
        completedKeys.Clear();
        expireRecapture = false;
        expireLeftoversDone = false;
        leftoverQueued = false;
        leftoverIndex = 0;
        leftoverKeys.Clear();
        stopBakePrepared = false;
        stopBakeIndex = 0;
        stopBakeKeys.Clear();
        revealRadius = LodLoginBakePlayerMove.ChunkVisibleRadius;
        spawnRevealRadius = LodLoginBakePlayerMove.ChunkVisibleRadius;
        scoutFill.Reset();
        scoutReady.Clear();

        overlay.Show();
        renderer.LoginBakeOverlayActive = true;
        renderer.LoginBakeBlocked = false;

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

        sweepTiming.BeginSession(LodLoginSweepTiming.MachineSecPerStop);
        if (!resuming)
            finished = 0;
        worldWaitTicks = 0;
        drainTicks = 0;
        auditTicks = 0;
        resweepRound = 0;
        retryingMisses = false;
        phase = Phase.OverlayWarmup;
        released = false;
        currentKey = null;
        if (!resuming)
            restoreCaptured = false;
        overlayWarmupTicks = 0;
        loggedTeleportBegin = false;
        loggedWarmupComplete = false;
        escWasDown = false;
        escGraceLeft = 40;
        progressUi.Reset();
        LodPauseOnStartCompat.KeepUnpaused(capi);
        stabilizeWindow.Clear();
        windowMedians.Clear();

        pipeline.DeferLegacyHeal = true;
        pipeline.FreezeCapture = false;
        pipeline.HoldUnloadedCaptures = true;
        viewBoost.EnsureBoosted();
        audioMute.EnsureMuted();
        timeFreeze.EnsureFrozen();
        gameMode.EnsureCreative();
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
            : $"{sweepModeLabel} — preparing overlay…";
        UpdateProgress(Progress, StatusWithEta($"{startMsg} (Esc to pause and return to the menu)"), force: true);
        statusWriter.TouchAdvance("armed");
        statusWriter.WriteNow(phase, sweepModeLabel, total, finished, force: true);

        if (!restoreCaptured)
            CaptureRestorePose(allowOverwrite: true);
    }

    /// <summary>
    /// Pin the player's pre-sweep pose (spawn / relog location) before any visit teleports.
    /// Warmup may recapture until the entity is actually at spawn; Sweeping never overwrites.
    /// </summary>
    void CaptureRestorePose(bool allowOverwrite = false)
    {
        EntityPlayer entity = capi.World.Player.Entity;
        if (entity == null) return;
        if (restoreCaptured && !allowOverwrite) return;
        if (restoreCaptured && resuming) return;
        if (LooksUnset(entity.Pos) && restoreCaptured && !LooksUnset(restorePos))
            return;

        restorePos.SetFrom(entity.Pos);
        restoreCameraPos.Set(entity.CameraPos);
        restoreCaptured = true;
        // #region agent log
        AgentEscLog("pose-capture", "H-P-pin",
            "\"overwrite\":" + (allowOverwrite ? "true" : "false")
            + ",\"phase\":\"" + phase + "\""
            + ",\"x\":" + restorePos.X.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"y\":" + restorePos.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"z\":" + restorePos.Z.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"resuming\":" + (resuming ? "true" : "false"));
        // #endregion
    }

    static bool LooksUnset(EntityPos pos) =>
        Math.Abs(pos.X) < 0.01 && Math.Abs(pos.Z) < 0.01;

    void TickOverlayWarmup()
    {
        overlayWarmupTicks++;
        overlay.EnsureInputBlocked();
        string detail = total > 0
            ? $"{sweepModeLabel} — preparing ({overlayWarmupTicks})…"
            : $"Starting visit sweep… ({overlayWarmupTicks})";
        UpdateProgress(Progress, StatusWithEta($"{detail} (Esc to pause and return to the menu)"), force: overlayWarmupTicks <= 1);

        if (overlayWarmupTicks < OverlayWarmupMinTicks) return;
        if (!overlay.HasRendered && overlayWarmupTicks < OverlayWarmupMaxTicks) return;

        if (!loggedWarmupComplete)
        {
            loggedWarmupComplete = true;
            statusWriter.TouchAdvance("warmup-complete");
            if (overlay.HasRendered)
            {
                capi.Logger.Notification(
                    "[DistantVistas] Login visit sweep: Distant Vistas overlay active.");
            }
            else
            {
                NoteLoadingCoverUnpainted();
            }

            capi.Logger.Notification(
                "[DistantVistas] Login visit sweep: warmup complete — entering visit teleports.");
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
        Teardown(success: false);
    }

    /// <summary>Never abort into play solely because the overlay failed to open.</summary>
    void NoteLoadingCoverUnpainted()
    {
        capi.Logger.Warning(
            "[DistantVistas] Login visit sweep: overlay not open yet — keeping sweep active (no abort).");
    }

    void PlanSweepQueue()
    {
        LodWorld world = pipeline.World;
        int visitedCount = LodLoginSweep.VisitedL0Keys(world).Count();

        // First successful sweep for this world: always bootstrap the Farseer-onset
        // disk (coast guard / radius), even if the player already walked some land.
        // Revisit of VisitedL0Keys would only re-cover the walked frontier and leave
        // a white/empty strip in front of the gray/black silhouette.
        if (LodLoginSweepComplete.TryLoad(capi) == null)
        {
            ApplyBootstrapPlan(PlanBootstrap(), visitedCount, "first sweep → bootstrap");
            return;
        }

        LodLoginSweepComplete? completeMarker = LodLoginSweepComplete.TryLoad(capi);
        string? expire = completeMarker == null
            ? null
            : LodLoginSweepWindow.RecaptureReason(capi.World, completeMarker);
        bool withinWindow = expire == null;

        if (!withinWindow)
        {
            int storedInDisk = 0;
            try
            {
                EntityPos pos = capi.World.Player.Entity.Pos;
                int radius = LodLoginSweepBootstrap.EmptyCanvasBootstrapRadiusBlocks;
                double radiusSq = (double)radius * radius;
                int footprint = LodSection.SectionBlocks;
                foreach (long visitedKey in LodLoginSweep.VisitedL0Keys(world))
                {
                    double cx = LodWorld.KeySx(visitedKey) * footprint + footprint * 0.5;
                    double cz = LodWorld.KeySz(visitedKey) * footprint + footprint * 0.5;
                    double dx = cx - pos.X;
                    double dz = cz - pos.Z;
                    if (dx * dx + dz * dz <= radiusSq) storedInDisk++;
                }
            }
            catch { }

            capi.Logger.Notification(
                "[DistantVistas] Login visit sweep: skip colormap rebake — season paint comes from live GetColor on streamed visit captures ({0} stored L0 in {1} km disk).",
                storedInDisk,
                LodLoginSweepBootstrap.EmptyCanvasBootstrapRadiusBlocks / 1000);
            LogSeasonRebake(
                capi, completeMarker, storedInDisk, 0,
                0, 0, within30: false, skip: true,
                0, storedInDisk, storedInDisk);

            // #region agent log
            try
            {
                System.IO.File.AppendAllText(
                    @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                    "{\"sessionId\":\"40cccb\",\"runId\":\"post-fix-expire\",\"hypothesisId\":\"H-REV\",\"location\":\"LodLoginBake.PlanSweepQueue\",\"message\":\"recapture-plan\",\"data\":{\"expire\":\""
                    + (expire ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"")
                    + "\",\"paintRev\":" + (completeMarker?.PaintRevision ?? -1)
                    + ",\"needPaint\":" + LodSurfaceMix.PaintRevision
                    + ",\"storedInDisk\":" + storedInDisk
                    + "},\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n");
            }
            catch { }
            // #endregion
            LodLoginSweepPlan expired = LodLoginSweepBootstrap.PlanSeasonExpired(
                world, capi.World, pipeline, capi.World.Blocks, plantTintFallback, untintedOf, capi);
            expireRecapture = true;
            sweepMode = expired.Mode;
            sweepModeLabel = expired.ModeLabel;
            oceanSampleKeys.Clear();
            openOceanFillKeys.Clear();
            foreach (long key in expired.Keys)
                pending.Enqueue(key);
            total = pending.Count;
            capi.Logger.Notification(
                "[DistantVistas] Login visit sweep: {0} ({1} visited in cache).",
                sweepModeLabel, visitedCount);
            // #region agent log
            try
            {
                System.IO.File.AppendAllText(
                    @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                    "{\"sessionId\":\"40cccb\",\"runId\":\"post-fix-expire\",\"hypothesisId\":\"H-S-plan\",\"location\":\"LodLoginBake.PlanSweepQueue\",\"message\":\"expire-queued\",\"data\":{\"planned\":"
                    + total + ",\"visited\":" + visitedCount
                    + ",\"label\":\"" + (sweepModeLabel ?? "").Replace("\\", "\\\\").Replace("\"", "'")
                    + "\"},\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n");
            }
            catch { }
            // #endregion
            LogPlannedStops("PlanSeasonExpired");
            return;
        }

        List<LodLoginBakeAudit.Miss> misses = LodLoginBakeAudit.FindMisses(
            world, pipeline, capi.World.Blocks, plantTintFallback, untintedOf);

        if (misses.Count > 0)
        {
            LodLoginSweepPlan plan = LodLoginSweepBootstrap.PlanIncomplete(misses, capi.World);
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
            // #region agent log
            try
            {
                System.IO.File.AppendAllText(
                    @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                    "{\"sessionId\":\"40cccb\",\"runId\":\"post-fix-4\",\"hypothesisId\":\"H-S-incomplete\",\"location\":\"LodLoginBake.PlanSweepQueue\",\"message\":\"plan-incomplete\",\"data\":{\"misses\":"
                    + misses.Count + ",\"planned\":" + total + ",\"visited\":" + visitedCount
                    + ",\"maxStops\":" + LodLoginSweepBootstrap.RevisitMaxVisitStops
                    + "},\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n");
            }
            catch { }
            // #endregion
            LogPlannedStops("PlanIncomplete");
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
            LogPlannedStops("PlanRevisitVisited");
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
        LogPlannedStops("ApplyBootstrapPlan");
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
        UpdateProgress(Progress,
            StatusWithEta($"{VisitPrefix()}scouting regions… ({Pct(finished, total)})"));
    }

    void LogTeleportBegin()
    {
        if (loggedTeleportBegin) return;
        loggedTeleportBegin = true;
        capi.Logger.Notification(
            "[DistantVistas] Login visit sweep: quiet teleports begin — {0} L0 region{1} (scout entities, player stays).",
            total, total == 1 ? "" : "s");
    }

    void TickSweeping()
    {
        LogTeleportBegin();

        GrowRevealAroundSpawn();

        List<long> ready = scoutFill.Tick(
            capi, pipeline, pending, completedKeys, LodLoginScoutFill.LocalVisitRevealChunks);
        for (int i = 0; i < ready.Count; i++)
            scoutReady.Enqueue(ready[i]);

        if (currentKey == null && scoutReady.Count > 0)
        {
            currentKey = scoutReady.Dequeue();
            revealRadius = LodLoginBakePlayerMove.ChunkVisibleRadius;
            stopBakePrepared = false;
            stopBakeIndex = 0;
            stopBakeKeys.Clear();
        }

        if (currentKey != null)
        {
            long key = currentKey.Value;
            GrowRevealAround(key);
            SweepColumnsAround(key);
            if (!BakeBatchAtStop(key))
            {
                SweepColumnsAroundSpawn();
                UpdateProgress(Progress,
                    StatusWithEta($"{VisitPrefix()}painting streamed ring… ({stopBakeIndex}/{Math.Max(1, stopBakeKeys.Count)})"));
                return;
            }
            finished++;
            SaveResumeSnapshot();
            sweepTiming.NoteFinished(finished);
            statusWriter.TouchAdvance($"region-{finished}-of-{total}");
            currentKey = null;
        }

        SweepColumnsAroundSpawn();

        int inFlight = scoutFill.LiveCount + scoutReady.Count + (currentKey != null ? 1 : 0);
        if (inFlight == 0 && pending.Count == 0)
        {
            RestorePlayerPose();
            BeginAuditing();
            return;
        }

        UpdateProgress(Progress,
            StatusWithEta($"{VisitPrefix()}{scoutFill.LiveCount} scout{(scoutFill.LiveCount == 1 ? "" : "s")} streaming… ({Pct(finished, total)})"));
    }

    void TickDraining()
    {
        drainTicks++;
        pipeline.DrainLoginMip(16);
        pipeline.DrainLoginPersistence(8);

        int mips = pipeline.World.MipDirty.Count;
        int dirty = pipeline.World.RenderDirty.Count;
        int pending = pipeline.PendingColumns;
        int capRes = pipeline.Worker.CaptureResults.Count;
        UpdateProgress(Progress,
            mips > 0
                ? $"Updating distant land… ({mips} parent sections left)"
                : dirty > 0
                    ? $"Building horizon meshes… ({dirty} left)"
                    : pending > 0 || capRes > 0
                        ? "Finishing captured land…"
                        : "Saving visited canvases…");

        bool idle = !pipeline.HasPendingLoginMip && !pipeline.HasPendingLoginPersistence
            && dirty == 0 && pending == 0 && capRes == 0;
        if (idle)
        {
            BeginStabilizing();
            return;
        }

        if (dirty == lastDrainDirty && pending == lastDrainPending)
            drainStallTicks++;
        else
            drainStallTicks = 0;
        lastDrainDirty = dirty;
        lastDrainPending = pending;

        if (drainTicks >= MaxDrainTicks || drainStallTicks >= DrainStallTicks)
        {
            AgentReleaseLog("drain-give-up", "H-P4",
                "\"dirty\":" + dirty + ",\"pending\":" + pending + ",\"capRes\":" + capRes
                + ",\"stall\":" + drainStallTicks + ",\"ticks\":" + drainTicks);
            BeginStabilizing();
        }
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

        if (expireRecapture && !expireLeftoversDone)
        {
            if (!leftoverQueued)
            {
                CollectExpireLeftovers();
                leftoverQueued = true;
            }
            if (leftoverIndex < leftoverKeys.Count)
            {
                DrainExpireLeftovers();
                UpdateProgress(Progress,
                    StatusWithEta($"Refreshing leftover season paint… ({leftoverIndex}/{leftoverKeys.Count})"),
                    force: true);
                return;
            }
            LogExpireLeftovers();
            expireLeftoversDone = true;
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
        LodLoginSweepPlan retry = LodLoginSweepBootstrap.PlanIncomplete(
            misses, capi.World, LodLoginSweepBootstrap.RetryMaxVisitStops);
        pending.Clear();
        foreach (long key in retry.Keys)
        {
            if (completedKeys.Contains(key)) continue;
            pending.Enqueue(key);
        }

        total = pending.Count;
        finished = 0;
        phase = Phase.Sweeping;
        capi.Logger.Notification(
            "[DistantVistas] Login visit sweep: retrying {0} of {1} missed regions (pass {2}).",
            total, misses.Count, resweepRound);
        LogPlannedStops("PlanRetry");
        sweepTiming.Begin(resetSamples: false);
        UpdateProgress(Progress,
            StatusWithEta($"Retrying {total} missed region{(total == 1 ? "" : "s")} (pass {resweepRound})…"),
            force: true);
    }

    string VisitPrefix()
    {
        if (retryingMisses)
            return $"Retrying missed regions (pass {resweepRound}) — {finished + 1}/{total} — ";
        return $"{sweepModeLabel} — {finished + 1}/{total} — ";
    }

    void BeginNextStop()
    {
        pipeline.PurgeUnloadedPendingColumns();
        while (pending.Count > 0)
        {
            long key = pending.Dequeue();
            if (completedKeys.Contains(key)) continue;

            currentKey = key;
            stopPhase = StopPhase.WaitChunks;
            stopTicks = 0;
            stopBakePrepared = false;
            stopBakeIndex = 0;
            stopBakeKeys.Clear();
            revealRadius = LodLoginBakePlayerMove.ChunkVisibleRadius;

            (double x, double y, double z) = LodLoginSweep.VisitPosition(capi.World, key);
            // 1.0.25: stream chunks without hopping the player (Pause-on-Start / AFK).
            LodLoginBakePlayerMove.RequestChunkColumnsVisible(
                capi, x, z, capi.World.Player.Entity.Pos.Dimension, revealRadius);
            stopPhase = StopPhase.WaitChunks;
            UpdateProgress(Progress,
                StatusWithEta($"{VisitPrefix()}scouting region… ({Pct(finished, total)})"));
            return;
        }
    }

    void SweepColumnsAround(long l0Key)
    {
        (double x, _, double z) = LodLoginSweep.VisitPosition(capi.World, l0Key);
        int cx = (int)Math.Floor(x / GlobalConstants.ChunkSize);
        int cz = (int)Math.Floor(z / GlobalConstants.ChunkSize);
        pipeline.SweepLoadedColumns(
            cx, cz, LodLoginScoutFill.SweepRadiusChunks, forceRecapture: true, rowsPerCall: SweepRowsPerCall);
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

        // #region agent log
        string prevVisit = LodSeasonBake.DebugVisitKind;
        LodSeasonBake.DebugVisitKind = "overlay";
        // #endregion
        LodSeasonBake.BakeSectionFromVisit(
            capi, section, l0Key, plantTintFallback, untintedOf, out LodSeasonBake.VisitBakeTally tally);
        // #region agent log
        LodSeasonBake.DebugVisitKind = prevVisit;
        // #endregion
        visitBakeGetColor += tally.GetColor;
        visitBakeChanged += tally.Changed;
        visitBakePale += tally.PaleKept;
        visitBakeZero += tally.Zero;
        visitBakeLeaves += tally.Leaves;
        visitBakeSnow += tally.Snow;

        seasonSamples.RecordSection(l0Key, section);

        world.MarkChanged(l0Key);
        pipeline.InvalidateGpuMesh?.Invoke(l0Key);
        world.RenderDirty.Add(l0Key);
        pipeline.DrainLoginPersistence(1);
    }

    /// <summary>
    /// After the stop's own capture idles, bake every in-memory neighbour whose
    /// map chunks are loaded. Visit colour is vanilla GetColor of live blocks,
    /// so a neighbour still sitting in the force-recapture queue is still
    /// paintable. One tick bakes at most MaxBakePerTick cells so Windows
    /// does not mark the client not responding.
    /// </summary>
    bool BakeBatchAtStop(long primaryKey)
    {
        if (!stopBakePrepared)
        {
            stopBakeKeys.Clear();
            stopBakeKeys.AddRange(CollectBatchBakeKeys(primaryKey));
            stopBakeIndex = 0;
            stopBakeSkipIdle = 0;
            stopBakeSkipMaps = 0;
            stopBakeBaked = 0;
            stopBakePrepared = true;
        }

        int steps = 0;
        while (steps < MaxBakePerTick && stopBakeIndex < stopBakeKeys.Count)
        {
            steps++;
            long key = stopBakeKeys[stopBakeIndex++];
            if (completedKeys.Contains(key)) continue;
            if (!pipeline.IsL0SectionCaptureIdle(key))
                stopBakeSkipIdle++;
            if (!LodLoginSweep.AllMapChunksLoaded(capi.World.BlockAccessor, key))
            {
                stopBakeSkipMaps++;
                if (!expireRecapture) continue;
            }

            if (TryBakeOne(key))
                stopBakeBaked++;
        }

        if (stopBakeIndex < stopBakeKeys.Count)
            return false;

        if (stopBakeBaked == 0
            && (expireRecapture
                || LodLoginSweep.AllMapChunksLoaded(capi.World.BlockAccessor, primaryKey)))
        {
            if (TryBakeOne(primaryKey))
                stopBakeBaked = 1;
        }

        int skipIdle = stopBakeSkipIdle;
        int skipMaps = stopBakeSkipMaps;
        int baked = stopBakeBaked;
        int candidates = stopBakeKeys.Count;
        // #region agent log
        try
        {
            int dayOfYear = 0;
            float seasonRel = 0f;
            try
            {
                dayOfYear = capi.World.Calendar.DayOfYear;
                BlockPos? climatePos = capi.World.Player?.Entity?.Pos?.AsBlockPos;
                if (climatePos != null)
                    seasonRel = capi.World.Calendar.GetSeasonRel(climatePos);
            }
            catch { }
            System.IO.File.AppendAllText(
                @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                "{\"sessionId\":\"40cccb\",\"runId\":\"post-fix-expire\",\"hypothesisId\":\"H-EXPIRE-SKIP\",\"location\":\"LodLoginBake.BakeBatchAtStop\",\"message\":\"visit-bake-stop\",\"data\":{\"baked\":" + baked + ",\"getColor\":" + visitBakeGetColor + ",\"changed\":" + visitBakeChanged + ",\"paleKept\":" + visitBakePale + ",\"zero\":" + visitBakeZero + ",\"leaves\":" + visitBakeLeaves + ",\"snow\":" + visitBakeSnow + ",\"boostVd\":" + viewBoost.BoostedViewDistanceBlocks + ",\"candidates\":" + candidates + ",\"idleQueued\":" + skipIdle + ",\"skipMaps\":" + skipMaps + ",\"expire\":" + (expireRecapture ? "true" : "false") + ",\"dayOfYear\":" + dayOfYear + ",\"seasonRel\":" + seasonRel.ToString(System.Globalization.CultureInfo.InvariantCulture) + "},\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n");
        }
        catch { }
        visitBakeGetColor = 0;
        visitBakeChanged = 0;
        visitBakePale = 0;
        visitBakeZero = 0;
        visitBakeLeaves = 0;
        visitBakeSnow = 0;
        // #endregion
        stopBakePrepared = false;
        stopBakeKeys.Clear();
        stopBakeIndex = 0;
        return true;
    }

    bool TryBakeOne(long key)
    {
        int getBefore = visitBakeGetColor;
        int changedBefore = visitBakeChanged;
        bool prevExpire = LodSeasonBake.AllowExpireNoMapSample;
        if (expireRecapture) LodSeasonBake.AllowExpireNoMapSample = true;
        try { BakeAndPersist(key); }
        finally { LodSeasonBake.AllowExpireNoMapSample = prevExpire; }
        if (visitBakeGetColor <= getBefore && visitBakeChanged <= changedBefore) return false;
        completedKeys.Add(key);
        return true;
    }

    void GrowRevealAround(long l0Key)
    {
        int target = LodLoginScoutFill.LocalVisitRevealChunks;
        if (revealRadius >= target) return;
        int before = revealRadius;
        revealRadius = Math.Min(target, revealRadius + RevealGrowPerTick);
        (double x, _, double z) = LodLoginSweep.VisitPosition(capi.World, l0Key);
        LodLoginBakePlayerMove.RequestChunkColumnRing(
            capi, x, z, capi.World.Player.Entity.Pos.Dimension, before, revealRadius);
    }

    void GrowRevealAroundSpawn()
    {
        if (!restoreCaptured) return;
        int target = viewBoost.ChunkVisibleRadius;
        if (spawnRevealRadius >= target) return;
        int before = spawnRevealRadius;
        spawnRevealRadius = Math.Min(target, spawnRevealRadius + RevealGrowPerTick);
        int dim = capi.World.Player.Entity.Pos.Dimension;
        LodLoginBakePlayerMove.RequestChunkColumnRing(
            capi, restorePos.X, restorePos.Z, dim, before, spawnRevealRadius);
    }

    void SweepColumnsAroundSpawn()
    {
        if (!restoreCaptured) return;
        int cx = (int)Math.Floor(restorePos.X / GlobalConstants.ChunkSize);
        int cz = (int)Math.Floor(restorePos.Z / GlobalConstants.ChunkSize);
        pipeline.SweepLoadedColumns(cx, cz, viewBoost.ChunkSweepRadiusChunks, forceRecapture: true, rowsPerCall: SweepRowsPerCall);
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
                if (!pipeline.World.Sections.TryGetValue(key, out LodSection? sec) || sec == null)
                    continue;
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

    /// <summary>
    /// After the budgeted expire hops: overwrite every resident L0 that the hops
    /// never locked. No extra teleports. Hop count stays ~64+16. Drain is
    /// MaxLeftoverBakePerTick cells per overlay tick (BakeExpireLeftovers).
    /// </summary>
    void CollectExpireLeftovers()
    {
        leftoverKeys.Clear();
        leftoverIndex = 0;
        leftoverBaked = 0;
        leftoverNearLeft = 0;
        leftoverFarLeft = 0;
        leftoverNearBaked = 0;
        leftoverFarBaked = 0;
        leftoverHopDone = completedKeys.Count;

        LodWorld world = pipeline.World;
        double px = 0, pz = 0;
        try
        {
            var pos = capi.World.Player.Entity.Pos;
            px = pos.X;
            pz = pos.Z;
        }
        catch { }

        const int nearR = 512;
        double nearRsq = (double)nearR * nearR;
        const int sb = LodSection.SectionBlocks;
        foreach (KeyValuePair<long, LodSection> kv in world.Sections)
        {
            if (LodWorld.KeyLevel(kv.Key) != 0) continue;
            if (kv.Value == null) continue;
            if (completedKeys.Contains(kv.Key)) continue;
            leftoverKeys.Add(kv.Key);
            double cx = LodWorld.KeySx(kv.Key) * sb + sb * 0.5 - px;
            double cz = LodWorld.KeySz(kv.Key) * sb + sb * 0.5 - pz;
            if (cx * cx + cz * cz <= nearRsq) leftoverNearLeft++;
            else leftoverFarLeft++;
        }
        leftoverTotal = leftoverKeys.Count;
    }

    void DrainExpireLeftovers()
    {
        double px = 0, pz = 0;
        try
        {
            var pos = capi.World.Player.Entity.Pos;
            px = pos.X;
            pz = pos.Z;
        }
        catch { }

        const int nearR = 512;
        double nearRsq = (double)nearR * nearR;
        const int sb = LodSection.SectionBlocks;
        int steps = 0;
        while (steps < MaxLeftoverBakePerTick && leftoverIndex < leftoverKeys.Count)
        {
            steps++;
            long key = leftoverKeys[leftoverIndex++];
            double cx = LodWorld.KeySx(key) * sb + sb * 0.5 - px;
            double cz = LodWorld.KeySz(key) * sb + sb * 0.5 - pz;
            bool near = cx * cx + cz * cz <= nearRsq;
            if (TryBakeOne(key))
            {
                leftoverBaked++;
                if (near) leftoverNearBaked++;
                else leftoverFarBaked++;
            }
        }
    }

    void LogExpireLeftovers()
    {
        visitBakeGetColor = 0;
        visitBakeChanged = 0;
        visitBakePale = 0;
        visitBakeZero = 0;
        visitBakeLeaves = 0;
        visitBakeSnow = 0;
        try
        {
            System.IO.File.AppendAllText(
                @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                "{\"sessionId\":\"40cccb\",\"runId\":\"post-fix-expire\",\"hypothesisId\":\"H-EXPIRE-LEFT\",\"location\":\"LodLoginBake.BakeExpireLeftovers\",\"message\":\"expire-leftover\",\"data\":{\"hopDone\":"
                + leftoverHopDone + ",\"leftover\":" + leftoverTotal + ",\"baked\":" + leftoverBaked
                + ",\"nearLeft\":" + leftoverNearLeft + ",\"farLeft\":" + leftoverFarLeft
                + ",\"nearBaked\":" + leftoverNearBaked + ",\"farBaked\":" + leftoverFarBaked
                + ",\"month\":" + capi.World.Calendar.Month
                + ",\"dayOfYear\":" + capi.World.Calendar.DayOfYear.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "},\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n");
        }
        catch { }
    }

    void BeginDraining()
    {
        RestorePlayerPose();
        phase = Phase.Draining;
        drainTicks = 0;
        drainStallTicks = 0;
        lastDrainDirty = -1;
        lastDrainPending = -1;
        // Splash HUD stays up; let the renderer upload meshes now so play does not
        // inherit a 200+ renderDirty backlog.
        renderer.LoginBakeOverlayActive = false;
        UpdateProgress(Progress, "Updating distant land…", force: true);
        AgentReleaseLog("drain-mesh-unlock", "H-A3",
            "\"renderDirty\":" + pipeline.World.RenderDirty.Count
            + ",\"mipDirty\":" + pipeline.World.MipDirty.Count);
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

        bool leftover = pipeline.World.RenderDirty.Count > 0
            || pipeline.HasPendingLoginMip
            || pipeline.HasPendingLoginPersistence
            || pipeline.PendingColumns > 0;
        if (leftover)
        {
            pipeline.DrainLoginMip(16);
            pipeline.DrainLoginPersistence(8);
            UpdateProgress(Progress,
                $"Building horizon meshes… ({pipeline.World.RenderDirty.Count} left)");
            if (now < 20.0) return;
        }

        bool stable = windowMedians.Count >= StabilizeWindowsRequired
            && windowMedians.All(m => m <= StabilizeMaxMs);
        bool timedOut = now >= StabilizeTimeoutSec;
        if (!stable && !timedOut && !leftover) return;

        Finish();
    }

    void Finish()
    {
        UpdateProgress(1f, "Ready.", force: true);
        ReleaseResources(success: true);
        capi.Logger.Notification(
            "[DistantVistas] Login visit sweep finished: {0}/{1} regions captured. New land still bakes on discover.",
            finished, total);
    }

    void CompleteRelease(bool success, bool keepResume = false) =>
        ReleaseResources(success, keepResume);

    /// <summary>
    /// Single idempotent release for success, abort, cancel, and world leave.
    /// </summary>
    void ReleaseResources(bool success, bool keepResume = false)
    {
        if (released) return;

        // Snapshot while scouts are still live and phase is not Done.
        // SaveResumeSnapshot no-ops once released, and Reset would drop in-flight keys.
        if (keepResume && !success)
            SaveResumeSnapshot();

        releaseSuccess = success;
        releaseKeepResume = keepResume;
        released = true;
        phase = Phase.Done;
        if (success)
        {
            pipeline.ExploreBake.Clear();
            pipeline.FreezeCapture = false;
            pipeline.HoldUnloadedCaptures = false;
            pipeline.DiscoverOnly = true;
            pipeline.PurgeUnloadedPendingColumns();
            pipeline.DeferLegacyHeal = false;
        }
        else
        {
            pipeline.DeferLegacyHeal = false;
            pipeline.FreezeCapture = false;
            pipeline.HoldUnloadedCaptures = false;
            pipeline.DiscoverOnly = false;
        }
        renderer.LoginBakeOverlayActive = false;
        renderer.LoginBakeComplete = true;
        progressUi.Reset();
        scoutFill.Reset();
        scoutReady.Clear();

        if (success)
        {
            LodLoginSweepComplete.RecordSuccess(capi, pipeline.World);
            LodLoginSweepTimingStore.RecordRun(capi, sweepTiming);
        }

        try
        {
            try { overlay.Hide(); } catch { }
            try { LodLoginBakeMouseDelta.Drain(capi); } catch { }
            try { ReleasePlayerControls(); } catch { }
            try { audioMute.Restore(); } catch { }
            try { gameMode.Restore(); } catch { }
            try { playerHide.Restore(); } catch { }
            try { viewBoost.Restore(); } catch { }
            try { seasonSamples.Dispose(); } catch { }
        }
        finally
        {
            // Always return the player home (position + look) and unfreeze time — success,
            // Esc cancel, abort, error, or world leave — even when other teardown throws.
            try { RestorePlayerPose(requestChunks: success); } catch { }
            try { LodLoginBakeMouseDelta.Drain(capi); } catch { }
            try { timeFreeze.Restore(); } catch { }
            // RequestMode copies the graphics slider onto DesiredViewDistance. Pose restore
            // can do that after we already wrote the player's slider back, so write it again.
            try { viewBoost.ReassertPlayerView(); } catch { }
            if (success)
            {
                try { LodPauseOnStartCompat.RestoreAfterLoginBake(capi); } catch { }
            }
            try
            {
                capi.Event.RegisterCallback(_ =>
                {
                    try { viewBoost.ReassertPlayerView(); } catch { }
                }, 250);
            }
            catch { }
            try
            {
                capi.Event.RegisterCallback(_ =>
                {
                    try { viewBoost.ReassertPlayerView(); } catch { }
                }, 1000);
            }
            catch { }
        }

        statusWriter.TouchAdvance(
            success ? "release-success" : keepResume ? "release-paused" : "release-cancel");
        statusWriter.WriteNow(Phase.Done, sweepModeLabel, total, finished, force: true);
        statusWriter.Clear();

        if (success || !keepResume)
            LodLoginSweepResume.Delete(capi);

        AgentReleaseLog("release-done", "H-A1",
            "\"success\":" + (success ? "true" : "false")
            + ",\"renderDirty\":" + pipeline.World.RenderDirty.Count
            + ",\"explorePending\":" + pipeline.ExploreBake.PendingCount
            + ",\"freeze\":" + (pipeline.FreezeCapture ? "true" : "false")
            + ",\"deferHeal\":" + (pipeline.DeferLegacyHeal ? "true" : "false")
            + ",\"overdraw\":" + renderer.OverdrawStart.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
            + ",\"audioMuted\":" + (audioMute.IsMuted ? "true" : "false"));
        AgentReleaseLog("release-discover", "H-P-discover",
            "\"success\":" + (success ? "true" : "false")
            + ",\"discoverOnly\":" + (pipeline.DiscoverOnly ? "true" : "false")
            + ",\"deferHeal\":" + (pipeline.DeferLegacyHeal ? "true" : "false")
            + ",\"freeze\":" + (pipeline.FreezeCapture ? "true" : "false"));
        if (success)
        {
            try { LogMayFlagBakedDump(); } catch { }
        }
    }

    void LogMayFlagBakedDump()
    {
        double px = 0, pz = 0;
        try
        {
            var pos = capi.World.Player.Entity.Pos;
            px = pos.X;
            pz = pos.Z;
        }
        catch { }

        AgentReleaseLog("flagbaked-near", "H-MAY7", FlagBakedBucketJson(512, px, pz));
        AgentReleaseLog("flagbaked-all", "H-MAY7", FlagBakedBucketJson(-1, px, pz));
    }

    string FlagBakedBucketJson(int radius, double px, double pz)
    {
        var acc = new int[5, 8];
        string grassPath = "", leafPath = "", pinePath = "";
        int grassRgb = 0, leafRgb = 0, pineRgb = 0;
        int nSec = 0, nCol = 0;
        var blocks = capi.World.Blocks;
        const int gs = LodSection.GridSize;
        const int sb = LodSection.SectionBlocks;
        foreach (var kv in pipeline.World.Sections)
        {
            if (LodWorld.KeyLevel(kv.Key) != 0) continue;
            LodSection? sec = kv.Value;
            if (sec == null) continue;
            int ox = LodWorld.KeySx(kv.Key) * sb;
            int oz = LodWorld.KeySz(kv.Key) * sb;
            if (radius > 0)
            {
                double cx = ox + sb * 0.5 - px;
                double cz = oz + sb * 0.5 - pz;
                if (cx * cx + cz * cz > (double)radius * radius) continue;
            }
            else if (nSec >= 400) break;
            nSec++;
            for (int col = 0; col < gs * gs; col++)
            {
                if (!sec.Captured[col]) continue;
                if (!sec.TryGetTopRun(col, out ulong run)) continue;
                int pid = LodSection.RunPaletteId(run);
                if (pid < 0 || pid >= sec.Palette.Count) continue;
                LodPaletteEntry entry = sec.Palette[pid];
                if (entry.BlockId <= 0 || entry.BlockId >= blocks.Count) continue;
                string path = blocks[entry.BlockId].Code?.Path ?? "";
                int bucket = MayBucket(path);
                nCol++;
                bool baked = (entry.Flags & LodPaletteEntry.FlagBaked) != 0;
                acc[bucket, baked ? 0 : 1]++;
                if (baked)
                {
                    LodPaletteRepair.Channels(entry.Color, out int r, out int g, out int b, out _, out _);
                    acc[bucket, 2] += r;
                    acc[bucket, 3] += g;
                    acc[bucket, 4] += b;
                    if (bucket == 1 && leafPath.Length == 0) { leafPath = path; leafRgb = entry.Color; }
                    if (bucket == 2 && pinePath.Length == 0) { pinePath = path; pineRgb = entry.Color; }
                    if (bucket == 3 && grassPath.Length == 0) { grassPath = path; grassRgb = entry.Color; }
                }
            }
        }

        return "\"radius\":" + radius
            + ",\"nSec\":" + nSec + ",\"nCol\":" + nCol
            + "," + BucketFields("snow", acc, 0)
            + "," + BucketFields("leaves", acc, 1)
            + "," + BucketFields("pine", acc, 2)
            + "," + BucketFields("grass", acc, 3)
            + "," + BucketFields("other", acc, 4)
            + ",\"leafPath\":\"" + leafPath.Replace("\\", "/").Replace("\"", "'")
            + "\",\"leafRgb\":" + leafRgb
            + ",\"pinePath\":\"" + pinePath.Replace("\\", "/").Replace("\"", "'")
            + "\",\"pineRgb\":" + pineRgb
            + ",\"grassPath\":\"" + grassPath.Replace("\\", "/").Replace("\"", "'")
            + "\",\"grassRgb\":" + grassRgb;
    }

    static int MayBucket(string path)
    {
        if (path.Contains("snow", StringComparison.Ordinal)) return 0;
        if (path.Contains("leaves", StringComparison.Ordinal)) return 1;
        if (path.Contains("pine", StringComparison.Ordinal)
            || path.Contains("conifer", StringComparison.Ordinal)) return 2;
        if (path.Contains("grass", StringComparison.Ordinal)) return 3;
        return 4;
    }

    static string BucketFields(string name, int[,] acc, int bucket)
    {
        int baked = acc[bucket, 0];
        int live = acc[bucket, 1];
        int n = baked > 0 ? baked : 1;
        return "\"" + name + "Baked\":" + baked
            + ",\"" + name + "Live\":" + live
            + ",\"" + name + "R\":" + (acc[bucket, 2] / n)
            + ",\"" + name + "G\":" + (acc[bucket, 3] / n)
            + ",\"" + name + "B\":" + (acc[bucket, 4] / n);
    }

    // #region agent log
    static void AgentEscLog(string message, string hypothesisId, string extra)
    {
        try
        {
            System.IO.File.AppendAllText(
                @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                "{\"sessionId\":\"40cccb\",\"runId\":\"post-fix-4\",\"hypothesisId\":\"" + hypothesisId
                + "\",\"location\":\"LodLoginBake\",\"message\":\"" + message
                + "\",\"data\":{" + extra + "},\"timestamp\":"
                + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n");
        }
        catch
        {
        }
    }

    static void AgentReleaseLog(string message, string hypothesisId, string extra)
    {
        try
        {
            System.IO.File.AppendAllText(
                @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                "{\"sessionId\":\"40cccb\",\"runId\":\"post-fix\",\"hypothesisId\":\"" + hypothesisId
                + "\",\"location\":\"LodLoginBake\",\"message\":\"" + message
                + "\",\"data\":{" + extra + "},\"timestamp\":"
                + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n");
        }
        catch
        {
        }
    }
    // #endregion

    /// <summary>
    /// Single idempotent teardown for abort, error, cancel, and world leave.
    /// </summary>
    void Teardown(bool success, bool keepResume = false) =>
        ReleaseResources(success, keepResume);

    void HoldPlayerControls()
    {
        overlay.EnsureInputBlocked();
        LodPauseOnStartCompat.KeepUnpaused(capi);
        CloseBlockingDialogs();

        IClientPlayer player = capi.World.Player;
        EntityPlayer entity = player.Entity;
        EntityControls controls = entity.Controls;

        if (phase == Phase.Done) return;

        audioMute.EnsureMuted();
        timeFreeze.EnsureFrozen();
        gameMode.EnsureCreative();
        viewBoost.EnsureBoosted();
        LodLoginBakeMouseDelta.Drain(capi);

        bool recapture = !resuming
            && phase is Phase.OverlayWarmup or Phase.WaitingForWorld;
        CaptureRestorePose(allowOverwrite: recapture);

        HoldPlayerPose(entity);
        // Drain leftover MouseDelta instead of snapping look after visits. Camera
        // lock during Auditing/Draining/Stabilizing fights look while the world is
        // already on screen and dumps leftover delta when the overlay hides.
        if (phase is Phase.OverlayWarmup or Phase.WaitingForWorld or Phase.Sweeping)
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
        expireRecapture = resume.ExpireRecapture
            || (resume.SweepModeLabel?.IndexOf("season", StringComparison.OrdinalIgnoreCase) >= 0);
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

        int left = pending.Count + scoutFill.LiveCount + scoutReady.Count
            + (currentKey != null ? 1 : 0);
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
        snap.ExpireRecapture = expireRecapture;
        snap.Completed = completedKeys.ToList();

        var pendingList = new List<long>(pending);
        scoutFill.CopyLiveKeys(pendingList);
        foreach (long key in scoutReady)
            pendingList.Add(key);
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
        if (phase is Phase.OverlayWarmup or Phase.WaitingForWorld)
        {
            // Do not snap to a maybe-stale capture while spawn is still settling.
            entity.Pos.Motion.Set(0, 0, 0);
            return;
        }

        if (!restoreCaptured)
        {
            entity.Pos.Motion.Set(0, 0, 0);
            return;
        }

        // Scout entities stream distant columns. Never HoldQuiet the player onto
        // visit cells — that reintroduced teleport hops after 1.0.25 dropped ApplyQuiet.
        entity.Pos.SetFrom(restorePos);
        entity.Pos.Motion.Set(0, 0, 0);
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
        EntityPlayer entity = capi.World.Player.Entity;
        LodLoginBakeInputLock.Release(entity.Controls);
        LodLoginBakeInputLock.Release(entity.ServerControls);
    }

    void CloseBlockingDialogs()
    {
        var open = capi.Gui.OpenedGuis;
        for (int i = open.Count - 1; i >= 0; i--)
        {
            GuiDialog dlg = open[i];
            if (dlg is LodLoginBakeInputGuard) continue;
            if (dlg.DialogType == EnumDialogType.HUD) continue;
            if (LodLoginBakeCharacterWait.IsProtectedDialog(dlg)) continue;
            dlg.TryClose();
        }
    }

    void RestorePlayerPose(bool requestChunks = false)
    {
        if (!restoreCaptured) return;

        IClientPlayer player = capi.World.Player;
        EntityPlayer entity = player.Entity;
        int desired = 256;
        try { desired = player.WorldData.DesiredViewDistance; } catch { }
        int lastApproved = 0;
        try { lastApproved = player.WorldData.LastApprovedViewDistance; } catch { }
        int radius = 0;
        if (requestChunks)
        {
            radius = LodLoginBakePlayerMove.SpawnRestoreRadius(desired);
            // Server has not approved the 256 disk yet. Asking for 19x19 columns
            // here is what dumped 361 SetChunkColumnVisible calls on land.
            if (lastApproved <= 0)
                radius = Math.Min(radius, 4);
        }
        LodLoginBakePlayerMove.ApplyQuietFrom(capi, entity, restorePos, requestChunks, radius);
        LockPlayerCamera(capi, player, restorePos, restoreCameraPos);
        LodLoginBakeMouseDelta.Drain(capi);
        // #region agent log
        try
        {
            System.IO.File.AppendAllText(
                @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                "{\"sessionId\":\"40cccb\",\"hypothesisId\":\"H3\",\"location\":\"LodLoginBake.RestorePlayerPose\",\"message\":\"spawn-restore\",\"data\":{\"requestChunks\":" + (requestChunks ? "true" : "false") + ",\"radius\":" + radius + ",\"lastApproved\":" + lastApproved + ",\"desired\":" + desired + "},\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n");
        }
        catch { }
        // #endregion
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
        total > 0
        && (phase == Phase.Sweeping || phase == Phase.OverlayWarmup || phase == Phase.WaitingForWorld)
            ? detail + sweepTiming.EtaSuffix(finished, total)
            : detail;

    void LogPlannedStops(string where)
    {
        // #region agent log
        try
        {
            System.IO.File.AppendAllText(
                @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                "{\"sessionId\":\"40cccb\",\"runId\":\"size-4x\",\"hypothesisId\":\"H-SIZE\",\"location\":\"LodLoginBake."
                + where + "\",\"message\":\"planned-stops\",\"data\":{\"planned\":" + total
                + ",\"minStops\":" + LodLoginSweepTiming.MinVisitStops
                + ",\"maxStops\":" + LodLoginSweepTiming.MaxVisitStops
                + ",\"bootstrapMax\":" + LodLoginSweepBootstrap.BootstrapMaxVisitStops
                + ",\"revisitMax\":" + LodLoginSweepBootstrap.RevisitMaxVisitStops
                + ",\"retryMax\":" + LodLoginSweepBootstrap.RetryMaxVisitStops
                + ",\"targetMaxSec\":" + LodLoginSweepTiming.TargetMaxSec.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"secPerStop\":" + LodLoginSweepTiming.MachineSecPerStop.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"boostVd\":" + viewBoost.BoostedViewDistanceBlocks
                + ",\"chunkRadius\":" + viewBoost.ChunkSweepRadiusChunks
                + ",\"label\":\"" + (sweepModeLabel ?? "").Replace("\\", "\\\\").Replace("\"", "'")
                + "\"},\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n");
        }
        catch { }
        // #endregion
    }

    public void Dispose()
    {
        if (Active)
            SaveResumeSnapshot();
        Teardown(success: false, keepResume: true);
    }

    // #region agent log
    static void LogSeasonRebake(
        ICoreClientAPI capi,
        LodLoginSweepComplete? completeMarker,
        int rebakeVisited,
        int rebakeLoaded,
        int changedSections,
        int paletteChanges,
        bool within30,
        bool skip,
        int elapsedMs,
        int diskCells,
        int storedInDisk)
    {
        try
        {
            IGameCalendar cal = capi.World.Calendar;
            var pos = new BlockPos(
                (int)capi.World.Player.Entity.Pos.X,
                capi.World.SeaLevel,
                (int)capi.World.Player.Entity.Pos.Z);
            string nowSeason = LodLoginSweepResume.SeasonSlug(cal.GetSeason(pos));
            System.IO.File.AppendAllText(
                @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                "{\"sessionId\":\"40cccb\",\"runId\":\"post-fix\",\"hypothesisId\":\"H-S-disk\",\"location\":\"LodLoginBake.LogSeasonRebake\",\"message\":\"season-rebake\",\"data\":{\"visited\":"
                + rebakeVisited + ",\"loaded\":" + rebakeLoaded + ",\"changedSections\":" + changedSections
                + ",\"paletteChanges\":" + paletteChanges
                + ",\"elapsedMs\":" + elapsedMs
                + ",\"radiusBlocks\":" + LodLoginSweepBootstrap.EmptyCanvasBootstrapRadiusBlocks
                + ",\"diskCells\":" + diskCells
                + ",\"storedInDisk\":" + storedInDisk
                + ",\"month\":" + cal.Month
                + ",\"nowSeason\":\"" + nowSeason
                + "\",\"savedSeason\":\"" + (completeMarker?.Season ?? "")
                + "\",\"within30\":" + (within30 ? "true" : "false")
                + ",\"skip\":" + (skip ? "true" : "false")
                + "},\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n");
        }
        catch { }
    }
    // #endregion
}
