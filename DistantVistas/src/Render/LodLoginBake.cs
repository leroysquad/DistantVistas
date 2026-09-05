using System.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Login visit sweep — gather live season truth by being at each visited square.
///
/// Purpose (locked): teleport the player (hidden behind a full-screen overlay) to every
/// previously visited L0 canvas cell so vanilla streams real chunks. The mod re-captures
/// voxel columns from that loaded terrain (snow blocks, leaf species, grass tops) and
/// season-bakes palette colours per column top. Those canvases persist to SQLite and
/// stay painted in near and far LOD until the next login. This is NOT a finalize-time
/// recolor of unloaded cache rows.
/// </summary>
public sealed class LodLoginBake
{
    public enum Phase { WaitingForWorld, Sweeping, Auditing, Draining, Stabilizing, Done }

    enum StopPhase { Teleport, WaitChunks, Capture, Bake, Done }

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
    readonly LodSeasonSampleExporter seasonSamples;
    readonly LodLoginSweepTiming sweepTiming = new();
    readonly Block? plantTintFallback;
    readonly System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf;
    readonly Queue<long> pending = new();
    readonly List<double> stabilizeWindow = new(128);
    readonly List<double> windowMedians = new(8);
    readonly EntityPos restorePos = new();
    readonly Stopwatch stabilizeClock = new();

    int total;
    int finished;
    int worldWaitTicks;
    int drainTicks;
    int auditTicks;
    int resweepRound;
    bool retryingMisses;
    LodLoginSweepPlanMode sweepMode = LodLoginSweepPlanMode.RevisitVisited;
    string sweepModeLabel = "Revisiting visited land";
    Phase phase = Phase.WaitingForWorld;
    bool released;
    long? currentKey;
    StopPhase stopPhase;
    int stopTicks;
    bool restoreCaptured;

