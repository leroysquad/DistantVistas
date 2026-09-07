using System.Globalization;
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
    /// <summary>Open-ocean cells visited for a real sample bake (not counted in land budget).</summary>
    public IReadOnlyList<long> OceanSampleKeys { get; }
    /// <summary>Open-ocean cells stamped from samples after the visit pass (no teleport).</summary>
    public IReadOnlyList<long> OpenOceanFillKeys { get; }

    public LodLoginSweepPlan(
        LodLoginSweepPlanMode mode,
        IReadOnlyList<long> keys,
        string modeLabel,
        IReadOnlyList<long>? oceanSampleKeys = null,
        IReadOnlyList<long>? openOceanFillKeys = null)
    {
        Mode = mode;
        Keys = keys;
        ModeLabel = modeLabel;
        OceanSampleKeys = oceanSampleKeys ?? Array.Empty<long>();
        OpenOceanFillKeys = openOceanFillKeys ?? Array.Empty<long>();
    }
}

/// <summary>
/// Plans which L0 cells the login visit sweep should touch. With no per-world complete
/// marker, always bootstraps a coast-guard ocean sweep or ~216 km radius around the player
/// (even if some land was already walked). After a successful complete, existing visited
/// canvases are spatially subsampled to a wall-clock revisit budget.
/// </summary>
public static class LodLoginSweepBootstrap
{
    /// <summary>
    /// Season-expired / first-join disk around the player. 216000 = 1.5× the prior
    /// 144 km radius. Canvas outside this disk is kept, never wiped.
    /// </summary>
    public const int EmptyCanvasBootstrapRadiusBlocks = 216000;

    /// <summary>
    /// Hard cap on bootstrap visit stops at this PC's measured stop rate.
    /// Spatial subsample uses <see cref="BudgetBootstrapVisitStops"/> (outer-weighted bands).
    /// </summary>
    public static int BootstrapMaxVisitStops =>
        LodLoginSweepTiming.BootstrapCellBudget(LodLoginSweepTiming.MachineSecPerStop);

    /// <summary>
    /// Hard cap on season-revisit stops at this PC's measured stop rate.
    /// Large visited canvases are spatially subsampled — never every L0 key.
    /// </summary>
    public static int RevisitMaxVisitStops =>
        LodLoginSweepTiming.RevisitCellBudget(LodLoginSweepTiming.MachineSecPerStop);

    /// <summary>Shorter miss-retry hop so the overlay does not run the first pass twice.</summary>
    public static int RetryMaxVisitStops =>
        LodLoginSweepTiming.RetryStopBudget(LodLoginSweepTiming.MachineSecPerStop);

    /// <summary>Ocean cells must span at least this many blocks to trigger coast-guard mode.</summary>
    public const int LargeOceanMinSpanBlocks = 1500;

    /// <summary>Minimum ocean L0 cells before coast-guard mode is considered.</summary>
    public const int LargeOceanMinCells = 32;

    /// <summary>Rain height within this delta of sea level counts as ocean surface.</summary>
    public const int OceanSeaDelta = 6;

    /// <summary>Representative open-ocean sample visits (full bake) spread across the body.</summary>
    public const int OpenOceanMaxSamples = 3;

    /// <summary>
    /// Hard ceiling on rain-height ClassifyCell calls during empty-canvas planning.
    /// Full-disk classify at 216 km would freeze the client; visit stops stay on the
    /// wall budget, so a stratified sample is enough. Scaled with the 1.5× radius
    /// (area ~2.25×) so coast/land classify still covers the larger disk.
    /// </summary>
    public const int MaxBootstrapClassifyCells = 9216;

    internal enum CellKind { Unknown, Ocean, Land }

    public static LodLoginSweepPlan Plan(
        LodWorld world,
        IClientWorldAccessor clientWorld,
        LodPipeline pipeline,
        IList<Block> blocks,
        Block? plantTintFallback,
        System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf,
        ICoreClientAPI? capi = null) =>
        Plan(world, clientWorld, pipeline, blocks, plantTintFallback, untintedOf, capi, RevisitMaxVisitStops);

