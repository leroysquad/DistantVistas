using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace DistantVistas;

public enum LodLoginSweepPlanMode
{
    /// <summary>Canvas already has visited L0 keys — season refresh (budgeted spatial subsample).</summary>
    RevisitVisited,

    /// <summary>Only L0 keys that failed the miss audit — not the whole visited canvas.</summary>
    RevisitIncomplete,

    /// <summary>Empty canvas with a large ocean near spawn — ocean body + coastline.</summary>
    BootstrapCoastGuard,

    /// <summary>Empty canvas without a dominant ocean — disk around spawn.</summary>
    BootstrapRadius,
}

/// <summary>
/// Result of planning which L0 cells the login visit sweep should touch.
/// </summary>
public readonly struct LodLoginSweepPlan
{
    public LodLoginSweepPlanMode Mode { get; }
    public IReadOnlyList<long> Keys { get; }
    public string ModeLabel { get; }

    public LodLoginSweepPlan(LodLoginSweepPlanMode mode, IReadOnlyList<long> keys, string modeLabel)
    {
        Mode = mode;
        Keys = keys;
        ModeLabel = modeLabel;
    }
}

/// <summary>
/// Plans which L0 cells the login visit sweep should touch. Existing visited canvases
/// are spatially subsampled to a wall-clock budget; an empty cache bootstraps a
/// coast-guard ocean sweep or a large radius around spawn.
/// </summary>
public static class LodLoginSweepBootstrap
{
    /// <summary>Default bootstrap probe radius for empty canvas (ocean/land classification).</summary>
    public const int EmptyCanvasBootstrapRadiusBlocks = 6000;

    /// <summary>
    /// Hard cap on bootstrap visit stops (~2.5 min at <see cref="LodLoginSweepTiming.InitialSecPerStop"/>).
    /// </summary>
    public static int BootstrapMaxVisitStops =>
        LodLoginSweepTiming.BootstrapCellBudget(LodLoginSweepTiming.InitialSecPerStop);

    /// <summary>
    /// Hard cap on season-revisit stops (~5 min at <see cref="LodLoginSweepTiming.InitialSecPerStop"/>).
    /// Large visited canvases are spatially subsampled — never every L0 key.
    /// </summary>
    public static int RevisitMaxVisitStops =>
        LodLoginSweepTiming.RevisitCellBudget(LodLoginSweepTiming.InitialSecPerStop);

    /// <summary>Ocean cells must span at least this many blocks to trigger coast-guard mode.</summary>
    public const int LargeOceanMinSpanBlocks = 1500;

    /// <summary>Minimum ocean L0 cells before coast-guard mode is considered.</summary>
    public const int LargeOceanMinCells = 32;

    /// <summary>Rain height within this delta of sea level counts as ocean surface.</summary>
    public const int OceanSeaDelta = 6;

    enum CellKind { Unknown, Ocean, Land }

    public static LodLoginSweepPlan Plan(
        LodWorld world,
        IClientWorldAccessor clientWorld,
        ICoreClientAPI? capi = null) =>
        Plan(world, clientWorld, capi, RevisitMaxVisitStops);

    /// <summary>
    /// Plans a season revisit for an existing visited canvas, applying the visit-stop budget.
    /// </summary>
    public static LodLoginSweepPlan PlanRevisitVisited(
        LodWorld world,
        IClientWorldAccessor clientWorld,
        ICoreClientAPI? capi = null) =>
        Plan(world, clientWorld, capi, RevisitMaxVisitStops);

    static LodLoginSweepPlan Plan(
        LodWorld world,
        IClientWorldAccessor clientWorld,
        ICoreClientAPI? capi,
        int maxVisitStops)
    {
        List<long> visited = LodLoginSweep.VisitedL0Keys(world).ToList();
        if (visited.Count > 0)
            return PlanRevisitKeys(world, clientWorld, capi, visited, maxVisitStops);

        return PlanEmptyCanvas(clientWorld, capi);
    }

    internal static LodLoginSweepPlan PlanRevisitKeys(
        LodWorld world,
        IClientWorldAccessor clientWorld,
        ICoreClientAPI? capi,
        List<long> visited,
        int maxVisitStops)
    {
        visited.Sort();
        int planned = visited.Count;
        EntityPos pos = clientWorld.Player.Entity.Pos;
        int footprint = LodSection.SectionBlocks;
        int centerSx = (int)Math.Floor(pos.X / footprint);
        int centerSz = (int)Math.Floor(pos.Z / footprint);

        if (planned > maxVisitStops)
        {
            visited = BudgetVisitStops(visited, centerSx, centerSz, maxVisitStops);
            LogBudget(capi, planned, visited.Count, "Revisit");
        }

        string label = LabelForBudgetedRevisit(planned, visited.Count);
        return new LodLoginSweepPlan(LodLoginSweepPlanMode.RevisitVisited, visited, label);
    }

