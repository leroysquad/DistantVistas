namespace DistantVistas;

/// <summary>
/// When a child can replace its parent's mesh, and when vanilla actually owns
/// a column this frame (3D + look-down). Completeness is L0-only; applying it
/// to L1+ hid the far landscape. PreferParentCoverage is the completeness gate
/// (do not punch sky by pretending an incomplete L0 replaced its parent). The
/// renderer must not use that as a license to draw the parent as a giant plate.
/// </summary>
public static class LodCoveragePolicy
{
    /// <summary>
    /// Captured L0/L1 near the player trail may bypass frustum cull so fast flight
    /// does not punch sky holes behind the camera. Horizon-wide L0 still culls.
    /// </summary>
    public const int VisitedKeepMaxLevel = 1;
    public static bool IsVisitedKeepLevel(int level) => level <= VisitedKeepMaxLevel;

    /// <summary>
    /// Keep-circle is vanilla view distance times this scale (2x on a typical 16 GB
    /// box, smaller on 8 GB, larger on 32 GB). LodMemoryBudget sets it at startup
    /// and may shrink it when live GPU meshes go over budget.
    /// </summary>
    public static float KeepCircleScale = LodMemoryBudget.DefaultKeepScale;

    /// <summary>
    /// Inside this circle, visited L0/L1 stays on the GPU and skips frustum cull.
    /// Outside it, oldest meshes un-render first; disk rows stay so walking back remeshes.
    /// </summary>
    public static bool IsNearVisitedTrail(double distance, double viewDistanceAnchor) =>
        distance < viewDistanceAnchor * KeepCircleScale;

    /// <summary>
    /// One L0 section is 64 blocks on a side. The render window origin only
    /// moves when the player walks at least that far in XZ. Looking around
    /// is not a move, and neither is a step that stays inside the same tile.
    /// </summary>
    public const int IdleOriginTileBlocks = LodSection.SectionBlocks;

    public static bool OriginShifted(double originX, double originZ, double x, double z)
    {
        double dx = x - originX;
        double dz = z - originZ;
        double tile = IdleOriginTileBlocks;
        return dx * dx + dz * dz >= tile * tile;
    }

    public static double KeepCircleRadius(double viewDistanceAnchor) =>
        viewDistanceAnchor * KeepCircleScale;

    /// <summary>
    /// Draw at full L0/L1 only inside live vanilla view distance. The keep-circle
    /// is larger and only about holding GPU meshes, not about which mesh we submit.
    /// </summary>
    public static bool IsDrawFullDetail(double distance, double viewDistanceAnchor) =>
        distance < viewDistanceAnchor;

    public static double DrawFullDetailRadius(double viewDistanceAnchor) =>
        viewDistanceAnchor;

    public static bool ShouldKeepVisitedDraw(int level, bool hasDataSet, double distance, double viewDistanceAnchor) =>
        hasDataSet && IsVisitedKeepLevel(level) && IsNearVisitedTrail(distance, viewDistanceAnchor);

    public static bool MustDescendForVisualCap(int level, int maxVisualLevel) =>
        level > Math.Clamp(maxVisualLevel, 0, LodWorld.MaxLevel);

    /// <summary>
    /// 0 at/above the horizon, 1 looking straight down. VS Y is up; GetViewVector
    /// Y is negative when the camera pitches toward the ground.
    /// </summary>
    public static float LookDownAmount(double viewY)
    {
        if (viewY >= 0) return 0f;
        return (float)Math.Min(1.0, -viewY);
    }

    /// <summary>
    /// Vanilla owns this ground only if the 3D distance from camera to the
    /// surface is inside the skip sphere AND the look-down skip disc still
    /// covers it. Horizontal-only is never enough: at altitude the look-down
    /// frustum sees ground vanilla is not drawing. Straight down shrinks the
    /// skip disc to nothing so existing LOD stays on screen. Missing surface
    /// bounds must not be passed here; the caller treats that as not owned.
    /// </summary>
    public static bool InsideVanillaCoverage(
        double horizontalDistanceSq, double cameraY, int surfaceYMin, int surfaceYMax,
        double radius, double lookDown01 = 0)
    {
        if (radius <= 0) return false;
        lookDown01 = Math.Clamp(lookDown01, 0, 1);

        double verticalDistance = cameraY > surfaceYMax
            ? cameraY - surfaceYMax
            : cameraY < surfaceYMin
                ? surfaceYMin - cameraY
                : 0;

        if (horizontalDistanceSq + verticalDistance * verticalDistance >= radius * radius)
            return false;
        if (verticalDistance >= radius) return false;

        double groundReachSq = radius * radius - verticalDistance * verticalDistance;
        double scale = 1.0 - lookDown01;
        groundReachSq *= scale * scale;
        return horizontalDistanceSq < groundReachSq;
    }

