using System.Globalization;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Session 40cccb: Farseer gray-smoke / silhouette flicker while standing still.
/// H-Flick-A farView/distStart variance; H-Flick-B DV rim draw thrash;
/// H-Flick-C fog mist input; H-Flick-D height-enrich rebuild; H-Flick-E mask upload.
/// </summary>
public static class FarseerFlickerDiag
{
    const string LogPath =
        @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log";

    const int Ring = 48;
    static readonly float[] farViews = new float[Ring];
    static readonly float[] distStarts = new float[Ring];
    static readonly float[] liveVds = new float[Ring];
    static readonly int[] rimDraws = new int[Ring];
    static int ringAt;
    static int ringFilled;
    static long lastDumpMs;
    static int dumpCount;
    static int enrichRebuilds;
    static int maskUploads;
    static int lastEnrichStamp;
    static double lastCamX = double.NaN;
    static double lastCamZ = double.NaN;

    public static void NoteEnrichRebuild(int visitStamp, int regionCount)
    {
        enrichRebuilds++;
        lastEnrichStamp = visitStamp;
        // #region agent log
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            System.IO.File.AppendAllText(LogPath,
                "{\"sessionId\":\"40cccb\",\"runId\":\"flicker-1\",\"hypothesisId\":\"H-Flick-D\","
                + "\"location\":\"FarseerFlickerDiag.NoteEnrichRebuild\",\"message\":\"enrich-rebuild\",\"data\":{"
                + "\"visitStamp\":" + visitStamp
                + ",\"regions\":" + regionCount
                + ",\"rebuilds\":" + enrichRebuilds
                + "},\"timestamp\":" + now + "}\n");
        }
        catch { }
        // #endregion
    }

    public static void NoteMaskUpload(int originSx, int originSz, int visitCount, int envBlocks)
    {
        maskUploads++;
        // #region agent log
        if (maskUploads <= 24 || maskUploads % 8 == 0)
        {
            try
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                System.IO.File.AppendAllText(LogPath,
                    "{\"sessionId\":\"40cccb\",\"runId\":\"flicker-1\",\"hypothesisId\":\"H-Flick-E\","
                    + "\"location\":\"FarseerFlickerDiag.NoteMaskUpload\",\"message\":\"visit-mask-upload\",\"data\":{"
                    + "\"originSx\":" + originSx
                    + ",\"originSz\":" + originSz
                    + ",\"visitCount\":" + visitCount
                    + ",\"envBlocks\":" + envBlocks
                    + ",\"n\":" + maskUploads
                    + "},\"timestamp\":" + now + "}\n");
            }
            catch { }
        }
        // #endregion
    }

    /// <summary>Call once per Farseer farViewDistance uniform bind.</summary>
    public static void NoteFarseerFrame(
        ICoreClientAPI? capi,
        float farView,
        float liveVd,
        int rimDrawCount)
    {
        if (capi == null) return;
        float onset = LodCoveragePolicy.FarseerSilhouetteOnsetScale;
        float distStart = liveVd > 0 ? liveVd * onset : 0f;
        if (farView > 0 && distStart > farView) distStart = farView;
        if (liveVd > 0 && distStart < liveVd * 0.5f) distStart = liveVd * 0.5f;

        farViews[ringAt] = farView;
        distStarts[ringAt] = distStart;
        liveVds[ringAt] = liveVd;
        rimDraws[ringAt] = rimDrawCount;
        ringAt = (ringAt + 1) % Ring;
        if (ringFilled < Ring) ringFilled++;

        Vec3d cam = capi.World.Player.Entity.CameraPos;
        double camMove = 0;
        if (!double.IsNaN(lastCamX))
        {
            double dx = cam.X - lastCamX;
            double dz = cam.Z - lastCamZ;
            camMove = Math.Sqrt(dx * dx + dz * dz);
        }
        lastCamX = cam.X;
        lastCamZ = cam.Z;

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (dumpCount >= 36) return;
        if (now - lastDumpMs < 1000) return;
        if (ringFilled < 12) return;
        lastDumpMs = now;
        dumpCount++;

        float farMin = float.MaxValue, farMax = float.MinValue;
        float dsMin = float.MaxValue, dsMax = float.MinValue;
        float vdMin = float.MaxValue, vdMax = float.MinValue;
        int rimMin = int.MaxValue, rimMax = int.MinValue;
        for (int i = 0; i < ringFilled; i++)
        {
            float f = farViews[i];
            float d = distStarts[i];
            float v = liveVds[i];
            int r = rimDraws[i];
            if (f < farMin) farMin = f;
            if (f > farMax) farMax = f;
            if (d < dsMin) dsMin = d;
            if (d > dsMax) dsMax = d;
            if (v < vdMin) vdMin = v;
            if (v > vdMax) vdMax = v;
            if (r < rimMin) rimMin = r;
            if (r > rimMax) rimMax = r;
        }

        float farDelta = farMax - farMin;
        float dsDelta = dsMax - dsMin;
        float vdDelta = vdMax - vdMin;
        int rimDelta = rimMax - rimMin;
        bool still = camMove < 0.35;

        string hyp = "ok";
        if (still && farDelta > 8f) hyp = "H-Flick-A";
        else if (still && dsDelta > 16f) hyp = "H-Flick-A";
        else if (still && rimDelta > 4) hyp = "H-Flick-B";
        else if (still && enrichRebuilds > 0 && dumpCount <= 8) hyp = "H-Flick-D";
        else if (still && maskUploads > 2 && dumpCount <= 8) hyp = "H-Flick-E";

        var inv = CultureInfo.InvariantCulture;
        // #region agent log
        try
        {
            System.IO.File.AppendAllText(LogPath,
                "{\"sessionId\":\"40cccb\",\"runId\":\"flicker-1\",\"hypothesisId\":\"" + hyp
                + "\",\"location\":\"FarseerFlickerDiag.NoteFarseerFrame\",\"message\":\"flicker-window\",\"data\":{"
                + "\"still\":" + (still ? "true" : "false")
                + ",\"camMove\":" + camMove.ToString("0.###", inv)
                + ",\"farMin\":" + farMin.ToString("0.#", inv)
                + ",\"farMax\":" + farMax.ToString("0.#", inv)
                + ",\"farDelta\":" + farDelta.ToString("0.#", inv)
                + ",\"dsMin\":" + dsMin.ToString("0.#", inv)
                + ",\"dsMax\":" + dsMax.ToString("0.#", inv)
                + ",\"dsDelta\":" + dsDelta.ToString("0.#", inv)
                + ",\"vdMin\":" + vdMin.ToString("0.#", inv)
                + ",\"vdMax\":" + vdMax.ToString("0.#", inv)
                + ",\"vdDelta\":" + vdDelta.ToString("0.#", inv)
                + ",\"rimMin\":" + rimMin
                + ",\"rimMax\":" + rimMax
                + ",\"rimDelta\":" + rimDelta
                + ",\"enrichRebuilds\":" + enrichRebuilds
                + ",\"maskUploads\":" + maskUploads
                + ",\"lastEnrichStamp\":" + lastEnrichStamp
                + ",\"samples\":" + ringFilled
                + ",\"n\":" + dumpCount
                + "},\"timestamp\":" + now + "}\n");
        }
        catch { }
        // #endregion

        // Per-window counters for next interval (keep enrich total cumulative).
        maskUploads = 0;
    }

    public static void ResetSession()
    {
        ringAt = 0;
        ringFilled = 0;
        lastDumpMs = 0;
        dumpCount = 0;
        enrichRebuilds = 0;
        maskUploads = 0;
        lastEnrichStamp = 0;
        lastCamX = double.NaN;
        lastCamZ = double.NaN;
    }
}
