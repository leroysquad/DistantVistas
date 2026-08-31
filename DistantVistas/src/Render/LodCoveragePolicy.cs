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
    /// Captured L0/L1 stay in the draw path even when the frustum would reject
    /// them (behind the camera, grazing side planes). Fast flight is a stress
    /// test, not a reason to punch sky holes in land the player already generated.
    /// </summary>
    public const int VisitedKeepMaxLevel = 1;
    public static bool IsVisitedKeepLevel(int level) => level <= VisitedKeepMaxLevel;
    public static bool ShouldKeepVisitedDraw(int level, bool hasDataSet) =>
        hasDataSet && IsVisitedKeepLevel(level);

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
    /// Ask for the same-quality GPU mesh of visited L0/L1 even when the
    /// camera has walked far enough that WantedLevel wants something coarser.
    /// Vanilla-owned columns stay the caller's problem.
    /// </summary>
    public static bool RequestVisitedKeepMesh(
        int level, bool hasMesh, bool hasData, bool insideVanilla) =>
        !hasMesh && !insideVanilla && KeepVisitedSurface(level, hasData);

    /// <summary>
    /// Walk into children that already hold captured land, even if this
    /// node is coarser than WantedLevel. Stopping at the wanted parent was
    /// how land behind the player turned into sky once the window moved.
    /// </summary>
    public static bool DescendForVisitedKeep(int level, bool childHasVisitedSurface) =>
        level > 0 && childHasVisitedSurface;
}
