namespace DistantVistas;

/// <summary>
/// Join / month catch-up policy. The disk index (<c>HasDataSet</c>) is not a work
/// queue. Walking and sorting it every tick is why a big cache sat for minutes
/// before the first mesh. Resident RAM plus demand-load is the work. Far plates
/// rebake when the renderer actually wants them.
/// </summary>
public static class LodSeasonCatchUp
{
    /// <summary>Do not copy HasDataSet into SeasonDirty.</summary>
    public const bool QueueDiskIndex = false;

    /// <summary>
    /// Join / month must not remesh the resident set. Live tint is shader uniforms.
    /// </summary>
    public const bool EnqueueResidentOnEpoch = false;

    /// <summary>
    /// Keep the join epoch alive so tiles streaming in still Cover even when
    /// their stored look-token already matches (same calendar, frost-revision bump).
    /// ~20s at 20 tps. After that, demand-load uses a stale token.
    /// </summary>
    public const int JoinEpochKeepTicks = 400;

    /// <summary>Catch-up must not demand-load the explored world off disk.</summary>
    public const int ColdLoadsPerTick = 0;

    /// <summary>
    /// Resident palette drain while nearby tiles are in RAM. Speed 1 is the bar;
    /// this does not need to survive a speed-20 Cover storm.
    /// </summary>
    public const int ResidentSectionsPerTick = 64;

    public static void EnqueueResident(HashSet<long> dest, IEnumerable<long> residentKeys)
    {
        foreach (long key in residentKeys)
            dest.Add(key);
    }

    /// <summary>
    /// Drop cold disk keys that leaked into the catch-up set. Epoch ends when
    /// resident dirty is empty; leftover HasDataSet keys used to hold it open forever.
    /// </summary>
    public static int PruneNonResident(HashSet<long> dirty, Func<long, bool> isResident)
    {
        List<long>? drop = null;
        foreach (long key in dirty)
        {
            if (isResident(key)) continue;
            (drop ??= new List<long>()).Add(key);
        }
        if (drop == null) return 0;
        foreach (long key in drop) dirty.Remove(key);
        return drop.Count;
    }

    public static bool OverlapsRegion(long key, int minX, int maxX, int minZ, int maxZ)
    {
        int footprint = LodWorld.KeyFootprintBlocks(key);
        int sx0 = LodWorld.KeySx(key) * footprint;
        int sz0 = LodWorld.KeySz(key) * footprint;
        int sx1 = sx0 + footprint;
        int sz1 = sz0 + footprint;
        return !(sx1 <= minX || sx0 >= maxX || sz1 <= minZ || sz0 >= maxZ);
    }

    public static int EnqueueResidentOverlapping(
        HashSet<long> dest,
        IEnumerable<long> residentKeys,
        int minX,
        int maxX,
        int minZ,
        int maxZ)
    {
        int queued = 0;
        foreach (long key in residentKeys)
        {
            if (!OverlapsRegion(key, minX, maxX, minZ, maxZ)) continue;
            if (dest.Add(key)) queued++;
        }
        return queued;
    }

    /// <summary>
    /// Epoch can finish with cold cache still on disk. Keep it only while join
    /// tiles are still streaming, or while resident remesh is draining.
    /// </summary>
    public static bool KeepJoinEpoch(
        int tickNow, int keepUntilTick, int residentDirty, int forcedRemesh)
    {
        if (residentDirty > 0 || forcedRemesh > 0) return true;
        return tickNow < keepUntilTick;
    }

    public static bool ShouldRebakeOnLoad(
        bool epochActive, bool tokenStale, bool needsMeltPass = false) =>
        epochActive || tokenStale || needsMeltPass;
}
