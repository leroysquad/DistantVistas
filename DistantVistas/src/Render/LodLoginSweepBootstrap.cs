using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace DistantVistas;

public enum LodLoginSweepPlanMode
{
    /// <summary>Canvas already has visited L0 keys — sweep all of them (season refresh).</summary>
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
/// are always included; an empty cache bootstraps a coast-guard ocean sweep or a large
/// radius around spawn — never an entire pre-explored mega world.
/// </summary>
public static class LodLoginSweepBootstrap
{
    /// <summary>Default bootstrap extent for empty canvas (tunable 4000–7000).</summary>
    public const int EmptyCanvasBootstrapRadiusBlocks = 6000;

    /// <summary>Ocean cells must span at least this many blocks to trigger coast-guard mode.</summary>
    public const int LargeOceanMinSpanBlocks = 1500;

    /// <summary>Minimum ocean L0 cells before coast-guard mode is considered.</summary>
    public const int LargeOceanMinCells = 32;

    /// <summary>Rain height within this delta of sea level counts as ocean surface.</summary>
    public const int OceanSeaDelta = 6;

    enum CellKind { Unknown, Ocean, Land }

    public static LodLoginSweepPlan Plan(
        LodWorld world,
        IClientWorldAccessor clientWorld)
    {
        List<long> visited = LodLoginSweep.VisitedL0Keys(world).ToList();
        if (visited.Count > 0)
        {
            visited.Sort();
            return new LodLoginSweepPlan(
                LodLoginSweepPlanMode.RevisitVisited,
                visited,
                "Revisiting visited land");
        }

        return PlanEmptyCanvas(clientWorld);
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

    static LodLoginSweepPlan PlanEmptyCanvas(IClientWorldAccessor world)
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
            coastKeys.Sort();
            return new LodLoginSweepPlan(
                LodLoginSweepPlanMode.BootstrapCoastGuard,
                coastKeys,
                "Bootstrap (coast guard)");
        }

        var radiusKeys = new List<long>(disk.Count);
        foreach ((int sx, int sz) in disk)
        {
            if (sx < 0 || sz < 0) continue;
            radiusKeys.Add(LodWorld.SectionKey(0, sx, sz));
        }
        radiusKeys.Sort();
        return new LodLoginSweepPlan(
            LodLoginSweepPlanMode.BootstrapRadius,
            radiusKeys,
            "Bootstrap (new world)");
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
