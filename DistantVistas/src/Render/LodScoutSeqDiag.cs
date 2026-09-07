using System.Globalization;
using System.Text;

namespace DistantVistas;

/// <summary>
/// Login-overlay scout sequence NDJSON (session 40cccb / H-SCOUT-SEQ).
/// Rate-limits repeated phase samples but always logs phase transitions, spawn, release, thrash.
/// </summary>
public static class LodScoutSeqDiag
{
    const string LogPath =
        @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log";

    const string HypothesisId = "H-SCOUT-SEQ";
    const string SessionId = "40cccb";
    const string RunId = "1033";

    const int ThrashMaxTicks = 5;
    const long ThrashRespawnMs = 2000;
    const long PhaseRepeatMinMs = 400;
    const long BudgetIntervalMs = 1000;

    static bool overlayActive;
    static long lastBudgetMs;
    static int spawnsWindow;
    static int releasesWindow;
    static long sumNearTicks;
    static long sumFarTicks;
    static int nearReleaseN;
    static int farReleaseN;
    static int paintMaxBakePerTick;
    static double paintMaxWallMs;
    static int paintScoutReady;

    static readonly Dictionary<long, long> lastReleaseMsByKey = new();
    static readonly Dictionary<int, (LodScoutEntity.Phase Phase, long Ms)> lastPhaseLogBySlot = new();
    static readonly Dictionary<long, long> lastHostUpMsByKey = new();

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static void Reset()
    {
        overlayActive = false;
        lastBudgetMs = 0;
        spawnsWindow = 0;
        releasesWindow = 0;
        sumNearTicks = 0;
        sumFarTicks = 0;
        nearReleaseN = 0;
        farReleaseN = 0;
        paintMaxBakePerTick = 0;
        paintMaxWallMs = 0;
        paintScoutReady = 0;
        lastReleaseMsByKey.Clear();
        lastPhaseLogBySlot.Clear();
    }

    public static void SetOverlayActive(bool active) => overlayActive = active;

    public static void NotePaintBudget(int maxBakePerTick, double maxPaintWallMs, int scoutReadyCount)
    {
        paintMaxBakePerTick = maxBakePerTick;
        paintMaxWallMs = maxPaintWallMs;
        paintScoutReady = scoutReadyCount;
    }

    public static void LogSpawn(
        int slot,
        long key,
        bool near,
        bool waitForMesh,
        double pickupX,
        double pickupY,
        double pickupZ,
        int revealChunks)
    {
        long now = NowMs();
        spawnsWindow++;

        if (lastReleaseMsByKey.TryGetValue(key, out long lastRel)
            && now - lastRel < ThrashRespawnMs)
        {
            LogThrash(slot, key, "rapid-respawn", 0, near, waitForMesh,
                "{\"msSinceRelease\":" + (now - lastRel) + "}");
        }

        Write("LodLoginScoutFill.StartSlot", "scout-spawn",
            "{\"slot\":" + slot
            + ",\"key\":" + key
            + ",\"band\":\"" + (near ? "near" : "far") + "\""
            + ",\"waitForMesh\":" + Bool(waitForMesh)
            + ",\"pickupX\":" + pickupX.ToString("0.##", Inv)
            + ",\"pickupY\":" + pickupY.ToString("0.##", Inv)
            + ",\"pickupZ\":" + pickupZ.ToString("0.##", Inv)
            + ",\"revealChunks\":" + revealChunks
            + "}");
    }

