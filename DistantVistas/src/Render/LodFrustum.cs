using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Six view-frustum planes extracted from a view-projection matrix (Gribb-Hartmann),
/// used to skip draw calls for sections outside the camera's view.
///
/// The planes come from the SAME matrices handed to the LOD shader, so the test can
/// never disagree with what is actually rendered - deriving them from the game's own
/// culler would tie us to the vanilla view distance and to its per-frame update order,
/// neither of which matches our extended ZFar.
///
/// All coordinates are camera-relative (camera at the origin), matching the LOD model
/// matrices.
/// </summary>
public class LodFrustum
{
    // plane[i] = (a, b, c, d), normal (a,b,c) pointing INTO the frustum.
    readonly float[,] planes = new float[6, 4];
    readonly float[] viewProj = new float[16];
    readonly float[,] leadPlanes = new float[4, 4];
    bool leadReady;

    public void Update(float[] projection, float[] view)
    {
        Mat4f.Multiply(viewProj, projection, view);
        float[] m = viewProj;

        // Column-major (OpenGL): m[col * 4 + row].
        // Row i of the matrix as used below: r0 = (m0, m4, m8, m12) etc.
        SetPlane(planes, 0, m[3] + m[0], m[7] + m[4], m[11] + m[8], m[15] + m[12]);   // left
        SetPlane(planes, 1, m[3] - m[0], m[7] - m[4], m[11] - m[8], m[15] - m[12]);   // right
        SetPlane(planes, 2, m[3] + m[1], m[7] + m[5], m[11] + m[9], m[15] + m[13]);   // bottom
        SetPlane(planes, 3, m[3] - m[1], m[7] - m[5], m[11] - m[9], m[15] - m[13]);   // top
        SetPlane(planes, 4, m[3] + m[2], m[7] + m[6], m[11] + m[10], m[15] + m[14]);  // near
        SetPlane(planes, 5, m[3] - m[2], m[7] - m[6], m[11] - m[10], m[15] - m[14]);  // far
        BuildLeadPlanes(projection, view);
    }

    void BuildLeadPlanes(float[] projection, float[] view)
    {
        leadReady = false;
        float m00 = projection[0];
        float m11 = projection[5];
        if (m00 <= 1e-6f || m11 <= 1e-6f) return;

        float lead = LodCoveragePolicy.LeadConeDegrees * (MathF.PI / 180f);
        float halfH = MathF.Atan(1f / m00);
        float halfV = MathF.Atan(1f / m11);
        float newHalfV = Math.Min(halfV + lead, 1.45f);
        float newHalfH = Math.Min(halfH + lead, 1.5f);
        float newFovy = newHalfV * 2f;
        float tanV = MathF.Tan(newHalfV);
        if (newFovy <= 0 || tanV <= 1e-6f) return;
        float newAspect = MathF.Tan(newHalfH) / tanV;
        if (newAspect <= 0) return;

        float[] leadProj = Mat4f.Perspective(Mat4f.Create(), newFovy, newAspect, 0.1f, 100000f);
        float[] leadVp = new float[16];
        Mat4f.Multiply(leadVp, leadProj, view);
        float[] m = leadVp;
        SetPlane(leadPlanes, 0, m[3] + m[0], m[7] + m[4], m[11] + m[8], m[15] + m[12]);
        SetPlane(leadPlanes, 1, m[3] - m[0], m[7] - m[4], m[11] - m[8], m[15] - m[12]);
        SetPlane(leadPlanes, 2, m[3] + m[1], m[7] + m[5], m[11] + m[9], m[15] + m[13]);
        SetPlane(leadPlanes, 3, m[3] - m[1], m[7] - m[5], m[11] - m[9], m[15] - m[13]);
        leadReady = true;
    }

    static void SetPlane(float[,] dest, int i, float a, float b, float c, float d)
    {
        float len = MathF.Sqrt(a * a + b * b + c * c);
        if (len <= 0) len = 1;
        dest[i, 0] = a / len;
        dest[i, 1] = b / len;
        dest[i, 2] = c / len;
        dest[i, 3] = d / len;
    }

    /// <summary>
    /// True if any part of the camera-relative box may be visible. Uses the p-vertex
    /// test: only the box corner furthest along each plane normal has to be checked,
    /// so a box is rejected only when it is fully behind some plane.
    /// </summary>
    public bool BoxInView(double minX, double minY, double minZ, double maxX, double maxY, double maxZ) =>
        BoxInside(planes, 6, minX, minY, minZ, maxX, maxY, maxZ);

    /// <summary>
    /// Tight frustum plus LeadConeDegrees past each side. Used to decide whether a
    /// tile is in front (never draw a plate; prefetch children) vs behind (cheap
    /// stand-in allowed). Returns true if lead planes are not ready so we fail
    /// closed and never treat unknown as behind.
    /// </summary>
    public bool BoxInLeadCone(double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
    {
        if (!leadReady) return true;
        if (minX <= 0 && maxX >= 0 && minY <= 0 && maxY >= 0 && minZ <= 0 && maxZ >= 0)
            return true;
        return BoxInside(leadPlanes, 4, minX, minY, minZ, maxX, maxY, maxZ);
    }

    static bool BoxInside(float[,] src, int count, double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
    {
        for (int i = 0; i < count; i++)
        {
            double a = src[i, 0], b = src[i, 1], c = src[i, 2], d = src[i, 3];
            double px = a >= 0 ? maxX : minX;
            double py = b >= 0 ? maxY : minY;
            double pz = c >= 0 ? maxZ : minZ;
            if (a * px + b * py + c * pz + d < 0) return false;
        }
        return true;
    }
}
