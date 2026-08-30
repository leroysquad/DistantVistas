namespace DistantVistas;

/// <summary>
/// Decides whether a child is ready to replace the broader mesh of its parent.
/// The L0 completeness guard is deliberately not applied to coarse levels: extending
/// it through the whole tree previously hid the far landscape.
/// </summary>
public static class LodCoveragePolicy
{
    public static bool MustDescendForVisualCap(int level, int maxVisualLevel) =>
        level > Math.Clamp(maxVisualLevel, 0, LodWorld.MaxLevel);

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
