using DistantVistas.Net;

namespace DistantVistas;

/// <summary>
/// Client-side pressure latch fed by server request-gate snapshots. Unhealthy
/// state enters immediately; recovery needs consecutive healthy snapshots.
/// While a request session is active, a missing heartbeat fails closed.
/// </summary>
internal sealed class LodScoutHostPressureState
{
    readonly int healthySnapshotsToClear;
    readonly long heartbeatGraceMs;
    int healthySnapshots;
    long sessionStartedMs;
    long lastReceivedMs;
    long lastSequence;
    bool sessionStarted;
    bool latched;

    internal LodScoutHostPressureState(
        int healthySnapshotsToClear = 3,
        long heartbeatGraceMs = 3000)
    {
        if (healthySnapshotsToClear < 1)
            throw new ArgumentOutOfRangeException(nameof(healthySnapshotsToClear));
        if (heartbeatGraceMs < 1)
            throw new ArgumentOutOfRangeException(nameof(heartbeatGraceMs));
        this.healthySnapshotsToClear = healthySnapshotsToClear;
        this.heartbeatGraceMs = heartbeatGraceMs;
    }

    internal int PriorityPending { get; private set; }
    internal int PriorityInFlight { get; private set; }
    internal int ForceSendPending { get; private set; }
    internal long OldestInFlightMs { get; private set; }
    internal long PriorityCompleted { get; private set; }
    internal long LastReceivedMs => lastReceivedMs;

    internal void BeginSession(long nowMs)
    {
        if (sessionStarted)
            return;
        sessionStarted = true;
        sessionStartedMs = nowMs;
    }

    internal void Note(ScoutHostStatus status, long receivedAtMs)
    {
        if (status.Sequence < lastSequence)
            return;

        sessionStarted = true;
        if (sessionStartedMs == 0)
            sessionStartedMs = receivedAtMs;
        lastSequence = status.Sequence;
        lastReceivedMs = receivedAtMs;
        PriorityPending = Math.Max(0, status.PriorityPending);
        PriorityInFlight = Math.Max(0, status.PriorityInFlight);
        ForceSendPending = Math.Max(0, status.ForceSendPending);
        OldestInFlightMs = Math.Max(0, status.OldestInFlightMs);
        PriorityCompleted = Math.Max(0, status.PriorityCompleted);

        if (status.Pressure)
        {
            latched = true;
            healthySnapshots = 0;
            return;
        }

        if (!latched)
        {
            healthySnapshots = 0;
            return;
        }

        healthySnapshots++;
        if (healthySnapshots >= healthySnapshotsToClear)
        {
            latched = false;
            healthySnapshots = 0;
        }
    }

    internal bool IsActive(long nowMs, bool requestsActive)
    {
        if (!sessionStarted)
            return false;
        if (!requestsActive)
            return false;
        if (latched)
            return true;

        long heartbeatBase = lastReceivedMs > 0 ? lastReceivedMs : sessionStartedMs;
        return nowMs - heartbeatBase > heartbeatGraceMs;
    }

    internal void Reset()
    {
        healthySnapshots = 0;
        sessionStartedMs = 0;
        lastReceivedMs = 0;
        lastSequence = 0;
        sessionStarted = false;
        latched = false;
        PriorityPending = 0;
        PriorityInFlight = 0;
        ForceSendPending = 0;
        OldestInFlightMs = 0;
        PriorityCompleted = 0;
    }
}
