using System;
using System.Collections.Generic;

namespace DistantVistas;

/// <summary>
/// Potato-tier FOV occlusion at draw-submit time. Casts a coarse XZ sample line
/// from the camera toward a tile and reads intervening column tops already in RAM.
/// Skips Submit when a nearer solid ridge clearly hides the tile top.
/// Fail open (draw) when height data is missing, or when intervening land is a
/// broken-up high ridge (sky gaps between peaks) — hiding behind that punches
/// holes through the map. Never deletes disk/RAM cache.
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

    /// <summary>
    /// Local column-top range (blocks) that marks a sample as porous / broken-up.
    /// Floating high chunks next to sky gaps exceed this; a solid ridge crest does not.
    /// </summary>
    public int BrokenReliefBlocks = 40;

    /// <summary>Only L0/L1 (expensive meshes). L2 temporary cover stays for holes.</summary>
    public int MaxLevel = 1;

    /// <summary>Skip the test inside this horizontal distance (blocks).</summary>
    public double MinDistanceBlocks = 96;

    /// <summary>Hard cap on fresh ray tests per frame. Cached results do not count.</summary>
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
    /// False means draw (including every uncertain / missing-height / porous / budget case).
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

        // Cliff / broken mountain face: never FOV-cull. A nearer shoulder of the same
        // landmass used to hide mid-slope tiles and punch sky through the silhouette.
        int brokenRelief = BrokenReliefBlocks < 16 ? 16 : BrokenReliefBlocks;
        if (tile.SurfaceRelief >= brokenRelief)
        {
            Remember(key, false);
            return false;
        }

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

        // Do not treat land in the last approach as an occluder — that is the same
        // mountain face / near shoulder, not a separate ridge in front.
        double nearSkipBlocks = footprint * 1.5;
        double nearSkipT = 1.0 - (nearSkipBlocks / dist);
        if (nearSkipT < 0.2) nearSkipT = 0.2;
        if (endT > nearSkipT) endT = nearSkipT;

        int hits = 0;
        int topBlockers = 0;
        int porousHits = 0;
        int maxOcc = int.MinValue;
        int tileSx = LodWorld.KeySx(key);
        int tileSz = LodWorld.KeySz(key);

        for (int i = 1; i <= samples; i++)
        {
            double t = (i / (double)(samples + 1)) * endT;
            double sx = camX + dx * t;
            double sz = camZ + dz * t;

            if (!TryPeekColumnTopY(world, sx, sz, tileSx, tileSz, level, brokenRelief, out int y, out bool porous))
                continue;

            hits++;
            if (y > maxOcc) maxOcc = y;

            if (porous)
            {
                // Broken-up high land (peaks with sky gaps). Never treat as a wall.
                porousHits++;
                continue;
            }

            if (y <= camY + 2.0) continue;

            double losTop = camY + t * (tileMaxY - camY);
            if (y > losTop + margin) topBlockers++;
        }

        if (hits < Math.Max(2, samples / 3))
        {
            Remember(key, false);
            return false;
        }

        // Any real broken-up band along the ray → draw everything behind it.
        // Hiding behind sparse high chunks is exactly the sky-hole bug.
        if (porousHits >= Math.Max(2, (hits + 2) / 3))
        {
            Remember(key, false);
            return false;
        }

        occluderMaxY = maxOcc == int.MinValue ? 0 : maxOcc;

        if (maxOcc == int.MinValue || tileMaxY >= maxOcc + margin)
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
    /// Resident column top at world XZ. Prefers L0, then L1. Never loads vanilla
    /// chunks or cold store rows — missing data means fail open.
    /// Porous = local neighborhood height range exceeds brokenRelief (broken-up peaks).
    /// </summary>
    public static bool TryPeekColumnTopY(
        LodWorld world,
        double worldX,
        double worldZ,
        int excludeSx,
        int excludeSz,
        int excludeLevel,
        int brokenRelief,
        out int maxY,
        out bool porous)
    {
        maxY = 0;
        porous = false;
        int step = LodSection.SectionBlocks; // 64

        int sx0 = FloorDiv((int)Math.Floor(worldX), step);
        int sz0 = FloorDiv((int)Math.Floor(worldZ), step);

        if (!(excludeLevel == 0 && sx0 == excludeSx && sz0 == excludeSz))
        {
            long k0 = LodWorld.SectionKey(0, sx0, sz0);
            if (world.Sections.TryGetValue(k0, out LodSection? s0)
                && TryColumnNeighborhood(s0, 0, worldX, worldZ, brokenRelief, out maxY, out porous))
                return true;
        }

        int sx1 = sx0 >> 1;
        int sz1 = sz0 >> 1;
        if (excludeLevel == 1 && sx1 == excludeSx && sz1 == excludeSz)
            return false;

        long k1 = LodWorld.SectionKey(1, sx1, sz1);
        if (world.Sections.TryGetValue(k1, out LodSection? s1)
            && TryColumnNeighborhood(s1, 1, worldX, worldZ, brokenRelief, out maxY, out porous))
            return true;

        return false;
    }

    /// <summary>
    /// Legacy name: section-max peek. Kept for call sites; now column-accurate and
    /// ignores porosity (caller that needs porous uses <see cref="TryPeekColumnTopY"/>).
    /// </summary>
    public static bool TryPeekSurfaceMaxY(
        LodWorld world,
        double worldX,
        double worldZ,
        int excludeSx,
        int excludeSz,
        int excludeLevel,
        out int maxY)
        => TryPeekColumnTopY(world, worldX, worldZ, excludeSx, excludeSz, excludeLevel, 40, out maxY, out _);

    /// <summary>
    /// Center column top plus same-section 4-neighbors. Porous when those tops
    /// span more than <paramref name="brokenRelief"/> blocks — broken-up peaks
    /// with sky gaps. Neighbors stay in-section so we never read the wrong tile.
    /// </summary>
    public static bool TryColumnNeighborhood(
        LodSection section,
        int level,
        double worldX,
        double worldZ,
        int brokenRelief,
        out int centerY,
        out bool porous)
    {
        centerY = 0;
        porous = false;
        if (!TryResolveColumn(section, level, worldX, worldZ, out int colX, out int colZ, out centerY))
            return false;

        int localMin = centerY;
        int localMax = centerY;
        int n = 1;
        SampleCol(section, colX + 1, colZ, ref localMin, ref localMax, ref n);
        SampleCol(section, colX - 1, colZ, ref localMin, ref localMax, ref n);
        SampleCol(section, colX, colZ + 1, ref localMin, ref localMax, ref n);
        SampleCol(section, colX, colZ - 1, ref localMin, ref localMax, ref n);

        // Two tops already disagree by a cliff — not a solid occluder wall.
        if (n >= 2 && (localMax - localMin) > brokenRelief)
            porous = true;

        return true;
    }

    static void SampleCol(
        LodSection section,
        int colX,
        int colZ,
        ref int localMin,
        ref int localMax,
        ref int n)
    {
        if (colX < 0 || colZ < 0 || colX >= LodSection.GridSize || colZ >= LodSection.GridSize)
            return;
        int col = colZ * LodSection.GridSize + colX;
        if (!section.TryGetTopRun(col, out ulong run)) return;
        int y = LodSection.RunYTop(run);
        if (y < localMin) localMin = y;
        if (y > localMax) localMax = y;
        n++;
    }

    public static bool TryColumnTopAt(
        LodSection section,
        int level,
        double worldX,
        double worldZ,
        out int yTop)
        => TryResolveColumn(section, level, worldX, worldZ, out _, out _, out yTop);

    public static bool TryResolveColumn(
        LodSection section,
        int level,
        double worldX,
        double worldZ,
        out int colX,
        out int colZ,
        out int yTop)
    {
        colX = colZ = 0;
        yTop = 0;
        int footprint = LodSection.SectionBlocks << level;
        int colStep = LodSection.ColumnStepBlocks << level;
        if (colStep < 1) colStep = 1;

        int sx = FloorDiv((int)Math.Floor(worldX), footprint);
        int sz = FloorDiv((int)Math.Floor(worldZ), footprint);
        int localX = (int)Math.Floor(worldX) - sx * footprint;
        int localZ = (int)Math.Floor(worldZ) - sz * footprint;
        if (localX < 0 || localZ < 0 || localX >= footprint || localZ >= footprint)
            return false;

        colX = localX / colStep;
        colZ = localZ / colStep;
        if (colX < 0 || colZ < 0 || colX >= LodSection.GridSize || colZ >= LodSection.GridSize)
            return false;

        int col = colZ * LodSection.GridSize + colX;
        if (!section.TryGetTopRun(col, out ulong run)) return false;
        yTop = LodSection.RunYTop(run);
        return true;
    }

    static int FloorDiv(int a, int b)
    {
        if (a >= 0) return a / b;
        return (a - (b - 1)) / b;
    }
}