    /// <summary>
    /// Plans a season revisit for an existing visited canvas, applying the visit-stop budget.
    /// Incomplete keys are visited first; remaining budget may season-refresh complete cells.
    /// </summary>
    public static LodLoginSweepPlan PlanRevisitVisited(
        LodWorld world,
        IClientWorldAccessor clientWorld,
        LodPipeline pipeline,
        IList<Block> blocks,
        Block? plantTintFallback,
        System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf,
        ICoreClientAPI? capi = null) =>
        Plan(world, clientWorld, pipeline, blocks, plantTintFallback, untintedOf, capi, RevisitMaxVisitStops);

    /// <summary>
    /// First-sweep / empty-canvas plan: coast-guard or ~216 km player-radius disk, spatially
    /// subsampled to <see cref="BootstrapMaxVisitStops"/>. Skips L0 cells already fully
    /// baked in the per-world cache; ocean sample/stamp rules unchanged.
    /// </summary>
    public static LodLoginSweepPlan PlanBootstrap(
        LodWorld world,
        IClientWorldAccessor clientWorld,
        LodPipeline pipeline,
        IList<Block> blocks,
        Block? plantTintFallback,
        System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf,
        ICoreClientAPI? capi = null) =>
        PlanEmptyCanvas(world, clientWorld, pipeline, blocks, plantTintFallback, untintedOf, capi);

    static LodLoginSweepPlan Plan(
        LodWorld world,
        IClientWorldAccessor clientWorld,
        LodPipeline pipeline,
        IList<Block> blocks,
        Block? plantTintFallback,
        System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf,
        ICoreClientAPI? capi,
        int maxVisitStops)
    {
        // No successful complete for this world → bootstrap the spawn disk so vistas expand.
        // Do not season-refresh a tiny walked set (PlanRevisitKeys) on first login.
        if (capi != null && LodLoginSweepComplete.TryLoad(capi) == null)
            return PlanEmptyCanvas(world, clientWorld, pipeline, blocks, plantTintFallback, untintedOf, capi);

        List<long> visited = LodLoginSweep.VisitedL0Keys(world).ToList();
        if (visited.Count > 0)
            return PlanRevisitKeys(world, clientWorld, pipeline, blocks, plantTintFallback, untintedOf,
                capi, visited, maxVisitStops);

        return PlanEmptyCanvas(world, clientWorld, pipeline, blocks, plantTintFallback, untintedOf, capi);
    }

    internal static LodLoginSweepPlan PlanRevisitKeys(
        LodWorld world,
        IClientWorldAccessor clientWorld,
        LodPipeline pipeline,
        IList<Block> blocks,
        Block? plantTintFallback,
        System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf,
        ICoreClientAPI? capi,
        List<long> visited,
        int maxVisitStops)
    {
        visited.Sort();
        int visitedTotal = visited.Count;
        EntityPos pos = clientWorld.Player.Entity.Pos;
        int footprint = LodSection.SectionBlocks;
        int centerSx = (int)Math.Floor(pos.X / footprint);
        int centerSz = (int)Math.Floor(pos.Z / footprint);

        LodLoginBakeAudit.PartitionVisitKeys(
            visited, world, pipeline, blocks, plantTintFallback, untintedOf,
            out List<long> needsVisit, out List<long> complete);

        int gapCount = needsVisit.Count;
        if (complete.Count > 0)
            LogSkipComplete(capi, complete.Count, visitedTotal, "Revisit");

        List<long> planned = new List<long>(needsVisit);
        bool seasonRefresh = false;
        if (planned.Count < maxVisitStops && complete.Count > 0)
        {
            int refreshSlots = maxVisitStops - planned.Count;
            List<long> refresh = BudgetVisitStops(complete, centerSx, centerSz, refreshSlots);
            if (refresh.Count > 0)
            {
                planned.AddRange(refresh);
                seasonRefresh = true;
            }
        }

        if (planned.Count > maxVisitStops)
        {
            if (needsVisit.Count >= maxVisitStops)
                planned = BudgetVisitStops(needsVisit, centerSx, centerSz, maxVisitStops);
            else
            {
                planned = new List<long>(needsVisit);
                int left = maxVisitStops - planned.Count;
                if (left > 0)
                    planned.AddRange(BudgetVisitStops(complete, centerSx, centerSz, left));
            }
            LogBudget(capi, visitedTotal, planned.Count, "Revisit");
        }

        string label = LabelForBudgetedRevisit(visitedTotal, planned.Count, gapCount, seasonRefresh);
        // #region agent log
        try
        {
            System.IO.File.AppendAllText(
                @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                "{\"sessionId\":\"40cccb\",\"runId\":\"season-1\",\"hypothesisId\":\"H-S-plan\",\"location\":\"LodLoginSweepBootstrap.PlanRevisitKeys\",\"message\":\"revisit-plan\",\"data\":{\"visited\":"
                + visitedTotal + ",\"needsVisit\":" + needsVisit.Count + ",\"complete\":" + complete.Count
                + ",\"planned\":" + planned.Count + ",\"maxStops\":" + maxVisitStops
                + ",\"seasonRefresh\":" + (seasonRefresh ? "true" : "false")
                + ",\"gapCount\":" + gapCount
                + "},\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n");
        }
        catch { }
        // #endregion
        return new LodLoginSweepPlan(LodLoginSweepPlanMode.RevisitVisited, planned, label);
    }

