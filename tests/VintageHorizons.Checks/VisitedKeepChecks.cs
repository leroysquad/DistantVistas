namespace DistantVistas.Checks;

/// <summary>
/// Visited L0/L1 must stay drawable regardless of view direction. The low-level frustum
/// still rejects boxes behind the camera; the renderer skips that test for captured near
/// tiles so fast flight does not punch sky holes in terrain the player already generated.
/// </summary>
public static class VisitedKeepChecks
{
    public static void Run(Check c)
    {
        Policy(c);
        FrustumStillRejectsBehindCamera(c);
    }

    static void Policy(Check c)
    {
        c.True(LodCoveragePolicy.ShouldKeepVisitedDraw(level: 0, hasDataSet: true),
            "captured L0 is a visited-keep draw");
        c.True(LodCoveragePolicy.ShouldKeepVisitedDraw(level: 1, hasDataSet: true),
            "captured L1 is a visited-keep draw");
        c.False(LodCoveragePolicy.ShouldKeepVisitedDraw(level: 2, hasDataSet: true),
            "coarser levels still use frustum cull");
        c.False(LodCoveragePolicy.ShouldKeepVisitedDraw(level: 0, hasDataSet: false),
            "uncaptured L0 is not visited-keep");
    }

    static void FrustumStillRejectsBehindCamera(Check c)
    {
        var frustum = new LodFrustum();
        frustum.Update(FrustumChecks.ProjectionForTests(), FrustumChecks.ViewForTests());

        // The primitive still rejects behind-camera boxes; the renderer bypasses this
        // only for visited L0/L1.
        c.False(Box(frustum, 0, 0, 100, 10), "LodFrustum still rejects behind-camera boxes");
        c.True(LodCoveragePolicy.ShouldKeepVisitedDraw(0, hasDataSet: true),
            "renderer policy keeps visited L0 off the frustum reject path");
    }

    static bool Box(LodFrustum f, double x, double y, double z, double half) =>
        f.BoxInView(x - half, y - half, z - half, x + half, y + half, z + half);
}
