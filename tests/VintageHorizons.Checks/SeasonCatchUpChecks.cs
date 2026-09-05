namespace DistantVistas.Checks;

public static class SeasonCatchUpChecks
{
    public static void Run(Check c)
    {
        DiskIndexIsNotAWorkQueue(c);
        EnqueueResidentIgnoresDiskKeys(c);
        PruneDropsColdKeysAndKeepsResident(c);
        MapRegionQueuesOnlyResidentOverlap(c);
        EpochEndsWithColdCacheOnDisk(c);
        LoadRebakeUsesEpochOrStaleToken(c);
    }

    static void DiskIndexIsNotAWorkQueue(Check c)
    {
        c.False(LodSeasonCatchUp.QueueDiskIndex,
            "join catch-up must not dump HasDataSet into SeasonDirty");
        c.False(LodSeasonCatchUp.EnqueueResidentOnEpoch,
            "join / month must not remesh the resident set — live tint is uniforms");
        c.Eq(0, LodSeasonCatchUp.ColdLoadsPerTick,
            "catch-up must not demand-load the explored world");
        c.True(LodSeasonCatchUp.JoinEpochKeepTicks > 0,
            "join epoch stays up so streaming view tiles still Cover");
        c.True(LodSeasonCatchUp.ResidentSectionsPerTick >= 16,
            "nearby resident drain is allowed to be faster than the speed-20 cruise");
    }

    static void EnqueueResidentIgnoresDiskKeys(Check c)
    {
        long ram = LodWorld.SectionKey(0, 0, 0);
        long disk = LodWorld.SectionKey(0, 40, 40);
        var dest = new HashSet<long>();
        LodSeasonCatchUp.EnqueueResident(dest, new[] { ram });
        c.Eq(1, dest.Count, "only resident keys are queued");
        c.True(dest.Contains(ram), "the in-RAM section is queued");
        c.False(dest.Contains(disk), "a HasDataSet-only key is not invented");
    }

    static void PruneDropsColdKeysAndKeepsResident(Check c)
    {
        long ram = LodWorld.SectionKey(0, 1, 0);
        long disk = LodWorld.SectionKey(2, 8, 8);
        var dirty = new HashSet<long> { ram, disk };
        int removed = LodSeasonCatchUp.PruneNonResident(dirty, k => k == ram);
        c.Eq(1, removed, "one cold key is dropped");
        c.Eq(1, dirty.Count, "dirty shrinks to resident");
        c.True(dirty.Contains(ram), "resident stays");
        c.False(dirty.Contains(disk), "disk index is not catch-up work");
    }

    static void MapRegionQueuesOnlyResidentOverlap(Check c)
    {
        // L0 footprint is 64. Region 0,0 at size 512 covers 0..512.
        long inside = LodWorld.SectionKey(0, 0, 0);
        long outside = LodWorld.SectionKey(0, 20, 0);
        var dest = new HashSet<long>();
        int queued = LodSeasonCatchUp.EnqueueResidentOverlapping(
            dest, new[] { inside, outside }, minX: 0, maxX: 512, minZ: 0, maxZ: 512);
        c.Eq(1, queued, "only the overlapping resident is queued");
        c.True(dest.Contains(inside), "section inside the climate region is queued");
        c.False(dest.Contains(outside), "resident outside the region is left alone");
        c.True(LodSeasonCatchUp.OverlapsRegion(inside, 0, 512, 0, 512),
            "origin L0 overlaps region 0");
        c.False(LodSeasonCatchUp.OverlapsRegion(outside, 0, 512, 0, 512),
            "sx 20 starts at 1280, past a 512 region");
    }

    static void EpochEndsWithColdCacheOnDisk(Check c)
    {
        c.True(LodSeasonCatchUp.KeepJoinEpoch(0, 400, residentDirty: 0, forcedRemesh: 0),
            "empty dirty still keeps the join window while tiles stream in");
        c.False(LodSeasonCatchUp.KeepJoinEpoch(400, 400, 0, 0),
            "epoch ends once the join window and remesh drain are done");
        c.True(LodSeasonCatchUp.KeepJoinEpoch(9999, 400, residentDirty: 3, forcedRemesh: 0),
            "resident dirty keeps the epoch even after the keep-alive ticks");
        c.True(LodSeasonCatchUp.KeepJoinEpoch(9999, 400, 0, forcedRemesh: 2),
            "forced remesh keeps the epoch so the first meshes are this season");
    }

    static void LoadRebakeUsesEpochOrStaleToken(Check c)
    {
        c.True(LodSeasonCatchUp.ShouldRebakeOnLoad(epochActive: true, tokenStale: false),
            "join epoch Cover-rebakes a demand-load even if the token already matches");
        c.True(LodSeasonCatchUp.ShouldRebakeOnLoad(epochActive: false, tokenStale: true),
            "a later demand-load whose stored season is stale still rebakes");
        c.False(LodSeasonCatchUp.ShouldRebakeOnLoad(epochActive: false, tokenStale: false),
            "same-season reload after join does not Cover again when there is nothing to melt");
        c.True(LodSeasonCatchUp.ShouldRebakeOnLoad(
                epochActive: false, tokenStale: false, needsMeltPass: true),
            "June leftover or inferred snow still Cover-rebakes after the join window");
    }
}
