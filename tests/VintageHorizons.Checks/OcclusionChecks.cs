using DistantVistas;

namespace DistantVistas.Checks;

/// <summary>
/// FOV occlusion cache must survive small yaws without a full-table clear.
/// </summary>
public static class OcclusionChecks
{
    public static void Run(Check c)
    {
        var occ = new LodHeightfieldOcclusion
        {
            YawInvalidateRadians = 0.18f,
            MoveInvalidateBlocks = 24.0,
            MaxTestsPerFrame = 48
        };

        occ.BeginFrame(100, 200, 0f);
        occ.BeginFrame(100, 200, 0.05f);
        c.Eq(0, occ.TestsThisFrame, "a 0.05 rad yaw nudge does not force fresh rays at BeginFrame");

        occ.BeginFrame(100, 200, 0f);
        c.Eq(0, occ.CacheHitsThisFrame, "no cache hits before any entry exists");

        occ.BeginFrame(500, 500, 1.0f);
        c.Eq(0, occ.TestsThisFrame, "a large camera jump alone does not test at BeginFrame");
    }
}