    /// <summary>
    /// Outside the 30-day window: hop a spatial sample of stored L0 in the player
    /// disk, capped at <see cref="RevisitMaxVisitStops"/>, then a short interior
    /// gap-fill between those stops. A full-disk hop is ~1 hour and is never the
    /// expire path. Outer-edge scraps stay. Gap audit is ignored for who to hop.
    /// </summary>
    public static LodLoginSweepPlan PlanSeasonExpired(
        LodWorld world,
        IClientWorldAccessor clientWorld,
        LodPipeline pipeline,
        IList<Block> blocks,
        Block? plantTintFallback,
        System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf,
        ICoreClientAPI? capi = null)
    {
        List<long> visited = LodLoginSweep.VisitedL0Keys(world).ToList();
        visited.Sort();
        int visitedTotal = visited.Count;
        EntityPos pos = clientWorld.Player.Entity.Pos;
        int footprint = LodSection.SectionBlocks;
        int centerSx = (int)Math.Floor(pos.X / footprint);
        int centerSz = (int)Math.Floor(pos.Z / footprint);
        double radiusSq = EmptyCanvasBootstrapRadiusBlocks * (double)EmptyCanvasBootstrapRadiusBlocks;
        visited = visited.Where(key =>
        {
            double cx = LodWorld.KeySx(key) * footprint + footprint * 0.5;
            double cz = LodWorld.KeySz(key) * footprint + footprint * 0.5;
            double dx = cx - pos.X;
            double dz = cz - pos.Z;
            return dx * dx + dz * dz <= radiusSq;
        }).ToList();
        if (visited.Count == 0)
            visited = LodLoginSweep.VisitedL0Keys(world).ToList();

        int diskCount = visited.Count;
        int maxVisitStops = RevisitMaxVisitStops;
        List<long> planned = BudgetVisitStops(visited, centerSx, centerSz, maxVisitStops);
        int interiorBudget = RetryMaxVisitStops;
        List<long> interior = InteriorGapsBetweenStops(visited, planned, interiorBudget);
        if (interior.Count > 0)
            planned.AddRange(interior);
        bool seasonRefresh = planned.Count > 0;
        if (planned.Count < diskCount)
            LogBudget(capi, diskCount, planned.Count, "Revisit");

        LodLoginBakeAudit.PartitionVisitKeys(
            visited, world, pipeline, blocks, plantTintFallback, untintedOf,
            out List<long> needsVisit, out List<long> complete);

        string label = LabelForBudgetedRevisit(diskCount, planned.Count, 0, seasonRefresh);
        // #region agent log
        try
        {
            System.IO.File.AppendAllText(
                @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                "{\"sessionId\":\"40cccb\",\"runId\":\"post-fix-expire\",\"hypothesisId\":\"H-S-plan\",\"location\":\"LodLoginSweepBootstrap.PlanSeasonExpired\",\"message\":\"revisit-plan\",\"data\":{\"visited\":"
                + visitedTotal + ",\"diskCount\":" + diskCount
                + ",\"needsVisit\":" + needsVisit.Count + ",\"complete\":" + complete.Count
                + ",\"planned\":" + planned.Count + ",\"maxStops\":" + maxVisitStops
                + ",\"interiorFill\":" + interior.Count
                + ",\"interiorBudget\":" + interiorBudget
                + ",\"seasonRefresh\":" + (seasonRefresh ? "true" : "false")
                + ",\"gapCount\":" + needsVisit.Count
                + ",\"seasonExpired\":true"
                + ",\"fullDiskRecapture\":false"
                + ",\"etaSec\":" + (LodLoginSweepTiming.MachineSecPerStop * planned.Count).ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "},\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n");
        }
        catch { }
        // #endregion
        return new LodLoginSweepPlan(LodLoginSweepPlanMode.RevisitVisited, planned, label);
    }

