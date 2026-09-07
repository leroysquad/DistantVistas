using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace DistantVistas;

/// <summary>
/// Overlay hop-unlock pump (Plan C): invisibly moves the real player to cold pending
/// L0 visit cells so vanilla's 750-block warm disk residents map chunks for scouts.
/// Scouts stay primary parallel bakers; exact pickup XYZ + look restored at end.
/// </summary>
public sealed class LodLoginHopUnlock
{
    /// <summary>Paint queue empty this long before first hop / advance.</summary>
    public const int TriggerPaintStarveTicks = 16;
    /// <summary>Ticks at an unlock point with all-WaitChunks stall before retarget.</summary>
    public const int AdvanceStallTicks = 96;
    /// <summary>Do not hop until warm-ring cliff band is reached.</summary>
    public const int MinFinishedForHop = 280;
    /// <summary>Each retarget must move at least this many blocks from the prior unlock.</summary>
    public const int MinHopDeltaBlocks = 128;
    /// <summary>Fallback radial pump step when no cold L0 sits outside prior warm disk.</summary>
    public const int FallbackRadiusStepBlocks = 192;
    /// <summary>Extra chunk columns for player stream pump while hop-unlock active.</summary>
    public const int StreamPumpExtraChunks = 4;

    public bool Active { get; private set; }
    public double X { get; private set; }
    public double Y { get; private set; }
    public double Z { get; private set; }
    public int Ring { get; private set; }
    public long TargetKey { get; private set; }
    public int TicksAtPoint { get; private set; }
    public int FinishedAtHop { get; private set; }
    int lastFallbackRadiusBlocks;
    long lastTargetKey;

    public void Reset()
    {
        Active = false;
        Ring = 0;
        TargetKey = 0;
        TicksAtPoint = 0;
        FinishedAtHop = 0;
        lastFallbackRadiusBlocks = 0;
        lastTargetKey = 0;
    }

    public void StreamCenter(double pickupX, double pickupZ, out double x, out double z)
    {
        if (Active)
        {
            x = X;
            z = Z;
            return;
        }
        x = pickupX;
        z = pickupZ;
    }

    public int StreamPumpRadiusChunks(LodLoginBakeViewBoost viewBoost) =>
        Active
            ? viewBoost.SpawnStreamRadiusChunks + StreamPumpExtraChunks
            : viewBoost.SpawnStreamRadiusChunks;

    /// <summary>
    /// 1038/1039 signature: paint starve, all live scouts WaitChunks, zero Capture.
    /// </summary>
    public static bool MatchesStallSignature(
        int paintStarveTicks,
        int waitChunksLive,
        int captureLive,
        int liveScouts,
        int finished)
    {
        if (liveScouts < LodLoginScoutFill.ChunkPressureMinLive) return false;
        if (paintStarveTicks < TriggerPaintStarveTicks) return false;
        if (captureLive > 0) return false;
        if (waitChunksLive < liveScouts) return false;
        if (finished < MinFinishedForHop) return false;
        return true;
    }

    public bool ShouldAdvance(
        int paintStarveTicks,
        int waitChunksLive,
        int captureLive,
        int liveScouts,
        int finished)
    {
        if (!Active) return false;
        if (!MatchesStallSignature(paintStarveTicks, waitChunksLive, captureLive, liveScouts, finished))
            return false;
        if (TicksAtPoint >= AdvanceStallTicks) return true;
        return finished - FinishedAtHop < 4 && TicksAtPoint >= AdvanceStallTicks / 2;
    }

    public bool TryFirstHop(
        ICoreClientAPI capi,
        double pickupX,
        double pickupY,
        double pickupZ,
        IReadOnlyList<long> pendingKeys,
        int finished)
    {
        if (Active) return false;
        Ring = 1;
        lastFallbackRadiusBlocks = 0;
        return ApplyHop(capi, pickupX, pickupY, pickupZ, pendingKeys, finished, advance: false);
    }

    /// <summary>Continuous retarget — no max ring cap; each hop must move meaningfully.</summary>
    public bool TryAdvanceHop(
        ICoreClientAPI capi,
        double pickupX,
        double pickupY,
        double pickupZ,
        IReadOnlyList<long> pendingKeys,
        int finished)
    {
        if (!Active) return false;
        Ring++;
        return ApplyHop(capi, pickupX, pickupY, pickupZ, pendingKeys, finished, advance: true);
    }