    /// <summary>
    /// Target only keys that still need capture/bake — never the full visited canvas when
    /// a handful of regions are incomplete.
    /// </summary>
    public static LodLoginSweepPlan PlanIncomplete(IReadOnlyList<LodLoginBakeAudit.Miss> misses)
    {
        var keys = new List<long>(misses.Count);
        var seen = new HashSet<long>();
        foreach (LodLoginBakeAudit.Miss miss in misses)
        {
            if (!seen.Add(miss.Key)) continue;
            keys.Add(miss.Key);
        }
        keys.Sort();
        string label = keys.Count == 1
            ? "Repairing 1 incomplete region"
            : $"Repairing {keys.Count} incomplete regions";
        return new LodLoginSweepPlan(LodLoginSweepPlanMode.RevisitIncomplete, keys, label);
    }

    public static IEnumerable<long> PlanKeys(
        LodWorld world,
        IClientWorldAccessor clientWorld) =>
        Plan(world, clientWorld).Keys;

    public static int BootstrapCellRadius(int footprint = LodSection.SectionBlocks) =>
        (int)Math.Ceiling(EmptyCanvasBootstrapRadiusBlocks / (double)footprint);

    static LodLoginSweepPlan PlanEmptyCanvas(IClientWorldAccessor world, ICoreClientAPI? capi)
    {
        EntityPos pos = world.Player.Entity.Pos;
        int footprint = LodSection.SectionBlocks;
        int centerSx = (int)Math.Floor(pos.X / footprint);
        int centerSz = (int)Math.Floor(pos.Z / footprint);
        int cellRadius = BootstrapCellRadius(footprint);
        int sea = world.SeaLevel;
        double radiusSq = EmptyCanvasBootstrapRadiusBlocks * (double)EmptyCanvasBootstrapRadiusBlocks;

        var disk = EnumerateDiskCells(centerSx, centerSz, cellRadius, footprint, pos.X, pos.Z, radiusSq)
            .ToList();

        var kinds = new Dictionary<long, CellKind>(disk.Count);
        foreach ((int sx, int sz) in disk)
        {
            if (sx < 0 || sz < 0) continue;
            long key = LodWorld.SectionKey(0, sx, sz);
            kinds[key] = ClassifyCell(world, sx, sz, footprint, sea);
        }

        if (TryPlanCoastGuard(kinds, centerSx, centerSz, footprint, out List<long> coastKeys))
        {
            int planned = coastKeys.Count;
            coastKeys = BudgetVisitStops(coastKeys, centerSx, centerSz, BootstrapMaxVisitStops);
            LogBudget(capi, planned, coastKeys.Count);
            coastKeys.Sort();
            return new LodLoginSweepPlan(
                LodLoginSweepPlanMode.BootstrapCoastGuard,
                coastKeys,
                LabelForBudgetedBootstrap("Bootstrap (coast guard)", planned, coastKeys.Count));
        }

        var radiusKeys = new List<long>(disk.Count);
        foreach ((int sx, int sz) in disk)
        {
            if (sx < 0 || sz < 0) continue;
            radiusKeys.Add(LodWorld.SectionKey(0, sx, sz));
        }
        int plannedRadius = radiusKeys.Count;
        radiusKeys = BudgetVisitStops(radiusKeys, centerSx, centerSz, BootstrapMaxVisitStops);
        LogBudget(capi, plannedRadius, radiusKeys.Count);
        radiusKeys.Sort();
        return new LodLoginSweepPlan(
            LodLoginSweepPlanMode.BootstrapRadius,
            radiusKeys,
            LabelForBudgetedBootstrap("Bootstrap (new world)", plannedRadius, radiusKeys.Count));
    }

    internal static List<long> BudgetVisitStops(
        List<long> keys,
        int centerSx,
        int centerSz,
        int max)
    {
        if (keys.Count <= max) return keys;

        keys.Sort((a, b) =>
        {
            long da = DistSq(a, centerSx, centerSz);
            long db = DistSq(b, centerSx, centerSz);
            return da.CompareTo(db);
        });

        var sampled = new List<long>(max);
        for (int i = 0; i < max; i++)
        {
            int idx = max == 1
                ? 0
                : (int)((long)i * (keys.Count - 1) / (max - 1));
            sampled.Add(keys[idx]);
        }
        return sampled;
    }

    static long DistSq(long key, int centerSx, int centerSz)
    {
        int dx = LodWorld.KeySx(key) - centerSx;
        int dz = LodWorld.KeySz(key) - centerSz;
        return (long)dx * dx + (long)dz * dz;
    }

