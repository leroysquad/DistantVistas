using System.Globalization;
using Vintagestory.API.Client;

namespace DistantVistas;

/// <summary>
/// Post soft-release play-mode throttle: steady GetColor / mesh / SQLite trickle so
/// background horizon bake stays below notice while the player moves. Full-disk
/// completion takes longer in wall clock — intentional.
/// </summary>
public static class PlayModeBakeBudget
{
    /// <summary>GetColor wall budget per game tick during DiscoverOnly play.</summary>
    public const double BaseExploreWallMs = 2.0;

    /// <summary>Captured columns painted per explore drain (steady trickle).</summary>
    public const int BaseExploreColumns = 48;

    /// <summary>New L0 starts per tick when nothing is in progress.</summary>
    public const int BaseExploreStarts = 1;

    /// <summary>Extra starts while capture results are stacked (still wall-capped).</summary>
    public const int BaseExploreStartsBusy = 1;

    /// <summary>SQLite rows flushed per tick after play-mode bake.</summary>
    public const int BaseSaveRows = 1;

    /// <summary>Parent mip propagations per tick (DiscoverOnly).</summary>
    public const int BaseMipPropagations = 2;

    /// <summary>Mesh job starts per render frame during background fill.</summary>
    public const int BaseMeshSchedules = 4;

    /// <summary>GPU mesh uploads per frame during background fill.</summary>
    public const int BaseMeshUploads = 3;

    /// <summary>Incomplete L0 parent fill jobs per frame.</summary>
    public const int BaseIncompleteFill = 4;

    /// <summary>Re-prioritize explore queue when player moves this many blocks/tick avg.</summary>
    public const double NearReprioritizeMotionBlocks = 6.0;

    /// <summary>Near ring for explore dequeue (blocks from player).</summary>
    public const int NearPriorityBlocks = 512;

    /// <summary>Paused menu or standing still — allow a little more background work.</summary>
    public const double IdleMultiplier = 2.5;

    /// <summary>Fast movement — cut background bake first.</summary>
    public const double HighMotionBlocksPerSec = 40.0;

    public const double MotionPenalty = 0.45;

    /// <summary>Recent frame or apply spike — yield background work.</summary>
    public const double HitchFrameMs = 22.0;

    public const double ApplySpikeMs = 8.0;

    public const double HitchPenalty = 0.35;

    static bool active;
    static double lastPx;
    static double lastPz;
    static bool hasPos;
    static double motionBlocksPerSec;
    static double emaFrameMs = 16.0;
    static bool reprioritizePending;
    static TickBudget lastBudget;

    public readonly struct TickBudget
    {
        public readonly double ExploreWallMs;
        public readonly int ExploreColumns;
        public readonly int ExploreStarts;
        public readonly int SaveRows;
        public readonly int MipPropagations;
        public readonly int MeshSchedules;
        public readonly int MeshUploads;
        public readonly int IncompleteFill;
        public readonly bool AllowExploreDrain;
        public readonly bool AllowFrontierScout;
        public readonly string Tier;

        public TickBudget(
            double exploreWallMs,
            int exploreColumns,
            int exploreStarts,
            int saveRows,
            int mipPropagations,
            int meshSchedules,
            int meshUploads,
            int incompleteFill,
            bool allowExploreDrain,
            bool allowFrontierScout,
            string tier)
        {
            ExploreWallMs = exploreWallMs;
            ExploreColumns = exploreColumns;
            ExploreStarts = exploreStarts;
            SaveRows = saveRows;
            MipPropagations = mipPropagations;
            MeshSchedules = meshSchedules;
            MeshUploads = meshUploads;
            IncompleteFill = incompleteFill;
            AllowExploreDrain = allowExploreDrain;
            AllowFrontierScout = allowFrontierScout;
            Tier = tier;
        }
    }

    public static bool Active => active;
    public static TickBudget Last => lastBudget;
    public static bool ReprioritizePending => reprioritizePending;

    public static void ActivateSoftRelease() => active = true;

    public static void Reset()
    {
        active = false;
        hasPos = false;
        motionBlocksPerSec = 0;
        emaFrameMs = 16.0;
        reprioritizePending = false;
        lastBudget = DefaultCatchUp();
    }

