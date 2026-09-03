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
        c.True(LodCoveragePolicy.InsideVanillaCoverage(0, 120, 100, 130, radius, 1),
            "on-foot look-down is still vanilla-owned (loaded ice, not sky)");
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
        c.True(LodCoveragePolicy.RequestVisitedKeepMesh(0, false, true, false, at2x, TrailAnchor, true, true),
            "visited L0 at 2x VD in the lead cone with land drawn past it is requested");
        c.False(LodCoveragePolicy.RequestVisitedKeepMesh(0, false, true, false, at2x, TrailAnchor, false, true),
            "behind the camera, farther land does not force an L0 request");
        c.False(LodCoveragePolicy.RequestVisitedKeepMesh(0, false, true, false, at2x, TrailAnchor, true, false),
            "the frontier tile (nothing drawn past it) is not intervening");
        c.False(LodCoveragePolicy.DescendForVisitedKeep(2, true, at2x, TrailAnchor),
            "do not walk every L0 at 2x view distance");
        c.True(LodCoveragePolicy.DescendForVisitedKeep(2, true, at2x, TrailAnchor, true, true),
            "a parent that could not stop walks into intervening visited children");
        c.False(LodCoveragePolicy.DescendForVisitedKeep(2, true, at2x, TrailAnchor, false, true),
            "behind the camera the parent mesh is still what we draw");
        c.False(LodCoveragePolicy.SkipDrawTooFine(0, 1, false, true),
            "a meshed L0 is never hidden just because wanted is coarser");
        c.False(LodCoveragePolicy.SkipDrawTooFine(0, 1, false, false),
            "L0 still draws when the parent has no real mesh");
        c.False(LodCoveragePolicy.SkipDrawTooFine(0, 1, true, true),
            "inside the 1.0x ring, L0 still draws");
        c.False(LodCoveragePolicy.SkipDrawTooFine(0, 1, false, true, false, true),
            "L0 still draws across the coarsen ring when the parent is a plate in the lead cone");
        c.True(LodCoveragePolicy.ShouldVisitChildForDraw(0, 2, false, false, true, false, 0f, true),
            "captured L0 is walked even when wanted is L2 and the parent has no mesh");
        c.True(LodCoveragePolicy.ShouldVisitChildForDraw(1, 2, false, false),
            "missing L2: visit L1 children for hole-fill");
        c.True(LodCoveragePolicy.ShouldVisitChildForDraw(0, 1, false, false),
            "missing L1 when wanted is L1: L0 is the hole-fill rung");
        c.True(LodCoveragePolicy.ShouldVisitChildForDraw(0, 2, false, true, true, false, 0f, true),
            "captured L0 is walked even when a parent mesh exists and wanted is L2");
        c.True(LodCoveragePolicy.ShouldVisitChildForDraw(0, 1, false, true, true, false, 0f, true),
            "captured L0 is walked under an L1 mesh");
        c.False(LodCoveragePolicy.ShouldVisitChildForDraw(0, 2, false, false),
            "an L0 with no data is still not walked as hole-fill of a missing L2");
        c.True(LodCoveragePolicy.ShouldVisitChildForDraw(0, 2, false, true, true, true, 0f, true, true),
            "intervening visited L0 in the lead cone is walked even when wanted is L2");
        c.True(LodCoveragePolicy.ShouldVisitChildForDraw(0, 2, false, false, true, true, 0f, true, true),
            "missing L2, intervening L0 in the cone: walk it, not just the L1 rung");
        c.False(LodCoveragePolicy.ShouldVisitChildForDraw(0, 2, false, true, true, true, 1f, false, true),
            "a child with no data is never intervening land");
        c.True(LodCoveragePolicy.ShouldVisitChildForDraw(0, 2, false, true, true, false, 1f, true, true),
            "captured L0 is walked behind the camera too");
        c.True(LodCoveragePolicy.ShouldVisitChildForDraw(0, 2, false, true, true, true, 1f, true, false),
            "captured L0 is walked even looking down with no land past it");
        c.True(LodCoveragePolicy.ShouldVisitChildForDraw(1, 3, false, true, true, false, 0f, true),
            "captured L1 is walked when wanted is L3 and a parent mesh exists");
        c.False(LodCoveragePolicy.ShouldVisitChildForDraw(2, 3, false, true, true, true, 1f, true, true),
            "intervening applies to L0/L1 only; look-down L2 under an L3 mesh is not forced");
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
        c.False(LodCoveragePolicy.SkipDrawTooFine(0, 2, false, true, true, true, 1f, true),
            "intervening L0 never steps aside for a parent mesh, even looking down");
        c.False(LodCoveragePolicy.SkipDrawTooFine(0, 2, false, true, true, true, 1f, false),
            "look-down L0 still does not step aside - the mesh is the land");

        InterveningSpan(c);
        GapFill(c);

        c.False(LodCoveragePolicy.HorizonLeadCone(true, 1f),
            "straight down is not the horizon shelf ban");
        c.True(LodCoveragePolicy.HorizonLeadCone(true, 0f),
            "horizon pitch still uses the lead-cone shelf ban");
        c.False(LodCoveragePolicy.HorizonLeadCone(true, LodCoveragePolicy.LookDownCoarseFill),
            "at the look-down fill pitch the shelf ban lets go");
        c.True(LodCoveragePolicy.MayDrawCoarseParent(2, false, true, true, 1f),
            "look-down L2 is coverage, not a sky square");
        c.True(LodCoveragePolicy.MayDrawCoarseParent(2, false, false, true, 1f),
            "look-down plains (a plate) still cover instead of punching sky");
        c.False(LodCoveragePolicy.MayDrawCoarseParent(2, true, true, true, 1f),
            "look-down still never draws LOD on vanilla-owned ground");
        c.False(LodCoveragePolicy.MayDrawCoarseParent(2, false, true, true, 0f),
            "horizon in-lead-cone L2 is still a shelf, not coverage");
        c.True(LodCoveragePolicy.StopDescentAtAvailableRung(2, 2, false, true, true, true, 1f),
            "look-down may stop on an L2 mesh instead of walking into incomplete L0");
        c.False(LodCoveragePolicy.StopDescentAtAvailableRung(2, 2, false, true, true, true, 0f),
            "horizon still does not stop on L2 in the lead cone");
        c.False(LodCoveragePolicy.ShouldVisitChildForDraw(0, 2, false, true, true, true, 1f),
            "look-down with an L2 mesh does not walk L0 just because the cone says so");
        c.True(LodCoveragePolicy.ShouldVisitChildForDraw(1, 2, false, true, true, true, 0f),
            "horizon lead cone still walks to L1 so the skyline is not a shelf");

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
        VanillaOwnsOnlyLoadedChunks(c);
    }

    /// <summary>
    /// The mid-ground sky rectangle next to kept forest. Two visited L0 under
    /// one L1: the hilly child wants L0, the flat child gets its wanted bumped
    /// by relief and used to step aside for the L1 mesh. The L1 never drew
    /// because its sibling did, so the flat tile was sky. With land drawn past
    /// the span, both children are intervening and both must stay.
    /// </summary>
    static void InterveningSpan(Check c)
    {
        int full = LodSection.GridSize * LodSection.GridSize;

        c.True(LodCoveragePolicy.MustCoverIntervening(0, true, true, true),
            "visited L0 in the lead cone with land past it is intervening");
        c.True(LodCoveragePolicy.MustCoverIntervening(1, true, true, true),
            "visited L1 is intervening too");
        c.False(LodCoveragePolicy.MustCoverIntervening(2, true, true, true),
            "L2 is a coarse rung, never the intervening surface");
        c.False(LodCoveragePolicy.MustCoverIntervening(0, false, true, true),
            "no data cannot be intervening");
        c.False(LodCoveragePolicy.MustCoverIntervening(0, true, false, true),
            "behind the camera is not intervening");
        c.False(LodCoveragePolicy.MustCoverIntervening(0, true, true, false),
            "the frontier is not intervening");

        c.True(LodCoveragePolicy.IsFartherLoaded(500, LodSection.SectionBlocks, 3000),
            "a 64x64 at 500 with a mesh at 3000 has land past it");
        c.False(LodCoveragePolicy.IsFartherLoaded(2950, LodSection.SectionBlocks, 3000),
            "the tile that is the far edge is the frontier");
        c.False(LodCoveragePolicy.IsFartherLoaded(500, LodSection.SectionBlocks, 0),
            "no meshes at all: nothing is intervening");

        // Sibling case. Parent L1 with a mesh; hilly child A wanted 0, flat child
        // B relief-bumped to wanted 2. Both have data; land is drawn past them.
        const bool inCone = true, farther = true;
        bool visitA = LodCoveragePolicy.ShouldVisitChildForDraw(0, 0, false, true, true, inCone, 0f, true, farther);
        bool visitB = LodCoveragePolicy.ShouldVisitChildForDraw(0, 2, false, true, true, inCone, 0f, true, farther);
        c.True(visitA, "hilly sibling at wanted 0 is visited");
        c.True(visitB, "flat sibling at wanted 2 is visited as intervening, not left to the parent");
        bool skipB = LodCoveragePolicy.SkipDrawTooFine(0, 2, false, true, true, inCone, 0f,
            LodCoveragePolicy.MustCoverIntervening(0, true, inCone, farther));
        c.False(skipB, "flat sibling draws its own L0; the L1 will not draw once A drew");

        // Same siblings behind the camera: the old rule stands. B steps aside,
        // and the renderer parks it until the parent decides.
        bool skipBehind = LodCoveragePolicy.SkipDrawTooFine(0, 2, false, true, true, false, 0f,
            LodCoveragePolicy.MustCoverIntervening(0, true, false, farther));
        c.False(skipBehind, "a meshed L0 is never hidden behind the camera either");

        // Incomplete L0 is still the renderer's skip, intervening or not.
        c.False(LodCoveragePolicy.ChildCanReplaceParent(0, true, full / 2, true),
            "intervening does not make a half-captured L0 complete");

        // Horizon shelf bans are untouched by the intervening rule.
        c.False(LodCoveragePolicy.MayDrawCoarseParent(2, false, true, true, 0f),
            "horizon in-lead-cone L2 is still a shelf with intervening children below it");
        c.False(LodCoveragePolicy.StopDescentAtAvailableRung(2, 2, false, true, true, true, 0f),
            "horizon L2 still does not stop; it walks to the intervening L0/L1");
        c.True(LodCoveragePolicy.StopDescentAtAvailableRung(1, 2, false, true, true, true, 0f),
            "a land-like L1 mesh still stands in for four visited L0 (whole footprint, no hole)");
    }

    /// <summary>
    /// Gap fill. Any captured footprint nothing drew this frame is painted by
    /// the nearest ancestor with a resident mesh, clipped to that footprint:
    /// horizon cone or not, frontier or interior, any rung. The horizon rules
    /// still decide what a parent may draw WHOLE (fidelity), never whether a
    /// hole stays sky. Only vanilla-owned ground is left alone.
    /// </summary>
    static void GapFill(Check c)
    {
        c.True(LodCoveragePolicy.MayFillGapWithParent(1, true, false, false),
            "L1 with a mesh fills a missing L0");
        c.True(LodCoveragePolicy.MayFillGapWithParent(2, true, false, false),
            "L2 with a mesh fills a missing L1 - the horizon L2 ban is about whole plates, not holes");
        c.True(LodCoveragePolicy.MayFillGapWithParent(3, true, false, false),
            "L3 fills too: one clipped draw cannot collapse its siblings' detail");
        c.True(LodCoveragePolicy.MayFillGapWithParent(LodWorld.MaxLevel, true, false, false),
            "the root fills when it is the only mesh left");
        c.False(LodCoveragePolicy.MayFillGapWithParent(1, false, false, false),
            "no parent mesh, nothing to fill with - the gap goes up to the grandparent");
        c.False(LodCoveragePolicy.MayFillGapWithParent(1, true, true, false),
            "a vanilla-owned parent never paints under loaded chunks");
        c.False(LodCoveragePolicy.MayFillGapWithParent(1, true, false, true),
            "a gap reaching into the vanilla bubble is loaded chunks, not sky");
        c.False(LodCoveragePolicy.MayFillGapWithParent(0, true, false, false),
            "L0 has no children to fill for");

        c.True(LodCoveragePolicy.DrawIncompleteL0(true, false),
            "a half-captured L0 draws the quadrants it has");
        c.False(LodCoveragePolicy.DrawIncompleteL0(true, true),
            "vanilla-owned incomplete L0 stays hidden");
        c.False(LodCoveragePolicy.DrawIncompleteL0(false, false),
            "no mesh, nothing to draw - the parent fills its footprint");

        c.True(LodCoveragePolicy.MaxGapDrawsPerFrame >= 256,
            "a fresh world's fill-in gets at least a few hundred clipped draws a frame");

        // Whole-plate rules are untouched: fidelity in front still prefers L0/L1.
        c.False(LodCoveragePolicy.StopDescentAtAvailableRung(1, 2, false, true, false, true, 0f),
            "an L1 plate in the lead cone still walks to L0 (and fills only the missing ones)");
        c.False(LodCoveragePolicy.MayDrawCoarseParent(1, false, false, true, 0f),
            "an L1 plate in the lead cone is still not drawn whole");
        c.False(LodCoveragePolicy.StopDescentAtAvailableRung(2, 2, false, true, true, true, 0f),
            "L2 still walks to L1 in the lead cone; it does not stop as a shelf");
        c.False(LodCoveragePolicy.MayDrawCoarseParent(2, false, true, true, 0f),
            "L2 is not a standalone draw in the lead cone");
        c.True(LodCoveragePolicy.StopDescentAtAvailableRung(1, 2, false, true, true, true, 0f),
            "land-like L1 still stands in for its four L0 as a whole");

        c.True(LodCoveragePolicy.MayPaintWholeAfterDescent(false, true, false, false, false),
            "a parent whose children all missed and that sits past vanilla paints whole");
        c.False(LodCoveragePolicy.MayPaintWholeAfterDescent(true, true, false, false, false),
            "a child that drew keeps the 0.7.20 no-box rule");
        c.False(LodCoveragePolicy.MayPaintWholeAfterDescent(false, true, false, false, true),
            "a parent that reaches into the vanilla bubble never paints whole over loaded chunks");
        c.False(LodCoveragePolicy.MayPaintWholeAfterDescent(false, false, false, false, false),
            "no drawable mesh, nothing to paint whole");
        c.False(LodCoveragePolicy.MayPaintWholeAfterDescent(false, true, true, false, false),
            "the visual cap still wants the children");
        c.False(LodCoveragePolicy.MayPaintWholeAfterDescent(false, true, false, true, false),
            "the visited-L0 hold still wants the children");
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

        // Standing on the tile and looking down: vanilla still owns it. Shrinking
        // the disc to zero here put LOD on loaded ice and the two meshes flickered.
        c.True(LodCoveragePolicy.EntireAabbInsideVanilla(
                -32, 32, -32, 32, camX, camZ, camY, yMin, yMax, radius, 1),
            "look-down at your feet is still vanilla-owned");
    }

    /// <summary>
    /// Raising view distance grows a geometric circle before vanilla has those
    /// columns. LOD stays until every map-chunk in the footprint is present.
    /// </summary>
    static void VanillaOwnsOnlyLoadedChunks(Check c)
    {
        c.False(LodCoveragePolicy.VanillaOwnsFootprint(true, false),
            "3D-inside without loaded chunks is not vanilla (grow-VD sky circle)");
        c.False(LodCoveragePolicy.VanillaOwnsFootprint(false, true),
            "loaded chunks outside the 3D sphere stay LOD (high camera / shrink VD)");
        c.True(LodCoveragePolicy.VanillaOwnsFootprint(true, true),
            "a loaded chunk whose whole tile sits inside view distance goes to vanilla");

        var have = new HashSet<long>();
        static long K(int cx, int cz) => ((long)cz << 32) | (uint)cx;
        c.False(LodCoveragePolicy.AnyMapChunkLoaded(
                0, 64, 0, 64, 32, (cx, cz) => have.Contains(K(cx, cz))),
            "no map-chunks: LOD keeps the L0 (walk-away / grow-VD)");
        have.Add(K(0, 0));
        c.True(LodCoveragePolicy.AnyMapChunkLoaded(
                0, 64, 0, 64, 32, (cx, cz) => have.Contains(K(cx, cz))),
            "one loaded chunk is visible to Any");
        c.False(LodCoveragePolicy.AllMapChunksLoaded(
                0, 64, 0, 64, 32, (cx, cz) => have.Contains(K(cx, cz))),
            "one of four chunks is not vanilla-owned (0.7.49)");
        have.Add(K(1, 0));
        have.Add(K(0, 1));
        have.Add(K(1, 1));
        c.True(LodCoveragePolicy.AllMapChunksLoaded(
                0, 64, 0, 64, 32, (cx, cz) => have.Contains(K(cx, cz))),
            "all four chunks of the L0: vanilla may own it");
    }

    const double TrailAnchor = 512;
    const double NearTrail = 400;
    const double FarTrail = TrailAnchor * LodMemoryBudget.DefaultKeepScale + 1000;
}
