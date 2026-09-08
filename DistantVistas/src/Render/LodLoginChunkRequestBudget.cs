using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DistantVistas;

public enum LodChunkRequestPriority : byte
{
    Background = 0,
    Normal = 1,
    Critical = 2,
}

/// <summary>
/// Coordinates overlay <see cref="IClientWorldAccessor.SetChunkColumnVisible"/> calls
/// so login stream growth and scout rings cannot flood the server
/// RequestChunkColumns FIFO (1.0.44 autosave death: chunkdbthread never paused).
///
/// This is deliberately a session coordinator rather than a call ceiling. Every
/// producer shares one budget, accepted columns are deduplicated, partially issued
/// shells resume at their cursor, and a column is retried only after an increasing
/// cooldown. A request is not treated as proof that the column arrived; the cooldown
/// only limits another producer asking for the same work immediately.
/// </summary>
public static class LodLoginChunkRequestBudget
{
    /// <summary>
    /// Conservative aggregate cap for all overlay visibility producers. This is
    /// intentionally below the old 96-call ceiling because the server queue is
    /// shared with view-distance and scout-host work.
    /// </summary>
    public const int MaxVisiblePerTick = 8;
    public const int VisibleRequestsPerSecond = 16;
    public const int BackgroundVisibleBurst = 2;
    public const int BackgroundVisibleRequestsPerSecond = 4;
    public const int MaxTrackedColumns = 8192;

    static bool overlayTick;
    static bool overlaySession;
    static bool requestSession;
    const int MaxPendingSequences = 64;
    const int MaxBackgroundSequences = 48;
    const int MaxNormalAndBackgroundSequences = 56;
    const int SequenceQuantum = 4;

    static readonly LodLoginChunkRequestCoordinator coordinator =
        new(MaxVisiblePerTick, VisibleRequestsPerSecond, MaxTrackedColumns);
    static readonly Queue<SequenceKey> pendingCriticalOrder = new();
    static readonly Queue<SequenceKey> pendingNormalOrder = new();
    static readonly Queue<SequenceKey> pendingBackgroundOrder = new();
    static readonly Dictionary<SequenceKey, PendingSequence> pendingSequences = new();
    static readonly Dictionary<SequenceKey, long> sequenceRetryAtMs = new();
    static readonly List<SequenceKey> sequenceRetryRemoveScratch = new();
    static long sequenceDropped;
    const long SequenceRetryCooldownMs = 1250;
    const int MaxSequenceRetryEntries = MaxPendingSequences * 4;

    sealed class ProducerStats
    {
        public long Attempts;
        public long Issued;
        public long Duplicates;
        public long Deferred;
        public long Invalid;
        public long Resident;
    }

    static readonly Dictionary<string, ProducerStats> producerStats =
        new(StringComparer.Ordinal);

    readonly record struct SequenceKey(
        string Producer,
        int Cx,
        int Cz,
        int Radius,
        int Dimension,
        long Token);

    sealed class PendingSequence
    {
        readonly List<(int Cx, int Cz)>? columns;
        readonly LodLoginBakePlayerMove.ChunkRingCursor? ring;
        int index;

        public PendingSequence(
            SequenceKey key,
            LodLoginBakePlayerMove.ChunkRingCursor ring,
            LodChunkRequestPriority priority)
        {
            Key = key;
            Producer = key.Producer;
            this.ring = ring;
            Priority = priority;
        }

        public PendingSequence(
            SequenceKey key,
            List<(int Cx, int Cz)> columns,
            LodChunkRequestPriority priority)
        {
            Key = key;
            Producer = key.Producer;
            this.columns = columns;
            Priority = priority;
        }

        public SequenceKey Key { get; }
        public string Producer { get; }
        public LodChunkRequestPriority Priority { get; }
        public bool Complete =>
            ring?.Complete ?? (columns == null || index >= columns.Count);

        public bool TryNext(out int cx, out int cz)
        {
            if (ring != null)
                return ring.TryNext(out cx, out cz);

            while (columns != null && index < columns.Count)
            {
                (cx, cz) = columns[index];
                if (cx >= 0 && cz >= 0)
                    return true;
                index++;
            }

            cx = 0;
            cz = 0;
            return false;
        }

