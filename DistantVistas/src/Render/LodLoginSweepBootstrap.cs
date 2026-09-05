using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Plans which L0 cells the login visit sweep should touch. Existing visited canvases
/// are always included; an empty cache gets a coast-biased ring sized to the timing budget.
/// </summary>
public static class LodLoginSweepBootstrap
{
    public static IEnumerable<long> PlanKeys(
        LodWorld world,
        IClientWorldAccessor clientWorld,
        double secPerStop)
    {
        List<long> visited = LodLoginSweep.VisitedL0Keys(world).ToList();
        if (visited.Count > 0)
        {
            visited.Sort();
            return visited;
        }

        int budget = LodLoginSweepTiming.BootstrapCellBudget(secPerStop);
        return CoastBiasedRing(clientWorld, budget);
    }

    /// <summary>
    /// Spiral ring around the player, preferring cells whose centre is near sea level
    /// (ocean + coast) so bootstrap captures shoreline without deep inland worldgen.
    /// </summary>
    static IEnumerable<long> CoastBiasedRing(IClientWorldAccessor world, int maxCells)
    {
        EntityPos pos = world.Player.Entity.Pos;
        int footprint = LodSection.SectionBlocks;
        int centerSx = (int)Math.Floor(pos.X / footprint);
        int centerSz = (int)Math.Floor(pos.Z / footprint);
        int sea = world.SeaLevel;

        var preferred = new List<(long Key, int Score)>();
        var fallback = new List<long>();

        for (int ring = 0; ring < 24; ring++)
        {
            foreach ((int sx, int sz) in RingCells(centerSx, centerSz, ring))
            {
                if (sx < 0 || sz < 0) continue;
                long key = LodWorld.SectionKey(0, sx, sz);
                int score = CoastScore(world, sx, sz, footprint, sea);
                if (score > 0)
                    preferred.Add((key, score));
                else
                    fallback.Add(key);
            }
        }

        preferred.Sort((a, b) => b.Score.CompareTo(a.Score));

        var chosen = new List<long>(maxCells);
        var seen = new HashSet<long>();
        foreach ((long key, _) in preferred)
        {
            if (!seen.Add(key)) continue;
            chosen.Add(key);
            if (chosen.Count >= maxCells) return chosen;
        }

        foreach (long key in fallback)
        {
            if (!seen.Add(key)) continue;
            chosen.Add(key);
            if (chosen.Count >= maxCells) return chosen;
        }

        return chosen;
    }

    static int CoastScore(IClientWorldAccessor world, int sx, int sz, int footprint, int sea)
    {
        int cx = (sx * footprint + footprint / 2) / GlobalConstants.ChunkSize;
        int cz = (sz * footprint + footprint / 2) / GlobalConstants.ChunkSize;
        IMapChunk? map = world.BlockAccessor.GetMapChunk(cx, cz);
        ushort[]? rain = map?.RainHeightMap;
        if (rain == null || rain.Length < GlobalConstants.ChunkSize * GlobalConstants.ChunkSize)
            return 0;

        int idx = (GlobalConstants.ChunkSize / 2) * GlobalConstants.ChunkSize
            + GlobalConstants.ChunkSize / 2;
        int y = rain[idx];
        int delta = Math.Abs(y - sea);
        if (delta <= 6) return 100 - delta;
        if (delta <= 18) return 40 - delta;
        return 0;
    }

    static IEnumerable<(int Sx, int Sz)> RingCells(int cx, int cz, int ring)
    {
        if (ring == 0)
        {
            yield return (cx, cz);
            yield break;
        }

        int x0 = cx - ring;
        int x1 = cx + ring;
        int z0 = cz - ring;
        int z1 = cz + ring;
        for (int x = x0; x <= x1; x++)
        {
            yield return (x, z0);
            yield return (x, z1);
        }
        for (int z = z0 + 1; z < z1; z++)
        {
            yield return (x0, z);
            yield return (x1, z);
        }
    }
}