    /// <summary>
    /// Vanilla owns this TILE only if the farthest XZ corner is still inside
    /// the skip sphere. The nearest-point test hid a whole 64x64 as soon as
    /// the circle touched it, which punched a camera-locked chopped ring.
    /// </summary>
    public static bool EntireAabbInsideVanilla(
        double minX, double maxX, double minZ, double maxZ,
        double camX, double camZ, double cameraY,
        int surfaceYMin, int surfaceYMax,
        double radius, double lookDown01 = 0)
    {
        double midX = (minX + maxX) * 0.5;
        double midZ = (minZ + maxZ) * 0.5;
        double farX = camX < midX ? maxX : minX;
        double farZ = camZ < midZ ? maxZ : minZ;
        double dx = farX - camX;
        double dz = farZ - camZ;
        return InsideVanillaCoverage(
            dx * dx + dz * dz, cameraY, surfaceYMin, surfaceYMax, radius, lookDown01);
    }

    public static bool ChildCanReplaceParent(
        int level, bool hasData, int capturedColumns, bool hasMesh)
    {
        if (!hasData) return false;
        if (capturedColumns == 0) return true;

        int full = LodSection.GridSize * LodSection.GridSize;
        if (level == 0 && capturedColumns < full) return false;

        return hasMesh;
    }

    /// <summary>
    /// Completeness gate: a parent is still the only coverage until every child
    /// can replace it. That does not mean draw the parent as a square plate.
    /// </summary>
    public static bool PreferParentCoverage(bool hasParentMesh, bool childrenFullyReplaced) =>
        hasParentMesh && !childrenFullyReplaced;

    /// <summary>
    /// L0/L1 we already captured is the picture. Wanted-level is a camera
    /// window; it must not refuse to keep or remesh those tiles.
    /// </summary>
    public static bool KeepVisitedSurface(int level, bool hasData) =>
        hasData && level >= 0 && level <= 1;

    /// <summary>
    /// Ask for the same-quality GPU mesh of visited L0/L1 inside the 1.0x draw
    /// ring even when WantedLevel wants something coarser. Far visited land
    /// meshes at the wanted rung instead; the keep-circle still holds meshes
    /// we already uploaded.
    /// </summary>
    public static bool RequestVisitedKeepMesh(
        int level, bool hasMesh, bool hasData, bool insideVanilla,
        double distance, double viewDistanceAnchor) =>
        !hasMesh && !insideVanilla && KeepVisitedSurface(level, hasData)
        && IsDrawFullDetail(distance, viewDistanceAnchor);

    /// <summary>
    /// Walk into children that already hold captured land inside the 1.0x draw
    /// ring, even if this node is coarser than WantedLevel. Past that ring the
    /// parent mesh is what we draw, as long as it is a real mesh.
    /// </summary>
    public static bool DescendForVisitedKeep(
        int level, bool childHasVisitedSurface, double distance, double viewDistanceAnchor) =>
        level > 0 && childHasVisitedSurface && IsDrawFullDetail(distance, viewDistanceAnchor);

    /// <summary>
    /// Degrees past each frustum edge that still count as in front: never draw
    /// L2+ (including plates), and start meshing children so land is ready
    /// before the player finishes turning. Only L0 and land-like L1 draw in
    /// this cone. Behind it a cheap parent (even a plate) may stay as a
    /// stand-in so the 360-degree map does not explode into L0 draws the
    /// camera cannot see.
    /// </summary>
    public const float LeadConeDegrees = 15f;

    /// <summary>
    /// Coarsest rung that may draw inside the lead cone. L2 and coarser,
    /// even with enough SurfaceRelief to pass IsLandLikeCoarseMesh, still
    /// reads as a cake shelf on the horizon. Those stand-ins are only
    /// allowed behind the camera.
    /// </summary>
    public const int LeadConeMaxDrawLevel = 1;

    /// <summary>
    /// Captured columns at or below this (one quadrant of a 64x64 grid) is a
    /// thin 1-of-4 slice, not a land-like coarse mesh.
    /// </summary>
    public const int CoarseSparseColumnLimit = (LodSection.GridSize * LodSection.GridSize) / 4;

    /// <summary>
    /// Minimum SurfaceRelief for an L1+ mesh to count as land rather than a
    /// slab. Scales with section footprint: a 256-block L2 that only varies a
    /// couple of blocks is a box; a mountain L2 with tens of blocks is land.
    /// L1: 4, L2: 8, L3: 16, L4+: 24.
    /// </summary>
    public static int MinLandLikeRelief(int level)
    {
        if (level < 1) return 0;
        int footprint = LodSection.SectionBlocks << level;
        return Math.Max(4, Math.Min(24, footprint / 32));
    }

