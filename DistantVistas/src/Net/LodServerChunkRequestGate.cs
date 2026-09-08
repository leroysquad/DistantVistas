namespace DistantVistas;

internal enum LodServerQueueDecision : byte
{
    Enqueued,
    Coalesced,
    Dropped,
}

internal enum LodServerChunkWorkPriority : byte
{
    Background = 0,
    Login = 1,
}

internal readonly record struct LodServerChunkColumn(int Cx, int Cz, int Dimension);

internal readonly record struct LodServerForceSendColumn(
    string PlayerUid,
    int Cx,
    int Cz,
    int Dimension);

internal readonly record struct LodServerPriorityStart(
    LodServerChunkColumn Column,
    bool KeepLoaded,
    int SubmissionGeneration);

/// <summary>
/// Pure admission state for every Distant Vistas server chunk request.
/// Pending work is bounded and coalesced; priority-load credits remain held
/// until the engine's OnLoaded callback settles them.
/// </summary>
internal sealed class LodServerChunkRequestGate
{
    sealed class PriorityState
    {
        public readonly Dictionary<string, Action?> Owners = new(StringComparer.Ordinal);
        public LodServerChunkWorkPriority Priority;
        public bool KeepLoaded;
        public bool InFlight;
        public long StartedMs;
        public int SubmissionGeneration;
    }

    sealed class ForceSendState
    {
        public readonly HashSet<string> Owners = new(StringComparer.Ordinal);
        public LodServerChunkWorkPriority Priority;
    }

    readonly Dictionary<LodServerChunkColumn, PriorityState> priority = new();
    readonly Queue<LodServerChunkColumn> loginPriorityOrder = new();
    readonly Queue<LodServerChunkColumn> backgroundPriorityOrder = new();
    readonly Dictionary<LodServerForceSendColumn, ForceSendState> forceSend = new();
    readonly Queue<LodServerForceSendColumn> loginForceSendOrder = new();
    readonly Queue<LodServerForceSendColumn> backgroundForceSendOrder = new();
    readonly List<LodServerChunkColumn> priorityRemoveScratch = new();
    readonly List<LodServerForceSendColumn> forceRemoveScratch = new();

    readonly int maxPriorityInFlight;
    readonly int maxPriorityStartsPerTick;
    readonly int maxForceSendsPerTick;
    readonly int maxPendingPriority;
    readonly int maxPendingForceSend;
    readonly int pressureEnterTicks;
    readonly int pressureExitTicks;
    readonly long staleInFlightMs;

    int priorityStartsThisTick;
    int forceSendsThisTick;
    int pressureTicks;
    int quietTicks;
    int nextSubmissionGeneration = 1;

    internal LodServerChunkRequestGate(
        int maxPriorityInFlight,
        int maxPriorityStartsPerTick,
        int maxForceSendsPerTick,
        int maxPendingPriority,
        int maxPendingForceSend,
        int pressureEnterTicks = 3,
        int pressureExitTicks = 3,
        long staleInFlightMs = 5000)
    {
        if (maxPriorityInFlight < 1)
            throw new ArgumentOutOfRangeException(nameof(maxPriorityInFlight));
        if (maxPriorityStartsPerTick < 1)
            throw new ArgumentOutOfRangeException(nameof(maxPriorityStartsPerTick));
        if (maxForceSendsPerTick < 1)
            throw new ArgumentOutOfRangeException(nameof(maxForceSendsPerTick));
        if (maxPendingPriority < maxPriorityInFlight)
            throw new ArgumentOutOfRangeException(nameof(maxPendingPriority));
        if (maxPendingForceSend < 1)
            throw new ArgumentOutOfRangeException(nameof(maxPendingForceSend));
        if (pressureEnterTicks < 1)
            throw new ArgumentOutOfRangeException(nameof(pressureEnterTicks));
        if (pressureExitTicks < 1)
            throw new ArgumentOutOfRangeException(nameof(pressureExitTicks));
        if (staleInFlightMs < 1)
            throw new ArgumentOutOfRangeException(nameof(staleInFlightMs));

        this.maxPriorityInFlight = maxPriorityInFlight;
        this.maxPriorityStartsPerTick = maxPriorityStartsPerTick;
        this.maxForceSendsPerTick = maxForceSendsPerTick;
        this.maxPendingPriority = maxPendingPriority;
        this.maxPendingForceSend = maxPendingForceSend;
        this.pressureEnterTicks = pressureEnterTicks;
        this.pressureExitTicks = pressureExitTicks;
        this.staleInFlightMs = staleInFlightMs;
    }

