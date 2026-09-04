using System;
using System.Collections.Generic;

namespace DistantVistas;

/// <summary>
/// Potato-tier FOV occlusion at draw-submit time. Casts a coarse XZ sample line
/// from the camera toward a tile and reads intervening LodSection SurfaceYMax
/// already in RAM. Skips Submit when a nearer ridge clearly hides the tile top.
/// Fail open (draw) when height data is missing. Never deletes disk/RAM cache.
/// Temporal cache + per-frame test budget keep turn hitch cheap when occCull=0.
/// </summary>
public sealed class LodHeightfieldOcclusion
{
    /// <summary>Default on. Toggle from DistantVistasConfig.FovOcclusion.</summary>
    public bool Enabled = true;

    /// <summary>Samples along the camera-to-tile XZ ray (clamped 4..16). Default 6 for turn cost.</summary>
    public int SampleCount = 6;

    /// <summary>
    /// Extra height (blocks) so peaks/towers that clear a ridge still draw, and
    /// uncertain cases bias toward drawing.
    /// </summary>
    public int PeekMarginBlocks = 32;

    /// <summary>Only L0/L1 (expensive meshes). L2 temporary cover stays for holes.</summary>
    public int MaxLevel = 1;

    /// <summary>Skip the test inside this horizontal distance (blocks).</summary>
    public double MinDistanceBlocks = 96;

    /// <summary>Hard cap on fresh ray tests per frame. Cached results do not count.</summary>
    public int MaxTestsPerFrame = 48;

    /// <summary>Yaw change (radians) that invalidates the temporal cache.</summary>
    public float YawInvalidateRadians = 0.12f; // ~7 deg

    /// <summary>Camera XZ move (blocks) that invalidates the temporal cache.</summary>
    public double MoveInvalidateBlocks = 24.0;

    /// <summary>Fresh ray tests run this frame (after BeginFrame).</summary>
    public int TestsThisFrame { get; private set; }

    /// <summary>Cache hits this frame.</summary>
    public int CacheHitsThisFrame { get; private set; }

    struct CacheEntry
    {
        public bool Occluded;
        public float Yaw;
        public double CamX;
        public double CamZ;
        public long Frame;
    }

    readonly Dictionary<long, CacheEntry> cache = new();
    float cacheYaw;
    double cacheCamX, cacheCamZ;
    bool cachePoseValid;
    int testsLeft;

    /// <summary>Call once per render frame before Submit walks.</summary>
    public void BeginFrame(double camX, double camZ, float yawRadians)
    {
        TestsThisFrame = 0;
        CacheHitsThisFrame = 0;
        testsLeft = MaxTestsPerFrame < 8 ? 8 : MaxTestsPerFrame;

        if (!cachePoseValid)
        {
            cacheYaw = yawRadians;
            cacheCamX = camX;
            cacheCamZ = camZ;
            cachePoseValid = true;
            return;
        }

        float dyaw = yawRadians - cacheYaw;
        if (dyaw > MathF.PI) dyaw -= MathF.Tau;
        if (dyaw < -MathF.PI) dyaw += MathF.Tau;
        double dx = camX - cacheCamX;
        double dz = camZ - cacheCamZ;
        bool moved = dx * dx + dz * dz >= MoveInvalidateBlocks * MoveInvalidateBlocks;
        bool turned = MathF.Abs(dyaw) >= YawInvalidateRadians;
        if (moved || turned)
        {
            cache.Clear();
            cacheYaw = yawRadians;
            cacheCamX = camX;
            cacheCamZ = camZ;
        }
    }