    bool ApplyHop(
        ICoreClientAPI capi,
        double pickupX,
        double pickupY,
        double pickupZ,
        IReadOnlyList<long> pendingKeys,
        int finished,
        bool advance)
    {
        double prevX = X;
        double prevZ = Z;
        bool havePrev = Active;

        if (!TryPickColdUnlockTarget(
                capi, pickupX, pickupZ, pendingKeys, prevX, prevZ, havePrev, lastTargetKey,
                out double x, out double y, out double z, out long targetKey, out int loaded,
                out int distPickupBlocks, out double bearingRad)
            && !TryFallbackRadialUnlock(
                capi, pickupX, pickupY, pickupZ, pendingKeys, finished, Ring, havePrev,
                prevX, prevZ, ref lastFallbackRadiusBlocks,
                out x, out y, out z, out targetKey, out loaded, out distPickupBlocks, out bearingRad))
            return false;

        if (havePrev && DistBlocks(prevX, prevZ, x, z) < MinHopDeltaBlocks / 2)
            return false;

        X = x;
        Y = y;
        Z = z;
        TargetKey = targetKey;
        lastTargetKey = targetKey;
        Active = true;
        TicksAtPoint = 0;
        FinishedAtHop = finished;

        LodScoutSeqDiag.LogHopUnlock(
            Ring, advance, x, y, z, distPickupBlocks, bearingRad, finished, pendingKeys.Count,
            targetKey, loaded, usedFallback: targetKey == 0);
        return true;
    }

    public void TickAtPoint()
    {
        if (Active) TicksAtPoint++;
    }

    static bool TryPickColdUnlockTarget(
        ICoreClientAPI capi,
        double pickupX,
        double pickupZ,
        IReadOnlyList<long> pendingKeys,
        double prevX,
        double prevZ,
        bool havePrev,
        long skipKey,
        out double x,
        out double y,
        out double z,
        out long targetKey,
        out int loadedChunks,
        out int distPickupBlocks,
        out double bearingRad)
    {
        x = y = z = 0;
        targetKey = 0;
        loadedChunks = 0;
        distPickupBlocks = 0;
        bearingRad = 0;

        int rHold = LodLoginBakeViewBoost.SweepBoostViewDistanceBlocks;
        double pickupWarmSq = (double)rHold * rHold;
        double prevWarmSq = havePrev ? pickupWarmSq * 0.64 : 0;
        double minHopSq = (double)MinHopDeltaBlocks * MinHopDeltaBlocks;
        var ba = capi.World.BlockAccessor;

        long bestKey = 0;
        int bestLoaded = LodLoginScoutFill.MapChunksPerL0 + 1;
        long bestScore = long.MinValue;
        double bestX = 0;
        double bestY = 0;
        double bestZ = 0;

        for (int i = 0; i < pendingKeys.Count; i++)
        {
            long key = pendingKeys[i];
            if (key == skipKey) continue;
            int loaded = LodLoginSweep.CountLoadedMapChunks(ba, key);
            if (loaded >= LodLoginScoutFill.MapChunksPerL0) continue;

            var (vx, vy, vz) = LodLoginSweep.VisitPosition(capi.World, key);
            double dxPickup = vx - pickupX;
            double dzPickup = vz - pickupZ;
            double distPickupSq = dxPickup * dxPickup + dzPickup * dzPickup;

            if (!havePrev)
            {
                if (distPickupSq <= pickupWarmSq) continue;
            }
            else
            {
                double dxPrev = vx - prevX;
                double dzPrev = vz - prevZ;
                double distPrevSq = dxPrev * dxPrev + dzPrev * dzPrev;
                if (distPrevSq < minHopSq) continue;
                if (distPrevSq <= prevWarmSq) continue;
            }

            long score = (long)(LodLoginScoutFill.MapChunksPerL0 - loaded) * 1_000_000_000L
                + (long)distPickupSq
                - (long)loaded * 10_000L;
            if (score > bestScore)
            {
                bestScore = score;
                bestKey = key;
                bestLoaded = loaded;
                bestX = vx;
                bestY = vy;
                bestZ = vz;
            }
        }

        if (bestKey == 0) return false;

        targetKey = bestKey;
        loadedChunks = bestLoaded;
        x = bestX;
        y = bestY;
        z = bestZ;
        distPickupBlocks = (int)Math.Round(Math.Sqrt(
            (bestX - pickupX) * (bestX - pickupX) + (bestZ - pickupZ) * (bestZ - pickupZ)));
        bearingRad = Math.Atan2(bestZ - pickupZ, bestX - pickupX);
        return true;
    }

