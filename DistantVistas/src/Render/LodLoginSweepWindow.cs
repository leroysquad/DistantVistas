using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DistantVistas;

/// <summary>
/// Shared in-game-day window for login sweep resume and skip-to-play.
/// Skip/resume only when the last successful (or checkpoint) sweep is within
/// <see cref="MaxDayGap"/> in-game days — season alone does not qualify.
/// </summary>
public static class LodLoginSweepWindow
{
    public const double MaxDayGap = LodLoginSweepResume.MaxResumeDayGap;

    public static bool IsWithin(IClientWorldAccessor world, string savedSeason, double savedTotalDays)
    {
        _ = savedSeason; // retained for call-site compatibility / marker schema
        IGameCalendar cal = world.Calendar;
        return cal.TotalDays - savedTotalDays <= MaxDayGap;
    }
}