    internal int PriorityPending { get; private set; }
    internal int PriorityInFlight { get; private set; }
    internal int ForceSendPending { get; private set; }
    internal int PriorityPendingPeak { get; private set; }
    internal int PriorityInFlightPeak { get; private set; }
    internal int ForceSendPendingPeak { get; private set; }
    internal bool PressureActive { get; private set; }
    internal long OldestInFlightAgeMs { get; private set; }
    internal long PriorityCoalesced { get; private set; }
    internal long PriorityDropped { get; private set; }
    internal long PriorityEnqueued { get; private set; }
    internal long PriorityStarted { get; private set; }
    internal long PriorityCompleted { get; private set; }
    internal long PriorityStaleReclaimed { get; private set; }
    internal long ForceSendCoalesced { get; private set; }
    internal long ForceSendDropped { get; private set; }
    internal long ForceSendEnqueued { get; private set; }
    internal long ForceSendStarted { get; private set; }
    internal long Cancelled { get; private set; }

    internal void BeginTick(long nowMs)
    {
        _ = nowMs;
        priorityStartsThisTick = 0;
        forceSendsThisTick = 0;
    }

    internal void EndTick(long nowMs)
    {
        OldestInFlightAgeMs = OldestAge(nowMs);
        bool unhealthy =
            (PriorityInFlight >= maxPriorityInFlight && PriorityPending > 0)
            || PriorityPending >= Math.Max(1, maxPendingPriority * 3 / 4)
            || (PriorityPending > 0 && OldestInFlightAgeMs >= staleInFlightMs);

        if (unhealthy)
        {
            pressureTicks++;
            quietTicks = 0;
            if (pressureTicks >= pressureEnterTicks)
                PressureActive = true;
            return;
        }

        pressureTicks = 0;
        if (!PressureActive)
        {
            quietTicks = 0;
            return;
        }

        quietTicks++;
        if (quietTicks >= pressureExitTicks)
        {
            PressureActive = false;
            quietTicks = 0;
        }
    }

    internal LodServerQueueDecision QueuePriority(
        string owner,
        LodServerChunkColumn column,
        bool keepLoaded,
        Action? onLoaded,
        LodServerChunkWorkPriority workPriority)
    {
        if (string.IsNullOrWhiteSpace(owner) || column.Cx < 0 || column.Cz < 0)
        {
            PriorityDropped++;
            return LodServerQueueDecision.Dropped;
        }

        if (priority.TryGetValue(column, out PriorityState? existing))
        {
            existing.Owners[owner] = onLoaded;
            existing.KeepLoaded |= keepLoaded;
            if (!existing.InFlight && workPriority > existing.Priority)
            {
                existing.Priority = workPriority;
                loginPriorityOrder.Enqueue(column);
            }
            PriorityCoalesced++;
            return LodServerQueueDecision.Coalesced;
        }

        if (PriorityPending >= maxPendingPriority)
        {
            if (workPriority != LodServerChunkWorkPriority.Login
                || !TryDropBackgroundPriority())
            {
                PriorityDropped++;
                return LodServerQueueDecision.Dropped;
            }
        }

        var state = new PriorityState
        {
            Priority = workPriority,
            KeepLoaded = keepLoaded,
        };
        state.Owners[owner] = onLoaded;
        priority[column] = state;
        PriorityPending++;
        PriorityPendingPeak = Math.Max(PriorityPendingPeak, PriorityPending);
        PriorityEnqueued++;
        PriorityQueue(workPriority).Enqueue(column);
        return LodServerQueueDecision.Enqueued;
    }