        public void CommitCurrent()
        {
            if (ring != null)
                ring.CommitCurrent();
            else
                index++;
        }
    }

    public static int IssuedThisTick => coordinator.IssuedThisTick;
    public static int DuplicateThisTick => coordinator.DuplicateThisTick;
    public static int DeferredThisTick => coordinator.DeferredThisTick;
    public static long Generation => coordinator.Generation;
    public static int RequestedColumnCount => coordinator.RequestedColumnCount;
    public static bool RequestPressureActive =>
        requestSession && coordinator.PressureActive;
    public static int PendingSequenceCount => pendingSequences.Count;
    public static long SequenceDropped => sequenceDropped;

    public static bool Exhausted =>
        overlayTick && coordinator.IssuedThisTick >= MaxVisiblePerTick;

    /// <summary>Starts a fresh overlay-generation coordinator.</summary>
    public static void BeginOverlaySession()
    {
        requestSession = true;
        overlaySession = true;
        overlayTick = false;
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        coordinator.ConfigureRateLimit(
            MaxVisiblePerTick, VisibleRequestsPerSecond, nowMs);
        coordinator.BeginGeneration(nowMs);
        ClearPendingOrder();
        pendingSequences.Clear();
        sequenceRetryAtMs.Clear();
        sequenceDropped = 0;
        producerStats.Clear();
    }

    /// <summary>Stops coordination and discards generation-local request state.</summary>
    public static void EndOverlaySession()
    {
        EndRequestSession();
    }

    /// <summary>
    /// Starts the bounded post-login request session. Frontier scouts use the same
    /// coordinator and per-tick budget as the login overlay, but keep their column
    /// cooldowns across game ticks instead of draining a whole shell immediately.
    /// </summary>
    public static void BeginBackgroundTick(IClientWorldAccessor world)
    {
        if (!requestSession)
        {
            requestSession = true;
            overlaySession = false;
            overlayTick = false;
            long sessionNowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            coordinator.ConfigureRateLimit(
                BackgroundVisibleBurst, BackgroundVisibleRequestsPerSecond, sessionNowMs);
            coordinator.BeginGeneration(sessionNowMs);
            ClearPendingOrder();
            pendingSequences.Clear();
            sequenceRetryAtMs.Clear();
            sequenceDropped = 0;
            producerStats.Clear();
        }
        else if (overlaySession)
        {
            TransitionToBackground();
        }

        overlayTick = true;
        coordinator.BeginTick();
        coordinator.SetExternalPressure(
            LodScoutHostSystem.ClientInstance?.HostPressureActive == true);
        PumpPending(world);
    }

    public static void EndBackgroundTick()
    {
        EndRequestTick();
    }

    /// <summary>Clears post-login request state when the world is left.</summary>
    public static void EndBackgroundSession()
    {
        if (requestSession)
            EndRequestSession();
    }

