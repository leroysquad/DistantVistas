using System.Diagnostics;

namespace DistantVistas;

/// <summary>
/// Measures per-stop timing during the login visit sweep and formats ETA strings.
/// Target window: ~1.5–2.5 minutes on typical hardware for small bootstrap runs;
/// large empty-canvas disks (6k radius) scale with cell count and show honest ETA.
/// </summary>
public sealed class LodLoginSweepTiming
{
    public const double TargetMinSec = 90.0;
    public const double TargetMaxSec = 150.0;
    public const double InitialSecPerStop = 3.5;

    readonly Stopwatch clock = new();
    readonly List<double> stopDurations = new();
    int lastFinished;

    public void Begin() => clock.Restart();

    public void NoteFinished(int finished)
    {
        if (finished <= lastFinished) return;
        lastFinished = finished;
        stopDurations.Add(clock.Elapsed.TotalSeconds);
        clock.Restart();
    }

    public double SecondsPerStop
    {
        get
        {
            if (stopDurations.Count == 0) return InitialSecPerStop;
            double sum = 0;
            foreach (double d in stopDurations) sum += d;
            return sum / stopDurations.Count;
        }
    }

    public double EstimateRemainingSec(int finished, int total)
    {
        int left = Math.Max(0, total - finished);
        return left * SecondsPerStop;
    }

    public double EstimateTotalSec(int total) => total * SecondsPerStop;

    public string EtaSuffix(int finished, int total)
    {
        if (total <= 0) return "";
        double remaining = EstimateRemainingSec(finished, total);
        return $" — ~{FormatDuration(remaining)} left";
    }

    public static int BootstrapCellBudget(double secPerStop) =>
        (int)Math.Clamp(Math.Round(TargetMaxSec / Math.Max(0.75, secPerStop)), 12, 72);

    public static string FormatDuration(double seconds)
    {
        if (seconds < 0) seconds = 0;
        if (seconds < 60) return $"{(int)Math.Ceiling(seconds)}s";
        int min = (int)Math.Floor(seconds / 60);
        int sec = (int)Math.Ceiling(seconds - min * 60);
        if (sec >= 60) { min++; sec = 0; }
        return sec == 0 ? $"{min}m" : $"{min}m {sec}s";
    }
}
