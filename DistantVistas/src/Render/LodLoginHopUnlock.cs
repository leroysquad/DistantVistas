using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DistantVistas;

/// <summary>
/// Overlay hop-unlock pump (Plan C): invisibly moves the real player to near-cliff
/// cold pending L0 visit cells (warm-ring annulus ~750–1024 blocks from pickup) so
/// vanilla's 750-block warm disk residents map chunks for scouts.
/// </summary>
public sealed class LodLoginHopUnlock
{
    public const int TriggerPaintStarveTicks = 16;
    public const int AdvanceStallTicks = 96;
    public const int MinFinishedForHop = 280;
    public const int MinHopDeltaBlocks = 128;
    public const int FallbackRadiusStepBlocks = 192;
    public const int StreamPumpExtraChunks = 4;
    /// <summary>Ticks at unlock before banning a key that stays at loaded=0.</summary>
    public const int FailedKeyDwellTicks = 48;
    /// <summary>Retarget cycles to skip a failed hop target.</summary>
    public const int FailedKeyBanRings = 8;

    public bool Active { get; private set; }
    public double X { get; private set; }
    public double Y { get; private set; }
    public double Z { get; private set; }
    public int Ring { get; private set; }
    public long TargetKey { get; private set; }
    public int TicksAtPoint { get; private set; }
    public int FinishedAtHop { get; private set; }

    readonly Dictionary<long, int> failedKeyBan = new();
    readonly List<long> banScratch = new(32);
    int lastFallbackRadiusBlocks;
    int lastSkippedCooldown;

