using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Shared skip/resume window for the login visit sweep. The window starts on the
/// first successful sweep (or first login that records one) and lasts
/// <see cref="MaxDayGap"/> in-game days <strong>or</strong>
/// <see cref="MaxWallMs"/> of real time, whichever hits first. Season slug is
/// not part of the window. A missing wall stamp (legacy markers) uses the day
/// gap alone until the next successful sweep writes both clocks.
/// </summary>
public static class LodLoginSweepWindow
{
    public const double MaxDayGap = LodLoginSweepResume.MaxResumeDayGap;
    public const long MaxWallMs = 30L * 24 * 60 * 60 * 1000;

    public const string OutsideDayWindowReason = "outside 30-day window since last sweep";
    public const string OutsideWallWindowReason = "outside 30-day wall-clock window since last sweep";
    public const string StalePaintRevisionReason = "paint revision recapture (frosted canopy, season ground)";
    public const string MonthChangedReason = "calendar month changed since last sweep";

    /// <summary>Diagnostic only. Season slug is not an expire trigger. Calendar month is.</summary>
    public static bool IsSameSeason(string savedSeason, string nowSeason)
    {
        if (string.IsNullOrEmpty(savedSeason)) return true;
        return string.Equals(savedSeason, nowSeason, StringComparison.OrdinalIgnoreCase);
    }

    public static long NowUtcMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static bool IsDayGapWithin(double nowTotalDays, double savedTotalDays) =>
        nowTotalDays - savedTotalDays <= MaxDayGap;

    /// <summary>
    /// Legacy markers with no wall stamp stay on the in-game day gap until the
    /// next successful sweep writes <c>WindowStartedUtcMs</c>.
    /// </summary>
    public static bool IsWallGapWithin(long nowUtcMs, long savedUtcMs) =>
        savedUtcMs <= 0 || nowUtcMs - savedUtcMs <= MaxWallMs;

    public static bool IsWithin(
        double nowTotalDays,
        double savedTotalDays,
        long nowUtcMs,
        long savedUtcMs) =>
        IsDayGapWithin(nowTotalDays, savedTotalDays)
        && IsWallGapWithin(nowUtcMs, savedUtcMs);

    /// <summary>Back-compat overload: season arguments are ignored.</summary>
    public static bool IsWithin(
        string savedSeason,
        string nowSeason,
        double nowTotalDays,
        double savedTotalDays) =>
        IsWithin(nowTotalDays, savedTotalDays, 0, 0);

    public static string? ExpireReason(
        double nowTotalDays,
        double savedTotalDays,
        long nowUtcMs,
        long savedUtcMs)
    {
        if (!IsDayGapWithin(nowTotalDays, savedTotalDays))
            return OutsideDayWindowReason;
        if (!IsWallGapWithin(nowUtcMs, savedUtcMs))
            return OutsideWallWindowReason;
        return null;
    }

    /// <summary>Back-compat overload: season arguments are ignored.</summary>
    public static string? ExpireReason(
        string savedSeason,
        string nowSeason,
        double nowTotalDays,
        double savedTotalDays) =>
        ExpireReason(nowTotalDays, savedTotalDays, 0, 0);

    public static string? RecaptureReason(
        double nowTotalDays,
        double savedTotalDays,
        long nowUtcMs,
        long savedUtcMs,
        int paintRevision)
    {
        string? expire = ExpireReason(nowTotalDays, savedTotalDays, nowUtcMs, savedUtcMs);
        if (expire != null) return expire;
        if (paintRevision < LodSurfaceMix.PaintRevision)
            return StalePaintRevisionReason;
        return null;
    }

    public static bool TryReadSavedMonth(string calendarToken, out int month)
    {
        month = 0;
        if (string.IsNullOrEmpty(calendarToken)) return false;
        int m = calendarToken.IndexOf('M');
        if (m < 0 || m + 1 >= calendarToken.Length) return false;
        int d = calendarToken.IndexOf('D', m + 1);
        if (d < 0) return false;
        return int.TryParse(calendarToken.AsSpan(m + 1, d - m - 1), out month)
            && month >= 1 && month <= 12;
    }

    public static bool MonthChanged(string calendarToken, int nowMonth) =>
        TryReadSavedMonth(calendarToken, out int saved) && nowMonth >= 1 && nowMonth <= 12 && saved != nowMonth;

    /// <summary>Back-compat overload: season arguments are ignored.</summary>
    public static string? RecaptureReason(
        string savedSeason,
        string nowSeason,
        double nowTotalDays,
        double savedTotalDays,
        int paintRevision) =>
        RecaptureReason(nowTotalDays, savedTotalDays, 0, 0, paintRevision);

    public static string? RecaptureReason(IClientWorldAccessor world, LodLoginSweepComplete complete)
    {
        string? reason = RecaptureReason(
            world.Calendar.TotalDays,
            complete.SavedTotalDays,
            NowUtcMs(),
            complete.WindowStartedUtcMs,
            complete.PaintRevision);
        if (reason != null) return reason;
        if (MonthChanged(complete.CalendarToken, world.Calendar.Month))
            return MonthChangedReason;
        return null;
    }

    public static string CurrentSeasonSlug(IClientWorldAccessor world)
    {
        IGameCalendar cal = world.Calendar;
        var pos = new BlockPos(
            (int)world.Player.Entity.Pos.X,
            world.SeaLevel,
            (int)world.Player.Entity.Pos.Z);
        return LodLoginSweepResume.SeasonSlug(cal.GetSeason(pos));
    }

    public static bool IsWithin(
        IClientWorldAccessor world,
        double savedTotalDays,
        long savedUtcMs) =>
        IsWithin(world.Calendar.TotalDays, savedTotalDays, NowUtcMs(), savedUtcMs);

    public static bool IsWithin(IClientWorldAccessor world, string savedSeason, double savedTotalDays) =>
        IsWithin(world, savedTotalDays, 0);

    public static string? ExpireReason(
        IClientWorldAccessor world,
        double savedTotalDays,
        long savedUtcMs) =>
        ExpireReason(world.Calendar.TotalDays, savedTotalDays, NowUtcMs(), savedUtcMs);

    public static string? ExpireReason(IClientWorldAccessor world, string savedSeason, double savedTotalDays) =>
        ExpireReason(world, savedTotalDays, 0);
}
