namespace DistantVistas;

/// <summary>
/// Throttles login-sweep status text so vanilla loading lines update about every 10%.
/// </summary>
public sealed class LodLoginBakeProgressUi
{
    const int PctStep = 5;

    int lastPctBucket = -1;
    LodLoginBake.Phase lastPhase = LodLoginBake.Phase.Done;
    string lastDetail = "";

    public bool ShouldUpdate(LodLoginBake.Phase phase, int finished, int total, string detail)
    {
        if (phase != lastPhase)
        {
            lastPhase = phase;
            lastPctBucket = -1;
            lastDetail = "";
            return true;
        }

        if (phase is not LodLoginBake.Phase.Sweeping and not LodLoginBake.Phase.Auditing
            and not LodLoginBake.Phase.Draining and not LodLoginBake.Phase.Stabilizing)
            return detail != lastDetail;

        int bucket = PctBucket(finished, total);
        if (bucket == lastPctBucket) return false;

        lastPctBucket = bucket;
        lastDetail = detail;
        return true;
    }

    public void Reset()
    {
        lastPctBucket = -1;
        lastPhase = LodLoginBake.Phase.Done;
        lastDetail = "";
    }

    static int PctBucket(int finished, int total)
    {
        if (total <= 0) return 0;
        int pct = finished * 100 / total;
        return pct / PctStep * PctStep;
    }
}