    public void Reset()
    {
        Active = false;
        Ring = 0;
        TargetKey = 0;
        TicksAtPoint = 0;
        FinishedAtHop = 0;
        lastFallbackRadiusBlocks = 0;
        lastSkippedCooldown = 0;
        failedKeyBan.Clear();
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

    public bool TryAdvanceHop(
        ICoreClientAPI capi,
        double pickupX,
        double pickupY,
        double pickupZ,
        IReadOnlyList<long> pendingKeys,
        int finished)
    {
        if (!Active) return false;
        BanCurrentTargetIfStillCold(capi);
        TickFailedKeyBans();
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
        int loadedAfterDwell = 0;
        if (advance && TargetKey != 0)
        {
            loadedAfterDwell = LodLoginSweep.CountLoadedMapChunks(
                capi.World.BlockAccessor, TargetKey);
        }

        if (!TryPickNearAnnulusTarget(
                capi, pickupX, pickupZ, pendingKeys, finished, prevX, prevZ, havePrev,
                out double x, out double y, out double z, out long targetKey, out int loaded,
                out int distPickupBlocks, out double bearingRad, out int pastWarmBlocks)
            && !TryFallbackAnnulusUnlock(
                capi, pickupX, pickupY, pickupZ, pendingKeys, finished, prevX, prevZ, havePrev,
                out x, out y, out z, out targetKey, out loaded, out distPickupBlocks,
                out bearingRad, out pastWarmBlocks))
            return false;

        if (havePrev && DistBlocks(prevX, prevZ, x, z) < MinHopDeltaBlocks / 2)
            return false;

        X = x;
        Y = y;
        Z = z;
        TargetKey = targetKey;
        Active = true;
        TicksAtPoint = 0;
        FinishedAtHop = finished;

        LodScoutSeqDiag.LogHopUnlock(
            Ring, advance, x, y, z, distPickupBlocks, bearingRad, finished, pendingKeys.Count,
            targetKey, loaded, usedFallback: targetKey == 0,
            distFromPickup: distPickupBlocks, pastWarmBlocks: pastWarmBlocks,
            loadedAfterDwell: loadedAfterDwell, skippedCooldown: lastSkippedCooldown);
        return true;
    }

    public void TickAtPoint()
    {
        if (Active) TicksAtPoint++;
    }

    void BanCurrentTargetIfStillCold(ICoreClientAPI capi)
    {
        if (TargetKey == 0 || TicksAtPoint < FailedKeyDwellTicks) return;
        int loaded = LodLoginSweep.CountLoadedMapChunks(capi.World.BlockAccessor, TargetKey);
        if (loaded == 0)
            failedKeyBan[TargetKey] = FailedKeyBanRings;
    }

    void TickFailedKeyBans()
    {
        if (failedKeyBan.Count == 0) return;
        banScratch.Clear();
        foreach (long key in failedKeyBan.Keys)
            banScratch.Add(key);
        for (int i = 0; i < banScratch.Count; i++)
        {
            long key = banScratch[i];
            int left = failedKeyBan[key] - 1;
            if (left <= 0) failedKeyBan.Remove(key);
            else failedKeyBan[key] = left;
        }
    }

    bool TryPickNearAnnulusTarget(
        ICoreClientAPI capi,
        double pickupX,
        double pickupZ,
        IReadOnlyList<long> pendingKeys,
        int finished,
        double prevX,
        double prevZ,
        bool havePrev,
        out double x,
        out double y,
        out double z,
        out long targetKey,
        out int loadedChunks,
        out int distPickupBlocks,
        out double bearingRad,
        out int pastWarmBlocks)
    {
        x = y = z = 0;
        targetKey = 0;
        loadedChunks = 0;
        distPickupBlocks = 0;
        bearingRad = 0;
        pastWarmBlocks = 0;
        lastSkippedCooldown = 0;

        int rHold = LodLoginBakeViewBoost.SweepBoostViewDistanceBlocks;
        int finishedR = LodLoginScoutFill.FinishedToRadiusBlocks(finished);
        double pickupWarmSq = (double)rHold * rHold;
        double spawnSq = LodLoginBake.SpawnSolidRadiusBlocks * LodLoginBake.SpawnSolidRadiusBlocks;
        double prevWarmSq = havePrev ? pickupWarmSq * 0.64 : 0;
        double minHopSq = (double)MinHopDeltaBlocks * MinHopDeltaBlocks;
        var ba = capi.World.BlockAccessor;

        long bestKey = 0;
        int bestLoaded = LodLoginScoutFill.MapChunksPerL0 + 1;
        long bestScore = long.MaxValue;
        double bestX = 0;
        double bestY = 0;
        double bestZ = 0;
        int bestDist = 0;
        int bestPastWarm = 0;

        for (int i = 0; i < pendingKeys.Count; i++)
        {
            long key = pendingKeys[i];
            if (failedKeyBan.ContainsKey(key))
            {
                lastSkippedCooldown++;
                continue;
            }

            int loaded = LodLoginSweep.CountLoadedMapChunks(ba, key);
            if (loaded >= LodLoginScoutFill.MapChunksPerL0) continue;

            var (vx, vy, vz) = LodLoginSweep.VisitPosition(capi.World, key);
            double dxPickup = vx - pickupX;
            double dzPickup = vz - pickupZ;
            double distPickupSq = dxPickup * dxPickup + dzPickup * dzPickup;

            if (distPickupSq <= pickupWarmSq) continue;
            if (distPickupSq > spawnSq) continue;

            int distBlocks = (int)Math.Round(Math.Sqrt(distPickupSq));
            int pastWarm = Math.Max(0, distBlocks - rHold);

            if (havePrev)
            {
                double dxPrev = vx - prevX;
                double dzPrev = vz - prevZ;
                double distPrevSq = dxPrev * dxPrev + dzPrev * dzPrev;
                if (distPrevSq < minHopSq) continue;
                if (distPrevSq <= prevWarmSq) continue;
            }

            long score = (long)pastWarm * 10_000_000L
                + (long)loaded * 100_000L
                + Math.Abs(distBlocks - Math.Max(finishedR, rHold));

            if (score < bestScore)
            {
                bestScore = score;
                bestKey = key;
                bestLoaded = loaded;
                bestX = vx;
                bestY = vy;
                bestZ = vz;
                bestDist = distBlocks;
                bestPastWarm = pastWarm;
            }
        }

        if (bestKey == 0) return false;

        targetKey = bestKey;
        loadedChunks = bestLoaded;
        x = bestX;
        y = bestY;
        z = bestZ;
        distPickupBlocks = bestDist;
        pastWarmBlocks = bestPastWarm;
        bearingRad = Math.Atan2(bestZ - pickupZ, bestX - pickupX);
        return true;
    }

    bool TryFallbackAnnulusUnlock(
        ICoreClientAPI capi,
        double pickupX,
        double pickupY,
        double pickupZ,
        IReadOnlyList<long> pendingKeys,
        int finished,
        double prevX,
        double prevZ,
        bool havePrev,
        out double x,
        out double y,
        out double z,
        out long targetKey,
        out int loadedChunks,
        out int distPickupBlocks,
        out double bearingRad,
        out int pastWarmBlocks)
    {
        int rHold = LodLoginBakeViewBoost.SweepBoostViewDistanceBlocks;
        int finishedR = LodLoginScoutFill.FinishedToRadiusBlocks(finished);
        int maxR = (int)LodLoginBake.SpawnSolidRadiusBlocks - LodSection.SectionBlocks;

        int nextR = havePrev
            ? Math.Max(lastFallbackRadiusBlocks + FallbackRadiusStepBlocks,
                Math.Max(finishedR + LodSection.SectionBlocks, rHold + MinHopDeltaBlocks))
            : Math.Max(rHold + MinHopDeltaBlocks, finishedR + LodSection.SectionBlocks);
        nextR = Math.Min(nextR, maxR);
        if (havePrev && nextR <= lastFallbackRadiusBlocks)
            nextR = Math.Min(lastFallbackRadiusBlocks + FallbackRadiusStepBlocks, maxR);
        lastFallbackRadiusBlocks = nextR;

        bearingRad = BearingTowardNearColdPending(capi, pickupX, pickupZ, pendingKeys, finishedR);
        x = pickupX + Math.Cos(bearingRad) * nextR;
        z = pickupZ + Math.Sin(bearingRad) * nextR;
        y = pickupY;
        targetKey = 0;
        loadedChunks = 0;
        distPickupBlocks = nextR;
        pastWarmBlocks = Math.Max(0, nextR - rHold);

        if (TryPickNearAnnulusTarget(
                capi, pickupX, pickupZ, pendingKeys, finished, prevX, prevZ, havePrev,
                out double sx, out double sy, out double sz, out long sKey, out int sLoaded,
                out int sDist, out _, out int sPastWarm))
        {
            x = sx;
            y = sy;
            z = sz;
            targetKey = sKey;
            loadedChunks = sLoaded;
            distPickupBlocks = sDist;
            pastWarmBlocks = sPastWarm;
            bearingRad = Math.Atan2(z - pickupZ, x - pickupX);
        }

        return true;
    }

    static double BearingTowardNearColdPending(
        ICoreClientAPI capi,
        double pickupX,
        double pickupZ,
        IReadOnlyList<long> pendingKeys,
        int finishedRadiusBlocks)
    {
        var ba = capi.World.BlockAccessor;
        double sumX = 0;
        double sumZ = 0;
        double weight = 0;
        int rHold = LodLoginBakeViewBoost.SweepBoostViewDistanceBlocks;
        double warmSq = (double)rHold * rHold;
        double spawnSq = LodLoginBake.SpawnSolidRadiusBlocks * LodLoginBake.SpawnSolidRadiusBlocks;
        for (int i = 0; i < pendingKeys.Count && weight < 128; i++)
        {
            long key = pendingKeys[i];
            int loaded = LodLoginSweep.CountLoadedMapChunks(ba, key);
            if (loaded >= LodLoginScoutFill.MapChunksPerL0) continue;
            var (vx, _, vz) = LodLoginSweep.VisitPosition(capi.World, key);
            double dx = vx - pickupX;
            double dz = vz - pickupZ;
            double distSq = dx * dx + dz * dz;
            if (distSq <= warmSq || distSq > spawnSq) continue;
            int dist = (int)Math.Round(Math.Sqrt(distSq));
            int pastWarm = Math.Max(1, dist - rHold);
            double w = 1.0 / pastWarm;
            sumX += dx * w;
            sumZ += dz * w;
            weight += w;
        }

        if (weight > 0 && sumX * sumX + sumZ * sumZ > 1)
            return Math.Atan2(sumZ, sumX);

        return finishedRadiusBlocks > 0 ? Math.PI * 0.25 : 0;
    }

    static double DistBlocks(double ax, double az, double bx, double bz)
    {
        double dx = bx - ax;
        double dz = bz - az;
        return Math.Sqrt(dx * dx + dz * dz);
    }
}
