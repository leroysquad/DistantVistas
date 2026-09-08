using DistantVistas;

namespace DistantVistas.Checks;

/// <summary>
/// FOV occlusion: cache yaw slack, column peeks, and broken-up ridge fail-open.
/// </summary>
public static class OcclusionChecks
{
    public static void Run(Check c)
    {
        CacheYawSlack(c);
        ColumnPeekReadsActualTop(c);
        BrokenUpNeighborhoodIsPorous(c);
        SolidRidgeNeighborhoodIsNotPorous(c);
        BrokenUpRidgeDoesNotOccludeBehind(c);
        SolidRidgeStillOccludes(c);
    }

    static void CacheYawSlack(Check c)
    {
        var occ = new LodHeightfieldOcclusion
        {
            YawInvalidateRadians = 0.18f,
            MoveInvalidateBlocks = 24.0,
            MaxTestsPerFrame = 48
        };

        occ.BeginFrame(100, 200, 0f);
        occ.BeginFrame(100, 200, 0.05f);
        c.Eq(0, occ.TestsThisFrame, "a 0.05 rad yaw nudge does not force fresh rays at BeginFrame");

        occ.BeginFrame(100, 200, 0f);
        c.Eq(0, occ.CacheHitsThisFrame, "no cache hits before any entry exists");

        occ.BeginFrame(500, 500, 1.0f);
        c.Eq(0, occ.TestsThisFrame, "a large camera jump alone does not test at BeginFrame");
    }

    static void ColumnPeekReadsActualTop(Check c)
    {
        // Section (0,0): one tall spike column, everything else low.
        // Old SurfaceYMax peek would report 180 everywhere in the section.
        var section = Fixtures.SolidSection(yTop: 80, yBottom: 0);
        int spikeCol = 10 * LodSection.GridSize + 10;
        section.SetColumn(spikeCol, new[] { LodSection.PackRun(0, 180, 0) });
        section.RefreshSurfaceBounds();

        var world = new LodWorld();
        world.Sections[LodWorld.SectionKey(0, 0, 0)] = section;

        c.True(
            LodHeightfieldOcclusion.TryPeekColumnTopY(
                world, worldX: 10.5, worldZ: 10.5,
                excludeSx: 99, excludeSz: 99, excludeLevel: 0, brokenRelief: 40,
                out int spikeY, out _),
            "spike column is readable");
        c.Eq(180, spikeY, "column peek returns the spike top, not a guess");

        c.True(
            LodHeightfieldOcclusion.TryPeekColumnTopY(
                world, worldX: 40.5, worldZ: 40.5,
                excludeSx: 99, excludeSz: 99, excludeLevel: 0, brokenRelief: 40,
                out int plainY, out bool plainPorous),
            "plain column is readable");
        c.Eq(80, plainY, "plain column is the low fill, not section SurfaceYMax");
        c.False(plainPorous, "flat neighborhood around plain column is not porous");
    }

    static void BrokenUpNeighborhoodIsPorous(Check c)
    {
        var section = Fixtures.SolidSection(yTop: 90, yBottom: 0);
        // Tall floating chunk beside low land — the sky-hole mountain break.
        int spike = 20 * LodSection.GridSize + 20;
        section.SetColumn(spike, new[] { LodSection.PackRun(0, 200, 150) });
        section.RefreshSurfaceBounds();

        c.True(
            LodHeightfieldOcclusion.TryColumnNeighborhood(
                section, level: 0, worldX: 20.5, worldZ: 20.5, brokenRelief: 40,
                out int y, out bool porous),
            "broken spike neighborhood resolves");
        c.Eq(200, y, "center is the floating chunk top");
        c.True(porous, "high chunk next to low neighbors is porous / broken-up");
    }

    static void SolidRidgeNeighborhoodIsNotPorous(Check c)
    {
        var section = Fixtures.SolidSection(yTop: 160, yBottom: 0);
        section.RefreshSurfaceBounds();

        c.True(
            LodHeightfieldOcclusion.TryColumnNeighborhood(
                section, level: 0, worldX: 32.5, worldZ: 32.5, brokenRelief: 40,
                out _, out bool porous),
            "solid ridge neighborhood resolves");
        c.False(porous, "uniform high crest is a solid occluder, not porous");
    }

    static void BrokenUpRidgeDoesNotOccludeBehind(Check c)
    {
        var world = new LodWorld();

        // Intervening L0 at sx=1 (x=64..128): mostly low with sparse high spikes.
        var ridge = Fixtures.SolidSection(yTop: 70, yBottom: 0);
        for (int z = 0; z < LodSection.GridSize; z += 8)
        {
            for (int x = 0; x < LodSection.GridSize; x += 8)
            {
                int col = z * LodSection.GridSize + x;
                ridge.SetColumn(col, new[] { LodSection.PackRun(0, 200, 140) });
            }
        }
        ridge.RefreshSurfaceBounds();
        world.Sections[LodWorld.SectionKey(0, 1, 0)] = ridge;

        // Far tile at sx=3 — would be falsely culled if we used section SurfaceYMax.
        var far = Fixtures.SolidSection(yTop: 100, yBottom: 0);
        far.RefreshSurfaceBounds();
        long farKey = LodWorld.SectionKey(0, 3, 0);
        world.Sections[farKey] = far;

        var occ = new LodHeightfieldOcclusion
        {
            Enabled = true,
            SampleCount = 8,
            PeekMarginBlocks = 32,
            BrokenReliefBlocks = 40,
            MinDistanceBlocks = 32,
            MaxTestsPerFrame = 64
        };
        occ.BeginFrame(camX: 10, camZ: 32, yawRadians: 0f);

        // Camera west of the broken ridge, looking at far land beyond it.
        bool occluded = occ.IsOccluded(
            world, farKey,
            camX: 10, camY: 90, camZ: 32,
            lookY: 0f, out _);
        c.False(occluded, "broken-up high chunks must not hide land behind them (sky-hole fix)");
    }

    static void SolidRidgeStillOccludes(Check c)
    {
        var world = new LodWorld();

        var ridge = Fixtures.SolidSection(yTop: 200, yBottom: 0);
        ridge.RefreshSurfaceBounds();
        world.Sections[LodWorld.SectionKey(0, 1, 0)] = ridge;

        var far = Fixtures.SolidSection(yTop: 90, yBottom: 0);
        far.RefreshSurfaceBounds();
        long farKey = LodWorld.SectionKey(0, 3, 0);
        world.Sections[farKey] = far;

        var occ = new LodHeightfieldOcclusion
        {
            Enabled = true,
            SampleCount = 8,
            PeekMarginBlocks = 32,
            BrokenReliefBlocks = 40,
            MinDistanceBlocks = 32,
            MaxTestsPerFrame = 64
        };
        occ.BeginFrame(camX: 10, camZ: 32, yawRadians: 0f);

        bool occluded = occ.IsOccluded(
            world, farKey,
            camX: 10, camY: 90, camZ: 32,
            lookY: 0f, out _);
        c.True(occluded, "a solid high ridge still hides low land behind it");
    }
}