    internal bool TryStartPriority(
        long nowMs,
        out LodServerPriorityStart start,
        bool allowBackground = true)
    {
        start = default;
        if (priorityStartsThisTick >= maxPriorityStartsPerTick
            || PriorityInFlight >= maxPriorityInFlight)
            return false;

        while (TryDequeuePriority(allowBackground, out LodServerChunkColumn column))
        {
            if (!priority.TryGetValue(column, out PriorityState? state)
                || state.InFlight
                || state.Owners.Count == 0)
                continue;

            state.InFlight = true;
            state.StartedMs = nowMs;
            state.SubmissionGeneration = nextSubmissionGeneration++;
            PriorityPending--;
            PriorityInFlight++;
            PriorityInFlightPeak = Math.Max(PriorityInFlightPeak, PriorityInFlight);
            priorityStartsThisTick++;
            PriorityStarted++;
            start = new LodServerPriorityStart(
                column, state.KeepLoaded, state.SubmissionGeneration);
            return true;
        }

        return false;
    }

    internal bool CompletePriority(
        LodServerChunkColumn column,
        out IReadOnlyList<Action> callbacks)
    {
        return CompletePriority(
            column, expectedGeneration: null, out callbacks, out _, out _);
    }

    internal bool CompletePriority(
        LodServerChunkColumn column,
        out IReadOnlyList<Action> callbacks,
        out bool stillWanted,
        out bool keepLoaded)
    {
        return CompletePriority(
            column, expectedGeneration: null, out callbacks, out stillWanted, out keepLoaded);
    }

    internal bool CompletePriority(
        LodServerChunkColumn column,
        int submissionGeneration,
        out IReadOnlyList<Action> callbacks,
        out bool stillWanted,
        out bool keepLoaded)
    {
        return CompletePriority(
            column, expectedGeneration: submissionGeneration,
            out callbacks, out stillWanted, out keepLoaded);
    }

    internal bool CompletePriority(
        LodServerChunkColumn column,
        int? expectedGeneration,
        out IReadOnlyList<Action> callbacks,
        out bool stillWanted,
        out bool keepLoaded)
    {
        callbacks = Array.Empty<Action>();
        stillWanted = false;
        keepLoaded = false;
        if (!priority.TryGetValue(column, out PriorityState? state)
            || !state.InFlight)
            return false;
        if (expectedGeneration.HasValue
            && state.SubmissionGeneration != expectedGeneration.Value)
            return false;

        priority.Remove(column);
        PriorityInFlight--;
        PriorityCompleted++;
        stillWanted = state.Owners.Count > 0;
        keepLoaded = state.KeepLoaded;
        if (state.Owners.Count == 0)
            return true;

        var live = new List<Action>(state.Owners.Count);
        foreach (Action? callback in state.Owners.Values)
        {
            if (callback != null)
                live.Add(callback);
        }
        callbacks = live;
        return true;
    }

    /// <summary>
    /// Free credits whose OnLoaded never arrived. Completes each stale flight once,
    /// invalidates late callbacks via a new submission generation on requeue, and
    /// does not invoke owner callbacks (the column may still be cold).
    /// </summary>
    internal int ReclaimStaleInFlight(long nowMs)
    {
        priorityRemoveScratch.Clear();
        foreach (KeyValuePair<LodServerChunkColumn, PriorityState> pair in priority)
        {
            if (!pair.Value.InFlight)
                continue;
            if (nowMs - pair.Value.StartedMs < staleInFlightMs)
                continue;
            priorityRemoveScratch.Add(pair.Key);
        }

        int reclaimed = 0;
        for (int i = 0; i < priorityRemoveScratch.Count; i++)
        {
            LodServerChunkColumn column = priorityRemoveScratch[i];
            if (!priority.TryGetValue(column, out PriorityState? state) || !state.InFlight)
                continue;

            int generation = state.SubmissionGeneration;
            bool keepLoaded = state.KeepLoaded;
            LodServerChunkWorkPriority workPriority = state.Priority;
            var owners = new List<KeyValuePair<string, Action?>>(state.Owners.Count);
            foreach (KeyValuePair<string, Action?> owner in state.Owners)
                owners.Add(owner);

            if (!CompletePriority(
                    column, generation, out _, out _, out _))
                continue;

            PriorityStaleReclaimed++;
            reclaimed++;

            if (owners.Count == 0)
                continue;

            // Force-requeue past the pending cap: InFlight converted back to Pending
            // must not be dropped or a still-wanted hold column is lost forever.
            if (priority.TryGetValue(column, out PriorityState? existing))
            {
                for (int o = 0; o < owners.Count; o++)
                {
                    KeyValuePair<string, Action?> owner = owners[o];
                    existing.Owners[owner.Key] = owner.Value;
                }
                existing.KeepLoaded |= keepLoaded;
                if (!existing.InFlight && workPriority > existing.Priority)
                {
                    existing.Priority = workPriority;
                    loginPriorityOrder.Enqueue(column);
                }
                PriorityCoalesced++;
                continue;
            }

            var restored = new PriorityState
            {
                Priority = workPriority,
                KeepLoaded = keepLoaded,
            };
            for (int o = 0; o < owners.Count; o++)
            {
                KeyValuePair<string, Action?> owner = owners[o];
                restored.Owners[owner.Key] = owner.Value;
            }
            priority[column] = restored;
            PriorityPending++;
            PriorityPendingPeak = Math.Max(PriorityPendingPeak, PriorityPending);
            PriorityEnqueued++;
            PriorityQueue(workPriority).Enqueue(column);
        }
        priorityRemoveScratch.Clear();
        return reclaimed;
    }

