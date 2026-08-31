namespace DistantVistas;

/// <summary>
/// When a child can replace its parent's mesh.
/// Completeness check is L0-only; applying it to L1+ hid the far landscape.
/// </summary>
public static class LodCoveragePolicy
{
    /// <summary>
    /// Captured L0/L1 must stay in the draw path even when the view frustum would
    /// reject them (behind the camera, grazing side planes). Fast flight is a stress
    /// test, not a reason to punch sky holes in terrain the player already generated.
    /// </summary>
    public const int VisitedKeepMaxLevel = 1;

    public static bool IsVisitedKeepLevel(int level) => level <= VisitedKeepMaxLevel;

    public static bool ShouldKeepVisitedDraw(int level, bool hasDataSet) =>
        hasDataSet && IsVisitedKeepLevel(level);

    public static bool MustDescendForVisualCap(int level, int maxVisualLevel) =>
        level > Math.Clamp(maxVisualLevel, 0, LodWorld.MaxLevel);

    public static bool InsideVanillaCoverage(
        double horizontalDistanceSq, double cameraY, int surfaceYMin, int surfaceYMax, double radius)
    {
        double verticalDistance = cameraY > surfaceYMax
            ? cameraY - surfaceYMax
            : cameraY < surfaceYMin
                ? surfaceYMin - cameraY
                : 0;
        return horizontalDistanceSq + verticalDistance * verticalDistance < radius * radius;
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
}
