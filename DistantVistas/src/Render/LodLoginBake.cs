using System.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// One-shot login bake of every cached section key. Runs behind the loading overlay,
/// then waits for frame time to settle before play is released.
/// </summary>
public sealed class LodLoginBake
{
    public enum Phase { Baking, Stabilizing, Done }

    const int SectionsPerTick = 6;
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
    readonly EntityPos holdPos = new();
    readonly Stopwatch stabilizeClock = new();

    int total;
    int finished;
    Phase phase = Phase.Baking;
    double phaseStartedSec;
    double windowStartedSec;
    bool holdPoseCaptured;

    public Phase CurrentPhase => phase;
    public bool Active => phase != Phase.Done;
    public float Progress
    {
        get
        {
            if (phase == Phase.Baking)
                return total <= 0 ? 0.05f : (float)finished / total * 0.85f;
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
        finished = 0;
        phase = Phase.Baking;
        phaseStartedSec = capi.World.Calendar.TotalHours;
        holdPoseCaptured = false;
        stabilizeWindow.Clear();
        windowMedians.Clear();

        foreach (long key in pipeline.World.HasDataSet)
            pending.Enqueue(key);
        total = pending.Count;

        overlay.Show();
        overlay.UpdateProgress(Progress, total == 0
            ? "No visited land to bake."
            : $"Baking {total} visited sections…");

        if (total == 0)
        {
            BeginStabilizing();
        }
    }

    public void Tick(float dt)
    {
        if (phase == Phase.Done) return;

        HoldPlayerPose();

        if (phase == Phase.Baking)
        {
            TickBake();
            return;
        }

        TickStabilizing(dt);
    }

    void TickBake()
    {
        int budget = SectionsPerTick;
        while (budget > 0 && pending.Count > 0)
        {
            long key = pending.Dequeue();
            BakeOne(key);
            finished++;
            budget--;
        }

        overlay.UpdateProgress(Progress,
            $"Baking visited land… {finished}/{total} ({pending.Count} left)");

        if (pending.Count > 0) return;
        BeginStabilizing();
    }

    void BakeOne(long key)
    {
        LodWorld world = pipeline.World;
        LodSection? section = world.Sections.TryGetValue(key, out LodSection? resident)
            ? resident
            : world.LoadFromStore?.Invoke(key);

        if (section == null) return;

        if (!world.Sections.ContainsKey(key))
            world.InstallLoaded(key, section);

        if (!LodSeasonBake.SectionNeedsLoginBake(section)
            && LodSeasonBake.SectionHasBakedEntries(section))
        {
            return;
        }

        int changed = LodSeasonBake.BakeSection(
            capi.World, section, key, plantTintFallback, untintedOf);
        if (changed > 0)
        {
            world.MarkChanged(key);
            pipeline.InvalidateGpuMesh?.Invoke(key);
            world.RenderDirty.Add(key);
        }
    }

    void BeginStabilizing()
    {
        phase = Phase.Stabilizing;
        stabilizeClock.Restart();
        phaseStartedSec = 0;
        windowStartedSec = 0;
        overlay.UpdateProgress(Progress, "Waiting for frame time to settle…");
    }

    void TickStabilizing(float dt)
    {
        double ms = dt > 0 ? dt * 1000.0 : 16.0;
        stabilizeWindow.Add(ms);
        if (stabilizeWindow.Count > StabilizeWindowFrames)
            stabilizeWindow.RemoveAt(0);

        double now = stabilizeClock.Elapsed.TotalSeconds;
        if (stabilizeWindow.Count >= StabilizeWindowFrames
            && now - windowStartedSec >= 1.0)
        {
            stabilizeWindow.Sort();
            windowMedians.Add(stabilizeWindow[stabilizeWindow.Count / 2]);
            stabilizeWindow.Clear();
            windowStartedSec = now;
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
        if (overlay.IsOpened()) overlay.TryClose();
        capi.Logger.Notification(
            "[DistantVistas] Login bake finished: {0} sections, season locked until relog.",
            finished);
    }

    void HoldPlayerPose()
    {
        var entity = capi.World.Player.Entity;
        if (!holdPoseCaptured)
        {
            holdPos.SetFrom(entity.Pos);
            holdPoseCaptured = true;
        }

        entity.Pos.SetFrom(holdPos);
        entity.Pos.Motion.Set(0, 0, 0);
        entity.Controls.MovespeedMultiplier = 0f;
    }

    public void Dispose()
    {
        if (overlay.IsOpened()) overlay.TryClose();
        capi.World.Player.Entity.Controls.MovespeedMultiplier = 1f;
    }
}