    public static void LogPhase(
        int slot,
        LodScoutEntity scout,
        LodTerrainRenderer? renderer,
        LodPipeline? pipeline,
        bool forceTransition = false)
    {
        long now = NowMs();
        if (!forceTransition
            && lastPhaseLogBySlot.TryGetValue(slot, out var prev)
            && prev.Phase == scout.Current
            && now - prev.Ms < PhaseRepeatMinMs)
            return;

        lastPhaseLogBySlot[slot] = (scout.Current, now);

        var sb = new StringBuilder(256);
        sb.Append("{\"slot\":").Append(slot)
            .Append(",\"key\":").Append(scout.Key)
            .Append(",\"phase\":\"").Append(PhaseName(scout.Current)).Append('"')
            .Append(",\"ticksInPhase\":").Append(scout.Ticks)
            .Append(",\"waitForMesh\":").Append(Bool(scout.WaitForMesh))
            .Append(",\"revealChunks\":").Append(scout.RevealRadius);
        AppendSectionState(sb, renderer, pipeline, scout.Key);
        sb.Append(",\"paintQueued\":").Append(Bool(scout.PaintQueued))
            .Append(",\"painted\":").Append(Bool(scout.Painted))
            .Append('}');

        Write("LodLoginScoutFill.Tick", "scout-phase", sb.ToString());
    }

    public static void LogRelease(
        int slot,
        long key,
        string reason,
        int ticksLived,
        bool near,
        bool waitForMesh,
        LodTerrainRenderer? renderer,
        LodPipeline? pipeline)
    {
        long now = NowMs();
        releasesWindow++;
        lastReleaseMsByKey[key] = now;
        lastPhaseLogBySlot.Remove(slot);

        if (near)
        {
            sumNearTicks += ticksLived;
            nearReleaseN++;
        }
        else
        {
            sumFarTicks += ticksLived;
            farReleaseN++;
        }

        var sb = new StringBuilder(192);
        sb.Append("{\"slot\":").Append(slot)
            .Append(",\"key\":").Append(key)
            .Append(",\"reason\":\"").Append(reason).Append('"')
            .Append(",\"ticksLived\":").Append(ticksLived)
            .Append(",\"band\":\"").Append(near ? "near" : "far").Append('"')
            .Append(",\"waitForMesh\":").Append(Bool(waitForMesh));
        AppendSectionState(sb, renderer, pipeline, key);
        sb.Append('}');

        Write("LodLoginScoutFill.ReleaseSlot", "scout-release", sb.ToString());

        if (ticksLived < ThrashMaxTicks)
        {
            LogThrash(slot, key, "short-lived", ticksLived, near, waitForMesh,
                "{\"ticksLived\":" + ticksLived + ",\"reason\":\"" + reason + "\"}");
        }
    }

    public static void MaybeBudget(
        int nearLive,
        int farLive,
        int heldNear,
        int heldFar,
        int scoutReady)
    {
        if (!overlayActive) return;
        long now = NowMs();
        if (lastBudgetMs != 0 && now - lastBudgetMs < BudgetIntervalMs)
            return;
        lastBudgetMs = now;

        double avgNear = nearReleaseN > 0 ? sumNearTicks / (double)nearReleaseN : 0;
        double avgFar = farReleaseN > 0 ? sumFarTicks / (double)farReleaseN : 0;

        Write("LodLoginScoutFill.Tick", "scout-budget",
            "{\"nearLive\":" + nearLive
            + ",\"farLive\":" + farLive
            + ",\"heldNear\":" + heldNear
            + ",\"heldFar\":" + heldFar
            + ",\"scoutReady\":" + scoutReady
            + ",\"spawnsLastSec\":" + spawnsWindow
            + ",\"releasesLastSec\":" + releasesWindow
            + ",\"avgNearTicks\":" + avgNear.ToString("0.#", Inv)
            + ",\"avgFarTicks\":" + avgFar.ToString("0.#", Inv)
            + ",\"maxBakePerTick\":" + paintMaxBakePerTick
            + ",\"maxPaintWallMs\":" + paintMaxWallMs.ToString("0.#", Inv)
            + ",\"paintReadyQueued\":" + paintScoutReady
            + "}");

        spawnsWindow = 0;
        releasesWindow = 0;
    }

    static void LogThrash(
        int slot,
        long key,
        string kind,
        int ticksLived,
        bool near,
        bool waitForMesh,
        string extraJson)
    {
        Write("LodLoginScoutFill.ReleaseSlot", "scout-thrash",
            "{\"slot\":" + slot
            + ",\"key\":" + key
            + ",\"kind\":\"" + kind + "\""
            + ",\"ticksLived\":" + ticksLived
            + ",\"band\":\"" + (near ? "near" : "far") + "\""
            + ",\"waitForMesh\":" + Bool(waitForMesh)
            + ",\"extra\":" + extraJson
            + "}");
    }

