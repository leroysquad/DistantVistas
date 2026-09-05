using System.Diagnostics;

namespace DistantVistas;

/// <summary>
/// Measures per-stop timing during the login visit sweep and formats ETA strings.
/// Target window: ~3–5 minutes wall-clock for season revisit on large canvases;
/// empty-canvas bootstrap uses a shorter <see cref="BootstrapTargetMaxSec"/> budget.
/// </summary>
public sealed class LodLoginSweepTiming
{
    /// <summary>Lower bound of the revisit sweep target window (3 minutes).</summary>
    public const double TargetMinSec = 180.0;

    /// <summary>Hard upper bound of the revisit sweep target window (5 minutes).</summary>
    public const double TargetMaxSec = 300.0;

    /// <summary>Shorter budget for empty-canvas bootstrap (~2.5 min at typical stop rate).</summary>
    public const double BootstrapTargetMaxSec = 150.0;

    /// <summary>Typical per-stop estimate before measured samples (reduced waits vs 0.8.15).</summary>
    public const double InitialSecPerStop = 4.0;

    public const int MinVisitStops = 24;
    /// <summary>Revisit ceiling — yields 60–100 stops depending on measured sec/stop.</summary>
    public const int MaxVisitStops = 100;

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
    /// At <see cref="InitialSecPerStop"/> and <see cref="TargetMaxSec"/>, yields 75 stops (~5 min).
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
