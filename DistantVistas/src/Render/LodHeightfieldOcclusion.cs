using System;
using System.Collections.Generic;

namespace DistantVistas;

/// <summary>
/// Potato-tier FOV occlusion at draw-submit time. Casts a coarse XZ sample line
/// from the camera toward a tile and reads intervening LodSection SurfaceYMax
/// already in RAM. Skips Submit when a nearer ridge clearly hides the tile top.
/// Fail open (draw) when height data is missing. Never deletes disk/RAM cache.
/// Temporal cache + per-frame test budget keep turn hitch cheap when occCull=0.
///
/// Side-of-mountain sky holes: a single center ray through a cliff will claim
/// "occluded" while the player can still see the tile around the edge. 0.7.84
/// requires a clear majority on the center ray AND matching occlusion on both
/// lateral offset rays, with a larger peek margin, before skipping draw.
/// </summary>
public sealed class LodHeightfieldOcclusion
{
    /// <summary>Default on. Toggle from DistantVistasConfig.FovOcclusion.</summary>
    public bool Enabled = true;

    /// <summary>Samples along the camera-to-tile XZ ray (clamped 4..16). Default 6 for turn cost.</summary>
    public int SampleCount = 6;

    /// <summary>
    /// Extra height (blocks) so peaks/towers that clear a ridge still draw, and
    /// uncertain cases bias toward drawing. 0.7.85 default 160 (was 32→96).
    /// </summary>
    public int PeekMarginBlocks = 160;

    /// <summary>Only L0/L1 (expensive meshes). L2 temporary cover stays for holes.</summary>
    public int MaxLevel = 1;

    /// <summary>Skip the test inside this horizontal distance (blocks). 0.7.85 default 384.</summary>
    public double MinDistanceBlocks = 384;

    /// <summary>Fresh ray tests per frame (cached results free). Default 48.</summary>
    public int MaxTestsPerFrame = 48;

    /// <summary>Per-entry yaw slack (radians) before a cached ray result is discarded.</summary>
    public float YawInvalidateRadians = 0.18f; // ~10 deg; lazy per-key, not a full clear

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
    int testsLeft;

    /// <summary>Call once per render frame before Submit walks.</summary>
    public void BeginFrame(double camX, double camZ, float yawRadians)
    {
        TestsThisFrame = 0;
        CacheHitsThisFrame = 0;
        testsLeft = MaxTestsPerFrame < 8 ? 8 : MaxTestsPerFrame;
        cacheYaw = yawRadians;
        cacheCamX = camX;
        cacheCamZ = camZ;
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

        if (cache.TryGetValue(key, out CacheEntry hit) && EntryStillValid(hit, camX, camZ, yawRadians: cacheYaw))
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
        if (lookY > 0f) margin += (int)(lookY * 64f);
        if (margin < 96) margin = 96;

        int samples = SampleCount;
        if (samples < 4) samples = 4;
        if (samples > 16) samples = 16;

        int tileSx = LodWorld.KeySx(key);
        int tileSz = LodWorld.KeySz(key);

        // Center ray first. Soft majority is not enough for side-of-cliff holes.
        if (!RayFullyOccludes(
                world, camX, camY, camZ, dx, dz, dist, tileMaxY, tileMinY,
                tileSx, tileSz, level, samples, margin,
                out int maxOcc, out bool centerHard))
        {
            Remember(key, false);
            return false;
        }

        occluderMaxY = maxOcc;
        if (!centerHard)
        {
            Remember(key, false);
            return false;
        }

        // Lateral rays: half a footprint left/right of the tile center. If either
        // side still sees the tile top over the ridge, the player can too.
        double invLen = 1.0 / dist;
        double px = -dz * invLen; // unit perpendicular in XZ
        double pz = dx * invLen;
        // Wide lateral probes: a tall ridge can hide the tile center while the
        // player still sees land left/right of the silhouette (the sky holes).
        double side = Math.Max(48.0, footprint * 0.85);

        double ldx = (tileCx + px * side) - camX;
        double ldz = (tileCz + pz * side) - camZ;
        double lDist = Math.Sqrt(ldx * ldx + ldz * ldz);
        if (lDist < 1.0
            || !RayFullyOccludes(
                world, camX, camY, camZ, ldx, ldz, lDist, tileMaxY, tileMinY,
                tileSx, tileSz, level, samples, margin,
                out _, out bool leftHard)
            || !leftHard)
        {
            Remember(key, false);
            return false;
        }

        double rdx = (tileCx - px * side) - camX;
        double rdz = (tileCz - pz * side) - camZ;
        double rDist = Math.Sqrt(rdx * rdx + rdz * rdz);
        if (rDist < 1.0
            || !RayFullyOccludes(
                world, camX, camY, camZ, rdx, rdz, rDist, tileMaxY, tileMinY,
                tileSx, tileSz, level, samples, margin,
                out _, out bool rightHard)
            || !rightHard)
        {
            Remember(key, false);
            return false;
        }

        Remember(key, true);
        return true;
    }

    /// <summary>
    /// Hard occlusion along one XZ ray: enough height samples, tile top/mid clearly
    /// under the ridge, and at least ~3/4 of samples block LoS to the tile top.
    /// </summary>
    bool RayFullyOccludes(
        LodWorld world,
        double camX,
        double camY,
        double camZ,
        double dx,
        double dz,
        double dist,
        int tileMaxY,
        int tileMinY,
        int tileSx,
        int tileSz,
        int level,
        int samples,
        int margin,
        out int occluderMaxY,
        out bool hardOcclude)
    {
        occluderMaxY = 0;
        hardOcclude = false;

        double footprint = LodSection.SectionBlocks << level;
        double endT = 1.0 - (footprint * 0.45) / dist;
        if (endT > 0.92) endT = 0.92;
        if (endT < 0.35) endT = 0.35;

        int hits = 0;
        int topBlockers = 0;
        int maxOcc = int.MinValue;

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

        if (hits < Math.Max(3, samples / 2))
            return false;

        occluderMaxY = maxOcc;

        if (tileMaxY >= maxOcc + margin)
            return false;

        int tileMidY = (tileMinY + tileMaxY) / 2;
        // Mid must clear with full margin, not half — side silhouettes leak otherwise.
        if (tileMidY >= maxOcc + margin)
            return false;

        // Nearly every hit must block LoS to the tile top (was ~50%→75%).
        // One soft sample = draw — chopping mid-ground for FPS is worse.
        int need = Math.Max(hits, Math.Max(3, (hits * 9 + 9) / 10));
        hardOcclude = topBlockers >= need;
        return hardOcclude;
    }

    bool EntryStillValid(CacheEntry hit, double camX, double camZ, float yawRadians)
    {
        float dyaw = yawRadians - hit.Yaw;
        if (dyaw > MathF.PI) dyaw -= MathF.Tau;
        if (dyaw < -MathF.PI) dyaw += MathF.Tau;
        if (MathF.Abs(dyaw) >= YawInvalidateRadians) return false;
        double dx = camX - hit.CamX;
        double dz = camZ - hit.CamZ;
        return dx * dx + dz * dz < MoveInvalidateBlocks * MoveInvalidateBlocks;
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
        // Bound cache size — evict stale entries first, then clear if still huge.
        if (cache.Count > 4096)
        {
            var stale = new List<long>();
            foreach (var kv in cache)
            {
                if (!EntryStillValid(kv.Value, cacheCamX, cacheCamZ, cacheYaw))
                    stale.Add(kv.Key);
            }
            foreach (long k in stale) cache.Remove(k);
            if (cache.Count > 4096) cache.Clear();
        }
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
