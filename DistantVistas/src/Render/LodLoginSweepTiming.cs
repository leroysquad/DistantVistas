using System.Diagnostics;

namespace DistantVistas;

/// <summary>
/// Measures per-stop timing during the login visit sweep and formats ETA strings.
/// Target window: ~1 minute wall-clock for season revisit and first-join bootstrap
/// at ~2048-block view distance with batch neighbour bakes per stop.
/// </summary>
public sealed class LodLoginSweepTiming
{
    /// <summary>Lower bound of the sweep target window (~45 seconds).</summary>
    public const double TargetMinSec = 45.0;

    /// <summary>Hard upper bound of the sweep target window (~1 minute).</summary>
    public const double TargetMaxSec = 60.0;

    /// <summary>First-join bootstrap uses the same ~1 minute wall-clock cap as revisit.</summary>
    public const double BootstrapTargetMaxSec = TargetMaxSec;

    /// <summary>
    /// Typical per-stop estimate before measured samples (~2s with 2048 view, batch bake,
    /// and tightened chunk/capture waits).
    /// </summary>
    public const double InitialSecPerStop = 2.0;

    public const int MinVisitStops = 20;
    /// <summary>Revisit ceiling — yields ~30 stops at <see cref="InitialSecPerStop"/> / 60s.</summary>
    public const int MaxVisitStops = 50;

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

    /// <summary>
    /// Max visit stops for a sweep given a wall-clock budget and measured/estimated stop rate.
    /// At <see cref="InitialSecPerStop"/> and <see cref="TargetMaxSec"/>, yields 30 stops (~1 min).
    /// </summary>
    public static int VisitStopBudget(double secPerStop, double targetMaxSec) =>
        (int)Math.Clamp(
            Math.Round(targetMaxSec / Math.Max(0.75, secPerStop)),
            MinVisitStops,
            MaxVisitStops);

    public static int BootstrapCellBudget(double secPerStop) =>
        VisitStopBudget(secPerStop, BootstrapTargetMaxSec);

    public static int RevisitCellBudget(double secPerStop) =>
        VisitStopBudget(secPerStop, TargetMaxSec);

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
