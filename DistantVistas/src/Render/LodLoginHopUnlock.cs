using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace DistantVistas;

/// <summary>
/// Overlay hop-unlock pump: priority RequestUp at a frontier L0 while the real player
/// stays at pickup. Vanilla stream (frontier-relative) plus scout KeepLoaded resident
/// the next ring. Client hops of Pos/CameraPos fight WorldManager and were the 1.0.43
/// late-run residencyLoaded=0 regression.
/// </summary>
public sealed class LodLoginHopUnlock
{
    public const int TriggerPaintStarveTicks = 16;
    public const int AdvanceStallTicks = 96;
    public const int MinFinishedForHop = 280;
    public const int MinHopDeltaBlocks = 128;
    public const int FallbackRadiusStepBlocks = 192;
    public const int StreamPumpExtraChunks = 4;
    public const int FailedKeyDwellTicks = 48;
    public const int FailedKeyBanRings = 8;
    public const int FailedKeyLongBanRings = 16;
    /// <summary>Hold unlock while forcing server KeepLoaded before retarget.</summary>
    public const int MaxResidencyForceTicks = 128;
    public const int ResidencyPumpIntervalTicks = 4;
    /// <summary>Chebyshev radius for unlock anchor — covers L0 2×2 map columns.</summary>
    public const int UnlockHoldRadiusChunks = 2;

    public bool Active { get; private set; }
    public double X { get; private set; }
    public double Y { get; private set; }
    public double Z { get; private set; }
    public int Ring { get; private set; }
    public long TargetKey { get; private set; }
    public int TicksAtPoint { get; private set; }
    public int FinishedAtHop { get; private set; }
    public int LastResidencyLoaded { get; private set; }

    readonly Dictionary<long, int> failedKeyBan = new();
    readonly List<long> banScratch = new(32);
    int lastFallbackRadiusBlocks;
    int lastSkippedCooldown;
    long pumpAnchorKey;

    public void Reset()
    {
        ReleasePumpAnchor();
        Active = false;
        Ring = 0;
        TargetKey = 0;
        TicksAtPoint = 0;
        FinishedAtHop = 0;
        LastResidencyLoaded = 0;
        lastFallbackRadiusBlocks = 0;
        lastSkippedCooldown = 0;
        pumpAnchorKey = 0;
        failedKeyBan.Clear();
    }

    public void StreamCenter(double pickupX, double pickupZ, out double x, out double z)
    {
        // Server WorldManager streams around the real IPlayer at pickup.
        // 1.0.43 late hops moved this center to ~840 and residencyLoaded went back to 0.
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
        ICoreClientAPI capi,
        int paintStarveTicks,
        int waitChunksLive,
        int captureLive,
        int liveScouts,
        int finished)
    {
        if (!Active) return false;
        if (!MatchesStallSignature(paintStarveTicks, waitChunksLive, captureLive, liveScouts, finished))
            return false;

        LastResidencyLoaded = ReadTargetLoaded(capi);
        if (LastResidencyLoaded >= 1 && TicksAtPoint >= 16) return true;
        if (TicksAtPoint >= MaxResidencyForceTicks) return true;
        return false;
    }

    public bool TryFirstHop(
        ICoreClientAPI capi,
        LodLoginBakeViewBoost viewBoost,
        double pickupX,
        double pickupY,
        double pickupZ,
        IReadOnlyList<long> pendingKeys,
        int finished)
    {
        if (Active) return false;
        Ring = 1;
        lastFallbackRadiusBlocks = 0;
        return ApplyHop(capi, viewBoost, pickupX, pickupY, pickupZ, pendingKeys, finished, advance: false);
    }

    public bool TryAdvanceHop(
        ICoreClientAPI capi,
        LodLoginBakeViewBoost viewBoost,
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
        return ApplyHop(capi, viewBoost, pickupX, pickupY, pickupZ, pendingKeys, finished, advance: true);
    }

