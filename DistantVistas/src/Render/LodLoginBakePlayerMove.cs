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

    /// <summary>
    /// Resumable cursor for one Chebyshev annulus. The cursor intentionally keeps
    /// the coordinate that hit the shared budget, so the next tick never restarts
    /// a partially issued shell.
    /// </summary>
    public sealed class ChunkRingCursor
    {
        int centerCx;
        int centerCz;
        int innerRadius;
        int outerRadius;
        int dx;
        int dz;
        bool configured;
        bool complete;

        public bool Complete => complete;

        public void Configure(int cx, int cz, int inner, int outer)
        {
            if (configured
                && centerCx == cx
                && centerCz == cz
                && innerRadius == inner
                && outerRadius == outer)
                return;

            configured = true;
            complete = false;
            centerCx = cx;
            centerCz = cz;
            innerRadius = Math.Max(-1, inner);
            outerRadius = Math.Max(-1, outer);
            dx = -outerRadius;
            dz = -outerRadius;
            if (outerRadius < 0 || outerRadius <= innerRadius)
                complete = true;
        }

        public void Reset()
        {
            configured = false;
            complete = false;
            dx = 0;
            dz = 0;
        }

        public bool TryNext(out int cx, out int cz)
        {
            cx = 0;
            cz = 0;
            if (!configured || complete)
                return false;

            while (dz <= outerRadius)
            {
                int currentDx = dx;
                int currentDz = dz;

                if (Math.Max(Math.Abs(currentDx), Math.Abs(currentDz)) <= innerRadius)
                {
                    Advance();
                    continue;
                }

                cx = centerCx + currentDx;
                cz = centerCz + currentDz;
                return true;
            }

            complete = true;
            return false;
        }

        /// <summary>Commits the current coordinate after the request coordinator accepted it.</summary>
        public void CommitCurrent() => Advance();

        void Advance()
        {
            dx++;
            if (dx > outerRadius)
            {
                dx = -outerRadius;
                dz++;
            }
        }
    }

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
            RequestChunkColumnsVisible(
                capi, x, z, entity.Pos.Dimension, chunkVisibleRadius, "pickup-restore",
                LodChunkRequestPriority.Critical);
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
            RequestChunkColumnsVisible(
                capi, x, z, entity.Pos.Dimension, chunkVisibleRadius, "player-move");
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

    /// <summary>
    /// Mark the Chebyshev annulus visible. Returns false if the overlay request
    /// budget ran out mid-ring so the caller can retry the same shell.
    /// </summary>
    public static bool RequestChunkColumnRing(
        ICoreClientAPI capi,
        double x,
        double z,
        int dimension,
        int innerRadius,
        int outerRadius,
        ChunkRingCursor? cursor = null,
        string producer = "ring")
    {
        if (outerRadius < 0) return true;
        int cx = (int)Math.Floor(x / GlobalConstants.ChunkSize);
        int cz = (int)Math.Floor(z / GlobalConstants.ChunkSize);
        IClientWorldAccessor world = capi.World;
        cursor ??= new ChunkRingCursor();
        cursor.Configure(cx, cz, innerRadius, outerRadius);
        while (cursor.TryNext(out int tx, out int tz))
        {
            if (!LodLoginChunkRequestBudget.TrySetVisible(
                    world, tx, tz, dimension, producer))
                return false;
            cursor.CommitCurrent();
        }
        return true;
    }

    public static void RequestChunkColumnsVisible(
        ICoreClientAPI capi,
        double x,
        double z,
        int dimension,
        int chunkVisibleRadius,
        string producer = "visible-shell",
        LodChunkRequestPriority priority = LodChunkRequestPriority.Normal)
    {
        int cx = (int)Math.Floor(x / GlobalConstants.ChunkSize);
        int cz = (int)Math.Floor(z / GlobalConstants.ChunkSize);
        LodLoginChunkRequestBudget.QueueVisibleSquare(
            capi.World,
            cx,
            cz,
            dimension,
            chunkVisibleRadius,
            producer,
            priority);
    }

    /// <summary>Mark all four map columns of an L0 footprint visible on the client.</summary>
    public static void RequestL0MapChunksVisible(ICoreClientAPI capi, long l0Key, int dimension)
    {
        LodLoginChunkRequestBudget.QueueVisibleColumns(
            capi.World,
            LodLoginSweep.ChunkColumnsForL0(l0Key),
            dimension,
            "hop-l0",
            l0Key);
    }
}
