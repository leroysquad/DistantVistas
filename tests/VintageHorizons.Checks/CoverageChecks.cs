namespace DistantVistas.Checks;

/// <summary>
/// Parent coverage and 3D vanilla-owns: incomplete L0 cannot punch sky,
/// look-down must not hide existing LOD, high camera is not 2D-owned.
/// </summary>
public static class CoverageChecks
{
    public static void Run(Check c)
    {
        int full = LodSection.GridSize * LodSection.GridSize;

        c.False(LodCoveragePolicy.ChildCanReplaceParent(0, true, full / 2, true),
            "incomplete L0 cannot replace parent");
        c.True(LodCoveragePolicy.ChildCanReplaceParent(0, true, full, true),
            "complete meshed L0 replaces parent");
        c.False(LodCoveragePolicy.ChildCanReplaceParent(0, true, full, false),
            "complete L0 without a mesh cannot replace parent");
        c.True(LodCoveragePolicy.ChildCanReplaceParent(1, true, full / 2, true),
            "L1 completeness is not required to replace a grandparent");

        c.True(LodCoveragePolicy.PreferParentCoverage(true, false),
            "parent mesh covers when children do not replace");
        c.False(LodCoveragePolicy.PreferParentCoverage(true, true),
            "parent yields when children fully replace");
        c.False(LodCoveragePolicy.PreferParentCoverage(false, false),
            "no parent mesh cannot cover a hole");

        c.True(LodCoveragePolicy.MustDescendForVisualCap(1, 0),
            "MaxVisualLevel 0 still wants to descend to L0");
        c.False(LodCoveragePolicy.MustDescendForVisualCap(0, 0),
            "L0 is already the visual cap");

        c.Eq(0f, LodCoveragePolicy.LookDownAmount(0),
            "horizon is not look-down");
        c.Eq(0f, LodCoveragePolicy.LookDownAmount(0.5),
            "looking up is not look-down");
        c.True(LodCoveragePolicy.LookDownAmount(-1) >= 0.99f,
            "straight down is full look-down");

        const double radius = 281;
        // At the surface, horizontal 0 is inside vanilla.
        c.True(LodCoveragePolicy.InsideVanillaCoverage(0, 120, 100, 130, radius, 0),
            "on-foot nadir is vanilla-owned");
        // Camera at Y 438, surface 110: vert 328 > 281, 3D says vanilla cannot own.
        c.False(LodCoveragePolicy.InsideVanillaCoverage(0, 438, 90, 110, radius, 0),
            "high-alt nadir is not vanilla-owned in 3D");
        // Same height but still inside 3D sphere (vert 200 < 281): horizon skip, look-down no skip.
        c.True(LodCoveragePolicy.InsideVanillaCoverage(0, 320, 100, 120, radius, 0),
            "mid-alt nadir still skip at horizon");
        c.False(LodCoveragePolicy.InsideVanillaCoverage(0, 320, 100, 120, radius, 1),
            "straight down does not skip existing LOD");
        // Look-down frustum / far XY: 200 blocks out, high camera, must not 2D-own.
        c.False(LodCoveragePolicy.InsideVanillaCoverage(200 * 200, 438, 90, 110, radius, 0.8),
            "look-down mid-ground is not 2D vanilla-owned");

        c.True(LodCoveragePolicy.KeepVisitedSurface(0, true),
            "visited L0 is keep-surface");
        c.True(LodCoveragePolicy.KeepVisitedSurface(1, true),
            "visited L1 is keep-surface");
        c.False(LodCoveragePolicy.KeepVisitedSurface(2, true),
            "L2 is not the visited surface");
        c.False(LodCoveragePolicy.KeepVisitedSurface(0, false),
            "no data is not visited");
        c.True(LodCoveragePolicy.RequestVisitedKeepMesh(0, false, true, false, NearTrail, TrailAnchor),
            "unmeshed visited L0 near the trail still requests mesh");
        c.False(LodCoveragePolicy.RequestVisitedKeepMesh(0, false, true, false, FarTrail, TrailAnchor),
            "unmeshed visited L0 far away meshes at wanted rung instead");
        c.False(LodCoveragePolicy.RequestVisitedKeepMesh(0, true, true, false, NearTrail, TrailAnchor),
            "already-meshed L0 does not re-request");
        c.False(LodCoveragePolicy.RequestVisitedKeepMesh(0, false, true, true, NearTrail, TrailAnchor),
            "vanilla-owned L0 is not this helper");
        c.False(LodCoveragePolicy.RequestVisitedKeepMesh(3, false, true, false, NearTrail, TrailAnchor),
            "coarse wanted-level tiles are not keep-surface requests");
        c.True(LodCoveragePolicy.DescendForVisitedKeep(2, true, NearTrail, TrailAnchor),
            "parent descends into visited children near the trail");
        c.False(LodCoveragePolicy.DescendForVisitedKeep(2, true, FarTrail, TrailAnchor),
            "parent does not descend to every L0 far from the trail");
        c.False(LodCoveragePolicy.DescendForVisitedKeep(0, true, NearTrail, TrailAnchor),
            "L0 has no children to keep");
        c.False(LodCoveragePolicy.DescendForVisitedKeep(3, false, NearTrail, TrailAnchor),
            "empty children do not force descent");

        c.Eq(LodSection.SectionBlocks, LodCoveragePolicy.IdleOriginTileBlocks,
            "idle threshold is one L0 tile");
        c.False(LodCoveragePolicy.OriginShifted(100, 200, 100, 200),
            "standing still is not an origin shift");
        c.False(LodCoveragePolicy.OriginShifted(100, 200, 100, 200 + 32),
            "a half-tile step is still idle");
        c.True(LodCoveragePolicy.OriginShifted(100, 200, 100 + LodSection.SectionBlocks, 200),
            "one L0 tile of XZ travel moves the window");
        c.False(LodCoveragePolicy.OriginShifted(0, 0, 0, 0),
            "looking around (same XZ) is not movement");
    }

    const double TrailAnchor = 512;
    const double NearTrail = 400;
    const double FarTrail = TrailAnchor * LodMemoryBudget.DefaultKeepScale + 1000;
}
