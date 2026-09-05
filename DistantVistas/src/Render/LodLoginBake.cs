using System.Diagnostics;
using System.Globalization;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Login visit sweep: wait for the world to stream, teleport the player (hidden) to each
/// visited L0 section, re-capture live terrain, season-bake, restore pose, then release.
/// </summary>
public sealed class LodLoginBake
{
    public enum Phase { WaitingForWorld, Sweeping, Stabilizing, Done }

    enum StopPhase { Teleport, WaitChunks, Capture, Bake, Done }

    const int StabilizeWindowFrames = 90;
    const int StabilizeWindowsRequired = 4;
    const double StabilizeMaxMs = 28.0;
    const double StabilizeTimeoutSec = 12.0;

    readonly ICoreClientAPI capi;
    readonly LodPipeline pipeline;
    readonly LodTerrainRenderer renderer;
    readonly LodLoginBakeOverlay overlay;
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
                return total <= 0 ? 0.05f : 0.05f + (float)finished / total * 0.80f;
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
    }

    public void Begin()
    {
        pending.Clear();
        foreach (long key in LodLoginSweep.VisitedL0Keys(pipeline.World))
            pending.Enqueue(key);
        total = pending.Count;
        finished = 0;
        worldWaitTicks = 0;
        phase = Phase.WaitingForWorld;
        currentKey = null;
        restoreCaptured = false;
        stabilizeWindow.Clear();
        windowMedians.Clear();

        overlay.Show();
        overlay.UpdateProgress(Progress, "Waiting for world to load…");

        if (total == 0)
            BeginStabilizing();
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
            BeginStabilizing();
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
                BeginStabilizing();
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
                        $"Visiting {finished + 1}/{total} — capturing… ({Pct(finished, total)})");
                    return;
                }
                stopPhase = StopPhase.Bake;
                break;

            case StopPhase.Bake:
                BakeSection(key);
                finished++;
                stopPhase = StopPhase.Done;
                currentKey = null;
                overlay.UpdateProgress(Progress,
                    $"Visited {finished}/{total} ({Pct(finished, total)})");
                break;
        }
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

    void BakeSection(long l0Key)
    {
        LodWorld world = pipeline.World;
        if (!world.Sections.TryGetValue(l0Key, out LodSection? section))
        {
            section = world.LoadFromStore?.Invoke(l0Key);
            if (section != null) world.InstallLoaded(l0Key, section);
        }
        if (section == null) return;

        int changed = LodSeasonBake.BakeSection(
            capi.World, section, l0Key, plantTintFallback, untintedOf);
        if (changed > 0)
        {
            world.MarkChanged(l0Key);
            pipeline.InvalidateGpuMesh?.Invoke(l0Key);
            world.RenderDirty.Add(l0Key);
        }
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
        capi.World.Player.Entity.Controls.MovespeedMultiplier = 1f;
        overlay.UpdateProgress(1f, "Ready.");
        if (overlay.IsOpened()) overlay.TryClose();
        capi.Logger.Notification(
            "[DistantVistas] Login visit sweep finished: {0}/{1} regions, season locked until relog.",
            finished, total);
    }

    void HoldPlayerControls()
    {
        var entity = capi.World.Player.Entity;
        if (!restoreCaptured)
        {
            restorePos.SetFrom(entity.Pos);
            restoreCaptured = true;
        }

        if (phase == Phase.WaitingForWorld)
        {
            entity.Pos.SetFrom(restorePos);
            entity.Pos.Motion.Set(0, 0, 0);
        }

        entity.Controls.MovespeedMultiplier = 0f;
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
        RestorePlayerPose();
        if (overlay.IsOpened()) overlay.TryClose();
    }
}
