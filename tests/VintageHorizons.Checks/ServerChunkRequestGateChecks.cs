using DistantVistas;

namespace DistantVistas.Checks;

public static class ServerChunkRequestGateChecks
{
    public static void Run(Check c)
    {
        StalledLoadsBoundEngineSubmissions(c);
        CompletionReleasesOneCredit(c);
        OwnersCoalesceAndCancel(c);
        LoginWorkPreemptsBackground(c);
        ForceSendIsBurstBounded(c);
        PressureUsesOutstandingAgeAndHysteresis(c);
        StaleInFlightReclaimsOneCredit(c);
        ClientPressureUsesStatusAndHeartbeat(c);
    }

    static LodServerChunkRequestGate Gate() => new(
        maxPriorityInFlight: 2,
        maxPriorityStartsPerTick: 2,
        maxForceSendsPerTick: 2,
        maxPendingPriority: 4,
        maxPendingForceSend: 4,
        pressureEnterTicks: 2,
        pressureExitTicks: 2,
        staleInFlightMs: 1000);

    static void StalledLoadsBoundEngineSubmissions(Check c)
    {
        var gate = Gate();
        for (int i = 0; i < 4; i++)
        {
            c.Eq(LodServerQueueDecision.Enqueued,
                gate.QueuePriority(
                    $"owner-{i}", new LodServerChunkColumn(i, 0, 0),
                    keepLoaded: true, onLoaded: null,
                    LodServerChunkWorkPriority.Login),
                "priority load enters the bounded pending set");
        }
        c.Eq(LodServerQueueDecision.Dropped,
            gate.QueuePriority(
                "owner-overflow", new LodServerChunkColumn(99, 0, 0),
                keepLoaded: true, onLoaded: null,
                LodServerChunkWorkPriority.Background),
            "bounded pending priority work refuses overflow");

        gate.BeginTick(nowMs: 0);
        c.True(gate.TryStartPriority(nowMs: 0, out _),
            "first priority load receives an engine credit");
        c.True(gate.TryStartPriority(nowMs: 0, out _),
            "second priority load receives an engine credit");
        c.False(gate.TryStartPriority(nowMs: 0, out _),
            "one tick cannot exceed the priority start burst");
        c.Eq(2, gate.PriorityInFlight, "in-flight priority loads reach the hard window");

        gate.BeginTick(nowMs: 50);
        c.False(gate.TryStartPriority(nowMs: 50, out _),
            "a new 50 ms tick does not bypass the in-flight window");
        c.Eq(2, gate.PriorityInFlight,
            "stalled callbacks hold credits instead of growing the engine FIFO");
    }

    static void CompletionReleasesOneCredit(Check c)
    {
        var gate = Gate();
        var first = new LodServerChunkColumn(1, 2, 0);
        gate.QueuePriority(
            "owner", first, keepLoaded: true, onLoaded: null,
            LodServerChunkWorkPriority.Login);
        gate.QueuePriority(
            "owner-2", new LodServerChunkColumn(2, 2, 0),
            keepLoaded: true, onLoaded: null,
            LodServerChunkWorkPriority.Login);
        gate.QueuePriority(
            "owner-3", new LodServerChunkColumn(3, 2, 0),
            keepLoaded: true, onLoaded: null,
            LodServerChunkWorkPriority.Login);

        gate.BeginTick(0);
        gate.TryStartPriority(0, out _);
        gate.TryStartPriority(0, out _);
        c.True(gate.CompletePriority(first, out IReadOnlyList<Action> callbacks),
            "the matching completion releases one in-flight credit");
        c.Eq(0, callbacks.Count, "a load without consumers completes without callbacks");
        c.False(gate.CompletePriority(first, out _),
            "a duplicate completion cannot release the same credit twice");

        gate.BeginTick(50);
        c.True(gate.TryStartPriority(50, out _),
            "completion admits exactly one queued priority load");
        c.Eq(2, gate.PriorityInFlight, "the in-flight window remains bounded after refill");
    }

