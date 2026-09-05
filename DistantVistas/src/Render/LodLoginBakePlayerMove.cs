using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Client-only player repositioning for the login visit sweep. Never sends chat
/// commands or server packets — avoids /tp echo, audit spam, and server log noise.
/// </summary>
public static class LodLoginBakePlayerMove
{
    /// <summary>Chunk columns to mark visible around each visit (matches sweep load radius).</summary>
    public const int ChunkVisibleRadius = 2;

    /// <summary>
    /// Teleport the local player on the client: set entity pose, clear motion, refresh
    /// partitioning, and nudge the chunk loader. No chat or server commands.
    /// </summary>
    public static void ApplyQuiet(
        ICoreClientAPI capi,
        EntityPlayer entity,
        double x,
        double y,
        double z,
        bool requestChunks = true)
    {
        entity.Pos.SetPos(x, y, z);
        entity.Pos.Motion.Set(0, 0, 0);
        entity.PositionBeforeFalling.Set(x, y, z);
        entity.UpdatePartitioning();

        if (requestChunks)
            RequestChunkColumnsVisible(capi, x, z, entity.Pos.Dimension);
    }

    public static void ApplyQuietFrom(
        ICoreClientAPI capi,
        EntityPlayer entity,
        EntityPos pose,
        bool requestChunks = true)
    {
        entity.Pos.SetFrom(pose);
        entity.Pos.Motion.Set(0, 0, 0);
        entity.PositionBeforeFalling.Set(pose.X, pose.Y, pose.Z);
        entity.UpdatePartitioning();

        if (requestChunks)
            RequestChunkColumnsVisible(capi, pose.X, pose.Z, entity.Pos.Dimension);
    }

    /// <summary>Hold pose between ticks without re-requesting chunks every frame.</summary>
    public static void HoldQuiet(EntityPlayer entity, double x, double y, double z)
    {
        entity.Pos.SetPos(x, y, z);
        entity.Pos.Motion.Set(0, 0, 0);
    }

    static void RequestChunkColumnsVisible(ICoreClientAPI capi, double x, double z, int dimension)
    {
        int cx = (int)Math.Floor(x / GlobalConstants.ChunkSize);
        int cz = (int)Math.Floor(z / GlobalConstants.ChunkSize);
        IClientWorldAccessor world = capi.World;
        for (int dz = -ChunkVisibleRadius; dz <= ChunkVisibleRadius; dz++)
        {
            for (int dx = -ChunkVisibleRadius; dx <= ChunkVisibleRadius; dx++)
            {
                int tx = cx + dx;
                int tz = cz + dz;
                if (tx < 0 || tz < 0) continue;
                world.SetChunkColumnVisible(tx, tz, dimension);
            }
        }
    }
}