    internal LodServerQueueDecision QueueForceSend(
        string owner,
        LodServerForceSendColumn column,
        LodServerChunkWorkPriority workPriority)
    {
        if (string.IsNullOrWhiteSpace(owner)
            || string.IsNullOrWhiteSpace(column.PlayerUid)
            || column.Cx < 0
            || column.Cz < 0)
        {
            ForceSendDropped++;
            return LodServerQueueDecision.Dropped;
        }

        if (forceSend.TryGetValue(column, out ForceSendState? existing))
        {
            existing.Owners.Add(owner);
            if (workPriority > existing.Priority)
            {
                existing.Priority = workPriority;
                loginForceSendOrder.Enqueue(column);
            }
            ForceSendCoalesced++;
            return LodServerQueueDecision.Coalesced;
        }

        if (ForceSendPending >= maxPendingForceSend)
        {
            if (workPriority != LodServerChunkWorkPriority.Login
                || !TryDropBackgroundForceSend())
            {
                ForceSendDropped++;
                return LodServerQueueDecision.Dropped;
            }
        }

        var state = new ForceSendState { Priority = workPriority };
        state.Owners.Add(owner);
        forceSend[column] = state;
        ForceSendPending++;
        ForceSendPendingPeak = Math.Max(ForceSendPendingPeak, ForceSendPending);
        ForceSendEnqueued++;
        ForceQueue(workPriority).Enqueue(column);
        return LodServerQueueDecision.Enqueued;
    }

    internal bool TryStartForceSend(
        out LodServerForceSendColumn column,
        bool allowBackground = true)
    {
        column = default;
        if (forceSendsThisTick >= maxForceSendsPerTick)
            return false;

        while (TryDequeueForceSend(allowBackground, out LodServerForceSendColumn candidate))
        {
            if (!forceSend.Remove(candidate, out ForceSendState? state)
                || state.Owners.Count == 0)
                continue;

            ForceSendPending--;
            forceSendsThisTick++;
            ForceSendStarted++;
            column = candidate;
            return true;
        }

        return false;
    }