    bool ApplyHop(
        ICoreClientAPI capi,
        LodLoginBakeViewBoost viewBoost,
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
            loadedAfterDwell = LodLoginSweep.CountLoadedMapChunks(capi.World.BlockAccessor, TargetKey);

        if (havePrev)
            ReleasePumpAnchor();

        int streamBlocks = viewBoost.LiveStreamViewDistanceBlocks;
        if (!TryPickNearAnnulusTarget(
                capi, pickupX, pickupZ, pendingKeys, finished, streamBlocks,
                prevX, prevZ, havePrev,
                out double x, out double y, out double z, out long targetKey, out int loaded,
                out int distPickupBlocks, out double bearingRad, out int pastWarmBlocks)
            && !TryFallbackAnnulusUnlock(
                capi, pickupX, pickupY, pickupZ, pendingKeys, finished, streamBlocks,
                prevX, prevZ, havePrev,
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
        pumpAnchorKey = targetKey != 0 ? targetKey : -(long)Ring;

        PumpUnlockResidency(capi, viewBoost, forceHost: true);

        LodScoutSeqDiag.LogHopUnlock(
            Ring, advance, x, y, z, distPickupBlocks, bearingRad, finished, pendingKeys.Count,
            targetKey, loaded, usedFallback: targetKey == 0,
            distFromPickup: distPickupBlocks, pastWarmBlocks: pastWarmBlocks,
            loadedAfterDwell: loadedAfterDwell, skippedCooldown: lastSkippedCooldown,
            residencyLoaded: LastResidencyLoaded, pumpAnchorKey: pumpAnchorKey,
            streamViewBlocks: streamBlocks);
        return true;
    }

    /// <summary>
    /// Force map-chunk residency via scout-host KeepLoaded + ForceSend at the frontier L0.
    /// Player stays at pickup so vanilla stream matches the server IPlayer.
    /// </summary>
    public void PumpUnlockResidency(ICoreClientAPI capi, LodLoginBakeViewBoost viewBoost, bool forceHost = false)
    {
        if (!Active) return;
        _ = viewBoost;

        int dim = capi.World.Player.Entity.Pos.Dimension;

        if (TargetKey != 0)
            LodLoginBakePlayerMove.RequestL0MapChunksVisible(capi, TargetKey, dim);
        // Neighbourhood only — 1.0.44 used StreamPumpRadiusChunks (~40) here and
        // dumped thousands of SetChunkColumnVisible into the server FIFO each pump.
        LodLoginBakePlayerMove.RequestChunkColumnsVisible(
            capi, X, Z, dim, UnlockHoldRadiusChunks, "hop-visible");

        if (!forceHost && TicksAtPoint % ResidencyPumpIntervalTicks != 0)
        {
            LastResidencyLoaded = ReadTargetLoaded(capi);
            return;
        }

        int cx = (int)Math.Floor(X / GlobalConstants.ChunkSize);
        int cz = (int)Math.Floor(Z / GlobalConstants.ChunkSize);
        LodScoutHostSystem.ClientInstance?.RequestUp(
            pumpAnchorKey, cx, cz, UnlockHoldRadiusChunks, dim, X, Y, Z, priority: true);

        LastResidencyLoaded = ReadTargetLoaded(capi);
    }

    public void MaybeResidencyProbe(
        ICoreClientAPI capi,
        int finished,
        int paintStarveTicks,
        int waitChunksLive,
        int captureLive)
    {
        if (!Active || TargetKey == 0) return;
        EntityPlayer? entity = capi.World.Player.Entity;
        double playerX = entity?.Pos.X ?? 0;
        double playerZ = entity?.Pos.Z ?? 0;
        double cameraX = entity?.CameraPos.X ?? 0;
        double cameraZ = entity?.CameraPos.Z ?? 0;
        LodScoutSeqDiag.MaybeHopResidencyProbe(
            Ring, TargetKey, pumpAnchorKey, TicksAtPoint, finished, paintStarveTicks,
            waitChunksLive, captureLive, LastResidencyLoaded, X, Y, Z,
            LodScoutHostSystem.ClientInstance?.ChannelConnected ?? false,
            playerX, playerZ, cameraX, cameraZ,
            LodLoginBakeViewBoost.OverlayStreamBlocks(finished));
    }

    public void TickAtPoint()
    {
        if (Active) TicksAtPoint++;
    }

    public void ReleasePumpAnchor()
    {
        if (pumpAnchorKey == 0) return;
        LodScoutHostSystem.ClientInstance?.RequestDown(pumpAnchorKey);
        pumpAnchorKey = 0;
    }

    int ReadTargetLoaded(ICoreClientAPI capi)
    {
        if (TargetKey == 0) return 0;
        return LodLoginSweep.CountLoadedMapChunks(capi.World.BlockAccessor, TargetKey);
    }

    void BanCurrentTargetIfStillCold(ICoreClientAPI capi)
    {
        if (TargetKey == 0 || TicksAtPoint < FailedKeyDwellTicks) return;
        int loaded = LodLoginSweep.CountLoadedMapChunks(capi.World.BlockAccessor, TargetKey);
        if (loaded > 0) return;
        int ban = TicksAtPoint >= MaxResidencyForceTicks ? FailedKeyLongBanRings : FailedKeyBanRings;
        failedKeyBan[TargetKey] = ban;
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
        int streamBlocks,
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

        int rInner = Math.Max(
            LodLoginScoutFill.FinishedToRadiusBlocks(finished),
            LodLoginBakeViewBoost.SweepBoostViewDistanceBlocks);
        int rOuter = Math.Max(streamBlocks, rInner + LodLoginHopUnlock.MinHopDeltaBlocks);
        double pickupWarmSq = (double)rInner * rInner;
        double outerSq = (double)rOuter * rOuter;
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
            if (distPickupSq > outerSq) continue;

            int distBlocks = (int)Math.Round(Math.Sqrt(distPickupSq));
            int pastWarm = Math.Max(0, distBlocks - rInner);

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
                + Math.Abs(distBlocks - rInner);

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
        int streamBlocks,
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
        int rInner = Math.Max(
            LodLoginScoutFill.FinishedToRadiusBlocks(finished),
            LodLoginBakeViewBoost.SweepBoostViewDistanceBlocks);
        int maxR = Math.Max(rInner + MinHopDeltaBlocks, streamBlocks - LodSection.SectionBlocks);

        int nextR = havePrev
            ? Math.Max(lastFallbackRadiusBlocks + FallbackRadiusStepBlocks,
                rInner + LodSection.SectionBlocks)
            : rInner + MinHopDeltaBlocks;
        nextR = Math.Min(nextR, maxR);
        if (havePrev && nextR <= lastFallbackRadiusBlocks)
            nextR = Math.Min(lastFallbackRadiusBlocks + FallbackRadiusStepBlocks, maxR);
        lastFallbackRadiusBlocks = nextR;

        bearingRad = BearingTowardNearColdPending(
            capi, pickupX, pickupZ, pendingKeys, rInner, streamBlocks);
        x = pickupX + Math.Cos(bearingRad) * nextR;
        z = pickupZ + Math.Sin(bearingRad) * nextR;
        y = pickupY;
        targetKey = 0;
        loadedChunks = 0;
        distPickupBlocks = nextR;
        pastWarmBlocks = Math.Max(0, nextR - rInner);

        if (TryPickNearAnnulusTarget(
                capi, pickupX, pickupZ, pendingKeys, finished, streamBlocks, prevX, prevZ, havePrev,
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
        int innerRadiusBlocks,
        int streamBlocks)
    {
        var ba = capi.World.BlockAccessor;
        double sumX = 0;
        double sumZ = 0;
        double weight = 0;
        double warmSq = (double)innerRadiusBlocks * innerRadiusBlocks;
        double outerSq = (double)streamBlocks * streamBlocks;
        for (int i = 0; i < pendingKeys.Count && weight < 128; i++)
        {
            long key = pendingKeys[i];
            int loaded = LodLoginSweep.CountLoadedMapChunks(ba, key);
            if (loaded >= LodLoginScoutFill.MapChunksPerL0) continue;
            var (vx, _, vz) = LodLoginSweep.VisitPosition(capi.World, key);
            double dx = vx - pickupX;
            double dz = vz - pickupZ;
            double distSq = dx * dx + dz * dz;
            if (distSq <= warmSq || distSq > outerSq) continue;
            int dist = (int)Math.Round(Math.Sqrt(distSq));
            int pastWarm = Math.Max(1, dist - innerRadiusBlocks);
            double w = 1.0 / pastWarm;
            sumX += dx * w;
            sumZ += dz * w;
            weight += w;
        }

        if (weight > 0 && sumX * sumX + sumZ * sumZ > 1)
            return Math.Atan2(sumZ, sumX);

        return innerRadiusBlocks > 0 ? Math.PI * 0.25 : 0;
    }

    static double DistBlocks(double ax, double az, double bx, double bz)
    {
        double dx = bx - ax;
        double dz = bz - az;
        return Math.Sqrt(dx * dx + dz * dz);
    }
}
