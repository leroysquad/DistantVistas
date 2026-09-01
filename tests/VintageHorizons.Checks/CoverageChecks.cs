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
        c.True(LodCoveragePolicy.IsDrawFullDetail(NearTrail, TrailAnchor),
            "inside vanilla view distance is full-detail draw");
        c.False(LodCoveragePolicy.IsDrawFullDetail(TrailAnchor, TrailAnchor),
            "the 1.0x ring is exclusive of the far edge");
        c.True(LodCoveragePolicy.RequestVisitedKeepMesh(0, false, true, false, NearTrail, TrailAnchor),
            "unmeshed visited L0 inside the 1.0x draw ring still requests mesh");
        c.False(LodCoveragePolicy.RequestVisitedKeepMesh(0, false, true, false, FarTrail, TrailAnchor),
            "unmeshed visited L0 far away meshes at wanted rung instead");
        c.False(LodCoveragePolicy.RequestVisitedKeepMesh(0, true, true, false, NearTrail, TrailAnchor),
            "already-meshed L0 does not re-request");
        c.False(LodCoveragePolicy.RequestVisitedKeepMesh(0, false, true, true, NearTrail, TrailAnchor),
            "vanilla-owned L0 is not this helper");
        c.False(LodCoveragePolicy.RequestVisitedKeepMesh(3, false, true, false, NearTrail, TrailAnchor),
            "coarse wanted-level tiles are not keep-surface requests");
        c.True(LodCoveragePolicy.DescendForVisitedKeep(2, true, NearTrail, TrailAnchor),
            "parent descends into visited children inside the 1.0x draw ring");
        c.False(LodCoveragePolicy.DescendForVisitedKeep(2, true, FarTrail, TrailAnchor),
            "parent does not descend to every L0 far from the draw ring");
        c.False(LodCoveragePolicy.DescendForVisitedKeep(0, true, NearTrail, TrailAnchor),
            "L0 has no children to keep");
        c.False(LodCoveragePolicy.DescendForVisitedKeep(3, false, NearTrail, TrailAnchor),
            "empty children do not force descent");

        float savedScale = LodCoveragePolicy.KeepCircleScale;
        LodCoveragePolicy.KeepCircleScale = 3f;
        const double at2x = TrailAnchor * 2;
        c.True(LodCoveragePolicy.IsNearVisitedTrail(at2x, TrailAnchor),
            "2x view distance still sits inside a 3x keep-circle");
        c.False(LodCoveragePolicy.IsDrawFullDetail(at2x, TrailAnchor),
            "2x view distance is outside the 1.0x draw-full-detail ring");
        c.True(LodCoveragePolicy.ShouldKeepVisitedDraw(0, true, at2x, TrailAnchor),
            "keep-circle still holds the L0 GPU mesh at 2x view distance");
        c.False(LodCoveragePolicy.RequestVisitedKeepMesh(0, false, true, false, at2x, TrailAnchor),
            "do not request L0 at 2x view distance just because it was visited");
        c.False(LodCoveragePolicy.DescendForVisitedKeep(2, true, at2x, TrailAnchor),
            "do not walk every L0 at 2x view distance");
        c.True(LodCoveragePolicy.SkipDrawTooFine(0, 1, false, true),
            "L0 with a parent mesh is not drawn when wanted is L1 or coarser");
        c.False(LodCoveragePolicy.SkipDrawTooFine(0, 1, false, false),
            "L0 still draws when the parent has no real mesh");
        c.False(LodCoveragePolicy.SkipDrawTooFine(0, 1, true, true),
            "inside the 1.0x ring, L0 still draws");
        c.False(LodCoveragePolicy.SkipDrawTooFine(0, 1, false, true, false, true),
            "L0 still draws across the coarsen ring when the parent is a plate in the lead cone");
        c.False(LodCoveragePolicy.ShouldVisitChildForDraw(0, 2, false, false),
            "missing L2 must not walk L0; hole-fill stops at L1");
        c.True(LodCoveragePolicy.ShouldVisitChildForDraw(1, 2, false, false),
            "missing L2: visit L1 children for hole-fill");
        c.True(LodCoveragePolicy.ShouldVisitChildForDraw(0, 1, false, false),
            "missing L1 when wanted is L1: L0 is the hole-fill rung");
        c.False(LodCoveragePolicy.ShouldVisitChildForDraw(0, 2, false, true),
            "parent mesh exists: do not walk L0 when wanted is L2");
        c.False(LodCoveragePolicy.ShouldVisitChildForDraw(0, 1, false, true),
            "L1 mesh at 2x VD: do not walk L0 children when wanted >= 1");
        c.True(LodCoveragePolicy.ShouldVisitChildForDraw(2, 2, false, true),
            "child at wanted level is visited");
        c.True(LodCoveragePolicy.ShouldVisitChildForDraw(0, 2, true, true),
            "inside the 1.0x ring, walk to L0");
        c.True(LodCoveragePolicy.ShouldVisitChildForDraw(0, 2, false, true, false, true),
            "a plate in the lead cone walks to L0 across the coarsen ring");
        c.True(LodCoveragePolicy.ShouldVisitChildForDraw(1, 2, false, true, true, true),
            "land-like L2 in the lead cone still walks to L1 so there is no sky hole");
        c.True(LodCoveragePolicy.StopDescentAtAvailableRung(1, 1, false, true, true, true),
            "L1 land-like mesh at wanted 1 draws L1, not L0 children");
        c.True(LodCoveragePolicy.StopDescentAtAvailableRung(1, 2, false, true, true, true),
            "L1 land-like mesh when wanted is L2 still draws L1");
        c.False(LodCoveragePolicy.StopDescentAtAvailableRung(2, 2, false, true, true, true),
            "in-lead-cone L2 with a mesh does not stop, even when land-like");
        c.False(LodCoveragePolicy.StopDescentAtAvailableRung(3, 3, false, true, true, true),
            "in-lead-cone L3 with a mesh does not stop, even when land-like");
        c.False(LodCoveragePolicy.StopDescentAtAvailableRung(2, 2, false, true, false, true),
            "low-relief L2 with a mesh does not stop descent in the lead cone");
        c.True(LodCoveragePolicy.StopDescentAtAvailableRung(2, 2, false, true, false, false),
            "a plate behind the lead cone may stop as a cheap stand-in");
        c.False(LodCoveragePolicy.StopDescentAtAvailableRung(2, 1, false, true, true, true),
            "L2 that is coarser than wanted still descends toward L1");
        c.False(LodCoveragePolicy.StopDescentAtAvailableRung(1, 1, true, true, true, true),
            "inside the 1.0x ring, L1 still walks to L0");
        c.False(LodCoveragePolicy.StopDescentAtAvailableRung(0, 1, false, true, true, true),
            "L0 is not a coarse rung to stop on");
        c.False(LodCoveragePolicy.StopDescentAtAvailableRung(1, 2, false, false, true, true),
            "no mesh cannot stop descent");
        int fullCols = LodSection.GridSize * LodSection.GridSize;
        c.Eq(15f, LodCoveragePolicy.LeadConeDegrees, "lead cone is 15 degrees");
        c.Eq(1, LodCoveragePolicy.LeadConeMaxDrawLevel, "in-cone max draw level is L1");
        c.Eq(4, LodCoveragePolicy.MinLandLikeRelief(1), "L1 min relief is 4");
        c.Eq(8, LodCoveragePolicy.MinLandLikeRelief(2), "L2 min relief is 8");
        c.Eq(16, LodCoveragePolicy.MinLandLikeRelief(3), "L3 min relief is 16");
        c.Eq(24, LodCoveragePolicy.MinLandLikeRelief(4), "L4 min relief caps at 24");
        c.Eq(24, LodCoveragePolicy.MinLandLikeRelief(6), "L6 min relief caps at 24");
        c.Eq(0, LodCoveragePolicy.MinLandLikeRelief(0), "L0 is not gated by relief");
        c.Eq(fullCols / 4, LodCoveragePolicy.CoarseSparseColumnLimit, "1-of-4 of 64x64 is 1024 columns");
        c.True(LodCoveragePolicy.IsLandLikeCoarseMesh(0, false, 0, 0),
            "L0 is not gated by the coarse land-like predicate");
        c.False(LodCoveragePolicy.IsLandLikeCoarseMesh(2, false, 40, fullCols),
            "missing surface bounds is a plate");
        c.False(LodCoveragePolicy.IsLandLikeCoarseMesh(2, true, 2, fullCols),
            "L2 with a couple of blocks of relief is a slab");
        c.True(LodCoveragePolicy.IsLandLikeCoarseMesh(2, true, 40, fullCols),
            "L2 mountain with tens of blocks of relief is land");
        c.False(LodCoveragePolicy.IsLandLikeCoarseMesh(2, true, 40, fullCols / 4),
            "exactly 1-of-4 columns is still a thin slice");
        c.False(LodCoveragePolicy.ParentFollowsChildSurface(false, 0, 0, true, 80, 140),
            "missing parent bounds is a plate");
        c.False(LodCoveragePolicy.ParentFollowsChildSurface(true, 200, 210, true, 80, 140),
            "parent sitting above the hills is a sky gap");
        c.False(LodCoveragePolicy.ParentFollowsChildSurface(true, 110, 112, true, 80, 140),
            "flat shelf slicing the hill is a plate");
        c.True(LodCoveragePolicy.ParentFollowsChildSurface(true, 78, 142, true, 80, 140),
            "parent that spans the same hills is land-like");
        c.False(LodCoveragePolicy.MayDrawCoarseParent(2, false, false, true),
            "a plate parent is not drawn in the lead cone");
        c.False(LodCoveragePolicy.MayDrawCoarseParent(2, true, true, true),
            "look-down / insideVanilla does not draw L2 plates");
        c.False(LodCoveragePolicy.MayDrawCoarseParent(2, true, false, false),
            "insideVanilla does not draw an L2 plate behind either");
        c.True(LodCoveragePolicy.MayDrawCoarseParent(2, false, false, false),
            "a plate behind the lead cone may stay as a stand-in");
        c.True(LodCoveragePolicy.MayDrawCoarseParent(2, false, true, false),
            "land-like L2 behind the lead cone may stand in");
        c.False(LodCoveragePolicy.MayDrawCoarseParent(2, false, true, true),
            "in-lead-cone L2 is never drawn, even when land-like");
        c.False(LodCoveragePolicy.MayDrawCoarseParent(3, false, true, true),
            "in-lead-cone L3 is never drawn, even when land-like");
        c.True(LodCoveragePolicy.MayDrawCoarseParent(1, false, true, true),
            "land-like L1 in the lead cone may still draw");
        c.False(LodCoveragePolicy.MayDrawCoarseParent(1, false, false, true),
            "an L1 plate in the lead cone is not drawn");
        c.True(LodCoveragePolicy.MayDrawCoarseParent(0, true, false, true),
            "L0 is not a coarse parent plate");
        c.False(LodCoveragePolicy.SkipDrawTooFine(1, 2, false, true, true, true),
            "L1 still draws in the lead cone; L2 parent cannot stand in");

        c.Eq(LodWorld.MaxLevel, new DistantVistasConfig().MaxVisualLodLevel,
            "default coarsest visible is the full pyramid, not L0-only");
        LodCoveragePolicy.KeepCircleScale = savedScale;

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

        SkipDiscOwnsWholeTileOnly(c);
    }

    /// <summary>
    /// The camera-locked chopped circle: a skip test that used the NEAREST
    /// point of a 64x64 tile hid the whole square as soon as the circle
    /// touched it. Vanilla does not cover the far half, so that was a moving
    /// sky hole. Only a tile whose farthest corner is still inside the skip
    /// sphere may be treated as vanilla-owned.
    /// </summary>
    static void SkipDiscOwnsWholeTileOnly(Check c)
    {
        const double radius = 281;
        const double camX = 0, camZ = 0, camY = 120;
        int yMin = 100, yMax = 130;

        // Tile underfoot: every corner is well inside the skip sphere.
        c.True(LodCoveragePolicy.EntireAabbInsideVanilla(
                -32, 32, -32, 32, camX, camZ, camY, yMin, yMax, radius, 0),
            "the L0 tile underfoot is fully vanilla-owned");

        // Circle clips the near edge of a tile whose far edge is outside.
        // Nearest point at 250 is inside 281; farthest at 250+64=314 is not.
        c.True(LodCoveragePolicy.InsideVanillaCoverage(250 * 250, camY, yMin, yMax, radius, 0),
            "the near edge of a ring tile is inside the skip sphere");
        c.False(LodCoveragePolicy.EntireAabbInsideVanilla(
                250, 250 + 64, -32, 32, camX, camZ, camY, yMin, yMax, radius, 0),
            "a tile the skip circle only clips is not fully vanilla-owned");

        // Looking straight down shrinks the disc to nothing, even underfoot.
        c.False(LodCoveragePolicy.EntireAabbInsideVanilla(
                -32, 32, -32, 32, camX, camZ, camY, yMin, yMax, radius, 1),
            "look-down does not skip the tile under the camera");
    }

    const double TrailAnchor = 512;
    const double NearTrail = 400;
    const double FarTrail = TrailAnchor * LodMemoryBudget.DefaultKeepScale + 1000;
}