    static void OwnersCoalesceAndCancel(Check c)
    {
        var gate = Gate();
        var column = new LodServerChunkColumn(7, 8, 0);
        int callbacks = 0;
        gate.QueuePriority(
            "hold-a", column, keepLoaded: true, onLoaded: () => callbacks++,
            LodServerChunkWorkPriority.Login);
        c.Eq(LodServerQueueDecision.Coalesced,
            gate.QueuePriority(
                "hold-b", column, keepLoaded: true, onLoaded: () => callbacks += 10,
                LodServerChunkWorkPriority.Login),
            "two holds coalesce onto one engine column load");
        c.Eq(1, gate.PriorityPending, "coalescing does not duplicate pending engine work");

        gate.BeginTick(0);
        gate.TryStartPriority(0, out _);
        gate.CancelOwner("hold-a");
        c.True(gate.CompletePriority(column, out IReadOnlyList<Action> liveCallbacks),
            "the in-flight column still settles after one owner cancels");
        foreach (Action callback in liveCallbacks) callback();
        c.Eq(10, callbacks, "late completion invokes only the still-live owner's callback");

        var cancelled = new LodServerChunkColumn(9, 8, 0);
        gate.QueuePriority(
            "gone", cancelled, keepLoaded: false, onLoaded: () => callbacks++,
            LodServerChunkWorkPriority.Background);
        gate.CancelOwner("gone");
        c.Eq(0, gate.PriorityPending, "cancelling the last owner removes pending work");

        var late = new LodServerChunkColumn(10, 8, 0);
        gate.QueuePriority(
            "late", late, keepLoaded: true, onLoaded: () => callbacks++,
            LodServerChunkWorkPriority.Login);
        gate.BeginTick(100);
        gate.TryStartPriority(100, out _);
        gate.CancelOwner("late");
        c.True(gate.CompletePriority(
                late, out IReadOnlyList<Action> cancelledCallbacks,
                out bool stillWanted, out bool keepLoaded),
            "a canceled in-flight load still returns its completion credit");
        c.False(stillWanted, "late completion reports that no owner still wants the column");
        c.True(keepLoaded, "late completion preserves whether engine cleanup is required");
        c.Eq(0, cancelledCallbacks.Count, "a canceled in-flight load invokes no consumer");
    }

    static void LoginWorkPreemptsBackground(Check c)
    {
        var gate = Gate();
        var background = new LodServerChunkColumn(20, 20, 0);
        var login = new LodServerChunkColumn(21, 20, 0);
        gate.QueuePriority(
            "background", background, keepLoaded: false, onLoaded: null,
            LodServerChunkWorkPriority.Background);
        gate.QueuePriority(
            "login", login, keepLoaded: true, onLoaded: null,
            LodServerChunkWorkPriority.Login);

        gate.BeginTick(0);
        c.True(gate.TryStartPriority(0, out LodServerPriorityStart first),
            "queued priority work can start");
        c.Eq(login, first.Column, "login work starts before older background work");
    }

    static void ForceSendIsBurstBounded(Check c)
    {
        var gate = Gate();
        for (int i = 0; i < 3; i++)
        {
            gate.QueueForceSend(
                $"hold-{i}", new LodServerForceSendColumn("player", i, 0, 0),
                LodServerChunkWorkPriority.Login);
        }

        gate.BeginTick(0);
        c.True(gate.TryStartForceSend(out _), "first ForceSend receives a tick credit");
        c.True(gate.TryStartForceSend(out _), "second ForceSend receives a tick credit");
        c.False(gate.TryStartForceSend(out _), "ForceSend cannot exceed its per-tick burst");
        c.Eq(1, gate.ForceSendPending, "unsent ForceSend work stays queued for the next tick");
    }

    static void PressureUsesOutstandingAgeAndHysteresis(Check c)
    {
        var gate = Gate();
        for (int i = 0; i < 3; i++)
        {
            gate.QueuePriority(
                $"owner-{i}", new LodServerChunkColumn(i, 5, 0),
                keepLoaded: true, onLoaded: null,
                LodServerChunkWorkPriority.Login);
        }
        gate.BeginTick(0);
        gate.TryStartPriority(0, out LodServerPriorityStart first);
        gate.TryStartPriority(0, out LodServerPriorityStart second);
        gate.EndTick(1000);
        c.False(gate.PressureActive, "pressure requires a sustained unhealthy window");
        gate.BeginTick(1050);
        gate.EndTick(1050);
        c.True(gate.PressureActive,
            "saturated in-flight work with old callbacks enters pressure");

        gate.CompletePriority(first.Column, out _);
        gate.CompletePriority(second.Column, out _);
        gate.CancelOwner("owner-2");
        gate.BeginTick(1100);
        gate.EndTick(1100);
        c.True(gate.PressureActive, "one quiet tick does not clear host pressure");
        gate.BeginTick(1150);
        gate.EndTick(1150);
        c.False(gate.PressureActive, "pressure clears after the configured quiet hysteresis");
    }

