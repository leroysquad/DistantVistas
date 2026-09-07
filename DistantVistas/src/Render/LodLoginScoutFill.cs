using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace DistantVistas;

/// <summary>
/// Login-overlay coverage without hopping the player: stagger real
/// <see cref="LodScoutViewerEntity"/> workers. Each viewer is a player-style
/// stream/render center at a visit cell. Slots are capture-only; GetColor paint runs
/// from scoutReady so slow paint never pins all 16 slots. Spawn-solid mesh wait is at
/// overlay end (CountMissingSpawnDrawable), not by holding scouts through paint/mesh.
/// </summary>
public sealed class LodLoginScoutFill
{
    public const int MaxConcurrent = 16;
    /// <summary>Legacy near/far slot caps (telemetry). All slots share one FIFO queue.</summary>
    public const int MaxNearConcurrent = 8;
    public const int MaxFarConcurrent = 8;
    public const int MaxWaitTicks = 24;
    /// <summary>Do not park more than this many spawn-disk scouts in WaitChunks at once.</summary>
    public const int MaxNearWaitChunksLive = 4;
    /// <summary>Cap far-ring WaitChunks so paint-near keys are not starved by rim streamers.</summary>
    public const int MaxFarWaitChunksLive = 6;
    /// <summary>Always enter Capture after this many WaitChunks ticks (~400 ms).</summary>
    public const int WaitChunksForceCaptureTicks = 8;
    /// <summary>Try paint handoff while still waiting for map (resident / partial capture).</summary>
    public const int WaitChunksHandoffTicks = 12;
    /// <summary>When this many scouts WaitChunks, rotate stale slots into paint.</summary>
    public const int WaitChunksRotateMinLive = 6;
    /// <summary>Rotate WaitChunks slots into paint handoff (~800 ms).</summary>
    public const int WaitChunksRotateTicks = 16;
    public const int MaxCaptureWaitTicks = 6;
    public const int MaxMeshWaitTicks = 120;
    /// <summary>After this many maxWait requeues, hand off paint if any section data exists.</summary>
    public const int MaxWaitKeyedRetries = 1;
    /// <summary>HasDataSet revisit with this many captured cols skips long WaitChunks.</summary>
    public const int ResidentFastHandoffCols = 64;
    /// <summary>Paint queue empty while scouts live — tighten waits further.</summary>
    public const int PaintStarveForceCaptureTicks = 4;
    public const int ChunkVisibleRadius = 2;
    public const int SweepRadiusChunks = 2;
    public const int SweepRowsPerCall = 1;
    public const int RevealGrowPerTick = 8;
    public const int RequestUpRetryTicks = 8;
    /// <summary>KeepLoaded / visible ring at spawn-solid visit cells.</summary>
    public const int NearRevealChunks = 4;
    /// <summary>Far visit cells only need the L0 footprint (64 blocks ≈ 2 chunks).</summary>
    public const int FarRevealChunks = 2;
    /// <summary>Pinned scouts re-partition this often; spawn still partitions once.</summary>
    public const int PartitioningEveryTicks = 8;
    /// <summary>
    /// Cap passed into Tick as chunkVisibleTarget. Must stay a neighbourhood,
    /// not the ~130-chunk onset disk.
    /// </summary>
    public const int LocalVisitRevealChunks = NearRevealChunks;

    readonly LodScoutEntity?[] slots = new LodScoutEntity[MaxConcurrent];
    readonly Queue<long> heldNear = new();
    readonly Queue<long> heldFar = new();
    readonly List<long> readyScratch = new(MaxConcurrent);
    readonly List<long> pendingScratch = new(256);
    readonly Dictionary<long, int> waitRetries = new();
    bool paintStarving;
    int liveCount;

    public void SetPaintStarving(bool starving) => paintStarving = starving;

    public int LiveCount => liveCount;
    public int HeldCount => heldNear.Count + heldFar.Count;
    public int FinishedThisTick { get; private set; }
    public long? LastFinishedKey { get; private set; }

    public void Reset(ICoreClientAPI? capi = null)
    {
        DespawnLive(capi, "reset");
        LodScoutHostSystem.ClientInstance?.RequestClear();
        if (capi != null)
            LodScoutViewerEntity.DespawnAll(capi.World);
        for (int i = 0; i < slots.Length; i++) slots[i] = null;
        heldNear.Clear();
        heldFar.Clear();
        readyScratch.Clear();
        pendingScratch.Clear();
        waitRetries.Clear();
        paintStarving = false;
        liveCount = 0;
        FinishedThisTick = 0;
        LastFinishedKey = null;
        LodScoutSeqDiag.Reset();
    }

