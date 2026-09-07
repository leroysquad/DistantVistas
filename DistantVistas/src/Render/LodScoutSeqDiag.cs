using System.Globalization;
using System.Text;
using Vintagestory.API.Client;

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
    const string RunId = "1042";

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
    static int paintStarveTicks;
    static bool chunkPressureActive;

    static readonly Dictionary<long, long> lastReleaseMsByKey = new();
    static readonly Dictionary<int, (LodScoutEntity.Phase Phase, long Ms)> lastPhaseLogBySlot = new();
    static readonly Dictionary<long, long> lastHostUpMsByKey = new();
    static readonly Dictionary<long, string> lastHostOutcomeByKey = new();
    static readonly Dictionary<long, int> stallCountByKey = new();

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
        paintStarveTicks = 0;
        chunkPressureActive = false;
        lastWarmRingMs = 0;
        lastHopResidencyMs = 0;
        lastHopResidencyLoaded = -1;
        lastReleaseMsByKey.Clear();
        lastPhaseLogBySlot.Clear();
        lastHostUpMsByKey.Clear();
        lastHostOutcomeByKey.Clear();
        stallCountByKey.Clear();
        lastStalledLiveMs = 0;
    }

    public static void SetOverlayActive(bool active) => overlayActive = active;

    public static void NotePaintBudget(int maxBakePerTick, double maxPaintWallMs, int scoutReadyCount)
    {
        paintMaxBakePerTick = maxBakePerTick;
        paintMaxWallMs = maxPaintWallMs;
        paintScoutReady = scoutReadyCount;
    }

    public static void NotePaintStarve(int ticks) => paintStarveTicks = ticks;

    public static void NoteChunkPressure(bool active) => chunkPressureActive = active;

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

        if (reason is "captureStall" or "maxWait")
        {
            stallCountByKey.TryGetValue(key, out int prior);
            stallCountByKey[key] = prior + 1;
            if (prior >= 2)
            {
                LogThrash(slot, key, "key-thrash", ticksLived, near, waitForMesh,
                    "{\"stallCount\":" + (prior + 1) + ",\"reason\":\"" + reason + "\"}");
            }
        }
    }

    /// <summary>
    /// Per-slot stall forensics: distance, map residency, reveal, host outcome (H-SCOUT-SEQ).
    /// </summary>
    public static void LogStallForensics(
        int slot,
        long key,
        string reason,
        LodScoutEntity scout,
        ICoreClientAPI capi,
        LodPipeline pipeline,
        double pickupX,
        double pickupZ,
        int warmHoldBlocks,
        LodTerrainRenderer? renderer)
    {
        var (vx, _, vz) = LodLoginSweep.VisitPosition(capi.World, key);
        double dx = vx - pickupX;
        double dz = vz - pickupZ;
        int distBlocks = (int)Math.Round(Math.Sqrt(dx * dx + dz * dz));
        int loaded = LodLoginSweep.CountLoadedMapChunks(capi.World.BlockAccessor, key);
        int resident = pipeline.World.HasDataSet.Contains(key)
            ? LodLoginScoutFill.ResidentFastHandoffCols
            : 0;
        bool coldNear = LodLoginScoutFill.IsColdNearVisitPublic(
            capi, key, pickupX, pickupZ, warmHoldBlocks, loaded, resident);
        stallCountByKey.TryGetValue(key, out int stallCount);
        lastHostOutcomeByKey.TryGetValue(key, out string? hostOutcome);
        if (hostOutcome == null) hostOutcome = "none";

        Write("LodLoginScoutFill.ReleaseSlot", "stall-forensics",
            "{\"slot\":" + slot
            + ",\"key\":" + key
            + ",\"reason\":\"" + reason + "\""
            + ",\"distBlocks\":" + distBlocks
            + ",\"loadedMapChunks\":" + loaded
            + ",\"residentCols\":" + resident
            + ",\"coldNear\":" + Bool(coldNear)
            + ",\"revealRadius\":" + scout.RevealRadius
            + ",\"holdRadius\":" + scout.HoldRadius
            + ",\"phase\":\"" + PhaseName(scout.Current) + "\""
            + ",\"ticksInPhase\":" + scout.Ticks
            + ",\"hostOutcome\":\"" + hostOutcome + "\""
            + ",\"stallCount\":" + stallCount
            + ",\"waitForMesh\":" + Bool(scout.WaitForMesh)
            + ",\"warmHoldBlocks\":" + warmHoldBlocks
            + ",\"playerAtPickup\":true"
            + ",\"captureRequiresPlayer\":false"
            + "}");
    }

    static long lastStalledLiveMs;

    /// <summary>
    /// Aggregate live scouts in cliff band when paint starves — proves "can't enter next square".
    /// </summary>
    public static void MaybeStalledLiveProbe(
        int finished,
        ICoreClientAPI capi,
        LodPipeline pipeline,
        LodLoginScoutFill scoutFill,
        double pickupX,
        double pickupZ,
        int warmHoldBlocks,
        int paintStarveTicks,
        int scoutReady)
    {
        if (!overlayActive) return;
        if (finished < 320 || finished > 400) return;
        if (paintStarveTicks < 8) return;
        long now = NowMs();
        if (lastStalledLiveMs != 0 && now - lastStalledLiveMs < 5000) return;
        lastStalledLiveMs = now;

        var ba = capi.World.BlockAccessor;
        var sb = new StringBuilder(512);
        sb.Append("{\"finished\":").Append(finished)
            .Append(",\"paintStarveTicks\":").Append(paintStarveTicks)
            .Append(",\"scoutReady\":").Append(scoutReady)
            .Append(",\"slots\":[");
        bool first = true;
        int coldNearLive = 0;
        int zeroLoadedLive = 0;
        for (int i = 0; i < LodLoginScoutFill.MaxConcurrent; i++)
        {
            if (!scoutFill.TryGetLiveSlot(i, out LodScoutEntity? scout) || scout == null)
                continue;
            int loaded = LodLoginSweep.CountLoadedMapChunks(ba, scout.Key);
            int resident = pipeline.World.HasDataSet.Contains(scout.Key)
                ? LodLoginScoutFill.ResidentFastHandoffCols
                : 0;
            bool coldNear = LodLoginScoutFill.IsColdNearVisitPublic(
                capi, scout.Key, pickupX, pickupZ, warmHoldBlocks, loaded, resident);
            if (coldNear) coldNearLive++;
            if (loaded == 0) zeroLoadedLive++;
            lastHostOutcomeByKey.TryGetValue(scout.Key, out string? hostOutcome);
            var (vx, _, vz) = LodLoginSweep.VisitPosition(capi.World, scout.Key);
            double dx = vx - pickupX;
            double dz = vz - pickupZ;
            int distBlocks = (int)Math.Round(Math.Sqrt(dx * dx + dz * dz));
            stallCountByKey.TryGetValue(scout.Key, out int stallCount);
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"slot\":").Append(i)
                .Append(",\"key\":").Append(scout.Key)
                .Append(",\"phase\":\"").Append(PhaseName(scout.Current)).Append('"')
                .Append(",\"ticks\":").Append(scout.Ticks)
                .Append(",\"distBlocks\":").Append(distBlocks)
                .Append(",\"loadedMapChunks\":").Append(loaded)
                .Append(",\"revealRadius\":").Append(scout.RevealRadius)
                .Append(",\"holdRadius\":").Append(scout.HoldRadius)
                .Append(",\"coldNear\":").Append(Bool(coldNear))
                .Append(",\"hostOutcome\":\"").Append(hostOutcome ?? "none").Append('"')
                .Append(",\"stallCount\":").Append(stallCount)
                .Append('}');
        }
        sb.Append("],\"coldNearLive\":").Append(coldNearLive)
            .Append(",\"zeroLoadedLive\":").Append(zeroLoadedLive)
            .Append(",\"playerAtPickup\":true")
            .Append(",\"captureRequiresPlayer\":false")
            .Append('}');

        Write("LodLoginScoutFill.Tick", "stalled-live-probe", sb.ToString());
    }

    public static void MaybeBudget(
        int nearLive,
        int farLive,
        int heldNear,
        int heldFar,
        int scoutReady,
        int waitChunksLive = 0,
        int captureLive = 0)
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
            + ",\"waitChunksLive\":" + waitChunksLive
            + ",\"captureLive\":" + captureLive
            + ",\"spawnsLastSec\":" + spawnsWindow
            + ",\"releasesLastSec\":" + releasesWindow
            + ",\"avgNearTicks\":" + avgNear.ToString("0.#", Inv)
            + ",\"avgFarTicks\":" + avgFar.ToString("0.#", Inv)
            + ",\"maxBakePerTick\":" + paintMaxBakePerTick
            + ",\"maxPaintWallMs\":" + paintMaxWallMs.ToString("0.#", Inv)
            + ",\"paintReadyQueued\":" + paintScoutReady
            + ",\"paintStarveTicks\":" + paintStarveTicks
            + ",\"chunkPressure\":" + Bool(chunkPressureActive)
            + "}");

        spawnsWindow = 0;
        releasesWindow = 0;
    }

    public static void LogHopUnlock(
        int ring,
        bool advance,
        double x,
        double y,
        double z,
        int targetRadiusBlocks,
        double bearingRad,
        int finished,
        int pendingCount,
        long targetKey,
        int loadedMapChunks,
        bool usedFallback,
        int distFromPickup,
        int pastWarmBlocks,
        int loadedAfterDwell,
        int skippedCooldown,
        int residencyLoaded,
        long pumpAnchorKey)
    {
        if (!overlayActive) return;
        Write("LodLoginHopUnlock.ApplyHop", "hop-unlock",
            "{\"ring\":" + ring
            + ",\"advance\":" + Bool(advance)
            + ",\"x\":" + x.ToString("0.##", Inv)
            + ",\"y\":" + y.ToString("0.##", Inv)
            + ",\"z\":" + z.ToString("0.##", Inv)
            + ",\"targetRadiusBlocks\":" + targetRadiusBlocks
            + ",\"distFromPickup\":" + distFromPickup
            + ",\"pastWarmBlocks\":" + pastWarmBlocks
            + ",\"targetKey\":" + targetKey
            + ",\"loadedMapChunks\":" + loadedMapChunks
            + ",\"loadedAfterDwell\":" + loadedAfterDwell
            + ",\"residencyLoaded\":" + residencyLoaded
            + ",\"pumpAnchorKey\":" + pumpAnchorKey
            + ",\"skippedCooldown\":" + skippedCooldown
            + ",\"usedFallback\":" + Bool(usedFallback)
            + ",\"bearingRad\":" + bearingRad.ToString("0.####", Inv)
            + ",\"finished\":" + finished
            + ",\"pending\":" + pendingCount
            + ",\"pickupRestore\":true"
            + "}");
    }

    static long lastWarmRingMs;
    static long lastHopResidencyMs;
    static int lastHopResidencyLoaded = -1;

    /// <summary>
    /// Prove forced residency pump: loaded map chunks at unlock L0 while hop dwells.
    /// </summary>
    public static void MaybeHopResidencyProbe(
        int ring,
        long targetKey,
        long pumpAnchorKey,
        int ticksAtPoint,
        int finished,
        int paintStarveTicks,
        int waitChunksLive,
        int captureLive,
        int residencyLoaded,
        double x,
        double y,
        double z,
        bool hostConnected)
    {
        if (!overlayActive) return;
        if (finished < 320 || finished > 420) return;
        long now = NowMs();
        bool loadedChanged = residencyLoaded != lastHopResidencyLoaded;
        if (!loadedChanged && now - lastHopResidencyMs < 2000) return;
        if (residencyLoaded >= LodLoginScoutFill.MapChunksPerL0 && lastHopResidencyLoaded >= LodLoginScoutFill.MapChunksPerL0)
            return;
        lastHopResidencyMs = now;
        lastHopResidencyLoaded = residencyLoaded;

        Write("LodLoginHopUnlock.PumpUnlockResidency", "hop-residency-probe",
            "{\"ring\":" + ring
            + ",\"targetKey\":" + targetKey
            + ",\"pumpAnchorKey\":" + pumpAnchorKey
            + ",\"ticksAtPoint\":" + ticksAtPoint
            + ",\"residencyLoaded\":" + residencyLoaded
            + ",\"mapChunksPerL0\":" + LodLoginScoutFill.MapChunksPerL0
            + ",\"x\":" + x.ToString("0.##", Inv)
            + ",\"y\":" + y.ToString("0.##", Inv)
            + ",\"z\":" + z.ToString("0.##", Inv)
            + ",\"finished\":" + finished
            + ",\"paintStarveTicks\":" + paintStarveTicks
            + ",\"waitChunksLive\":" + waitChunksLive
            + ",\"captureLive\":" + captureLive
            + ",\"hostConnected\":" + Bool(hostConnected)
            + "}");
    }

    /// <summary>
    /// Prove/disprove warm-ring cliff: log pending residency vs finished radius near ~358 band.
    /// </summary>
    public static void MaybeWarmRingProbe(
        int finished,
        int total,
        ICoreClientAPI capi,
        LodPipeline pipeline,
        Queue<long> pending,
        double pickupX,
        double pickupZ,
        int warmHoldBlocks,
        int waitChunksLive,
        int captureLive,
        int liveScouts,
        int scoutReady)
    {
        if (!overlayActive) return;
        if (finished < 320 || finished > 400) return;
        long now = NowMs();
        if (lastWarmRingMs != 0 && now - lastWarmRingMs < 5000) return;
        lastWarmRingMs = now;

        int warmL0Est = LodLoginScoutFill.WarmRingL0CellEstimate(warmHoldBlocks);
        int finishedRadius = LodLoginScoutFill.FinishedToRadiusBlocks(finished);
        var ba = capi.World.BlockAccessor;
        int pendingN = pending.Count;
        int loaded4 = 0;
        int loaded1Plus = 0;
        int coldNear = 0;
        int insideWarm = 0;
        int annulus = 0;
        int outsideSpawn = 0;
        double warmSq = (double)warmHoldBlocks * warmHoldBlocks;
        double spawnSq = LodLoginBake.SpawnSolidRadiusBlocks * LodLoginBake.SpawnSolidRadiusBlocks;
        int sampled = 0;
        foreach (long key in pending)
        {
            if (sampled++ >= 256) break;
            int loaded = LodLoginSweep.CountLoadedMapChunks(ba, key);
            if (loaded >= 4) loaded4++;
            if (loaded >= 1) loaded1Plus++;
            var (x, _, z) = LodLoginSweep.VisitPosition(capi.World, key);
            double dx = x - pickupX;
            double dz = z - pickupZ;
            double distSq = dx * dx + dz * dz;
            int resident = pipeline.World.HasDataSet.Contains(key) ? LodLoginScoutFill.ResidentFastHandoffCols : 0;
            if (distSq <= warmSq) insideWarm++;
            else if (distSq <= spawnSq) annulus++;
            else outsideSpawn++;
            if (distSq <= spawnSq && distSq > warmSq && loaded == 0 && resident < LodLoginScoutFill.ResidentFastHandoffCols)
                coldNear++;
        }

        Write("LodLoginScoutFill.Tick", "warm-ring-probe",
            "{\"finished\":" + finished
            + ",\"total\":" + total
            + ",\"warmHoldBlocks\":" + warmHoldBlocks
            + ",\"warmL0Estimate\":" + warmL0Est
            + ",\"finishedRadiusBlocks\":" + finishedRadius
            + ",\"pendingSampled\":" + sampled
            + ",\"pendingLoaded4\":" + loaded4
            + ",\"pendingLoaded1Plus\":" + loaded1Plus
            + ",\"pendingColdNear\":" + coldNear
            + ",\"pendingInsideWarm\":" + insideWarm
            + ",\"pendingAnnulus\":" + annulus
            + ",\"pendingOutsideSpawn\":" + outsideSpawn
            + ",\"waitChunksLive\":" + waitChunksLive
            + ",\"captureLive\":" + captureLive
            + ",\"liveScouts\":" + liveScouts
            + ",\"scoutReady\":" + scoutReady
            + ",\"paintStarveTicks\":" + paintStarveTicks
            + ",\"chunkPressure\":" + Bool(chunkPressureActive)
            + "}");
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
        long now = NowMs();
        if (!capped && !pending
            && lastHostUpMsByKey.TryGetValue(key, out long lastUp)
            && now - lastUp < 2000)
            return;
        lastHostUpMsByKey[key] = now;
        lastHostOutcomeByKey[key] = capped ? "capped" : pending ? "pending" : "sent";

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
        lastHostOutcomeByKey[key] = "held";
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
            for (int i = 0; i < section.Palette.Count; i++)
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
