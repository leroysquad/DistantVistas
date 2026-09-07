using System.Globalization;
using Vintagestory.API.Client;

namespace DistantVistas;

/// <summary>
/// Debug-session probe: far mesh reach vs login sweep boost vs tree-top GetColor.
/// Writes NDJSON to debug-40cccb.log (session 40cccb).
/// </summary>
public static class FarCoverageDiag
{
    const string LogPath =
        @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log";

    static long lastLogMs;
    static int logCount;
    static int canopyGetColorOk;
    static int canopyGetColorZero;
    static int lastSweepRadiusChunks;
    static int lastBoostVd;
    static int lastSwept;
    static int lastPending;
    static bool lastDiscoverOnly;
    static int emptyMeshClaims;
    static int emptyMeshClears;
    static int lastEmptyCols;

    public static void NoteCanopySample(bool getColor, bool zero)
    {
        if (getColor) canopyGetColorOk++;
        if (zero) canopyGetColorZero++;
    }

    public static void NoteEmptyMeshClaim(long key, int capturedColumns)
    {
        emptyMeshClaims++;
        lastEmptyCols = capturedColumns;
        _ = key;
    }

    public static void NoteEmptyMeshClear()
    {
        emptyMeshClears++;
    }

    public static void NoteSweepTick(int radiusChunks, int boostVd, int swept, int pending, bool discoverOnly)
    {
        lastSweepRadiusChunks = radiusChunks;
        lastBoostVd = boostVd;
        lastSwept = swept;
        lastPending = pending;
        lastDiscoverOnly = discoverOnly;
    }

    public static void MaybeLogPlay(
        ICoreClientAPI? capi,
        LodWorld? world,
        float liveVd,
        double meshedDist,
        double capturedDist,
        double effFar,
        int drawCount,
        bool discoverOnly,
        bool drawAfterCompanion)
    {
        if (capi == null || world == null) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (logCount >= 48) return;
        if (now - lastLogMs < 2000) return;
        lastLogMs = now;
        logCount++;

        double onset = LodCoveragePolicy.FarseerSilhouetteOnsetDistance(liveVd);
        double env = 0;
        try
        {
            var spawn = capi.World.DefaultSpawnPosition;
            env = FarseerVisitOnset.CaptureEnvelopeRadiusBlocks(world, spawn.X, spawn.Z);
            var complete = LodLoginSweepComplete.TryLoad(capi);
            if (complete != null && complete.SweepRadiusBlocks > env)
                env = complete.SweepRadiusBlocks;
        }
        catch { }

        int l0 = 0;
        foreach (long k in world.HasDataSet)
            if (LodWorld.KeyLevel(k) == 0) l0++;

        // H-E: sticky emptyMeshKeys claimed coverage (claims without clears).
        // H-S1: login sweep radius still thin vs onset.
        // H-S2: PastHorizonEmptyStop + late onset leave a sky band past meshedDist.
        // H-S3: DiscoverOnly starves far capture after login.
        // H-T1: tree tops bake with GetColor=0 (no colormap ride).
        string hyp;
        if (emptyMeshClaims > 0 && emptyMeshClears == 0 && emptyMeshClaims >= 3)
            hyp = "H-E";
        else if (lastSweepRadiusChunks > 0 && lastSweepRadiusChunks < 80
            && env + 512 < onset)
            hyp = "H-S1";
        else if (meshedDist + 256 < onset && drawAfterCompanion)
            hyp = "H-S2";
        else if (discoverOnly && meshedDist < Math.Max(onset, 4096))
            hyp = "H-S3";
        else if (canopyGetColorZero > canopyGetColorOk && canopyGetColorZero + canopyGetColorOk > 20)
            hyp = "H-T1";
        else
            hyp = "ok";

        var inv = CultureInfo.InvariantCulture;
        try
        {
            System.IO.File.AppendAllText(LogPath,
                "{\"sessionId\":\"40cccb\",\"runId\":\"post-fix\",\"hypothesisId\":\"" + hyp
                + "\",\"location\":\"FarCoverageDiag.MaybeLogPlay\",\"message\":\"far-coverage\",\"data\":{"
                + "\"liveVd\":" + liveVd.ToString("0.#", inv)
                + ",\"onsetBlocks\":" + onset.ToString("0.#", inv)
                + ",\"meshedDist\":" + meshedDist.ToString("0.#", inv)
                + ",\"capturedDist\":" + capturedDist.ToString("0.#", inv)
                + ",\"effFar\":" + effFar.ToString("0.#", inv)
                + ",\"envRadius\":" + env.ToString("0.#", inv)
                + ",\"gapMeshToOnset\":" + (onset - meshedDist).ToString("0.#", inv)
                + ",\"drawCount\":" + drawCount
                + ",\"l0Count\":" + l0
                + ",\"hasData\":" + world.HasDataSet.Count
                + ",\"discoverOnly\":" + (discoverOnly ? "true" : "false")
                + ",\"drawAfterCompanion\":" + (drawAfterCompanion ? "true" : "false")
                + ",\"sweepRadiusChunks\":" + lastSweepRadiusChunks
                + ",\"boostVd\":" + lastBoostVd
                + ",\"swept\":" + lastSwept
                + ",\"pending\":" + lastPending
                + ",\"sweepDiscoverOnly\":" + (lastDiscoverOnly ? "true" : "false")
                + ",\"canopyGetColorOk\":" + canopyGetColorOk
                + ",\"canopyGetColorZero\":" + canopyGetColorZero
                + ",\"emptyClaims\":" + emptyMeshClaims
                + ",\"emptyClears\":" + emptyMeshClears
                + ",\"lastEmptyCols\":" + lastEmptyCols
                + ",\"visitRadiusBlocks\":" + LodLoginBakeViewBoost.SweepVisitRadiusBlocks
                + ",\"boostCapBlocks\":" + LodLoginBakeViewBoost.SweepBoostViewDistanceBlocks
                + ",\"bootstrapRadius\":" + LodLoginSweepBootstrap.EmptyCanvasBootstrapRadiusBlocks
                + ",\"n\":" + logCount
                + "},\"timestamp\":" + now + "}\n");
        }
        catch { }

        canopyGetColorOk = 0;
        canopyGetColorZero = 0;
    }

    public static void ResetSession()
    {
        logCount = 0;
        lastLogMs = 0;
        canopyGetColorOk = 0;
        canopyGetColorZero = 0;
        lastSweepRadiusChunks = 0;
        lastBoostVd = 0;
        lastSwept = 0;
        lastPending = 0;
        lastDiscoverOnly = false;
        emptyMeshClaims = 0;
        emptyMeshClears = 0;
        lastEmptyCols = 0;
    }
}