    public Phase CurrentPhase => phase;
    public bool Active => phase != Phase.Done;
    public float Progress
    {
        get
        {
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
        seasonSamples = new LodSeasonSampleExporter(capi);
    }

    public void Begin()
    {
        pending.Clear();
        LodLoginSweepPlan plan = LodLoginSweepBootstrap.Plan(pipeline.World, capi.World);
        sweepMode = plan.Mode;
        sweepModeLabel = plan.ModeLabel;
        foreach (long key in plan.Keys)
            pending.Enqueue(key);
        total = pending.Count;
        sweepTiming.Begin();
        finished = 0;
        worldWaitTicks = 0;
        drainTicks = 0;
        auditTicks = 0;
        resweepRound = 0;
        retryingMisses = false;
        phase = Phase.WaitingForWorld;
        released = false;
        currentKey = null;
        restoreCaptured = false;
        stabilizeWindow.Clear();
        windowMedians.Clear();

        overlay.Show();
        pipeline.DeferLegacyHeal = true;
        audioMute.EnsureMuted();
        timeFreeze.EnsureFrozen();
        gameMode.EnsureCreative();
        hudHide.EnsureHidden();
        playerHide.EnsureHidden();
        capi.Logger.Notification(
            "[DistantVistas] Login visit sweep: {0} — {1} L0 region{2}.",
            sweepModeLabel, total, total == 1 ? "" : "s");
        seasonSamples.BeginSession(sweepMode, sweepModeLabel, total);
        overlay.UpdateProgress(Progress, StatusWithEta($"{sweepModeLabel} — waiting for world to load…"));

        if (total == 0)
        {
            BeginAuditing();
            return;
        }
    }

    public void Tick(float dt)
    {
        if (phase == Phase.Done) return;

        HoldPlayerControls();

        switch (phase)
        {
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
    }

    void TickWaitingForWorld()
    {
        worldWaitTicks++;
        if (!LodLoginSweep.IsWorldReady(capi.World))
        {
            overlay.UpdateProgress(Progress,
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
        BeginNextStop();
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
            BeginNextStop();
            return;
        }

        long key = currentKey.Value;
        switch (stopPhase)
        {
            case StopPhase.Teleport:
                stopPhase = StopPhase.WaitChunks;
                stopTicks = 0;
                break;

            case StopPhase.WaitChunks:
                stopTicks++;
                if (!LodLoginSweep.AllMapChunksLoaded(capi.World.BlockAccessor, key)
                    && stopTicks < LodLoginSweep.MaxChunkWaitTicks)
                {
                    overlay.UpdateProgress(Progress,
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
                    overlay.UpdateProgress(Progress,
                        StatusWithEta($"{VisitPrefix()}capturing live terrain… ({Pct(finished, total)})"));
                    return;
                }
                stopPhase = StopPhase.Bake;
                break;

            case StopPhase.Bake:
                BakeAndPersist(key);
                finished++;
                sweepTiming.NoteFinished(finished);
                stopPhase = StopPhase.Done;
                currentKey = null;
                overlay.UpdateProgress(Progress,
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
        overlay.UpdateProgress(Progress,
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
        phase = Phase.Auditing;
        auditTicks = 0;
        currentKey = null;
        overlay.UpdateProgress(Progress, StatusWithEta("Checking visited regions for gaps…"));
    }

    void TickAuditing()
    {
        auditTicks++;
        pipeline.DrainLoginPersistence(8);

        if (auditTicks < AuditSettleTicks)
        {
            overlay.UpdateProgress(Progress, StatusWithEta("Checking visited regions for gaps…"));
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
        overlay.UpdateProgress(Progress,
            StatusWithEta($"Retrying {total} missed region{(total == 1 ? "" : "s")} (pass {resweepRound})…"));
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
        if (pending.Count == 0) return;
        long key = pending.Dequeue();
        currentKey = key;
        stopPhase = StopPhase.Teleport;
        stopTicks = 0;

        (double x, double y, double z) = LodLoginSweep.VisitPosition(capi.World, key);
        TeleportPlayer(x, y, z);
        overlay.UpdateProgress(Progress,
            StatusWithEta($"{VisitPrefix()}moving to region… ({Pct(finished, total)})"));
    }

    void SweepColumnsAround(long l0Key)
    {
        (double x, _, double z) = LodLoginSweep.VisitPosition(capi.World, l0Key);
        int cx = (int)Math.Floor(x / GlobalConstants.ChunkSize);
        int cz = (int)Math.Floor(z / GlobalConstants.ChunkSize);
        pipeline.SweepLoadedColumns(cx, cz, 2);
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

    void BeginDraining()
    {
        RestorePlayerPose();
        phase = Phase.Draining;
        drainTicks = 0;
        overlay.UpdateProgress(Progress, "Updating distant land…");
    }

    void BeginStabilizing()
    {
        RestorePlayerPose();
        phase = Phase.Stabilizing;
        stabilizeClock.Restart();
        stabilizeWindow.Clear();
        windowMedians.Clear();
        overlay.UpdateProgress(Progress, "Waiting for frame time to settle…");
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

        overlay.UpdateProgress(Progress,
            $"Stabilizing frame time… {windowMedians.Count}/{StabilizeWindowsRequired}");

        bool stable = windowMedians.Count >= StabilizeWindowsRequired
            && windowMedians.All(m => m <= StabilizeMaxMs);
        bool timedOut = now >= StabilizeTimeoutSec;
        if (!stable && !timedOut) return;

        Finish();
    }

    void Finish()
    {
        overlay.UpdateProgress(1f, "Ready.");
        Teardown(success: true);
        capi.Logger.Notification(
            "[DistantVistas] Login visit sweep finished: {0}/{1} regions captured and locked until relog.",
            finished, total);
    }

    /// <summary>
    /// Single idempotent teardown for success, abort, error, and world leave.
    /// Undoes overlay, pose, controls, audio mute, and calendar freeze with no leftovers.
    /// </summary>
    void Teardown(bool success)
    {
        if (released) return;

        released = true;
        phase = Phase.Done;
        pipeline.DeferLegacyHeal = false;

        if (success)
            renderer.LoginBakeComplete = true;

        try
        {
            overlay.Hide();
        }
        catch
        {
            // Best-effort.
        }

        try
        {
            RestorePlayerPose();
        }
        catch
        {
            // Best-effort.
        }

        try
        {
            ReleasePlayerControls();
        }
        catch
        {
            // Best-effort.
        }

        try
        {
            audioMute.Restore();
        }
        catch
        {
            // Best-effort.
        }

        try
        {
            timeFreeze.Restore();
        }
        catch
        {
            // Best-effort.
        }

        try
        {
            gameMode.Restore();
        }
        catch
        {
            // Best-effort.
        }

        try
        {
            hudHide.Restore();
        }
        catch
        {
            // Best-effort.
        }

        try
        {
            playerHide.Restore();
        }
        catch
        {
            // Best-effort.
        }

        try
        {
            seasonSamples.Dispose();
        }
        catch
        {
            // Best-effort.
        }
    }

    void HoldPlayerControls()
    {
        audioMute.EnsureMuted();
        timeFreeze.EnsureFrozen();
        gameMode.EnsureCreative();
        hudHide.EnsureHidden();
        overlay.EnsureInputBlocked();
        CloseBlockingDialogs();

        IClientPlayer player = capi.World.Player;
        EntityPlayer entity = player.Entity;
        EntityControls controls = entity.Controls;

        if (!restoreCaptured)
        {
            restorePos.SetFrom(entity.Pos);
            restoreCaptured = true;
        }

        HoldPlayerPose(entity);
        LockPlayerCamera(capi, player, restorePos);
        BlockPlayerInput(controls);
        playerHide.EnsureHidden();

        capi.Input.MouseGrabbed = true;
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

    static void LockPlayerCamera(ICoreClientAPI capi, IClientPlayer player, EntityPos pose)
    {
        player.CameraYaw = pose.Yaw;
        player.CameraPitch = pose.Pitch;
        player.Entity.Pos.Yaw = pose.Yaw;
        player.Entity.Pos.Pitch = pose.Pitch;
        capi.Input.MouseYaw = pose.Yaw;
        capi.Input.MousePitch = pose.Pitch;
    }

    static void BlockPlayerInput(EntityControls controls)
    {
        controls.StopAllMovement();
        controls.MovespeedMultiplier = 0f;
        controls.WalkVector.Set(0, 0, 0);
        controls.FlyVector.Set(0, 0, 0);
        controls.IsFlying = true;
        controls.NoClip = true;
        controls.Gliding = false;
        controls.DetachedMode = false;

        for (int i = 0; i <= (int)EnumEntityAction.InWorldRightMouseDown; i++)
            controls[(EnumEntityAction)i] = false;
    }

    void ReleasePlayerControls()
    {
        capi.World.Player.Entity.Controls.MovespeedMultiplier = 1f;
        capi.Input.MouseGrabbed = false;
    }

    void CloseBlockingDialogs()
    {
        var open = capi.Gui.OpenedGuis;
        for (int i = open.Count - 1; i >= 0; i--)
        {
            GuiDialog dlg = open[i];
            if (dlg.DialogType == EnumDialogType.HUD) continue;
            dlg.TryClose();
        }
    }

    void RestorePlayerPose()
    {
        if (!restoreCaptured) return;
        LodLoginBakePlayerMove.ApplyQuietFrom(capi, capi.World.Player.Entity, restorePos);
    }

    void TeleportPlayer(double x, double y, double z) =>
        LodLoginBakePlayerMove.ApplyQuiet(capi, capi.World.Player.Entity, x, y, z);

    static string Pct(int done, int total) =>
        total <= 0 ? "0%" : $"{done * 100 / total}%";

    string StatusWithEta(string detail) =>
        phase == Phase.Sweeping && total > 0
            ? detail + sweepTiming.EtaSuffix(finished, total)
            : detail;

    public void Dispose() => Teardown(success: false);
}
