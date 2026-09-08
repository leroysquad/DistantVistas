namespace DistantVistas;

/// <summary>
/// Throttles login-sweep status text. Percent buckets still collapse tiny
/// changes, but detail changes and a 3s heartbeat must get through so the
/// overlay does not sit on "1/16" while scouts are actually working.
/// </summary>
public sealed class LodLoginBakeProgressUi
{
    const int PctStep = 5;
    const long HeartbeatMs = 3000;

    int lastPctBucket = -1;
    LodLoginBake.Phase lastPhase = LodLoginBake.Phase.Done;
    string lastDetail = "";
    long lastPushMs;

    public bool ShouldUpdate(LodLoginBake.Phase phase, int finished, int total, string detail)
    {
        long now = Environment.TickCount64;
        if (phase != lastPhase)
        {
            lastPhase = phase;
            lastPctBucket = -1;
            lastDetail = "";
            lastPushMs = now;
            return true;
        }

        if (phase is not LodLoginBake.Phase.Sweeping and not LodLoginBake.Phase.Auditing
            and not LodLoginBake.Phase.Draining and not LodLoginBake.Phase.Stabilizing)
            return detail != lastDetail;

        if (detail != lastDetail)
        {
            lastDetail = detail;
            lastPctBucket = PctBucket(finished, total);
            lastPushMs = now;
            return true;
        }

        int bucket = PctBucket(finished, total);
        if (bucket != lastPctBucket)
        {
            lastPctBucket = bucket;
            lastPushMs = now;
            return true;
        }

        if (now - lastPushMs >= HeartbeatMs)
        {
            lastPushMs = now;
            return true;
        }

        return false;
    }

    public void Reset()
    {
        lastPctBucket = -1;
        lastPhase = LodLoginBake.Phase.Done;
        lastDetail = "";
        lastPushMs = 0;
    }

    static int PctBucket(int finished, int total)
    {
        if (total <= 0) return 0;
        int pct = finished * 100 / total;
        return pct / PctStep * PctStep;
    }
}