    static string LabelForBudgetedBootstrap(string baseLabel, int planned, int budgeted) =>
        budgeted < planned
            ? $"{baseLabel} ({budgeted} of {planned})"
            : baseLabel;

    static string LabelForBudgetedRevisit(int planned, int budgeted)
    {
        string baseLabel = "Refreshing visited land (season)";
        if (budgeted >= planned) return baseLabel;
        return $"{baseLabel} ({budgeted} of {planned})";
    }

    internal static void LogBudget(ICoreClientAPI? capi, int planned, int budgeted, string kind = "Bootstrap")
    {
        if (budgeted >= planned) return;
        double targetSec = kind == "Revisit"
            ? LodLoginSweepTiming.TargetMaxSec
            : LodLoginSweepTiming.BootstrapTargetMaxSec;
        capi?.Logger.Notification(
            "[DistantVistas] {0} budget: visiting {1} of {2} L0 cells (~{3} max).",
            kind, budgeted, planned, LodLoginSweepTiming.FormatDuration(targetSec));
    }

    static bool TryPlanCoastGuard(
        Dictionary<long, CellKind> kinds,
        int centerSx,
        int centerSz,
        int footprint,
        out List<long> keys)
    {
        keys = new List<long>();
        var ocean = new List<(int Sx, int Sz, long Key)>();
        int minSx = int.MaxValue, maxSx = int.MinValue;
        int minSz = int.MaxValue, maxSz = int.MinValue;

        foreach ((long key, CellKind kind) in kinds)
        {
            if (kind != CellKind.Ocean) continue;
            int sx = LodWorld.KeySx(key);
            int sz = LodWorld.KeySz(key);
            ocean.Add((sx, sz, key));
            minSx = Math.Min(minSx, sx);
            maxSx = Math.Max(maxSx, sx);
            minSz = Math.Min(minSz, sz);
            maxSz = Math.Max(maxSz, sz);
        }

        if (ocean.Count < LargeOceanMinCells) return false;

        int spanBlocks = Math.Max(maxSx - minSx, maxSz - minSz) * footprint;
        long spawnKey = LodWorld.SectionKey(0, centerSx, centerSz);
        bool spawnInOcean = kinds.TryGetValue(spawnKey, out CellKind spawnKind) && spawnKind == CellKind.Ocean;
        if (!spawnInOcean && spanBlocks < LargeOceanMinSpanBlocks) return false;

        var chosen = new HashSet<long>();
        foreach ((int _, int _, long key) in ocean)
            chosen.Add(key);

        foreach ((int sx, int sz, long _) in ocean)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    long nk = LodWorld.SectionKey(0, sx + dx, sz + dz);
                    if (!kinds.TryGetValue(nk, out CellKind nkKind)) continue;
                    if (nkKind == CellKind.Ocean) continue;
                    chosen.Add(nk);
                }
            }
        }

        keys = chosen.ToList();
        return keys.Count > 0;
    }

    static CellKind ClassifyCell(IClientWorldAccessor world, int sx, int sz, int footprint, int sea)
    {
        int cx = (sx * footprint + footprint / 2) / GlobalConstants.ChunkSize;
        int cz = (sz * footprint + footprint / 2) / GlobalConstants.ChunkSize;
        if (cx < 0 || cz < 0) return CellKind.Land;

        IMapChunk? map = world.BlockAccessor.GetMapChunk(cx, cz);
        ushort[]? rain = map?.RainHeightMap;
        if (rain == null || rain.Length < GlobalConstants.ChunkSize * GlobalConstants.ChunkSize)
            return CellKind.Unknown;

        int idx = (GlobalConstants.ChunkSize / 2) * GlobalConstants.ChunkSize
            + GlobalConstants.ChunkSize / 2;
        int y = rain[idx];
        return Math.Abs(y - sea) <= OceanSeaDelta ? CellKind.Ocean : CellKind.Land;
    }

    static IEnumerable<(int Sx, int Sz)> EnumerateDiskCells(
        int centerSx,
        int centerSz,
        int cellRadius,
        int footprint,
        double originX,
        double originZ,
        double radiusSq)
    {
        for (int dsx = -cellRadius; dsx <= cellRadius; dsx++)
        {
            for (int dsz = -cellRadius; dsz <= cellRadius; dsz++)
            {
                int sx = centerSx + dsx;
                int sz = centerSz + dsz;
                double cx = sx * footprint + footprint * 0.5;
                double cz = sz * footprint + footprint * 0.5;
                double dx = cx - originX;
                double dz = cz - originZ;
                if (dx * dx + dz * dz <= radiusSq)
                    yield return (sx, sz);
            }
        }
    }
}
