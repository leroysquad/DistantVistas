namespace DistantVistas;

/// <summary>
/// Player-centered idle season walk: unfinished nearest first, then the next
/// ring out. Dictionary order is hash-random and is how far pads beat the ones
/// under the nose.
/// </summary>
public static class LodSeasonIdleOrder
{
    /// <summary>Restart the inner ring when the player has walked four L0 tiles.</summary>
    public const double RecenterBlocks = 256;

    public static bool PlayerMovedEnough(
        double anchorX, double anchorZ, double px, double pz)
    {
        double dx = px - anchorX;
        double dz = pz - anchorZ;
        return dx * dx + dz * dz >= RecenterBlocks * RecenterBlocks;
    }

    public static void FillUnvisitedNearest(
        List<(long Key, double DistSq)> dest,
        IEnumerable<long> keys,
        HashSet<long> visited,
        double px,
        double pz)
    {
        dest.Clear();
        foreach (long key in keys)
        {
            if (visited.Contains(key)) continue;
            dest.Add((key, LodWorld.NearestDistanceSqTo(key, px, pz)));
        }
        dest.Sort(static (a, b) => a.DistSq.CompareTo(b.DistSq));
    }

    /// <summary>
    /// Keep the closest <paramref name="cap"/> unvisited keys. Insertion is bounded
    /// by cap, so a 6k resident set is never sorted. <paramref name="maxDistBlocks"/>
    /// ≤ 0 means no band; otherwise keys farther than that are dropped.
    /// </summary>
    public static void FillNearestCapped(
        List<(long Key, double DistSq)> dest,
        IEnumerable<long> keys,
        HashSet<long> visited,
        double px,
        double pz,
        int cap,
        double maxDistBlocks,
        Func<long, bool>? accept = null)
    {
        dest.Clear();
        if (cap <= 0) return;

        double maxDistSq = maxDistBlocks > 0
            ? maxDistBlocks * maxDistBlocks
            : double.PositiveInfinity;

        foreach (long key in keys)
        {
            if (visited.Contains(key)) continue;
            if (accept != null && !accept(key)) continue;
            double distSq = LodWorld.NearestDistanceSqTo(key, px, pz);
            if (distSq > maxDistSq) continue;
            if (dest.Count == cap && distSq >= dest[cap - 1].DistSq) continue;

            int at = dest.Count;
            while (at > 0 && dest[at - 1].DistSq > distSq) at--;
            dest.Insert(at, (key, distSq));
            if (dest.Count > cap) dest.RemoveAt(dest.Count - 1);
        }
    }
}
