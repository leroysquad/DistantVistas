using Vintagestory.API.Client;
using Vintagestory.API.Common;
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

    /// <summary>Spawn restore: full vanilla disk, not the 5x5 visit default.</summary>
    public static int SpawnRestoreRadius(int desiredViewDistance)
    {
        int vd = desiredViewDistance > 0 ? desiredViewDistance : 256;
        int cs = GlobalConstants.ChunkSize;
        return GameMath.Clamp((int)Math.Ceiling(vd / (double)cs) + 1, 4, 16);
    }

    /// <summary>
    /// Write the exact pickup doubles onto Pos and ServerPos. Never Floor to a
    /// chunk origin, never VisitPosition, never a spawn substitute. Yaw/pitch too.
    /// Safety net when any leftover hop still moved the player.
    /// </summary>
    public static void ApplyExactPickup(
        ICoreClientAPI capi,
        EntityPlayer entity,
        double x,
        double y,
        double z,
        float yaw,
        float pitch,
        bool requestChunks = false,
        int chunkVisibleRadius = ChunkVisibleRadius)
    {
        WriteExactPickup(entity, x, y, z, yaw, pitch);
        if (requestChunks)
            RequestChunkColumnsVisible(capi, x, z, entity.Pos.Dimension, chunkVisibleRadius);
    }

    public static void WriteExactPickup(
        EntityPlayer entity,
        double x,
        double y,
        double z,
        float yaw,
        float pitch)
    {
        // Direct doubles — do not round, Floor, or go through a BlockPos.
        entity.Pos.SetPos(x, y, z);
        entity.Pos.Yaw = yaw;
        entity.Pos.Pitch = pitch;
        entity.Pos.Motion.Set(0, 0, 0);
        entity.ServerPos.SetPos(x, y, z);
        entity.ServerPos.Yaw = yaw;
        entity.ServerPos.Pitch = pitch;
        entity.ServerPos.Motion.Set(0, 0, 0);
        entity.PositionBeforeFalling.Set(x, y, z);
        LodVsCompat.TryUpdatePartitioning(entity);
    }

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
        bool requestChunks = true,
        int chunkVisibleRadius = ChunkVisibleRadius)
    {
        entity.Pos.SetPos(x, y, z);
        entity.Pos.Motion.Set(0, 0, 0);
        entity.PositionBeforeFalling.Set(x, y, z);
        LodVsCompat.TryUpdatePartitioning(entity);

        if (requestChunks)
            RequestChunkColumnsVisible(capi, x, z, entity.Pos.Dimension, chunkVisibleRadius);
    }

    public static void ApplyQuietFrom(
        ICoreClientAPI capi,
        EntityPlayer entity,
        EntityPos pose,
        bool requestChunks = true,
        int chunkVisibleRadius = ChunkVisibleRadius)
    {
        ApplyExactPickup(
            capi, entity, pose.X, pose.Y, pose.Z, pose.Yaw, pose.Pitch,
            requestChunks, chunkVisibleRadius);
    }

    /// <summary>Hold pose between ticks without re-requesting chunks every frame.</summary>
    public static void HoldQuiet(EntityPlayer entity, double x, double y, double z)
    {
        entity.Pos.SetPos(x, y, z);
        entity.Pos.Motion.Set(0, 0, 0);
    }

    public static void RequestChunkColumnRing(
        ICoreClientAPI capi,
        double x,
        double z,
        int dimension,
        int innerRadius,
        int outerRadius)
    {
        if (outerRadius < 0) return;
        int cx = (int)Math.Floor(x / GlobalConstants.ChunkSize);
        int cz = (int)Math.Floor(z / GlobalConstants.ChunkSize);
        IClientWorldAccessor world = capi.World;
        for (int dz = -outerRadius; dz <= outerRadius; dz++)
        {
            for (int dx = -outerRadius; dx <= outerRadius; dx++)
            {
                int chebyshev = Math.Max(Math.Abs(dx), Math.Abs(dz));
                if (chebyshev <= innerRadius) continue;
                int tx = cx + dx;
                int tz = cz + dz;
                if (tx < 0 || tz < 0) continue;
                world.SetChunkColumnVisible(tx, tz, dimension);
            }
        }
    }

    public static void RequestChunkColumnsVisible(
        ICoreClientAPI capi,
        double x,
        double z,
        int dimension,
        int chunkVisibleRadius)
    {
        int cx = (int)Math.Floor(x / GlobalConstants.ChunkSize);
        int cz = (int)Math.Floor(z / GlobalConstants.ChunkSize);
        IClientWorldAccessor world = capi.World;
        for (int dz = -chunkVisibleRadius; dz <= chunkVisibleRadius; dz++)
        {
            for (int dx = -chunkVisibleRadius; dx <= chunkVisibleRadius; dx++)
            {
                int tx = cx + dx;
                int tz = cz + dz;
                if (tx < 0 || tz < 0) continue;
                world.SetChunkColumnVisible(tx, tz, dimension);
            }
        }
    }
}
