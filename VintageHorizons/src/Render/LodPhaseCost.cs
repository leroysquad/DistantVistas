using System.Diagnostics;

namespace VintageHorizons;

/// <summary>
/// Time spent in one phase of the render frame, since the last report.
///
/// These phases cost tens of microseconds against a frame budget of ten thousand, so a
/// frame-rate measurement cannot resolve them: the benchmark's own run-to-run spread is
/// larger than the whole of any one of them. Timing them directly is the only honest way
/// to tell whether a change to one did anything.
///
/// This is the same argument the server assist already makes for its blob reads, where
/// the serve loop records what it costs the tick "so the caps can be judged against a
/// measurement instead of an estimate".
///
/// The timing costs two Stopwatch.GetTimestamp calls per phase per frame, which is around
/// 40ns on this hardware. That is under a thousandth of the smallest phase measured, and
/// it applies equally to any before and after, so it cannot manufacture a difference.
/// </summary>
public struct LodPhaseCost
{
    long ticks;
    long maxTicks;
    int calls;

    /// <summary>Close a measurement opened with <see cref="Start"/>.</summary>
    public void Add(long startTimestamp)
    {
        long elapsed = Stopwatch.GetTimestamp() - startTimestamp;
        ticks += elapsed;
        if (elapsed > maxTicks) maxTicks = elapsed;
        calls++;
    }

    public static long Start() => Stopwatch.GetTimestamp();

    public int Calls => calls;
    public double AvgUs => calls == 0 ? 0 : ticks * 1_000_000.0 / Stopwatch.Frequency / calls;
    public double MaxUs => maxTicks * 1_000_000.0 / Stopwatch.Frequency;

    public void Reset()
    {
        ticks = 0;
        maxTicks = 0;
        calls = 0;
    }
}