    static bool TryFallbackRadialUnlock(
        ICoreClientAPI capi,
        double pickupX,
        double pickupY,
        double pickupZ,
        IReadOnlyList<long> pendingKeys,
        int finished,
        int ring,
        bool havePrev,
        double prevX,
        double prevZ,
        ref int lastFallbackRadiusBlocks,
        out double x,
        out double y,
        out double z,
        out long targetKey,
        out int loadedChunks,
        out int distPickupBlocks,
        out double bearingRad)
    {
        int rHold = LodLoginBakeViewBoost.SweepBoostViewDistanceBlocks;
        int finishedR = LodLoginScoutFill.FinishedToRadiusBlocks(finished);
        int maxR = (int)LodLoginBake.SpawnSolidRadiusBlocks - LodSection.SectionBlocks;

        int nextR = havePrev
            ? Math.Max(lastFallbackRadiusBlocks + FallbackRadiusStepBlocks, (int)DistBlocks(prevX, prevZ, pickupX, pickupZ) + MinHopDeltaBlocks)
            : Math.Max(rHold, finishedR + LodSection.SectionBlocks);
        nextR = Math.Min(nextR, maxR);
        if (havePrev && nextR <= lastFallbackRadiusBlocks)
            nextR = Math.Min(lastFallbackRadiusBlocks + FallbackRadiusStepBlocks, maxR);
        lastFallbackRadiusBlocks = nextR;

        bearingRad = BearingTowardColdPending(
            capi, pickupX, pickupZ, pendingKeys, finishedR, nextR, ring);
        x = pickupX + Math.Cos(bearingRad) * nextR;
        z = pickupZ + Math.Sin(bearingRad) * nextR;
        y = pickupY;
        targetKey = 0;
        loadedChunks = 0;
        distPickupBlocks = nextR;

        // Snap fallback to nearest cold pending visit if close enough.
        if (TryPickColdUnlockTarget(
                capi, pickupX, pickupZ, pendingKeys, prevX, prevZ, havePrev, 0,
                out double sx, out double sy, out double sz, out long sKey, out int sLoaded,
                out _, out _))
        {
            if (DistBlocks(x, z, sx, sz) < rHold * 0.5)
            {
                x = sx;
                y = sy;
                z = sz;
                targetKey = sKey;
                loadedChunks = sLoaded;
                distPickupBlocks = (int)Math.Round(DistBlocks(pickupX, pickupZ, x, z));
                bearingRad = Math.Atan2(z - pickupZ, x - pickupX);
            }
        }

        return true;
    }

    static double BearingTowardColdPending(
        ICoreClientAPI capi,
        double pickupX,
        double pickupZ,
        IReadOnlyList<long> pendingKeys,
        int finishedRadiusBlocks,
        int targetRadiusBlocks,
        int ring)
    {
        var ba = capi.World.BlockAccessor;
        double sumX = 0;
        double sumZ = 0;
        double weight = 0;
        double warmSq = (double)LodLoginBakeViewBoost.SweepBoostViewDistanceBlocks
            * LodLoginBakeViewBoost.SweepBoostViewDistanceBlocks;
        for (int i = 0; i < pendingKeys.Count && weight < 128; i++)
        {
            long key = pendingKeys[i];
            int loaded = LodLoginSweep.CountLoadedMapChunks(ba, key);
            if (loaded >= LodLoginScoutFill.MapChunksPerL0) continue;
            var (vx, _, vz) = LodLoginSweep.VisitPosition(capi.World, key);
            double dx = vx - pickupX;
            double dz = vz - pickupZ;
            double distSq = dx * dx + dz * dz;
            if (distSq <= warmSq) continue;
            double w = 1.0 / (1.0 + loaded * 4);
            sumX += dx * w;
            sumZ += dz * w;
            weight += w;
        }

        if (weight > 0 && sumX * sumX + sumZ * sumZ > 1)
            return Math.Atan2(sumZ, sumX);

        double baseAngle = finishedRadiusBlocks > 0
            ? Math.Atan2(targetRadiusBlocks * 0.3, finishedRadiusBlocks)
            : 0;
        return baseAngle + ring * 2.399963;
    }

    static double DistBlocks(double ax, double az, double bx, double bz)
    {
        double dx = bx - ax;
        double dz = bz - az;
        return Math.Sqrt(dx * dx + dz * dz);
    }
}
