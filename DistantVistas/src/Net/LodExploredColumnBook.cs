namespace DistantVistas.Net;

/// <summary>
/// Known / in-flight / skip / cooldown / exists for idle explored-column loads.
/// Singleplayer client tick calls <see cref="TryRequest"/>; <c>TestMapChunkExists</c>
/// completes on a server or worker thread. One lock covers every read and write of
/// those sets. WorldManager calls stay outside the lock so a sync callback cannot
/// deadlock the tick or the render thread.
/// </summary>
sealed class LodExploredColumnBook
{
    const int RequestCooldownTicks = 200;

    readonly object gate = new();
    readonly LodColumnMap exists = new();
    readonly HashSet<long> known = new();
    readonly HashSet<long> inFlight = new();
    readonly HashSet<long> skip = new();
    readonly Dictionary<long, int> requestedAt = new();
    int tick;

    public int InFlight
    {
        get { lock (gate) return inFlight.Count; }
    }

    public void Tick()
    {
        lock (gate)
        {
            tick++;
            if (requestedAt.Count == 0) return;
            List<long>? stale = null;
            foreach (KeyValuePair<long, int> pair in requestedAt)
            {
                if (tick - pair.Value > RequestCooldownTicks)
                    (stale ??= new List<long>()).Add(pair.Key);
            }
            if (stale == null) return;
            foreach (long key in stale) requestedAt.Remove(key);
        }
    }

    /// <summary>
    /// Probe callback. Safe from any thread. Does not touch WorldManager or GL.
    /// </summary>
    public void CompleteProbe(int cx, int cz, bool hit)
    {
        long key = LodColumnMap.Key(cx, cz);
        lock (gate)
        {
            inFlight.Remove(key);
            known.Add(key);
            if (hit) exists.Add(cx, cz);
        }
    }

    /// <summary>
    /// Same gates as the live loader. <paramref name="startProbe"/> and
    /// <paramref name="loadColumn"/> run with no book lock held. Yield
    /// (<paramref name="stillIdle"/> false) skips a new load; in-flight
    /// probes still complete through <see cref="CompleteProbe"/>.
    /// </summary>
    public ExploredLoadAttempt TryRequest(
        int cx, int cz, Func<bool> stillIdle, bool vanillaBusy,
        Action<int, int> startProbe, Action<int, int> loadColumn)
    {
        long key = LodColumnMap.Key(cx, cz);
        lock (gate)
        {
            if (skip.Contains(key)) return ExploredLoadAttempt.Refused;
            if (requestedAt.ContainsKey(key)) return ExploredLoadAttempt.None;
            if (inFlight.Count >= LodIdleRecapturePolicy.MaxInFlight)
                return ExploredLoadAttempt.None;
        }
        if (vanillaBusy || !stillIdle()) return ExploredLoadAttempt.None;

        int r = LodColumnMap.SafeNeighbourhood;
        bool missingProbe = false;
        for (int dz = -r; dz <= r; dz++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                int nx = cx + dx, nz = cz + dz;
                long nkey = LodColumnMap.Key(nx, nz);
                bool launch = false;
                lock (gate)
                {
                    if (known.Contains(nkey) || inFlight.Contains(nkey)) continue;
                    missingProbe = true;
                    if (inFlight.Count >= LodIdleRecapturePolicy.MaxInFlight)
                        return ExploredLoadAttempt.Probing;
                    inFlight.Add(nkey);
                    launch = true;
                }
                if (launch) startProbe(nx, nz);
            }
        }

        lock (gate)
        {
            if (missingProbe || HasUnknownNeighbour(cx, cz))
                return ExploredLoadAttempt.Probing;

            EnumColumnAction action = exists.Classify(cx, cz);
            if (!LodIdleRecapturePolicy.MayLoadExplored(action))
            {
                skip.Add(key);
                return ExploredLoadAttempt.Refused;
            }
        }

        if (vanillaBusy || !stillIdle()) return ExploredLoadAttempt.None;

        lock (gate)
        {
            if (requestedAt.ContainsKey(key)) return ExploredLoadAttempt.None;
            requestedAt[key] = tick;
        }
        loadColumn(cx, cz);
        return ExploredLoadAttempt.Loading;
    }

    bool HasUnknownNeighbour(int cx, int cz)
    {
        int r = LodColumnMap.SafeNeighbourhood;
        for (int dz = -r; dz <= r; dz++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                if (!known.Contains(LodColumnMap.Key(cx + dx, cz + dz))) return true;
            }
        }
        return false;
    }
}