    public static void LogHostUp(long key, int cx, int cz, int radius, bool capped, bool pending)
    {
        Write("LodScoutHostSystem.RequestUp", "scout-host-up",
            "{\"key\":" + key
            + ",\"cx\":" + cx
            + ",\"cz\":" + cz
            + ",\"radius\":" + radius
            + ",\"capped\":" + Bool(capped)
            + ",\"pending\":" + Bool(pending)
            + "}");
    }

    public static void LogHostDown(long key, string source)
    {
        Write("LodScoutHostSystem.RequestDown", "scout-host-down",
            "{\"key\":" + key + ",\"source\":\"" + source + "\"}");
    }

    public static void LogHostHold(long key, int holdCount, int pendingUps, int forceSendQueued)
    {
        if (!overlayActive) return;
        long now = NowMs();
        if (lastBudgetMs != 0 && now - lastBudgetMs < BudgetIntervalMs)
            return;

        Write("LodScoutHostSystem.HoldAnchor", "scout-host-hold",
            "{\"key\":" + key
            + ",\"holdCount\":" + holdCount
            + ",\"pendingUps\":" + pendingUps
            + ",\"forceSendQueued\":" + forceSendQueued
            + "}");
    }

    public static void LogViewerSpawn(string side, long visitKey, double x, double y, double z)
    {
        Write("LodScoutViewerEntity.SpawnAt", "scout-viewer-spawn",
            "{\"side\":\"" + side + "\""
            + ",\"key\":" + visitKey
            + ",\"x\":" + x.ToString("0.##", Inv)
            + ",\"y\":" + y.ToString("0.##", Inv)
            + ",\"z\":" + z.ToString("0.##", Inv)
            + "}");
    }

    public static void LogViewerDespawn(string side, long visitKey, int batchCount)
    {
        Write("LodScoutViewerEntity.DespawnOne", "scout-viewer-despawn",
            "{\"side\":\"" + side + "\""
            + ",\"key\":" + visitKey
            + ",\"batchCount\":" + batchCount
            + "}");
    }

    static void AppendSectionState(StringBuilder sb, LodTerrainRenderer? renderer, LodPipeline? pipeline, long key)
    {
        bool hasMesh = renderer?.HasDrawableMesh(key) ?? false;
        bool emptyClaim = renderer?.HasEmptyMeshClaim(key) ?? false;
        bool flagBaked = false;
        if (pipeline?.World.Sections.TryGetValue(key, out LodSection? section) == true)
        {
            for (int i = 0; i < section.Palette.Length; i++)
            {
                if ((section.Palette[i].Flags & LodPaletteEntry.FlagBaked) == 0) continue;
                flagBaked = true;
                break;
            }
        }

        sb.Append(",\"hasDrawableMesh\":").Append(Bool(hasMesh))
            .Append(",\"hasEmptyMeshClaim\":").Append(Bool(emptyClaim))
            .Append(",\"flagBaked\":").Append(Bool(flagBaked));
    }

    static string PhaseName(LodScoutEntity.Phase phase) => phase switch
    {
        LodScoutEntity.Phase.WaitChunks => "WaitChunks",
        LodScoutEntity.Phase.Capture => "Capture",
        LodScoutEntity.Phase.Paint => "Paint",
        LodScoutEntity.Phase.Mesh => "Mesh",
        LodScoutEntity.Phase.Done => "Release",
        _ => phase.ToString(),
    };

    static void Write(string location, string message, string dataJson)
    {
        try
        {
            System.IO.File.AppendAllText(LogPath,
                "{\"sessionId\":\"" + SessionId + "\",\"runId\":\"" + RunId
                + "\",\"hypothesisId\":\"" + HypothesisId + "\",\"location\":\"" + location
                + "\",\"message\":\"" + message + "\",\"data\":" + dataJson
                + ",\"timestamp\":" + NowMs() + "}\n");
        }
        catch { }
    }

    static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    static string Bool(bool v) => v ? "true" : "false";
}