    /// <summary>
    /// Target only keys that still need capture/bake — never the full visited canvas when
    /// a handful of regions are incomplete.
    /// </summary>
    public static LodLoginSweepPlan PlanIncomplete(
        IReadOnlyList<LodLoginBakeAudit.Miss> misses,
        IClientWorldAccessor? clientWorld = null,
        int? maxStops = null)
    {
        var keys = new List<long>(misses.Count);
        var seen = new HashSet<long>();
        foreach (LodLoginBakeAudit.Miss miss in misses)
        {
            if (!seen.Add(miss.Key)) continue;
            keys.Add(miss.Key);
        }
        keys.Sort();
        int gapCount = keys.Count;
        int maxVisitStops = maxStops ?? RevisitMaxVisitStops;
        if (keys.Count > maxVisitStops)
        {
            int centerSx = 0;
            int centerSz = 0;
            if (clientWorld?.Player?.Entity != null)
            {
                int footprint = LodSection.SectionBlocks;
                centerSx = (int)Math.Floor(clientWorld.Player.Entity.Pos.X / footprint);
                centerSz = (int)Math.Floor(clientWorld.Player.Entity.Pos.Z / footprint);
            }
            keys = BudgetVisitStops(keys, centerSx, centerSz, maxVisitStops);
        }
        string label = gapCount == 1
            ? "Repairing 1 incomplete region"
            : keys.Count < gapCount
                ? $"Repairing {keys.Count} of {gapCount} incomplete regions"
                : $"Repairing {keys.Count} incomplete regions";
        return new LodLoginSweepPlan(LodLoginSweepPlanMode.RevisitIncomplete, keys, label);
    }

    public static IEnumerable<long> PlanKeys(
        LodWorld world,
        IClientWorldAccessor clientWorld,
        LodPipeline pipeline,
        IList<Block> blocks,
        Block? plantTintFallback,
        System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf) =>
        Plan(world, clientWorld, pipeline, blocks, plantTintFallback, untintedOf).Keys;

    public static int BootstrapCellRadius(int footprint = LodSection.SectionBlocks) =>
        (int)Math.Ceiling(EmptyCanvasBootstrapRadiusBlocks / (double)footprint);