    /// <summary>
    /// L0 is not gated here (IncompleteL0 skip stays in the renderer). L1+
    /// must have surface bounds, enough captured columns, and relief at or
    /// above MinLandLikeRelief. Missing any of those is a parent plate: do
    /// not draw it in front of the camera.
    /// </summary>
    public static bool IsLandLikeCoarseMesh(
        int level, bool hasSurfaceBounds, int surfaceRelief, int capturedColumns)
    {
        if (level < 1) return true;
        if (!hasSurfaceBounds) return false;
        if (capturedColumns <= CoarseSparseColumnLimit) return false;
        return surfaceRelief >= MinLandLikeRelief(level);
    }

    /// <summary>
    /// Draw walk: children only when they are coarse enough to reach wanted,
    /// or we are still inside the 1.0x ring. A missing parent mesh is one-rung
    /// hole-fill (draw L1 children of a missing L2), not a walk all the way to L0.
    /// In the lead cone L2+ is never coverage, land-like or not: walk until
    /// real L0/L1 land so the horizon is not a shelf.
    /// </summary>
    public static bool ShouldVisitChildForDraw(
        int childLevel, int wanted, bool drawFullDetail, bool parentHasMesh,
        bool parentLandLike = true, bool inLeadCone = false)
    {
        if (drawFullDetail) return true;
        if (inLeadCone && (!parentLandLike || childLevel >= LeadConeMaxDrawLevel)) return true;
        if (childLevel >= wanted) return true;
        // Hole-fill of one rung. Missing L2 visits L1; it does not visit L0.
        if (!parentHasMesh && childLevel >= Math.Max(0, wanted - 1)) return true;
        return false;
    }

    /// <summary>
    /// This node has a GPU mesh, we are outside the 1.0x ring, and this rung is
    /// not coarser than wanted (L1 hole-fill when wanted is 2+ still counts).
    /// In the lead cone only L0/L1 land-like meshes may stop the walk; L2+
    /// never stops, even with enough relief, because a huge L2 still reads as
    /// a cake shelf on the horizon. Behind the lead cone a plate may stop
    /// (cheap stand-in; the tight frustum still culls the submit).
    /// </summary>
    public static bool StopDescentAtAvailableRung(
        int level, int wanted, bool drawFullDetail, bool hasMesh,
        bool landLike, bool inLeadCone) =>
        hasMesh && !drawFullDetail && level >= 1 && level <= Math.Max(wanted, 1)
        && !(inLeadCone && level > LeadConeMaxDrawLevel)
        && (landLike || !inLeadCone);

    /// <summary>
    /// Whether CollectDrawNodes may add this L1+ mesh to the draw list.
    /// Never over vanilla-owned ground. Never L2+ inside the lead cone (a
    /// land-like L2 is still a shelf from this camera). Never a plate inside
    /// the lead cone, including at the 1.0x coarsen ring. Behind the lead
    /// cone a plate may stay as a cheap stand-in. L0 is not a coarse parent;
    /// IncompleteL0 is a separate skip.
    /// </summary>
    public static bool MayDrawCoarseParent(
        int level, bool insideVanilla, bool landLike, bool inLeadCone)
    {
        if (level < 1) return true;
        if (insideVanilla) return false;
        if (inLeadCone && level > LeadConeMaxDrawLevel) return false;
        if (!landLike && inLeadCone) return false;
        return true;
    }

    /// <summary>
    /// A coarse parent is the same hills only if its surface band overlaps the
    /// children and is not a thin shelf through them. Missing bounds, a parent
    /// sitting above the real hills (sky gap), or a flat Y that would slice the
    /// hill is a plate: hide it and draw the children.
    /// </summary>
    public static bool ParentFollowsChildSurface(
        bool parentHasBounds, int parentYMin, int parentYMax,
        bool childrenHaveBounds, int childYMin, int childYMax)
    {
        if (!parentHasBounds || !childrenHaveBounds) return false;
        if (parentYMax < childYMin || parentYMin > childYMax) return false;
        int parentRelief = parentYMax - parentYMin;
        int childRelief = childYMax - childYMin;
        if (childRelief > parentRelief && parentRelief < Math.Max(4, childRelief / 2))
            return false;
        return true;
    }

    /// <summary>
    /// Too fine for this camera window, and the parent already has a real mesh
    /// to draw instead. Stamp lastSelectedFrame at the caller so keep-circle
    /// eviction does not dump the finer mesh. A plate in the lead cone is not
    /// a real parent, and neither is L2+: keep drawing L0/L1 across the 1.0x
    /// coarsen ring so the horizon is not a shelf.
    /// </summary>
    public static bool SkipDrawTooFine(
        int level, int wanted, bool drawFullDetail, bool parentHasMesh,
        bool parentLandLike = true, bool inLeadCone = false)
    {
        if (drawFullDetail || level >= wanted || !parentHasMesh) return false;
        if (inLeadCone && !parentLandLike) return false;
        // Current L1+ means the parent is L2+. That parent cannot stand in
        // inside the lead cone.
        if (inLeadCone && level >= LeadConeMaxDrawLevel) return false;
        return true;
    }
}
