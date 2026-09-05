namespace DistantVistas;

/// <summary>
/// Player-first throttle for extra explored-column work. Vanilla streaming and a
/// walking or looking player own the process; idle recapture only runs when the
/// camera is quiet. Pure: no game types, so the busy rule is checkable without a world.
/// </summary>
public sealed class LodStreamingGate
{
    public const int ArrivalWindowTicks = 20;
    public const int ArrivalBusyThreshold = 2;
    public const int MoveBusyTicks = 40;
    public const double MoveBlocks = 16;

    int tick;
    int busyUntilTick;
    int lookBusyUntilTick;
    int hitchBusyUntilTick;
    int stepBusyUntilTick;
    bool vanillaBusy;
    bool lookSampled;
    float lastYaw;
    float lastPitch;
    readonly Queue<int> arrivals = new();
    readonly Dictionary<string, (double X, double Z)> lastPos = new();

    public int TickCount => tick;

    public void Tick()
    {
        tick++;
        while (arrivals.Count > 0 && tick - arrivals.Peek() > ArrivalWindowTicks)
            arrivals.Dequeue();
    }

    public void NoteChunkArrival() => arrivals.Enqueue(tick);

    public void SetVanillaBusy(bool busy) => vanillaBusy = busy;

    public void NotePlayer(string id, double x, double z)
    {
        if (lastPos.TryGetValue(id, out (double X, double Z) prev))
        {
            double dx = x - prev.X;
            double dz = z - prev.Z;
            double d2 = dx * dx + dz * dz;
            if (d2 >= MoveBlocks * MoveBlocks)
                busyUntilTick = tick + MoveBusyTicks;
            if (d2 >= LodFrameBudget.StepBlocksSq)
                stepBusyUntilTick = tick + LodFrameBudget.StepBusyTicks;
        }
        lastPos[id] = (x, z);
    }

    public void NoteLook(float yaw, float pitch)
    {
        if (lookSampled && LodFrameBudget.LookMoved(lastYaw, lastPitch, yaw, pitch))
            lookBusyUntilTick = tick + LodFrameBudget.LookBusyTicks;
        lastYaw = yaw;
        lastPitch = pitch;
        lookSampled = true;
    }

    public void NoteFrameMs(float lastFrameMs)
    {
        if (LodFrameBudget.FrameIsHitch(lastFrameMs))
            hitchBusyUntilTick = tick + LodFrameBudget.HitchBusyTicks;
    }

    /// <summary>Copy per-frame renderer flags onto the tick gate so a 50 ms tick cannot miss a look.</summary>
    public void NoteFrameSignals(bool lookBusy, bool hitch, bool stepped)
    {
        if (lookBusy) lookBusyUntilTick = tick + LodFrameBudget.LookBusyTicks;
        if (hitch) hitchBusyUntilTick = tick + LodFrameBudget.HitchBusyTicks;
        if (stepped) stepBusyUntilTick = tick + LodFrameBudget.StepBusyTicks;
    }

    public bool IsStreaming =>
        vanillaBusy || tick < busyUntilTick || arrivals.Count >= ArrivalBusyThreshold;

    public bool IsLookBusy => tick < lookBusyUntilTick;
    public bool IsHitchBusy => tick < hitchBusyUntilTick;
    public bool IsStepBusy => tick < stepBusyUntilTick;
    public bool IsWalkBusy => tick < busyUntilTick;

    /// <summary>
    /// Looking, hitching, stepping, or a 16-block walk. Chunk arrivals are not
    /// this: they are <see cref="IsStreaming"/> so vanilla can own the process.
    /// </summary>
    public bool IsCameraBusy =>
        IsLookBusy || IsHitchBusy || IsStepBusy || IsWalkBusy;

    /// <summary>Camera busy or vanilla streaming. Mesh-burst / season-idle yield.</summary>
    public bool IsBusy => IsStreaming || IsCameraBusy;
}