    static LodLoginSweepPlan PlanEmptyCanvas(
        LodWorld lodWorld,
        IClientWorldAccessor world,
        LodPipeline pipeline,
        IList<Block> blocks,
        Block? plantTintFallback,
        System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf,
        ICoreClientAPI? capi)
    {
        EntityPos pos = world.Player.Entity.Pos;
        int footprint = LodSection.SectionBlocks;
        int centerSx = (int)Math.Floor(pos.X / footprint);
        int centerSz = (int)Math.Floor(pos.Z / footprint);
        int cellRadius = BootstrapCellRadius(footprint);
        int sea = world.SeaLevel;
        double radiusSq = EmptyCanvasBootstrapRadiusBlocks * (double)EmptyCanvasBootstrapRadiusBlocks;
        int diskEstimate = EstimateDiskCellCount(cellRadius);

        // Never walk every L0 cell in the probe disk on the main thread. At 216 km that is
        // millions of ClassifyCell / GetMapChunk calls and the client looks frozen forever.
        List<(int Sx, int Sz)> disk = diskEstimate <= MaxBootstrapClassifyCells
            ? EnumerateDiskCells(centerSx, centerSz, cellRadius, footprint, pos.X, pos.Z, radiusSq)
                .ToList()
            : SampleDiskCells(
                centerSx, centerSz, cellRadius, footprint, pos.X, pos.Z, radiusSq,
                MaxBootstrapClassifyCells);

        if (diskEstimate > MaxBootstrapClassifyCells)
        {
            capi?.Logger.Notification(
                "[DistantVistas] Bootstrap plan: classifying {0} of ~{1} L0 cells in the {2} km disk (sample — full classify freezes the client).",
                disk.Count, diskEstimate, EmptyCanvasBootstrapRadiusBlocks / 1000);
        }

        var kinds = new Dictionary<long, CellKind>(disk.Count);
        foreach ((int sx, int sz) in disk)
        {
            if (sx < 0 || sz < 0) continue;
            long key = LodWorld.SectionKey(0, sx, sz);
            kinds[key] = ClassifyCell(world, sx, sz, footprint, sea);
        }

        if (IsCoastGuardDisk(kinds, centerSx, centerSz, footprint))
        {
            return PlanBootstrapDisk(
                lodWorld, pipeline, blocks, plantTintFallback, untintedOf,
                kinds, centerSx, centerSz, capi,
                LodLoginSweepPlanMode.BootstrapCoastGuard,
                "Bootstrap (coast guard)",
                diskEstimate);
        }

        return PlanBootstrapDisk(
            lodWorld, pipeline, blocks, plantTintFallback, untintedOf,
            kinds, centerSx, centerSz, capi,
            LodLoginSweepPlanMode.BootstrapRadius,
            "Bootstrap (new world)",
            diskEstimate);
    }

    static LodLoginSweepPlan PlanBootstrapDisk(
        LodWorld world,
        LodPipeline pipeline,
        IList<Block> blocks,
        Block? plantTintFallback,
        System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf,
        Dictionary<long, CellKind> kinds,
        int centerSx,
        int centerSz,
        ICoreClientAPI? capi,
        LodLoginSweepPlanMode mode,
        string labelBase,
        int diskCellEstimate = 0)
    {
        List<long> landAll = SelectLandVisitCells(kinds);
        List<long> landVisit = LodLoginBakeAudit.FilterNeedsVisit(
            landAll, world, pipeline, blocks, plantTintFallback, untintedOf);
        if (landAll.Count > landVisit.Count)
            LogSkipComplete(capi, landAll.Count - landVisit.Count, landAll.Count, "Bootstrap land");

        List<long> openOcean = SelectOpenOceanCells(kinds);
        List<long> openOceanNeeding = LodLoginBakeAudit.FilterNeedsVisit(
            openOcean, world, pipeline, blocks, plantTintFallback, untintedOf);
        List<long> oceanSamples = PickOceanSampleCells(openOceanNeeding, centerSx, centerSz);
        var sampleSet = new HashSet<long>(oceanSamples);
        var openOceanFill = new List<long>(openOceanNeeding.Count);
        foreach (long key in openOceanNeeding)
        {
            if (!sampleSet.Contains(key))
                openOceanFill.Add(key);
        }

        List<long> landBudgeted = BudgetBootstrapVisitStops(
            landVisit, centerSx, centerSz, BootstrapMaxVisitStops);

        var visitKeys = new List<long>(landBudgeted);
        foreach (long key in oceanSamples)
        {
            if (!visitKeys.Contains(key))
                visitKeys.Add(key);
        }

        // Prefer the geometric disk size in UI/logs when we only classified a sample,
        // so "64 of ~4M" still reads as the probe footprint Jack asked for.
        int plannedForLabel = diskCellEstimate > landVisit.Count ? diskCellEstimate : landVisit.Count;

        LogOceanPlan(capi, openOceanNeeding.Count, oceanSamples.Count, openOceanFill.Count, landBudgeted.Count);
        LogBudget(capi, plannedForLabel, landBudgeted.Count);
        visitKeys.Sort();

        return new LodLoginSweepPlan(
            mode,
            visitKeys,
            LabelForBudgetedBootstrap(labelBase, plannedForLabel, landBudgeted.Count),
            oceanSamples,
            openOceanFill);
    }