    public static void NotePlayerFrame(ICoreClientAPI? capi, float dt, double applyMs, bool discoverOnly)
    {
        if (!discoverOnly)
        {
            active = false;
            return;
        }

        active = true;
        double frameMs = dt > 0 ? dt * 1000.0 : 16.0;
        emaFrameMs = emaFrameMs * 0.85 + frameMs * 0.15;

        if (capi?.World?.Player?.Entity == null)
            return;

        try
        {
            EntityPos pos = capi.World.Player.Entity.Pos;
            if (hasPos && dt > 0)
            {
                double dx = pos.X - lastPx;
                double dz = pos.Z - lastPz;
                double blocks = Math.Sqrt(dx * dx + dz * dz);
                motionBlocksPerSec = motionBlocksPerSec * 0.8 + (blocks / dt) * 0.2;
                if (blocks >= NearReprioritizeMotionBlocks * dt)
                    reprioritizePending = true;
            }

            lastPx = pos.X;
            lastPz = pos.Z;
            hasPos = true;
        }
        catch { }

        _ = applyMs;
    }

    public static TickBudget Compute(bool discoverOnly, bool paused, int captureBacklog, double applyMs)
    {
        if (!discoverOnly)
        {
            lastBudget = DefaultCatchUp();
            return lastBudget;
        }

        active = true;
        double scale = 1.0;
        string tier = "steady";

        if (paused || motionBlocksPerSec < 2.0)
        {
            scale *= IdleMultiplier;
            tier = paused ? "paused" : "idle";
        }

        if (motionBlocksPerSec >= HighMotionBlocksPerSec)
        {
            scale *= MotionPenalty;
            tier = "motion";
        }

        if (emaFrameMs >= HitchFrameMs || applyMs >= ApplySpikeMs)
        {
            scale *= HitchPenalty;
            tier = emaFrameMs >= HitchFrameMs ? "hitch" : "apply-spike";
        }

        int exploreStarts = captureBacklog >= LodPipeline.CaptureBusyThreshold
            ? BaseExploreStartsBusy
            : BaseExploreStarts;

        bool allowExplore = applyMs <= ApplySpikeMs * 1.25 && emaFrameMs <= HitchFrameMs * 1.15;
        bool allowFrontier = allowExplore
            && motionBlocksPerSec < HighMotionBlocksPerSec * 1.2
            && emaFrameMs <= HitchFrameMs;

        lastBudget = new TickBudget(
            exploreWallMs: Math.Max(0.6, BaseExploreWallMs * scale),
            exploreColumns: Math.Max(16, (int)Math.Round(BaseExploreColumns * scale)),
            exploreStarts: Math.Max(1, (int)Math.Round(exploreStarts * scale)),
            saveRows: Math.Max(1, (int)Math.Round(BaseSaveRows * scale)),
            mipPropagations: Math.Max(1, (int)Math.Round(BaseMipPropagations * scale)),
            meshSchedules: Math.Max(2, (int)Math.Round(BaseMeshSchedules * scale)),
            meshUploads: Math.Max(1, (int)Math.Round(BaseMeshUploads * scale)),
            incompleteFill: Math.Max(2, (int)Math.Round(BaseIncompleteFill * scale)),
            allowExploreDrain: allowExplore,
            allowFrontierScout: allowFrontier,
            tier: tier);

        return lastBudget;
    }

    public static void ClearReprioritizeFlag() => reprioritizePending = false;

    public static double PlayerX => lastPx;
    public static double PlayerZ => lastPz;

    static TickBudget DefaultCatchUp() => new(
        exploreWallMs: LodExploreBake.DrainBudgetMs,
        exploreColumns: LodExploreBake.ColumnsPerDrain,
        exploreStarts: LodExploreBake.SectionsPerTick,
        saveRows: 2,
        mipPropagations: 4,
        meshSchedules: 12,
        meshUploads: 8,
        incompleteFill: 16,
        allowExploreDrain: true,
        allowFrontierScout: true,
        tier: "catch-up");

    public static void MaybeLogBudget(int explorePending, int renderDirty)
    {
        if (!active) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now % 5000 > 50) return;
        try
        {
            var inv = CultureInfo.InvariantCulture;
            System.IO.File.AppendAllText(
                @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                "{\"sessionId\":\"40cccb\",\"runId\":\"1033\",\"hypothesisId\":\"H-PLAY-BUDGET\","
                + "\"location\":\"PlayModeBakeBudget.Compute\",\"message\":\"play-budget\",\"data\":{"
                + "\"tier\":\"" + lastBudget.Tier + "\""
                + ",\"exploreWallMs\":" + lastBudget.ExploreWallMs.ToString("0.##", inv)
                + ",\"exploreCols\":" + lastBudget.ExploreColumns
                + ",\"meshSched\":" + lastBudget.MeshSchedules
                + ",\"saveRows\":" + lastBudget.SaveRows
                + ",\"motionBps\":" + motionBlocksPerSec.ToString("0.#", inv)
                + ",\"emaMs\":" + emaFrameMs.ToString("0.#", inv)
                + ",\"explorePending\":" + explorePending
                + ",\"renderDirty\":" + renderDirty
                + "},\"timestamp\":" + now + "}\n");
        }
        catch { }
    }
}
