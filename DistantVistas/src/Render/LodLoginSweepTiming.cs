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

    /// <summary>
    /// First-pass wall-clock cap. 1.0.25: ~7 minutes so scout streams paint
    /// FlagBaked land out toward the Farseer rim (measured MachineSecPerStop).
    /// </summary>
    public const double TargetMaxSec = 420.0;

    /// <summary>First-join bootstrap uses the same first-pass wall cap.</summary>
    public const double BootstrapTargetMaxSec = TargetMaxSec;

    /// <summary>Retry pass wall cap — denser gap-fill after the first hop.</summary>
    public const double RetryTargetSec = 90.0;

    /// <summary>
    /// Fallback per-stop seconds only when this machine has no measured samples yet.
    /// Concurrent scouts have no hop cost; live ETA uses wall / finishes, not this
    /// seed, once a batch has painted.
    /// </summary>
    public const double InitialSecPerStop = 0.25;

    /// <summary>
    /// Parallel PaintReadyScouts can finish many stops in one overlay tick.
    /// Do not clamp measured rates up to 0.25s — that made ETA assume hop-era pacing.
    /// </summary>
    public const double MeasuredMinSecPerStop = 0.02;

    public const int MinVisitStops = 480;
    public const int MaxVisitStops = 1680;
    public const int MinRetryStops = 36;
    public const int MaxRetryStops = 96;

    /// <summary>This PC's measured (or fallback) seconds per visit stop.</summary>
    public static double MachineSecPerStop { get; private set; } = InitialSecPerStop;

    readonly Stopwatch clock = new();
    readonly Stopwatch wall = new();
    double measuredElapsed;
    int measuredStops;
    double? seeded;
    int lastFinished;

    public static void SetMachineSecPerStop(double secPerStop) =>
        MachineSecPerStop = Math.Clamp(secPerStop, MeasuredMinSecPerStop, 6.0);

    public void Seed(double secPerStop) =>
        seeded = Math.Clamp(secPerStop, MeasuredMinSecPerStop, 6.0);

    public void BeginSession(double seededSec)
    {
        Seed(seededSec);
        measuredElapsed = 0;
        measuredStops = 0;
        lastFinished = 0;
        clock.Restart();
        wall.Restart();
    }

    public void Begin(bool resetSamples = false)
    {
        clock.Restart();
        lastFinished = 0;
        if (resetSamples)
        {
            measuredElapsed = 0;
            measuredStops = 0;
        }
        if (!wall.IsRunning) wall.Start();
    }

    /// <summary>
    /// Record wall time for however many stops finished since the last note.
    /// Painting 24 scouts in one tick is not one 0.25s hop.
    /// </summary>
    public void NoteFinished(int finished)
    {
        int delta = finished - lastFinished;
        if (delta <= 0) return;
        measuredElapsed += clock.Elapsed.TotalSeconds;
        measuredStops += delta;
        lastFinished = finished;
        clock.Restart();
    }

    public int SampleCount => measuredStops;

    public double WallSec => wall.Elapsed.TotalSeconds;

    public double SecondsPerStop
    {
        get
        {
            if (measuredStops <= 0) return seeded ?? MachineSecPerStop;
            return measuredElapsed / measuredStops;
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
            Math.Round(targetMaxSec / Math.Max(InitialSecPerStop, secPerStop)),
            MinVisitStops,
            MaxVisitStops);

    public static int RetryStopBudget(double secPerStop) =>
        (int)Math.Clamp(
            Math.Round(RetryTargetSec / Math.Max(InitialSecPerStop, secPerStop)),
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