    public bool HasWork => liveCount > 0 || HeldCount > 0;

    public void CopyLiveKeys(List<long> dest)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            LodScoutEntity? scout = slots[i];
            if (scout is { Live: true }) dest.Add(scout.Key);
        }
    }

    public void CopyHeldKeys(List<long> dest)
    {
        foreach (long key in heldNear) dest.Add(key);
        foreach (long key in heldFar) dest.Add(key);
    }

    public void NotifyPainted(long key)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            LodScoutEntity? scout = slots[i];
            if (scout is { Live: true } && scout.Key == key)
                scout.Painted = true;
        }
    }

    public bool IsLive(long key)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            LodScoutEntity? scout = slots[i];
            if (scout is { Live: true } && scout.Key == key)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Advance every empty slot from <paramref name="pending"/> (FIFO). Returns keys
    /// ready for GetColor-paint. heldNear/heldFar are flushed each tick — no mid-disk backlog.
    /// </summary>
    public List<long> Tick(
        ICoreClientAPI capi,
        LodPipeline pipeline,
        LodTerrainRenderer renderer,
        Queue<long> pending,
        HashSet<long> completedKeys,
        int chunkVisibleTarget,
        int onsetChunks,
        double pickupX,
        double pickupZ)
    {
        FinishedThisTick = 0;
        LastFinishedKey = null;
        readyScratch.Clear();
        FlushHeldToPending(pending);
        int forceCaptureTicks = paintStarving ? PaintStarveForceCaptureTicks : WaitChunksForceCaptureTicks;
        int rotateTicks = paintStarving ? 8 : WaitChunksRotateTicks;
        int rotateMinLive = paintStarving ? 4 : WaitChunksRotateMinLive;
        int targetCap = Math.Min(
            LocalVisitRevealChunks,
            Math.Max(ChunkVisibleRadius, chunkVisibleTarget));
        int nearWaitChunks = CountNearWaitChunks();
        int farWaitChunks = CountFarWaitChunks();

        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] is { Live: true }) continue;
            long? key = TakeNextPending(
                capi, pipeline, pending, completedKeys, pickupX, pickupZ, nearWaitChunks, farWaitChunks);
            if (key == null) break;
            StartSlot(capi, i, key.Value, pickupX, pickupZ, onsetChunks, targetCap);
            if (VisitIsNear(capi, key.Value, pickupX, pickupZ))
                nearWaitChunks++;
            else
                farWaitChunks++;
        }

        for (int i = 0; i < slots.Length; i++)
        {
            LodScoutEntity? scout = slots[i];
            if (scout is not { Live: true }) continue;
            long key = scout.Key;
            scout.Ticks++;
            HoldViewer(scout);

            int dim = capi.World.Player.Entity.Pos.Dimension;
            bool farRing = !scout.WaitForMesh;
            int target = Math.Min(
                farRing ? FarRevealChunks : NearRevealChunks,
                ClampReveal(scout, pickupX, pickupZ, onsetChunks, targetCap, farRing));
            GrowReveal(capi, scout, dim, target);

            if (scout.Current == LodScoutEntity.Phase.WaitChunks)
            {
                int cx = scout.Cx;
                int cz = scout.Cz;
                if (scout.RunSpawnDiskSweep && scout.Ticks % 2 == 0)
                    pipeline.SweepLoadedColumns(
                        cx, cz, SweepRadiusChunks, forceRecapture: false,
                        rowsPerCall: SweepRowsPerCall, lane: LodPipeline.SweepLaneScout);

                if (scout.Ticks > 0 && scout.Ticks % RequestUpRetryTicks == 0)
                {
                    LodScoutHostSystem.ClientInstance?.RequestUp(
                        key, scout.Cx, scout.Cz, scout.HoldRadius, dim, scout.X, scout.Y, scout.Z);
                }

                bool loaded = LodLoginSweep.AllMapChunksLoaded(capi.World.BlockAccessor, key);

                if (TryResidentPaintHandoff(capi, pipeline, key, scout, out string residentReason))
                {
                    ReleaseSlot(capi, i, renderer, pipeline, residentReason);
                    waitRetries.Remove(key);
                    continue;
                }

                if (loaded)
                {
                    pipeline.QueueL0SectionForce(key);
                    scout.Current = LodScoutEntity.Phase.Capture;
                    scout.Ticks = 0;
                    LodScoutSeqDiag.LogPhase(i, scout, renderer, pipeline, forceTransition: true);
                    continue;
                }

                if (scout.Ticks >= 2
                    && TryTimeoutHandoff(
                        pipeline, key, scout, PartialCaptureMin(scout.Ticks), out string earlyReason))
                {
                    ReleaseSlot(capi, i, renderer, pipeline, earlyReason);
                    waitRetries.Remove(key);
                    continue;
                }

                if (scout.Ticks >= WaitChunksHandoffTicks
                    && TryTimeoutHandoff(
                        pipeline, key, scout, PartialCaptureMin(scout.Ticks), out string handoffReason))
                {
                    ReleaseSlot(capi, i, renderer, pipeline, handoffReason);
                    waitRetries.Remove(key);
                    continue;
                }

                if (scout.Ticks >= forceCaptureTicks)
                {
                    pipeline.QueueL0SectionForce(key);
                    scout.Current = LodScoutEntity.Phase.Capture;
                    scout.Ticks = 0;
                    LodScoutSeqDiag.LogPhase(i, scout, renderer, pipeline, forceTransition: true);
                    continue;
                }

                if (scout.Ticks >= rotateTicks
                    && CountPhase(LodScoutEntity.Phase.WaitChunks) >= rotateMinLive
                    && TryTimeoutHandoff(pipeline, key, scout, minCols: 1, out string rotateReason))
                {
                    ReleaseSlot(capi, i, renderer, pipeline, rotateReason);
                    waitRetries.Remove(key);
                    continue;
                }

                if (scout.Ticks < MaxWaitTicks)
                {
                    LodScoutSeqDiag.LogPhase(i, scout, renderer, pipeline);
                    continue;
                }

                pipeline.QueueL0SectionForce(key);
                if (TryTimeoutHandoff(pipeline, key, scout, minCols: 1, out string expireReason))
                {
                    ReleaseSlot(capi, i, renderer, pipeline, expireReason);
                    waitRetries.Remove(key);
                    continue;
                }

                waitRetries.TryGetValue(key, out int retries);
                waitRetries[key] = retries + 1;
                RequeuePending(pending, key);
                ReleaseSlot(capi, i, renderer, pipeline, "maxWait");
                continue;
            }

            if (scout.Current == LodScoutEntity.Phase.Capture)
            {
                if (!CaptureReady(pipeline, key, scout.Ticks))
                {
                    if (paintStarving
                        && scout.Ticks >= 1
                        && TryTimeoutHandoff(pipeline, key, scout, minCols: 1, out string starveReason))
                    {
                        ReleaseSlot(capi, i, renderer, pipeline, starveReason);
                        waitRetries.Remove(key);
                        continue;
                    }

                    if (scout.Ticks >= MaxCaptureWaitTicks)
                    {
                        if (TryTimeoutHandoff(pipeline, key, scout, minCols: 1, out string captureReason))
                        {
                            ReleaseSlot(capi, i, renderer, pipeline, captureReason);
                            waitRetries.Remove(key);
                            continue;
                        }

                        waitRetries.TryGetValue(key, out int captureRetries);
                        waitRetries[key] = captureRetries + 1;
                        RequeuePending(pending, key);
                        ReleaseSlot(capi, i, renderer, pipeline, "captureStall");
                        continue;
                    }

                    LodScoutSeqDiag.LogPhase(i, scout, renderer, pipeline);
                    continue;
                }

                TryHandoffPaint(key, scout);
                ReleaseSlot(capi, i, renderer, pipeline, "painted");
                waitRetries.Remove(key);
                continue;
            }
        }

        liveCount = CountLive();
        return readyScratch;
    }

    public int HeldNearCount => heldNear.Count;
    public int HeldFarCount => heldFar.Count;

    public void CountLiveBands(out int nearLive, out int farLive) =>
        CountLiveMix(out nearLive, out farLive);

    void CountLiveMix(out int nearLive, out int farLive)
    {
        nearLive = 0;
        farLive = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            LodScoutEntity? scout = slots[i];
            if (scout is not { Live: true }) continue;
            if (scout.WaitForMesh) nearLive++;
            else farLive++;
        }
    }

    void FlushHeldToPending(Queue<long> pending)
    {
        while (heldNear.Count > 0)
            pending.Enqueue(heldNear.Dequeue());
        while (heldFar.Count > 0)
            pending.Enqueue(heldFar.Dequeue());
    }

    long? TakeNextPending(
        ICoreClientAPI capi,
        LodPipeline pipeline,
        Queue<long> pending,
        HashSet<long> completedKeys,
        double pickupX,
        double pickupZ,
        int nearWaitChunksLive,
        int farWaitChunksLive)
    {
        if (pending.Count == 0) return null;

        pendingScratch.Clear();
        while (pending.Count > 0)
            pendingScratch.Add(pending.Dequeue());
        if (pendingScratch.Count == 0) return null;

        int pick = SelectPendingIndex(
            capi, pipeline, pendingScratch, completedKeys, pickupX, pickupZ,
            nearWaitChunksLive, farWaitChunksLive);
        if (pick < 0)
        {
            for (int i = 0; i < pendingScratch.Count; i++)
                pending.Enqueue(pendingScratch[i]);
            return null;
        }

        long chosen = pendingScratch[pick];
        for (int i = 0; i < pendingScratch.Count; i++)
        {
            if (i == pick) continue;
            pending.Enqueue(pendingScratch[i]);
        }
        return chosen;
    }

    int SelectPendingIndex(
        ICoreClientAPI capi,
        LodPipeline pipeline,
        List<long> keys,
        HashSet<long> completedKeys,
        double pickupX,
        double pickupZ,
        int nearWaitChunksLive,
        int farWaitChunksLive)
    {
        int best = -1;
        int bestRetries = int.MaxValue;
        int bestResidentCols = -1;
        bool bestNear = false;
        int fallback = -1;

        for (int i = 0; i < keys.Count; i++)
        {
            long key = keys[i];
            if (completedKeys.Contains(key)) continue;
            waitRetries.TryGetValue(key, out int retries);
            bool near = VisitIsNear(capi, key, pickupX, pickupZ);
            int residentCols = ResidentCaptureCols(pipeline, key);

            if (!paintStarving)
            {
                if (nearWaitChunksLive >= MaxNearWaitChunksLive && near)
                {
                    if (fallback < 0) fallback = i;
                    continue;
                }

                if (farWaitChunksLive >= MaxFarWaitChunksLive && !near)
                {
                    if (fallback < 0) fallback = i;
                    continue;
                }
            }

            if (retries >= MaxWaitKeyedRetries && residentCols < ResidentFastHandoffCols)
            {
                if (fallback < 0) fallback = i;
                continue;
            }

            if (best < 0
                || residentCols > bestResidentCols
                || (residentCols == bestResidentCols && retries < bestRetries)
                || (residentCols == bestResidentCols && retries == bestRetries && near && !bestNear))
            {
                best = i;
                bestRetries = retries;
                bestResidentCols = residentCols;
                bestNear = near;
            }
        }

        return best >= 0 ? best : fallback;
    }

    static void RequeuePending(Queue<long> pending, long key) => pending.Enqueue(key);

    static int ResidentCaptureCols(LodPipeline pipeline, long key)
    {
        if (!pipeline.World.HasDataSet.Contains(key)) return 0;
        if (!TryGetSection(pipeline, key, out LodSection? section) || section == null) return 0;
        return section.CapturedColumns;
    }

    int PartialCaptureMin(int waitTicks)
    {
        if (paintStarving || waitTicks >= 16) return 1;
        if (waitTicks >= 12) return 4;
        if (waitTicks >= 8) return 16;
        return ResidentFastHandoffCols;
    }

    bool CaptureReady(LodPipeline pipeline, long key, int ticksInCapture)
    {
        if (paintStarving && ticksInCapture >= 1 && HasPartialCapture(pipeline, key, 1))
            return true;
        if (pipeline.IsL0SectionCaptureIdle(key) && HasPartialCapture(pipeline, key, 1))
            return true;
        if (ticksInCapture >= 2 && HasPartialCapture(pipeline, key, 16))
            return true;
        if (ticksInCapture >= 4 && HasPartialCapture(pipeline, key, 4))
            return true;
        if (ticksInCapture >= MaxCaptureWaitTicks)
            return HasPartialCapture(pipeline, key, 1);
        return false;
    }

    bool TryResidentPaintHandoff(
        ICoreClientAPI capi,
        LodPipeline pipeline,
        long key,
        LodScoutEntity scout,
        out string reason)
    {
        reason = "";
        if (!pipeline.World.HasDataSet.Contains(key)) return false;
        if (!TryGetSection(pipeline, key, out LodSection? section) || section == null) return false;

        if (section.CapturedColumns >= ResidentFastHandoffCols
            && TryHandoffPaint(key, scout))
        {
            reason = "residentPaint";
            return true;
        }

        if (paintStarving
            && section.CapturedColumns >= 1
            && (LodLoginSweep.AnyMapChunksLoaded(capi.World.BlockAccessor, key) || scout.Ticks >= 2)
            && TryHandoffPaint(key, scout))
        {
            reason = "residentStarve";
            return true;
        }

        return false;
    }

    static bool HasPartialCapture(LodPipeline pipeline, long key, int minCols)
    {
        if (!TryGetSection(pipeline, key, out LodSection? section) || section == null)
            return false;
        return section.CapturedColumns >= minCols;
    }

    static bool TryGetSection(LodPipeline pipeline, long key, out LodSection? section)
    {
        if (pipeline.World.Sections.TryGetValue(key, out section) && section != null)
            return true;
        section = pipeline.World.LoadFromStore?.Invoke(key);
        if (section == null) return false;
        pipeline.World.InstallLoaded(key, section);
        return true;
    }

    bool TryTimeoutHandoff(
        LodPipeline pipeline,
        long key,
        LodScoutEntity scout,
        int minCols,
        out string reason)
    {
        reason = "";
        if (HasPartialCapture(pipeline, key, minCols) && TryHandoffPaint(key, scout))
        {
            reason = minCols <= 16 ? "waitExpirePaint" : "partialPaint";
            return true;
        }

        waitRetries.TryGetValue(key, out int retries);
        if (retries >= MaxWaitKeyedRetries
            && TryGetSection(pipeline, key, out LodSection? section)
            && section != null
            && section.CapturedColumns > 0
            && TryHandoffPaint(key, scout))
        {
            reason = "waitExpirePaint";
            return true;
        }

        return false;
    }

    int CountPhase(LodScoutEntity.Phase phase)
    {
        int n = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            LodScoutEntity? scout = slots[i];
            if (scout is { Live: true } && scout.Current == phase) n++;
        }
        return n;
    }

    int CountNearWaitChunks()
    {
        int n = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            LodScoutEntity? scout = slots[i];
            if (scout is { Live: true, WaitForMesh: true, Current: LodScoutEntity.Phase.WaitChunks })
                n++;
        }
        return n;
    }

    int CountFarWaitChunks()
    {
        int n = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            LodScoutEntity? scout = slots[i];
            if (scout is { Live: true, WaitForMesh: false, Current: LodScoutEntity.Phase.WaitChunks })
                n++;
        }
        return n;
    }

    bool TryHandoffPaint(long key, LodScoutEntity scout)
    {
        if (scout.PaintQueued) return true;
        scout.PaintQueued = true;
        readyScratch.Add(key);
        LastFinishedKey = key;
        FinishedThisTick++;
        return true;
    }

    static bool VisitIsNear(
        ICoreClientAPI capi,
        long key,
        double pickupX,
        double pickupZ)
    {
        var (x, _, z) = LodLoginSweep.VisitPosition(capi.World, key);
        double dx = x - pickupX;
        double dz = z - pickupZ;
        return dx * dx + dz * dz
            <= LodLoginBake.SpawnSolidRadiusBlocks * LodLoginBake.SpawnSolidRadiusBlocks;
    }

    static void HoldViewer(LodScoutEntity scout)
    {
        LodScoutViewerEntity? viewer = scout.Viewer;
        if (viewer == null) return;
        viewer.ServerPos.SetPos(scout.X, scout.Y, scout.Z);
        viewer.Pos.SetFrom(viewer.ServerPos);
        viewer.Pos.Motion.Set(0, 0, 0);
        viewer.ServerPos.Motion.Set(0, 0, 0);
        viewer.IsRendered = false;
        viewer.AlwaysActive = true;
        scout.PartitionTicks++;
        if (scout.PartitionTicks % PartitioningEveryTicks == 0)
            LodVsCompat.TryUpdatePartitioning(viewer);
    }

    static int ClampReveal(
        LodScoutEntity scout,
        double pickupX,
        double pickupZ,
        int onsetChunks,
        int targetCap,
        bool farRing)
    {
        double dx = scout.X - pickupX;
        double dz = scout.Z - pickupZ;
        int distChunks = (int)Math.Ceiling(Math.Sqrt(dx * dx + dz * dz) / GlobalConstants.ChunkSize);
        int room = Math.Max(ChunkVisibleRadius, onsetChunks - distChunks);
        int cap = Math.Min(targetCap, farRing ? FarRevealChunks : NearRevealChunks);
        return Math.Min(cap, room);
    }

    static void GrowReveal(
        ICoreClientAPI capi,
        LodScoutEntity scout,
        int dim,
        int target)
    {
        if (scout.RevealRadius >= target)
            return;

        int before = scout.RevealRadius;
        scout.RevealRadius = Math.Min(target, scout.RevealRadius + RevealGrowPerTick);
        LodLoginBakePlayerMove.RequestChunkColumnRing(
            capi, scout.X, scout.Z, dim, before, scout.RevealRadius);
    }

    void StartSlot(
        ICoreClientAPI capi,
        int index,
        long key,
        double pickupX,
        double pickupZ,
        int onsetChunks,
        int targetCap)
    {
        var (x, y, z) = LodLoginSweep.VisitPosition(capi.World, key);
        double dx = x - pickupX;
        double dz = z - pickupZ;
        bool insideSpawnDisk = dx * dx + dz * dz
            <= LodLoginBake.SpawnSolidRadiusBlocks * LodLoginBake.SpawnSolidRadiusBlocks;
        var scout = new LodScoutEntity(key)
        {
            X = x,
            Y = y,
            Z = z,
            Cx = (int)Math.Floor(x / GlobalConstants.ChunkSize),
            Cz = (int)Math.Floor(z / GlobalConstants.ChunkSize),
            RunSpawnDiskSweep = insideSpawnDisk,
            WaitForMesh = insideSpawnDisk,
        };

        try { scout.Viewer = LodScoutViewerEntity.SpawnAt(capi, key, x, y, z); }
        catch { scout.Viewer = null; }

        slots[index] = scout;
        liveCount = CountLive();
        int dim = capi.World.Player.Entity.Pos.Dimension;
        bool farRing = !insideSpawnDisk;
        int radius = Math.Min(
            farRing ? FarRevealChunks : NearRevealChunks,
            ClampReveal(scout, pickupX, pickupZ, onsetChunks, targetCap, farRing));
        scout.HoldRadius = radius;
        LodLoginBakePlayerMove.RequestChunkColumnsVisible(capi, x, z, dim, ChunkVisibleRadius);
        LodScoutHostSystem.ClientInstance?.RequestUp(key, scout.Cx, scout.Cz, radius, dim, x, y, z);
        LodScoutSeqDiag.LogSpawn(index, key, !farRing, scout.RunSpawnDiskSweep, x, y, z, radius);
        LodScoutSeqDiag.LogPhase(index, scout, null, null, forceTransition: true);
    }

    void ReleaseSlot(
        ICoreClientAPI capi,
        int index,
        LodTerrainRenderer renderer,
        LodPipeline pipeline,
        string reason)
    {
        LodScoutEntity? scout = slots[index];
        if (scout == null) return;
        bool near = scout.WaitForMesh;
        int ticks = scout.Ticks;
        long key = scout.Key;
        LodScoutSeqDiag.LogRelease(index, key, reason, ticks, near, scout.WaitForMesh, renderer, pipeline);
        LodScoutHostSystem.ClientInstance?.RequestDown(scout.Key);
        LodScoutViewerEntity.DespawnOne(capi.World, scout.Viewer);
        scout.Viewer = null;
        scout.Live = false;
        scout.Current = LodScoutEntity.Phase.Done;
        slots[index] = null;
    }

    void DespawnLive(ICoreClientAPI? capi, string reason)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            LodScoutEntity? scout = slots[i];
            if (scout == null) continue;
            LodScoutSeqDiag.LogRelease(
                i, scout.Key, reason, scout.Ticks, scout.WaitForMesh, scout.WaitForMesh,
                null, null);
            LodScoutHostSystem.ClientInstance?.RequestDown(scout.Key);
            if (capi != null)
                LodScoutViewerEntity.DespawnOne(capi.World, scout.Viewer);
            scout.Viewer = null;
            scout.Live = false;
            slots[i] = null;
        }
    }

    int CountLive()
    {
        int n = 0;
        for (int i = 0; i < slots.Length; i++)
            if (slots[i] is { Live: true }) n++;
        return n;
    }
}
