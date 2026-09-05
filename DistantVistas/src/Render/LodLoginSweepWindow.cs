using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Shared season / in-game-day window for login sweep resume and skip-to-play.
/// </summary>
public static class LodLoginSweepWindow
{
    public const double MaxDayGap = LodLoginSweepResume.MaxResumeDayGap;

    public static bool IsWithin(IClientWorldAccessor world, string savedSeason, double savedTotalDays)
    {
        IGameCalendar cal = world.Calendar;
        var pos = new BlockPos(
            (int)world.Player.Entity.Pos.X,
            world.SeaLevel,
            (int)world.Player.Entity.Pos.Z);
        string currentSeason = LodLoginSweepResume.SeasonSlug(cal.GetSeason(pos));
        if (string.Equals(currentSeason, savedSeason, StringComparison.OrdinalIgnoreCase))
            return true;

        return cal.TotalDays - savedTotalDays <= MaxDayGap;
    }
}
