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
    /// L0 tiles in this ring behind the player stay resident in RAM and on the GPU.
    /// Farther visited land pages out; RequestMesh reloads the same mesh from disk.
    /// </summary>
    public const int VisitedTrailRingTiles = 24;

    /// <summary>
    /// Ring where visited L0/L1 keeps full detail, frustum bypass, and mesh priority.
    /// Matches the renderer just-left pin and RAM/GPU eviction so a fast turn does not
    /// punch sky or remesh from scratch.
    /// </summary>
    public static bool IsNearVisitedTrail(double distance, double viewDistanceAnchor) =>
        distance < viewDistanceAnchor + LodSection.SectionBlocks * VisitedTrailRingTiles;

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
    /// Ask for the same-quality GPU mesh of visited L0/L1 near the trail even when
    /// WantedLevel wants something coarser. Far visited land meshes at wanted rung.
    /// Vanilla-owned columns stay the caller's problem.
    /// </summary>
    public static bool RequestVisitedKeepMesh(
        int level, bool hasMesh, bool hasData, bool insideVanilla,
        double distance, double viewDistanceAnchor) =>
        !hasMesh && !insideVanilla && KeepVisitedSurface(level, hasData)
        && IsNearVisitedTrail(distance, viewDistanceAnchor);

    /// <summary>
    /// Walk into children that already hold captured land near the trail, even if
    /// this node is coarser than WantedLevel. Far visited land coarsens via the
    /// parent mesh instead of drawing every L0 tile.
    /// </summary>
    public static bool DescendForVisitedKeep(
        int level, bool childHasVisitedSurface, double distance, double viewDistanceAnchor) =>
        level > 0 && childHasVisitedSurface && IsNearVisitedTrail(distance, viewDistanceAnchor);
}
