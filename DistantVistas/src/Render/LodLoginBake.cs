using System.Diagnostics;
using System.Globalization;
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
    public enum Phase { WaitingForWorld, Sweeping, Draining, Stabilizing, Done }

    enum StopPhase { Teleport, WaitChunks, Capture, Bake, Done }

    const int StabilizeWindowFrames = 90;
    const int StabilizeWindowsRequired = 4;
    const double StabilizeMaxMs = 28.0;
    const double StabilizeTimeoutSec = 12.0;
    const int MaxDrainTicks = 600;

    readonly ICoreClientAPI capi;
    readonly LodPipeline pipeline;
    readonly LodTerrainRenderer renderer;
    readonly LodLoginBakeOverlay overlay;
    readonly LodLoginBakeAudioMute audioMute;
    readonly LodLoginBakeTimeFreeze timeFreeze;
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
    Phase phase = Phase.WaitingForWorld;
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
                return total <= 0 ? 0.05f : 0.05f + (float)finished / total * 0.75f;
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
    }

    public void Begin()
    {
        pending.Clear();
        foreach (long key in LodLoginSweep.VisitedL0Keys(pipeline.World))
            pending.Enqueue(key);
        total = pending.Count;
        finished = 0;
        worldWaitTicks = 0;
        drainTicks = 0;
        phase = Phase.WaitingForWorld;
        currentKey = null;
        restoreCaptured = false;
        stabilizeWindow.Clear();
        windowMedians.Clear();

        overlay.Show();
        audioMute.EnsureMuted();
        timeFreeze.EnsureFrozen();
        overlay.UpdateProgress(Progress, "Waiting for world to load…");

        if (total == 0)
            BeginDraining();
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
                worldWaitTicks > LodLoginSweep.MaxWorldReadyTicks
                    ? "World slow to load — continuing anyway…"
                    : "Waiting for world and map to load…");
            if (worldWaitTicks < LodLoginSweep.MaxWorldReadyTicks) return;
        }

        if (total == 0)
        {
            BeginDraining();
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
                BeginDraining();
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
                        $"Visiting {finished + 1}/{total} — loading terrain… ({stopTicks})");
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
                        $"Visiting {finished + 1}/{total} — capturing live terrain… ({Pct(finished, total)})");
                    return;
                }
                stopPhase = StopPhase.Bake;
                break;

            case StopPhase.Bake:
                BakeAndPersist(key);
                finished++;
                stopPhase = StopPhase.Done;
                currentKey = null;
                overlay.UpdateProgress(Progress,
                    $"Visited {finished}/{total} ({Pct(finished, total)})");
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
            $"Visiting {finished + 1}/{total} — moving to region… ({Pct(finished, total)})");
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

        LodSeasonBake.BakeSection(
            capi.World, section, l0Key, plantTintFallback, untintedOf);

        world.MarkChanged(l0Key);
        pipeline.InvalidateGpuMesh?.Invoke(l0Key);
        world.RenderDirty.Add(l0Key);
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
        phase = Phase.Done;
        renderer.LoginBakeComplete = true;
        overlay.UpdateProgress(1f, "Ready.");
        overlay.Hide();
        ReleaseAll();
        capi.Logger.Notification(
            "[DistantVistas] Login visit sweep finished: {0}/{1} regions captured and locked until relog.",
            finished, total);
    }

    void ReleaseAll()
    {
        ReleasePlayerControls();
        audioMute.Restore();
        timeFreeze.Restore();
    }

    void HoldPlayerControls()
    {
        audioMute.EnsureMuted();
        timeFreeze.EnsureFrozen();
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

        capi.Input.MouseGrabbed = true;
    }

    void HoldPlayerPose(EntityPlayer entity)
    {
        if (phase == Phase.Sweeping && currentKey != null)
        {
            (double x, double y, double z) = LodLoginSweep.VisitPosition(capi.World, currentKey.Value);
            entity.Pos.SetPos(x, y, z);
        }
        else
        {
            entity.Pos.SetFrom(restorePos);
        }

        entity.Pos.Motion.Set(0, 0, 0);
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
        controls.IsFlying = false;
        controls.NoClip = false;
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
            if (dlg == overlay || dlg.DialogType == EnumDialogType.HUD) continue;
            dlg.TryClose();
        }
    }

    void RestorePlayerPose()
    {
        if (!restoreCaptured) return;
        var entity = capi.World.Player.Entity;
        entity.Pos.SetFrom(restorePos);
        entity.Pos.Motion.Set(0, 0, 0);
    }

    void TeleportPlayer(double x, double y, double z)
    {
        var entity = capi.World.Player.Entity;
        entity.Pos.SetPos(x, y, z);
        entity.Pos.Motion.Set(0, 0, 0);
        capi.SendChatMessage(string.Format(CultureInfo.InvariantCulture,
            "/tp ={0:0} ={1:0} ={2:0}", x, y, z));
    }

    static string Pct(int done, int total) =>
        total <= 0 ? "0%" : $"{done * 100 / total}%";

    public void Dispose()
    {
        try
        {
            RestorePlayerPose();
            overlay.Hide();
        }
        finally
        {
            ReleaseAll();
        }
    }
}
