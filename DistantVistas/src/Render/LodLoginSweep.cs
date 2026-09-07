using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Helpers for the login visit sweep: which L0 canvases to revisit, chunk coverage,
/// and world-readiness before teleports begin.
/// </summary>
public static class LodLoginSweep
{
    public const int WorldReadyRadiusChunks = 2;

    /// <summary>~1.2s max chunk wait at 50 ms pulse — hop on; ring still streams in.</summary>
    public const int MaxChunkWaitTicks = 24;

    /// <summary>~0.8s max capture wait at 50 ms pulse.</summary>
    public const int MaxCaptureWaitTicks = 16;

    public const int MaxWorldReadyTicks = 2400;

    public static IEnumerable<long> VisitedL0Keys(LodWorld world)
    {
        var keys = new List<long>();
        foreach (long key in world.HasDataSet)
        {
            if (LodWorld.KeyLevel(key) == 0) keys.Add(key);
        }
        keys.Sort();
        return keys;
    }

    public static IEnumerable<(int Cx, int Cz)> ChunkColumnsForL0(long l0Key)
    {
        int sx = LodWorld.KeySx(l0Key);
        int sz = LodWorld.KeySz(l0Key);
        int cx0 = (sx * LodSection.SectionBlocks) / GlobalConstants.ChunkSize;
        int cz0 = (sz * LodSection.SectionBlocks) / GlobalConstants.ChunkSize;
        yield return (cx0, cz0);
        yield return (cx0 + 1, cz0);
        yield return (cx0, cz0 + 1);
        yield return (cx0 + 1, cz0 + 1);
    }

    public static (double X, double Y, double Z) VisitPosition(IClientWorldAccessor world, long l0Key)
    {
        int footprint = LodWorld.KeyFootprintBlocks(l0Key);
        int minX = LodWorld.KeySx(l0Key) * footprint;
        int minZ = LodWorld.KeySz(l0Key) * footprint;
        int x = minX + footprint / 2;
        int z = minZ + footprint / 2;
        int y = world.SeaLevel + 140;

        int cx = x / GlobalConstants.ChunkSize;
        int cz = z / GlobalConstants.ChunkSize;
        IMapChunk? mapChunk = world.BlockAccessor.GetMapChunk(cx, cz);
        ushort[]? rain = mapChunk?.RainHeightMap;
        if (rain != null && rain.Length >= GlobalConstants.ChunkSize * GlobalConstants.ChunkSize)
        {
            int lx = x % GlobalConstants.ChunkSize;
            int lz = z % GlobalConstants.ChunkSize;
            y = rain[lz * GlobalConstants.ChunkSize + lx] + 4;
        }

        return (x, y, z);
    }

    public static bool AllMapChunksLoaded(IBlockAccessor blockAccessor, long l0Key)
    {
        int footprint = LodWorld.KeyFootprintBlocks(l0Key);
        int minX = LodWorld.KeySx(l0Key) * footprint;
        int minZ = LodWorld.KeySz(l0Key) * footprint;
        return LodCoveragePolicy.AllMapChunksLoaded(
            minX, minX + footprint, minZ, minZ + footprint,
            GlobalConstants.ChunkSize,
            (cx, cz) => blockAccessor.GetMapChunk(cx, cz) != null);
    }

    /// <summary>At least one map chunk of the L0 footprint is resident (partial stream).</summary>
    public static bool AnyMapChunksLoaded(IBlockAccessor blockAccessor, long l0Key)
    {
        foreach ((int cx, int cz) in ChunkColumnsForL0(l0Key))
        {
            if (blockAccessor.GetMapChunk(cx, cz) != null) return true;
        }
        return false;
    }

    /// <summary>How many of the four L0 map columns are resident (0–4).</summary>
    public static int CountLoadedMapChunks(IBlockAccessor blockAccessor, long l0Key)
    {
        int n = 0;
        foreach ((int cx, int cz) in ChunkColumnsForL0(l0Key))
        {
            if (blockAccessor.GetMapChunk(cx, cz) != null) n++;
        }
        return n;
    }

    /// <summary>
    /// Spawn neighbourhood has map chunks so teleports can stream real terrain.
    /// </summary>
    public static bool IsWorldReady(IClientWorldAccessor world)
    {
        EntityPos pos = world.Player.Entity.Pos;
        int cx = (int)Math.Floor(pos.X / GlobalConstants.ChunkSize);
        int cz = (int)Math.Floor(pos.Z / GlobalConstants.ChunkSize);
        var ba = world.BlockAccessor;
        for (int dz = -WorldReadyRadiusChunks; dz <= WorldReadyRadiusChunks; dz++)
        {
            for (int dx = -WorldReadyRadiusChunks; dx <= WorldReadyRadiusChunks; dx++)
            {
                if (cx + dx < 0 || cz + dz < 0) continue;
                if (ba.GetMapChunk(cx + dx, cz + dz) == null) return false;
            }
        }
        return true;
    }
}