    static bool IsCoastGuardDisk(
        Dictionary<long, CellKind> kinds,
        int centerSx,
        int centerSz,
        int footprint)
    {
        int oceanCount = 0;
        int minSx = int.MaxValue, maxSx = int.MinValue;
        int minSz = int.MaxValue, maxSz = int.MinValue;

        foreach ((long key, CellKind kind) in kinds)
        {
            if (kind != CellKind.Ocean) continue;
            oceanCount++;
            int sx = LodWorld.KeySx(key);
            int sz = LodWorld.KeySz(key);
            minSx = Math.Min(minSx, sx);
            maxSx = Math.Max(maxSx, sx);
            minSz = Math.Min(minSz, sz);
            maxSz = Math.Max(maxSz, sz);
        }

        if (oceanCount < LargeOceanMinCells) return false;

        int spanBlocks = Math.Max(maxSx - minSx, maxSz - minSz) * footprint;
        long spawnKey = LodWorld.SectionKey(0, centerSx, centerSz);
        bool spawnInOcean = kinds.TryGetValue(spawnKey, out CellKind spawnKind) && spawnKind == CellKind.Ocean;
        return spawnInOcean || spanBlocks >= LargeOceanMinSpanBlocks;
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

    /// <summary>
    /// After the 64-stop sample, hop leftovers that sit strictly inside the
    /// planned hull. Outer-edge scraps and disconnected rim bits stay.
    /// </summary>
    internal static List<long> InteriorGapsBetweenStops(
        List<long> visited,
        List<long> planned,
        int max)
    {
        if (max <= 0 || planned.Count < 3 || visited.Count == 0)
            return new List<long>();

        int minSx = int.MaxValue, maxSx = int.MinValue;
        int minSz = int.MaxValue, maxSz = int.MinValue;
        var plannedSet = new HashSet<long>(planned);
        foreach (long k in planned)
        {
            int sx = LodWorld.KeySx(k);
            int sz = LodWorld.KeySz(k);
            if (sx < minSx) minSx = sx;
            if (sx > maxSx) maxSx = sx;
            if (sz < minSz) minSz = sz;
            if (sz > maxSz) maxSz = sz;
        }

        minSx++;
        maxSx--;
        minSz++;
        maxSz--;
        if (maxSx < minSx || maxSz < minSz)
            return new List<long>();

        var interior = new List<long>();
        foreach (long k in visited)
        {
            if (plannedSet.Contains(k)) continue;
            int sx = LodWorld.KeySx(k);
            int sz = LodWorld.KeySz(k);
            if (sx < minSx || sx > maxSx || sz < minSz || sz > maxSz) continue;
            interior.Add(k);
        }
        if (interior.Count == 0)
            return interior;

        int cx = (minSx + maxSx) / 2;
        int cz = (minSz + maxSz) / 2;
        return BudgetVisitStops(interior, cx, cz, max);
    }

    /// <summary>
    /// First-join bootstrap subsample across the ~216 km probe disk. Linear distance picks
    /// (see <see cref="BudgetVisitStops"/>) left ~1 stop per long outer arc. Outer-weighted
    /// distance bands put more teleports on the horizon ring so the far LOD looks filled.
    /// </summary>
    internal static List<long> BudgetBootstrapVisitStops(
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

        int bands = Math.Clamp(max / 5, 10, 18);
        var result = new List<long>(max);
        var used = new HashSet<long>();
        int weightSum = bands * (bands + 1) / 2;

        for (int b = 0; b < bands && result.Count < max; b++)
        {
            int start = (int)((long)b * keys.Count / bands);
            int end = (int)((long)(b + 1) * keys.Count / bands);
            if (end <= start) end = Math.Min(start + 1, keys.Count);
            int span = end - start;
            if (span <= 0) continue;

            int weight = b + 1;
            int picks = Math.Max(1, (int)Math.Round(max * weight / (double)weightSum));
            picks = Math.Min(picks, max - result.Count);
            picks = Math.Min(picks, span);

            for (int p = 0; p < picks; p++)
            {
                int idx = span == 1
                    ? start
                    : start + (int)((long)p * (span - 1) / Math.Max(1, picks - 1));
                long key = keys[idx];
                if (used.Add(key))
                    result.Add(key);
            }
        }

        for (int i = 0; result.Count < max && i < keys.Count; i++)
        {
            int idx = max == 1
                ? 0
                : (int)((long)i * (keys.Count - 1) / (max - 1));
            if (used.Add(keys[idx]))
                result.Add(keys[idx]);
        }

        return result;
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

    static string LabelForBudgetedRevisit(int visitedTotal, int planned, int gapCount, bool seasonRefresh)
    {
        if (seasonRefresh && gapCount > 0)
            return $"Gaps + season refresh ({planned} stops)";
        if (seasonRefresh)
        {
            string baseLabel = "Refreshing visited land (season)";
            if (planned >= visitedTotal) return baseLabel;
            return $"{baseLabel} ({planned} of {visitedTotal})";
        }
        if (gapCount > 0 && planned <= gapCount)
            return gapCount == 1
                ? "Filling 1 incomplete region"
                : $"Filling gaps ({planned} of {gapCount} incomplete)";
        string fallback = "Refreshing visited land (season)";
        if (planned >= visitedTotal) return fallback;
        return $"{fallback} ({planned} of {visitedTotal})";
    }

    static void LogSkipComplete(ICoreClientAPI? capi, int skipped, int total, string kind)
    {
        if (skipped <= 0 || capi == null) return;
        capi.Logger.Notification(
            "[DistantVistas] {0}: skipping {1} of {2} L0 cells already baked in cache.",
            kind, skipped, total);
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

    static void LogOceanPlan(
        ICoreClientAPI? capi,
        int openOceanTotal,
        int sampleVisits,
        int stampFill,
        int landVisitStops)
    {
        if (openOceanTotal <= 0 || capi == null) return;
        capi.Logger.Notification(
            "[DistantVistas] Bootstrap ocean: {0} open-water L0 cells — {1} sample visit(s), {2} stamp fill; {3} land/coast visit stops.",
            openOceanTotal, sampleVisits, stampFill, landVisitStops);
    }

    /// <summary>Land, unclassified, and coastline ocean — full visit+bake stops.</summary>
    internal static List<long> SelectLandVisitCells(Dictionary<long, CellKind> kinds)
    {
        var worthy = new List<long>(kinds.Count);
        foreach ((long key, CellKind kind) in kinds)
        {
            if (kind == CellKind.Land || kind == CellKind.Unknown)
                worthy.Add(key);
        }

        foreach ((long key, CellKind kind) in kinds)
        {
            if (kind != CellKind.Ocean) continue;
            if (TouchesLand(key, kinds))
                worthy.Add(key);
        }

        return worthy;
    }

    /// <summary>Open-ocean body tiles (no land neighbour) — sample visit + stamp fill only.</summary>
    internal static List<long> SelectOpenOceanCells(Dictionary<long, CellKind> kinds)
    {
        var open = new List<long>();
        foreach ((long key, CellKind kind) in kinds)
        {
            if (kind == CellKind.Ocean && !TouchesLand(key, kinds))
                open.Add(key);
        }
        return open;
    }

    internal static List<long> PickOceanSampleCells(
        List<long> openOcean,
        int centerSx,
        int centerSz)
    {
        if (openOcean.Count == 0) return new List<long>();
        int max = Math.Min(OpenOceanMaxSamples, openOcean.Count);
        if (openOcean.Count <= max) return openOcean;
        return BudgetVisitStops(openOcean, centerSx, centerSz, max);
    }

    internal static List<long> SelectVisitWorthyCells(Dictionary<long, CellKind> kinds) =>
        SelectLandVisitCells(kinds);

    static bool TouchesLand(long oceanKey, Dictionary<long, CellKind> kinds)
    {
        int sx = LodWorld.KeySx(oceanKey);
        int sz = LodWorld.KeySz(oceanKey);
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dz == 0) continue;
                long nk = LodWorld.SectionKey(0, sx + dx, sz + dz);
                if (!kinds.TryGetValue(nk, out CellKind nkKind)) continue;
                if (nkKind == CellKind.Land || nkKind == CellKind.Unknown)
                    return true;
            }
        }
        return false;
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

    /// <summary>Approximate L0 cells inside a square-bounded circle of the given cell radius.</summary>
    internal static int EstimateDiskCellCount(int cellRadius) =>
        (int)Math.Ceiling(Math.PI * cellRadius * (double)cellRadius);

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

    /// <summary>
    /// Outer-weighted ring sample of L0 cells across the probe disk. Same spirit as
    /// <see cref="BudgetBootstrapVisitStops"/>, but chosen before ClassifyCell so a
    /// 216 km radius never freezes on millions of map-chunk probes.
    /// </summary>
    internal static List<(int Sx, int Sz)> SampleDiskCells(
        int centerSx,
        int centerSz,
        int cellRadius,
        int footprint,
        double originX,
        double originZ,
        double radiusSq,
        int maxSamples)
    {
        var result = new List<(int Sx, int Sz)>(Math.Min(maxSamples, 512));
        var used = new HashSet<(int, int)>();

        void TryAdd(int sx, int sz)
        {
            if (sx < 0 || sz < 0) return;
            if (result.Count >= maxSamples) return;
            double cx = sx * footprint + footprint * 0.5;
            double cz = sz * footprint + footprint * 0.5;
            double dx = cx - originX;
            double dz = cz - originZ;
            if (dx * dx + dz * dz > radiusSq) return;
            if (!used.Add((sx, sz))) return;
            result.Add((sx, sz));
        }

        TryAdd(centerSx, centerSz);
        if (cellRadius <= 0 || maxSamples <= 1)
            return result;

        int bands = Math.Clamp(maxSamples / 8, 12, 48);
        int weightSum = bands * (bands + 1) / 2;

        for (int b = 0; b < bands && result.Count < maxSamples; b++)
        {
            double t0 = b / (double)bands;
            double t1 = (b + 1) / (double)bands;
            double rCells = (t0 + t1) * 0.5 * cellRadius;
            int weight = b + 1;
            int picks = Math.Max(1, (int)Math.Round(maxSamples * weight / (double)weightSum));
            picks = Math.Min(picks, maxSamples - result.Count);

            for (int p = 0; p < picks; p++)
            {
                double ang = (2.0 * Math.PI * (p + 0.5) / picks) + (b * 0.41);
                int dsx = (int)Math.Round(rCells * Math.Cos(ang));
                int dsz = (int)Math.Round(rCells * Math.Sin(ang));
                TryAdd(centerSx + dsx, centerSz + dsz);
            }
        }

        // If rings collided / clipped, pad with a denser outer ring.
        for (int p = 0; result.Count < maxSamples && p < maxSamples * 2; p++)
        {
            double ang = 2.0 * Math.PI * p / (maxSamples * 2.0);
            int dsx = (int)Math.Round(cellRadius * 0.92 * Math.Cos(ang));
            int dsz = (int)Math.Round(cellRadius * 0.92 * Math.Sin(ang));
            TryAdd(centerSx + dsx, centerSz + dsz);
        }

        return result;
    }
}
