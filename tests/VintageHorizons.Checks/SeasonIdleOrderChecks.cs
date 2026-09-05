namespace DistantVistas.Checks;

public static class SeasonIdleOrderChecks
{
    public static void Run(Check c)
    {
        CloserKeySortsFirst(c);
        VisitedIsSkipped(c);
        PlayerMoveRestartsInnerRing(c);
        FillNearestCappedKeepsClosest(c);
    }

    static void CloserKeySortsFirst(Check c)
    {
        long near = LodWorld.SectionKey(0, 0, 0);
        long mid = LodWorld.SectionKey(0, 2, 0);
        long far = LodWorld.SectionKey(0, 8, 0);
        var dest = new List<(long Key, double DistSq)>();
        var visited = new HashSet<long>();
        // Insert far first so hash/list order cannot accidentally match distance.
        LodSeasonIdleOrder.FillUnvisitedNearest(
            dest, new[] { far, mid, near }, visited, px: 32, pz: 32);
        c.Eq(3, dest.Count, "all unvisited keys are candidates");
        c.Eq(near, dest[0].Key, "closest section is first, not insertion order");
        c.Eq(mid, dest[1].Key, "mid distance is second");
        c.Eq(far, dest[2].Key, "farthest is last");
        c.True(dest[0].DistSq < dest[1].DistSq, "nearest distSq is strictly closer than mid");
        c.True(dest[1].DistSq < dest[2].DistSq, "mid distSq is strictly closer than far");
    }

    static void VisitedIsSkipped(Check c)
    {
        long a = LodWorld.SectionKey(0, 0, 0);
        long b = LodWorld.SectionKey(0, 3, 0);
        var dest = new List<(long Key, double DistSq)>();
        var visited = new HashSet<long> { a };
        LodSeasonIdleOrder.FillUnvisitedNearest(
            dest, new[] { a, b }, visited, px: 32, pz: 32);
        c.Eq(1, dest.Count, "already-baked inner ring is not picked again this lap");
        c.Eq(b, dest[0].Key, "next ring out is the closest leftover");
    }

    static void PlayerMoveRestartsInnerRing(Check c)
    {
        c.False(LodSeasonIdleOrder.PlayerMovedEnough(0, 0, 63, 0),
            "63 blocks stays on the current ring");
        c.False(LodSeasonIdleOrder.PlayerMovedEnough(0, 0, 64, 0),
            "one L0 tile is not enough to rebuild the sort at speed 20");
        c.False(LodSeasonIdleOrder.PlayerMovedEnough(0, 0, 255, 0),
            "255 blocks still stays on the current ring");
        c.True(LodSeasonIdleOrder.PlayerMovedEnough(0, 0, 256, 0),
            "four L0 tiles recenters so the new closest is first");
        c.True(LodSeasonIdleOrder.PlayerMovedEnough(1000, 1000, 1000 + 256, 1000),
            "recenter is relative to the last anchor, not world origin");
    }

    static void FillNearestCappedKeepsClosest(Check c)
    {
        long near = LodWorld.SectionKey(0, 0, 0);
        long mid = LodWorld.SectionKey(0, 2, 0);
        long far = LodWorld.SectionKey(0, 8, 0);
        var dest = new List<(long Key, double DistSq)>();
        var visited = new HashSet<long>();
        LodSeasonIdleOrder.FillNearestCapped(
            dest, new[] { far, mid, near }, visited, px: 32, pz: 32,
            cap: 2, maxDistBlocks: 0);
        c.Eq(2, dest.Count, "cap is not dest.Count == all keys");
        c.Eq(near, dest[0].Key, "capped fill still puts closest first");
    }
}
