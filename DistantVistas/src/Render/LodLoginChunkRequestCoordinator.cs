namespace DistantVistas;

internal enum LodLoginChunkRequestDecision
{
    Invalid,
    Duplicate,
    Deferred,
    Issue,
}

/// <summary>
/// Pure state machine for overlay chunk-column visibility requests.
/// Producers share one per-tick issue budget, while columns retain retry
/// cooldown state for the current overlay generation.
/// </summary>
internal sealed class LodLoginChunkRequestCoordinator
{
    const long InitialRetryCooldownMs = 1250;
    const long RetryCooldownStepMs = 750;
    const long MaxRetryCooldownMs = 8000;
    const int PressureEnterTicks = 3;
    const int PressureExitTicks = 3;

    readonly record struct ColumnKey(int Cx, int Cz, int Dimension);

    sealed class ColumnState
    {
        public long NextRetryMs;
        public long LastTouchedMs;
        public int Attempts;
    }

    readonly Dictionary<ColumnKey, ColumnState> columns = new();
    readonly List<ColumnKey> pruneScratch = new();
    readonly int maxTrackedColumns;
    int maxBurst;
    double maxRequestsPerSecond;
    double availableTokens;
    long lastRefillMs;
    int quietTicks;
    bool budgetBacklog;
    bool externalPressure;
    bool localPressureActive;

    internal LodLoginChunkRequestCoordinator(
        int maxBurst,
        double maxRequestsPerSecond,
        int maxTrackedColumns = 8192)
    {
        if (maxBurst < 1)
            throw new ArgumentOutOfRangeException(nameof(maxBurst));
        if (maxRequestsPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRequestsPerSecond));
        if (maxTrackedColumns < maxBurst)
            throw new ArgumentOutOfRangeException(nameof(maxTrackedColumns));

        this.maxBurst = maxBurst;
        this.maxRequestsPerSecond = maxRequestsPerSecond;
        this.maxTrackedColumns = maxTrackedColumns;
        availableTokens = maxBurst;
    }

    internal int IssuedThisTick { get; private set; }
    internal int DuplicateThisTick { get; private set; }
    internal int DeferredThisTick { get; private set; }
    internal int RequestedColumnCount => columns.Count;
    internal bool PressureActive => externalPressure || localPressureActive;
    internal int PressureStreak { get; private set; }
    internal long Generation { get; private set; }

    internal void BeginGeneration(long nowMs = -1)
    {
        if (nowMs < 0)
            nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Generation++;
        columns.Clear();
        pruneScratch.Clear();
        availableTokens = maxBurst;
        lastRefillMs = nowMs;
        externalPressure = false;
        localPressureActive = false;
        PressureStreak = 0;
        quietTicks = 0;
        budgetBacklog = false;
        BeginTick();
    }

    internal void EndGeneration()
    {
        columns.Clear();
        IssuedThisTick = 0;
        DuplicateThisTick = 0;
        DeferredThisTick = 0;
        availableTokens = maxBurst;
        externalPressure = false;
        localPressureActive = false;
        PressureStreak = 0;
        quietTicks = 0;
        budgetBacklog = false;
        pruneScratch.Clear();
    }

    internal void BeginTick()
    {
        IssuedThisTick = 0;
        DuplicateThisTick = 0;
        DeferredThisTick = 0;
        budgetBacklog = false;
    }

    internal void NoteBudgetBacklog() => budgetBacklog = true;

    internal void SetExternalPressure(bool active)
    {
        externalPressure = active;
        if (active)
            quietTicks = 0;
    }

    internal void ConfigureRateLimit(
        int maxBurst,
        double maxRequestsPerSecond,
        long nowMs)
    {
        if (maxBurst < 1)
            throw new ArgumentOutOfRangeException(nameof(maxBurst));
        if (maxRequestsPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRequestsPerSecond));

        Refill(nowMs);
        this.maxBurst = maxBurst;
        this.maxRequestsPerSecond = maxRequestsPerSecond;
        availableTokens = Math.Min(availableTokens, maxBurst);
    }

    internal void MarkResident(int cx, int cz, int dimension)
    {
        if (cx < 0 || cz < 0)
            return;
        columns.Remove(new ColumnKey(cx, cz, dimension));
    }

    internal void EndTick()
    {
        if (DeferredThisTick > 0 || budgetBacklog || externalPressure)
        {
            PressureStreak++;
            quietTicks = 0;
            if (PressureStreak >= PressureEnterTicks)
                localPressureActive = true;
            return;
        }

        PressureStreak = 0;
        if (!localPressureActive)
        {
            quietTicks = 0;
            return;
        }

        quietTicks++;
        if (quietTicks >= PressureExitTicks)
        {
            localPressureActive = false;
            quietTicks = 0;
        }
    }

    internal LodLoginChunkRequestDecision TryAcquire(
        int cx,
        int cz,
        int dimension,
        long nowMs)
    {
        if (cx < 0 || cz < 0)
            return LodLoginChunkRequestDecision.Invalid;

        var key = new ColumnKey(cx, cz, dimension);
        if (columns.TryGetValue(key, out ColumnState? state)
            && nowMs < state.NextRetryMs)
        {
            state.LastTouchedMs = nowMs;
            DuplicateThisTick++;
            return LodLoginChunkRequestDecision.Duplicate;
        }

        if (externalPressure)
        {
            DeferredThisTick++;
            return LodLoginChunkRequestDecision.Deferred;
        }

        Refill(nowMs);
        if (availableTokens < 1d)
        {
            DeferredThisTick++;
            return LodLoginChunkRequestDecision.Deferred;
        }

        if (state == null && columns.Count >= maxTrackedColumns)
        {
            PruneExpired(nowMs);
            if (columns.Count >= maxTrackedColumns)
            {
                DeferredThisTick++;
                return LodLoginChunkRequestDecision.Deferred;
            }
        }

        availableTokens -= 1d;
        IssuedThisTick++;
        state ??= new ColumnState();
        state.Attempts++;
        long cooldown = Math.Min(
            MaxRetryCooldownMs,
            InitialRetryCooldownMs + (state.Attempts - 1) * RetryCooldownStepMs);
        state.NextRetryMs = nowMs + cooldown;
        state.LastTouchedMs = nowMs;
        columns[key] = state;
        return LodLoginChunkRequestDecision.Issue;
    }

    void Refill(long nowMs)
    {
        if (nowMs <= lastRefillMs)
            return;

        long elapsedMs = nowMs - lastRefillMs;
        availableTokens = Math.Min(
            maxBurst,
            availableTokens + elapsedMs * maxRequestsPerSecond / 1000d);
        lastRefillMs = nowMs;
    }

    void PruneExpired(long nowMs)
    {
        pruneScratch.Clear();
        foreach (KeyValuePair<ColumnKey, ColumnState> pair in columns)
        {
            if (pair.Value.NextRetryMs <= nowMs)
                pruneScratch.Add(pair.Key);
        }

        pruneScratch.Sort((a, b) =>
            columns[a].LastTouchedMs.CompareTo(columns[b].LastTouchedMs));
        for (int i = 0; i < pruneScratch.Count && columns.Count >= maxTrackedColumns; i++)
            columns.Remove(pruneScratch[i]);
        pruneScratch.Clear();
    }
}