    /// <summary>
    /// Switches a successful login request generation to its lower background
    /// rate without replaying columns or discarding unfinished sequences.
    /// </summary>
    public static void TransitionToBackground()
    {
        if (!requestSession)
            return;

        EndRequestTick();
        overlaySession = false;
        coordinator.ConfigureRateLimit(
            BackgroundVisibleBurst,
            BackgroundVisibleRequestsPerSecond,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public static void BeginOverlayTick(IClientWorldAccessor? world = null)
    {
        if (!requestSession || !overlaySession)
            BeginOverlaySession();
        overlayTick = true;
        coordinator.BeginTick();
        coordinator.SetExternalPressure(
            LodScoutHostSystem.ClientInstance?.HostPressureActive == true);
        if (world != null)
            PumpPending(world);
    }

    public static void EndOverlayTick()
    {
        EndRequestTick();
    }

    public static bool TrySetVisible(
        IClientWorldAccessor world,
        int cx,
        int cz,
        int dimension,
        string producer = "unknown")
    {
        ProducerStats stats = GetStats(producer);
        stats.Attempts++;

        if (cx < 0 || cz < 0)
        {
            stats.Invalid++;
            return true;
        }

        try
        {
            if (world.BlockAccessor.GetMapChunk(cx, cz) != null)
            {
                coordinator.MarkResident(cx, cz, dimension);
                stats.Resident++;
                return true;
            }
        }
        catch
        {
            // An unreadable residency check is not proof that the column is absent.
            // Leave admission to the bounded coordinator.
        }

        if (!requestSession || !overlayTick)
        {
            stats.Deferred++;
            return false;
        }

        LodLoginChunkRequestDecision decision = coordinator.TryAcquire(
            cx,
            cz,
            dimension,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        switch (decision)
        {
            case LodLoginChunkRequestDecision.Invalid:
                stats.Invalid++;
                return true;
            case LodLoginChunkRequestDecision.Duplicate:
                stats.Duplicates++;
                return true;
            case LodLoginChunkRequestDecision.Deferred:
                stats.Deferred++;
                return false;
            case LodLoginChunkRequestDecision.Issue:
                world.SetChunkColumnVisible(cx, cz, dimension);
                stats.Issued++;
                return true;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    /// <summary>
    /// Queues one square visibility shell. The shell is drained over later
    /// overlay ticks instead of abandoning the remainder when this tick's
    /// aggregate budget is exhausted.
    /// </summary>
    public static void QueueVisibleSquare(
        IClientWorldAccessor world,
        int cx,
        int cz,
        int dimension,
        int radius,
        string producer,
        LodChunkRequestPriority priority = LodChunkRequestPriority.Normal)
    {
        var key = new SequenceKey(
            producer,
            cx,
            cz,
            Math.Max(0, radius),
            dimension,
            Token: 0);
        var ring = new LodLoginBakePlayerMove.ChunkRingCursor();
        ring.Configure(cx, cz, inner: -1, outer: Math.Max(0, radius));
        EnqueueSequence(world, key, new PendingSequence(key, ring, priority));
    }

    /// <summary>Queues a finite list of map columns under one resumable identity.</summary>
    public static void QueueVisibleColumns(
        IClientWorldAccessor world,
        IEnumerable<(int Cx, int Cz)> columns,
        int dimension,
        string producer,
        long sequenceId,
        LodChunkRequestPriority priority = LodChunkRequestPriority.Normal)
    {
        var key = new SequenceKey(
            producer,
            0,
            0,
            0,
            dimension,
            sequenceId);
        EnqueueSequence(
            world,
            key,
            new PendingSequence(key, new List<(int Cx, int Cz)>(columns), priority));
    }

    static void EnqueueSequence(
        IClientWorldAccessor world,
        SequenceKey key,
        PendingSequence sequence)
    {
        if (!requestSession)
        {
            requestSession = true;
            overlaySession = false;
            overlayTick = false;
            long sessionNowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            coordinator.ConfigureRateLimit(
                BackgroundVisibleBurst, BackgroundVisibleRequestsPerSecond, sessionNowMs);
            coordinator.BeginGeneration(sessionNowMs);
            ClearPendingOrder();
            pendingSequences.Clear();
            sequenceRetryAtMs.Clear();
            sequenceDropped = 0;
            producerStats.Clear();
        }

        if (pendingSequences.ContainsKey(key))
            return;

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        PruneSequenceRetryCooldowns(nowMs);
        if (sequenceRetryAtMs.TryGetValue(key, out long retryAtMs)
            && nowMs < retryAtMs)
            return;

        int priorityLimit = sequence.Priority switch
        {
            LodChunkRequestPriority.Background => MaxBackgroundSequences,
            LodChunkRequestPriority.Normal => MaxNormalAndBackgroundSequences,
            _ => MaxPendingSequences,
        };
        if (pendingSequences.Count >= priorityLimit)
        {
            if (sequence.Priority == LodChunkRequestPriority.Critical
                && TryDropOneBackground())
            {
                // Reserved critical work displaced one stale/far sequence.
            }
            else
            {
                sequenceDropped++;
                return;
            }
        }

        if (sequence.Complete)
        {
            MarkSequenceRetry(key, nowMs);
            return;
        }

        pendingSequences[key] = sequence;
        EnqueuePendingKey(key, sequence.Priority);
        if (overlayTick)
            PumpPending(world);
    }

    static void PumpPending(IClientWorldAccessor world)
    {
        while (overlayTick
            && !Exhausted
            && PendingOrderCount > 0)
        {
            int sequenceCount = PendingOrderCount;
            for (int i = 0; i < sequenceCount && !Exhausted; i++)
            {
                if (!TryDequeuePendingKey(out SequenceKey key))
                    break;
                if (!pendingSequences.TryGetValue(key, out PendingSequence? sequence))
                    continue;

                int work = 0;
                while (work < SequenceQuantum && !Exhausted)
                {
                    if (!sequence.TryNext(out int cx, out int cz))
                    {
                        pendingSequences.Remove(key);
                        MarkSequenceRetry(
                            key,
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                        break;
                    }

                    if (!TrySetVisible(world, cx, cz, key.Dimension, sequence.Producer))
                    {
                        EnqueuePendingKey(key, sequence.Priority);
                        return;
                    }

                    sequence.CommitCurrent();
                    work++;
                }

                if (!pendingSequences.ContainsKey(key))
                    continue;

                if (sequence.Complete)
                {
                    pendingSequences.Remove(key);
                    MarkSequenceRetry(
                        key,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                }
                else
                {
                    EnqueuePendingKey(key, sequence.Priority);
                }
            }
        }
    }

    /// <summary>
    /// Returns compact producer-attributed counters for the periodic scout snapshot.
    /// </summary>
    public static string TelemetryJson()
    {
        var sb = new StringBuilder(256);
        sb.Append("{\"generation\":").Append(coordinator.Generation)
            .Append(",\"sessionActive\":").Append(requestSession ? "true" : "false")
            .Append(",\"overlaySession\":").Append(overlaySession ? "true" : "false")
            .Append(",\"issuedThisTick\":").Append(coordinator.IssuedThisTick)
            .Append(",\"duplicateThisTick\":").Append(coordinator.DuplicateThisTick)
            .Append(",\"deferredThisTick\":").Append(coordinator.DeferredThisTick)
            .Append(",\"pressureStreak\":").Append(coordinator.PressureStreak)
            .Append(",\"pressureActive\":").Append(coordinator.PressureActive ? "true" : "false")
            .Append(",\"hostPressure\":")
            .Append(LodScoutHostSystem.ClientInstance?.HostPressureActive == true ? "true" : "false")
            .Append(",\"hostPriorityPending\":")
            .Append(LodScoutHostSystem.ClientInstance?.HostPriorityPending ?? 0)
            .Append(",\"hostPriorityInFlight\":")
            .Append(LodScoutHostSystem.ClientInstance?.HostPriorityInFlight ?? 0)
            .Append(",\"hostForceSendPending\":")
            .Append(LodScoutHostSystem.ClientInstance?.HostForceSendPending ?? 0)
            .Append(",\"hostOldestInFlightMs\":")
            .Append(LodScoutHostSystem.ClientInstance?.HostOldestInFlightMs ?? 0)
            .Append(",\"pendingSequences\":").Append(pendingSequences.Count)
            .Append(",\"pendingCritical\":").Append(pendingCriticalOrder.Count)
            .Append(",\"pendingNormal\":").Append(pendingNormalOrder.Count)
            .Append(",\"pendingBackground\":").Append(pendingBackgroundOrder.Count)
            .Append(",\"sequenceDropped\":").Append(sequenceDropped)
            .Append(",\"requestedColumns\":").Append(coordinator.RequestedColumnCount)
            .Append(",\"producers\":{");

        bool first = true;
        foreach (KeyValuePair<string, ProducerStats> pair in producerStats)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(pair.Key).Append("\":{")
                .Append("\"attempts\":").Append(pair.Value.Attempts)
                .Append(",\"issued\":").Append(pair.Value.Issued)
                .Append(",\"duplicates\":").Append(pair.Value.Duplicates)
                .Append(",\"deferred\":").Append(pair.Value.Deferred)
                .Append(",\"invalid\":").Append(pair.Value.Invalid)
                .Append(",\"resident\":").Append(pair.Value.Resident)
                .Append('}');
        }

        return sb.Append("}}").ToString();
    }

    static ProducerStats GetStats(string producer)
    {
        if (string.IsNullOrWhiteSpace(producer))
            producer = "unknown";
        if (!producerStats.TryGetValue(producer, out ProducerStats? stats))
        {
            stats = new ProducerStats();
            producerStats[producer] = stats;
        }
        return stats;
    }

    static void MarkSequenceRetry(SequenceKey key, long nowMs)
    {
        sequenceRetryAtMs[key] = nowMs + SequenceRetryCooldownMs;
        PruneSequenceRetryCooldowns(nowMs);
    }

    static int PendingOrderCount =>
        pendingCriticalOrder.Count + pendingNormalOrder.Count + pendingBackgroundOrder.Count;

    static void EnqueuePendingKey(SequenceKey key, LodChunkRequestPriority priority)
    {
        Queue<SequenceKey> queue = priority switch
        {
            LodChunkRequestPriority.Critical => pendingCriticalOrder,
            LodChunkRequestPriority.Normal => pendingNormalOrder,
            _ => pendingBackgroundOrder,
        };
        queue.Enqueue(key);
    }

    static bool TryDequeuePendingKey(out SequenceKey key)
    {
        if (pendingCriticalOrder.Count > 0)
        {
            key = pendingCriticalOrder.Dequeue();
            return true;
        }
        if (pendingNormalOrder.Count > 0)
        {
            key = pendingNormalOrder.Dequeue();
            return true;
        }
        if (pendingBackgroundOrder.Count > 0)
        {
            key = pendingBackgroundOrder.Dequeue();
            return true;
        }

        key = default;
        return false;
    }

    static bool TryDropOneBackground()
    {
        while (pendingBackgroundOrder.Count > 0)
        {
            SequenceKey key = pendingBackgroundOrder.Dequeue();
            if (pendingSequences.Remove(key))
            {
                sequenceDropped++;
                return true;
            }
        }
        return false;
    }

    static void ClearPendingOrder()
    {
        pendingCriticalOrder.Clear();
        pendingNormalOrder.Clear();
        pendingBackgroundOrder.Clear();
    }

    static void PruneSequenceRetryCooldowns(long nowMs)
    {
        if (sequenceRetryAtMs.Count == 0)
            return;

        sequenceRetryRemoveScratch.Clear();
        foreach (KeyValuePair<SequenceKey, long> pair in sequenceRetryAtMs)
        {
            if (pair.Value <= nowMs)
                sequenceRetryRemoveScratch.Add(pair.Key);
        }

        foreach (SequenceKey key in sequenceRetryRemoveScratch)
            sequenceRetryAtMs.Remove(key);

        sequenceRetryRemoveScratch.Clear();
        while (sequenceRetryAtMs.Count > MaxSequenceRetryEntries)
        {
            bool found = false;
            SequenceKey oldestKey = default;
            long oldestRetryAt = long.MaxValue;
            foreach (KeyValuePair<SequenceKey, long> pair in sequenceRetryAtMs)
            {
                if (pair.Value < oldestRetryAt)
                {
                    found = true;
                    oldestKey = pair.Key;
                    oldestRetryAt = pair.Value;
                }
            }

            if (!found)
                break;

            sequenceRetryAtMs.Remove(oldestKey);
        }
    }

    static void EndRequestTick()
    {
        if (overlayTick)
        {
            if (coordinator.IssuedThisTick >= MaxVisiblePerTick
                && pendingSequences.Count > 0)
                coordinator.NoteBudgetBacklog();
            coordinator.EndTick();
        }
        overlayTick = false;
    }

    static void EndRequestSession()
    {
        EndRequestTick();
        requestSession = false;
        overlaySession = false;
        coordinator.EndGeneration();
        ClearPendingOrder();
        pendingSequences.Clear();
        sequenceRetryAtMs.Clear();
        sequenceRetryRemoveScratch.Clear();
    }
}