    static void StaleInFlightReclaimsOneCredit(Check c)
    {
        var gate = Gate();
        var column = new LodServerChunkColumn(30, 5, 0);
        int callbacks = 0;
        gate.QueuePriority(
            "owner", column, keepLoaded: true, onLoaded: () => callbacks++,
            LodServerChunkWorkPriority.Login);
        gate.BeginTick(0);
        c.True(gate.TryStartPriority(0, out LodServerPriorityStart start),
            "stale-reclaim fixture starts one priority load");
        c.Eq(1, gate.PriorityInFlight, "stale-reclaim fixture holds one in-flight credit");

        c.Eq(0, gate.ReclaimStaleInFlight(500),
            "age below the stale window does not reclaim");
        c.Eq(1, gate.PriorityInFlight, "fresh in-flight work stays until OnLoaded or stale age");

        c.Eq(1, gate.ReclaimStaleInFlight(1000),
            "stale OnLoaded frees exactly one credit through CompletePriority");
        c.Eq(0, gate.PriorityInFlight, "reclaim returns the in-flight credit");
        c.Eq(1, gate.PriorityPending, "still-wanted owners are requeued without invoking callbacks");
        c.Eq(0, callbacks, "stale reclaim does not pretend the column loaded");
        c.Eq(1, gate.PriorityStaleReclaimed, "stale reclaim is counted once");

        c.False(gate.CompletePriority(
                column, start.SubmissionGeneration, out _, out _, out _),
            "late OnLoaded from the reclaimed submission cannot double-credit");

        gate.BeginTick(1050);
        c.True(gate.TryStartPriority(1050, out LodServerPriorityStart restarted),
            "requeued owners can start again after reclaim");
        c.True(restarted.SubmissionGeneration > start.SubmissionGeneration,
            "restart uses a newer submission generation than the stale flight");
        c.True(gate.CompletePriority(
                column, restarted.SubmissionGeneration, out IReadOnlyList<Action> live, out _, out _),
            "only the live submission generation settles");
        foreach (Action callback in live) callback();
        c.Eq(1, callbacks, "owner callback runs once on the live completion");
        c.False(gate.CompletePriority(
                column, restarted.SubmissionGeneration, out _, out _, out _),
            "a second completion cannot release the same credit twice");
    }

    static void ClientPressureUsesStatusAndHeartbeat(Check c)
    {
        var state = new LodScoutHostPressureState(
            healthySnapshotsToClear: 3,
            heartbeatGraceMs: 3000);
        state.BeginSession(nowMs: 100);
        state.Note(new DistantVistas.Net.ScoutHostStatus
        {
            Sequence = 1,
            Pressure = true,
            PriorityInFlight = 24,
        }, receivedAtMs: 200);
        c.True(state.IsActive(nowMs: 200, requestsActive: true),
            "one unhealthy host snapshot immediately pauses client admission");

        for (int i = 0; i < 2; i++)
        {
            state.Note(new DistantVistas.Net.ScoutHostStatus
            {
                Sequence = 2 + i,
                Pressure = false,
            }, receivedAtMs: 300 + i * 100);
        }
        c.True(state.IsActive(nowMs: 500, requestsActive: true),
            "two healthy snapshots do not clear the pressure latch");
        state.Note(new DistantVistas.Net.ScoutHostStatus
        {
            Sequence = 4,
            Pressure = false,
        }, receivedAtMs: 600);
        c.False(state.IsActive(nowMs: 600, requestsActive: true),
            "the configured healthy snapshot window clears pressure");

        c.True(state.IsActive(nowMs: 3601, requestsActive: true),
            "a stale host heartbeat fails closed while requests are active");
        c.False(state.IsActive(nowMs: 3601, requestsActive: false),
            "an idle client does not stay pressured solely by an old heartbeat");
        state.Reset();
        c.False(state.IsActive(nowMs: 10000, requestsActive: true),
            "reset state does not invent pressure before a request session begins");
    }
}
