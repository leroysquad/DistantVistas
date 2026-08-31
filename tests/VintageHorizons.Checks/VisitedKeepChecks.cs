namespace DistantVistas.Checks;

/// <summary>
/// Visited L0/L1 near the trail bypass frustum cull so fast flight does not punch sky
/// holes behind the camera. Horizon-wide L0 still culls and coarsens with wanted level.
/// </summary>
public static class VisitedKeepChecks
{
    const double TrailAnchor = 512;
    const double NearTrail = 400;
    const double FarTrail = TrailAnchor + LodSection.SectionBlocks * 80 + 1000;

    public static void Run(Check c)
    {
        Policy(c);
        FrustumStillRejectsBehindCamera(c);
    }

    static void Policy(Check c)
    {
        c.True(LodCoveragePolicy.ShouldKeepVisitedDraw(level: 0, hasDataSet: true, NearTrail, TrailAnchor),
            "captured L0 near the trail bypasses frustum cull");
        c.True(LodCoveragePolicy.ShouldKeepVisitedDraw(level: 1, hasDataSet: true, NearTrail, TrailAnchor),
            "captured L1 near the trail bypasses frustum cull");
        c.False(LodCoveragePolicy.ShouldKeepVisitedDraw(level: 0, hasDataSet: true, FarTrail, TrailAnchor),
            "horizon L0 still uses frustum cull");
        c.False(LodCoveragePolicy.ShouldKeepVisitedDraw(level: 1, hasDataSet: true, FarTrail, TrailAnchor),
            "horizon L1 still uses frustum cull");
        c.False(LodCoveragePolicy.ShouldKeepVisitedDraw(level: 2, hasDataSet: true, NearTrail, TrailAnchor),
            "coarser levels still use frustum cull");
        c.False(LodCoveragePolicy.ShouldKeepVisitedDraw(level: 0, hasDataSet: false, NearTrail, TrailAnchor),
            "uncaptured L0 is not visited-keep");
    }

    static void FrustumStillRejectsBehindCamera(Check c)
    {
        var frustum = new LodFrustum();
        frustum.Update(FrustumChecks.ProjectionForTests(), FrustumChecks.ViewForTests());

        // The primitive still rejects behind-camera boxes; the renderer bypasses this
        // only for visited L0/L1 near the trail.
        c.False(Box(frustum, 0, 0, 100, 10), "LodFrustum still rejects behind-camera boxes");
        c.True(LodCoveragePolicy.ShouldKeepVisitedDraw(0, hasDataSet: true, NearTrail, TrailAnchor),
            "renderer policy keeps near visited L0 off the frustum reject path");
        c.False(LodCoveragePolicy.ShouldKeepVisitedDraw(0, hasDataSet: true, FarTrail, TrailAnchor),
            "renderer policy does not exempt horizon L0 from frustum cull");
    }

    static bool Box(LodFrustum f, double x, double y, double z, double half) =>
        f.BoxInView(x - half, y - half, z - half, x + half, y + half, z + half);
}