    /// <summary>
    /// True when intervening surface tops clearly block the tile's visible top.
    /// False means draw (including every uncertain / missing-height / budget case).
    /// </summary>
    public bool IsOccluded(
        LodWorld world,
        long key,
        double camX,
        double camY,
        double camZ,
        float lookY,
        out int occluderMaxY)
    {
        occluderMaxY = 0;
        if (!Enabled) return false;

        int level = LodWorld.KeyLevel(key);
        if (level > MaxLevel || level < 0) return false;

        if (cache.TryGetValue(key, out CacheEntry hit))
        {
            CacheHitsThisFrame++;
            occluderMaxY = 0;
            return hit.Occluded;
        }

        // Out of budget: fail open (draw). Do not thrash rays while turning.
        if (testsLeft <= 0) return false;
        testsLeft--;
        TestsThisFrame++;

        int footprint = LodWorld.KeyFootprintBlocks(key);
        double minX = LodWorld.KeySx(key) * (double)footprint;
        double minZ = LodWorld.KeySz(key) * (double)footprint;
        double tileCx = minX + footprint * 0.5;
        double tileCz = minZ + footprint * 0.5;

        if (!world.Sections.TryGetValue(key, out LodSection? tile) || !tile.HasSurfaceBounds)
            return false;

        int tileMaxY = tile.SurfaceYMax;
        int tileMinY = tile.SurfaceYMin;

        double dx = tileCx - camX;
        double dz = tileCz - camZ;
        double distSq = dx * dx + dz * dz;
        double minDist = MinDistanceBlocks;
        if (distSq < minDist * minDist) return false;
        if (distSq < 1.0) return false;

        double dist = Math.Sqrt(distSq);

        int margin = PeekMarginBlocks;
        if (lookY > 0f) margin += (int)(lookY * 48f);
        if (margin < 8) margin = 8;

        int samples = SampleCount;
        if (samples < 4) samples = 4;
        if (samples > 16) samples = 16;

        double endT = 1.0 - (footprint * 0.45) / dist;
        if (endT > 0.92) endT = 0.92;
        if (endT < 0.35) endT = 0.35;

        int hits = 0;
        int topBlockers = 0;
        int maxOcc = int.MinValue;
        int tileSx = LodWorld.KeySx(key);
        int tileSz = LodWorld.KeySz(key);

        for (int i = 1; i <= samples; i++)
        {
            double t = (i / (double)(samples + 1)) * endT;
            double sx = camX + dx * t;
            double sz = camZ + dz * t;

            if (!TryPeekSurfaceMaxY(world, sx, sz, tileSx, tileSz, level, out int y))
                continue;

            hits++;
            if (y > maxOcc) maxOcc = y;

            if (y <= camY + 2.0) continue;

            double losTop = camY + t * (tileMaxY - camY);
            if (y > losTop + margin) topBlockers++;
        }

        if (hits < Math.Max(2, samples / 3))
        {
            Remember(key, false);
            return false;
        }

        occluderMaxY = maxOcc;

        if (tileMaxY >= maxOcc + margin)
        {
            Remember(key, false);
            return false;
        }

        int tileMidY = (tileMinY + tileMaxY) / 2;
        if (tileMidY >= maxOcc + (margin / 2))
        {
            Remember(key, false);
            return false;
        }

        bool occluded = topBlockers >= Math.Max(2, (hits + 1) / 2);
        Remember(key, occluded);
        return occluded;
    }

    void Remember(long key, bool occluded)
    {
        cache[key] = new CacheEntry
        {
            Occluded = occluded,
            Yaw = cacheYaw,
            CamX = cacheCamX,
            CamZ = cacheCamZ,
            Frame = 0
        };
        // Bound cache size — drop all on next pose invalidate if it grows huge.
        if (cache.Count > 4096) cache.Clear();
    }

    /// <summary>
    /// Resident section SurfaceYMax at world XZ. Prefers L0, then L1. Never loads
    /// vanilla chunks or cold store rows — missing data means fail open.
    /// </summary>
    public static bool TryPeekSurfaceMaxY(
        LodWorld world,
        double worldX,
        double worldZ,
        int excludeSx,
        int excludeSz,
        int excludeLevel,
        out int maxY)
    {
        maxY = 0;
        int step = LodSection.SectionBlocks; // 64

        int sx0 = FloorDiv((int)Math.Floor(worldX), step);
        int sz0 = FloorDiv((int)Math.Floor(worldZ), step);

        if (excludeLevel == 0 && sx0 == excludeSx && sz0 == excludeSz)
            return false;

        long k0 = LodWorld.SectionKey(0, sx0, sz0);
        if (world.Sections.TryGetValue(k0, out LodSection? s0) && s0.HasSurfaceBounds)
        {
            maxY = s0.SurfaceYMax;
            return true;
        }

        int sx1 = sx0 >> 1;
        int sz1 = sz0 >> 1;
        if (excludeLevel == 1 && sx1 == excludeSx && sz1 == excludeSz)
            return false;

        long k1 = LodWorld.SectionKey(1, sx1, sz1);
        if (world.Sections.TryGetValue(k1, out LodSection? s1) && s1.HasSurfaceBounds)
        {
            maxY = s1.SurfaceYMax;
            return true;
        }

        return false;
    }

    static int FloorDiv(int a, int b)
    {
        if (a >= 0) return a / b;
        return (a - (b - 1)) / b;
    }
}