    internal void CancelOwner(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner))
            return;

        priorityRemoveScratch.Clear();
        foreach (KeyValuePair<LodServerChunkColumn, PriorityState> pair in priority)
        {
            if (!pair.Value.Owners.Remove(owner))
                continue;

            Cancelled++;
            if (!pair.Value.InFlight && pair.Value.Owners.Count == 0)
                priorityRemoveScratch.Add(pair.Key);
        }
        foreach (LodServerChunkColumn column in priorityRemoveScratch)
        {
            if (priority.Remove(column))
                PriorityPending--;
        }
        priorityRemoveScratch.Clear();

        forceRemoveScratch.Clear();
        foreach (KeyValuePair<LodServerForceSendColumn, ForceSendState> pair in forceSend)
        {
            if (!pair.Value.Owners.Remove(owner))
                continue;

            Cancelled++;
            if (pair.Value.Owners.Count == 0)
                forceRemoveScratch.Add(pair.Key);
        }
        foreach (LodServerForceSendColumn column in forceRemoveScratch)
        {
            if (forceSend.Remove(column))
                ForceSendPending--;
        }
        forceRemoveScratch.Clear();
    }

    internal void Clear()
    {
        priority.Clear();
        loginPriorityOrder.Clear();
        backgroundPriorityOrder.Clear();
        forceSend.Clear();
        loginForceSendOrder.Clear();
        backgroundForceSendOrder.Clear();
        priorityRemoveScratch.Clear();
        forceRemoveScratch.Clear();
        PriorityPending = 0;
        PriorityInFlight = 0;
        ForceSendPending = 0;
        nextSubmissionGeneration = 1;
        PressureActive = false;
        OldestInFlightAgeMs = 0;
        pressureTicks = 0;
        quietTicks = 0;
        priorityStartsThisTick = 0;
        forceSendsThisTick = 0;
    }

    Queue<LodServerChunkColumn> PriorityQueue(LodServerChunkWorkPriority workPriority) =>
        workPriority == LodServerChunkWorkPriority.Login
            ? loginPriorityOrder
            : backgroundPriorityOrder;

    Queue<LodServerForceSendColumn> ForceQueue(LodServerChunkWorkPriority workPriority) =>
        workPriority == LodServerChunkWorkPriority.Login
            ? loginForceSendOrder
            : backgroundForceSendOrder;

    bool TryDequeuePriority(bool allowBackground, out LodServerChunkColumn column)
    {
        if (TryDequeuePriority(loginPriorityOrder, LodServerChunkWorkPriority.Login, out column))
            return true;
        if (!allowBackground)
        {
            column = default;
            return false;
        }
        return TryDequeuePriority(
            backgroundPriorityOrder, LodServerChunkWorkPriority.Background, out column);
    }

    bool TryDequeuePriority(
        Queue<LodServerChunkColumn> queue,
        LodServerChunkWorkPriority expectedPriority,
        out LodServerChunkColumn column)
    {
        while (queue.Count > 0)
        {
            LodServerChunkColumn candidate = queue.Dequeue();
            if (priority.TryGetValue(candidate, out PriorityState? state)
                && !state.InFlight
                && state.Priority == expectedPriority)
            {
                column = candidate;
                return true;
            }
        }

        column = default;
        return false;
    }

    bool TryDequeueForceSend(bool allowBackground, out LodServerForceSendColumn column)
    {
        if (TryDequeueForceSend(
                loginForceSendOrder, LodServerChunkWorkPriority.Login, out column))
            return true;
        if (!allowBackground)
        {
            column = default;
            return false;
        }
        return TryDequeueForceSend(
            backgroundForceSendOrder, LodServerChunkWorkPriority.Background, out column);
    }

    bool TryDequeueForceSend(
        Queue<LodServerForceSendColumn> queue,
        LodServerChunkWorkPriority expectedPriority,
        out LodServerForceSendColumn column)
    {
        while (queue.Count > 0)
        {
            LodServerForceSendColumn candidate = queue.Dequeue();
            if (forceSend.TryGetValue(candidate, out ForceSendState? state)
                && state.Priority == expectedPriority)
            {
                column = candidate;
                return true;
            }
        }

        column = default;
        return false;
    }

    bool TryDropBackgroundPriority()
    {
        while (backgroundPriorityOrder.Count > 0)
        {
            LodServerChunkColumn column = backgroundPriorityOrder.Dequeue();
            if (!priority.TryGetValue(column, out PriorityState? state)
                || state.InFlight
                || state.Priority != LodServerChunkWorkPriority.Background)
                continue;

            priority.Remove(column);
            PriorityPending--;
            PriorityDropped++;
            return true;
        }
        return false;
    }

    bool TryDropBackgroundForceSend()
    {
        while (backgroundForceSendOrder.Count > 0)
        {
            LodServerForceSendColumn column = backgroundForceSendOrder.Dequeue();
            if (!forceSend.TryGetValue(column, out ForceSendState? state)
                || state.Priority != LodServerChunkWorkPriority.Background)
                continue;

            forceSend.Remove(column);
            ForceSendPending--;
            ForceSendDropped++;
            return true;
        }
        return false;
    }

    long OldestAge(long nowMs)
    {
        long oldest = 0;
        foreach (PriorityState state in priority.Values)
        {
            if (!state.InFlight)
                continue;
            oldest = Math.Max(oldest, Math.Max(0, nowMs - state.StartedMs));
        }
        return oldest;
    }
}
