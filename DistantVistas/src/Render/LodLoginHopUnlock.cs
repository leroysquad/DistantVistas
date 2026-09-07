using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DistantVistas;

/// <summary>
/// Overlay hop-unlock pump (Plan C): invisibly moves the real player to geometry-derived
/// unlock points so vanilla's 750-block warm disk residents cold annulus map chunks.
/// Scouts stay the primary parallel bakers; exact pickup XYZ + look restored at end.
/// </summary>
public sealed class LodLoginHopUnlock
{
    /// <summary>Paint queue empty this long before first hop / advance.</summary>
    public const int TriggerPaintStarveTicks = 16;
    /// <summary>Ticks at an unlock point with all-WaitChunks stall before advancing outward.</summary>
    public const int AdvanceStallTicks = 96;
    /// <summary>Do not hop until warm-ring cliff band is reached.</summary>
    public const int MinFinishedForHop = 280;
    /// <summary>Adjacent unlock disk overlap (~0.88 × R_hold).</summary>
    public const double OverlapFactor = 0.88;
    public const int MaxUnlockRings = 4;

    public bool Active { get; private set; }
    public double X { get; private set; }
    public double Y { get; private set; }
    public double Z { get; private set; }
    public int Ring { get; private set; }
    public int TicksAtPoint { get; private set; }
    public int FinishedAtHop { get; private set; }

    public void Reset()
    {
        Active = false;
        Ring = 0;
        TicksAtPoint = 0;
        FinishedAtHop = 0;
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

    /// <summary>
    /// 1038 signature: paint starve, all live scouts WaitChunks, zero Capture.
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
        if (!Active || Ring >= MaxUnlockRings) return false;
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
        ComputeUnlockPoint(
            capi, pickupX, pickupY, pickupZ, pendingKeys, finished, Ring,
            out double x, out double y, out double z, out int targetRadius, out double bearingRad);

        X = x;
        Y = y;
        Z = z;
        Active = true;
        TicksAtPoint = 0;
        FinishedAtHop = finished;

        LodScoutSeqDiag.LogHopUnlock(
            Ring, advance, x, y, z, targetRadius, bearingRad, finished, pendingKeys.Count);
        return true;
    }

    public void TickAtPoint()
    {
        if (Active) TicksAtPoint++;
    }

    internal static void ComputeUnlockPoint(
        ICoreClientAPI capi,
        double pickupX,
        double pickupY,
        double pickupZ,
        IReadOnlyList<long> pendingKeys,
        int finished,
        int ring,
        out double x,
        out double y,
        out double z,
        out int targetRadiusBlocks,
        out double bearingRad)
    {
        int rHold = LodLoginBakeViewBoost.SweepBoostViewDistanceBlocks;
        int deltaR = Math.Max(LodSection.SectionBlocks, (int)Math.Round(rHold * OverlapFactor));
        int finishedR = LodLoginScoutFill.FinishedToRadiusBlocks(finished);
        int targetR = ring <= 1
            ? Math.Max(rHold, finishedR + LodSection.SectionBlocks)
            : rHold + (ring - 1) * deltaR;
        int maxR = (int)LodLoginBake.SpawnSolidRadiusBlocks - LodSection.SectionBlocks;
        targetR = Math.Min(Math.Max(rHold, targetR), maxR);
        targetRadiusBlocks = targetR;

        bearingRad = BearingTowardColdPending(
            capi, pickupX, pickupZ, pendingKeys, finishedR, targetR, ring);

        x = pickupX + Math.Cos(bearingRad) * targetR;
        z = pickupZ + Math.Sin(bearingRad) * targetR;
        y = SampleHopY(capi, x, z, pickupY);
    }

    static double BearingTowardColdPending(
        ICoreClientAPI capi,
        double pickupX,
        double pickupZ,
        IReadOnlyList<long> pendingKeys,
        int finishedRadiusBlocks,
        int targetRadiusBlocks)
    {
        var ba = capi.World.BlockAccessor;
        double sumX = 0;
        double sumZ = 0;
        int n = 0;
        double warmSq = (double)LodLoginBakeViewBoost.SweepBoostViewDistanceBlocks
            * LodLoginBakeViewBoost.SweepBoostViewDistanceBlocks;
        for (int i = 0; i < pendingKeys.Count && n < 128; i++)
        {
            long key = pendingKeys[i];
            if (LodLoginSweep.CountLoadedMapChunks(ba, key) >= LodLoginScoutFill.MapChunksPerL0)
                continue;
            var (vx, _, vz) = LodLoginSweep.VisitPosition(capi.World, key);
            double dx = vx - pickupX;
            double dz = vz - pickupZ;
            double distSq = dx * dx + dz * dz;
            if (distSq <= warmSq) continue;
            sumX += dx;
            sumZ += dz;
            n++;
        }

        if (n > 0 && sumX * sumX + sumZ * sumZ > 1)
            return Math.Atan2(sumZ, sumX);

        // Default: push outward along finished-radius bearing (golden angle by ring).
        double baseAngle = finishedRadiusBlocks > 0
            ? Math.Atan2(targetRadiusBlocks * 0.3, finishedRadiusBlocks)
            : 0;
        return baseAngle + ring * 2.399963;
    }

    static double SampleHopY(ICoreClientAPI capi, double x, double z, double fallbackY)
    {
        _ = capi;
        _ = x;
        _ = z;
        return fallbackY;
    }
}
