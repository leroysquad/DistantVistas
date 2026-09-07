using System.Diagnostics;

namespace DistantVistas;

/// <summary>
/// Measures per-stop timing during the login visit sweep and formats ETA strings.
/// First ETA and stop budget use this machine's measured rate (persisted / harvested
/// from client logs), not a guessed constant.
/// </summary>
public sealed class LodLoginSweepTiming
{
    /// <summary>Lower bound of the first-pass wall target.</summary>
    public const double TargetMinSec = 30.0;

    /// <summary>First-pass wall-clock cap. 4x the 0.8.64/65 shrink (40s → 160s).</summary>
    public const double TargetMaxSec = 160.0;

    /// <summary>First-join bootstrap uses the same first-pass wall cap.</summary>
    public const double BootstrapTargetMaxSec = TargetMaxSec;

    /// <summary>Retry pass wall cap — 2x the shrink, still shorter than first pass.</summary>
    public const double RetryTargetSec = 32.0;

    /// <summary>
    /// Fallback per-stop seconds only when this machine has no measured samples yet.
    /// </summary>
    public const double InitialSecPerStop = 2.0;

    public const int MinVisitStops = 64;
    public const int MaxVisitStops = 96;
    public const int MinRetryStops = 16;
    public const int MaxRetryStops = 32;

    /// <summary>This PC's measured (or fallback) seconds per visit stop.</summary>
    public static double MachineSecPerStop { get; private set; } = InitialSecPerStop;

    readonly Stopwatch clock = new();
    readonly Stopwatch wall = new();
    readonly List<double> stopDurations = new();
    double? seeded;
    int lastFinished;

    public static void SetMachineSecPerStop(double secPerStop) =>
        MachineSecPerStop = Math.Clamp(secPerStop, 0.75, 6.0);

    public void Seed(double secPerStop) =>
        seeded = Math.Clamp(secPerStop, 0.75, 6.0);

    public void BeginSession(double seededSec)
    {
        Seed(seededSec);
        stopDurations.Clear();
        lastFinished = 0;
        clock.Restart();
        wall.Restart();
    }

    public void Begin(bool resetSamples = false)
    {
        clock.Restart();
        lastFinished = 0;
        if (resetSamples) stopDurations.Clear();
        if (!wall.IsRunning) wall.Start();
    }

    public void NoteFinished(int finished)
    {
        if (finished <= lastFinished) return;
        lastFinished = finished;
        stopDurations.Add(clock.Elapsed.TotalSeconds);
        clock.Restart();
    }

    public int SampleCount => stopDurations.Count;

    public double WallSec => wall.Elapsed.TotalSeconds;

    public double SecondsPerStop
    {
        get
        {
            if (stopDurations.Count == 0) return seeded ?? MachineSecPerStop;
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
    /// </summary>
    public static int VisitStopBudget(double secPerStop, double targetMaxSec) =>
        (int)Math.Clamp(
            Math.Round(targetMaxSec / Math.Max(0.75, secPerStop)),
            MinVisitStops,
            MaxVisitStops);

    public static int RetryStopBudget(double secPerStop) =>
        (int)Math.Clamp(
            Math.Round(RetryTargetSec / Math.Max(0.75, secPerStop)),
            MinRetryStops,
            MaxRetryStops);

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
