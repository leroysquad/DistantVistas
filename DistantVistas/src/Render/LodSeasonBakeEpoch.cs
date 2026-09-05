using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Calendar bake choreography keyed to Vintage Story's own season curve
/// (<c>GameCalendar.GetSeason</c> / <c>GetSeasonRel</c>), so LODs rebake when
/// vanilla foliage starts looking different — not on arbitrary month days.
/// Spring and Fall split into early/mid/late sub-epochs so FlagBaked RGB tracks
/// mid-season leaf shifts without live seas/clim.
/// </summary>
public static class LodSeasonBakeEpoch
{
    // Same thresholds as Vintagestory.Common.GameCalendar.GetSeason (Lib).
    public const float SeasonRelWinterEnd = 0.2164f;
    public const float SeasonRelSpringEnd = 0.4685f;
    public const float SeasonRelSummerEnd = 0.726f;
    public const float SeasonRelWinterStart = 0.9726f;

    /// <summary>Sub-slots for Spring/Fall (0=early, 1=mid, 2=late). Winter/Summer use 0.</summary>
    public const int SubEpochCount = 3;

    /// <summary>
    /// Pack (Year, SeasonIndex, SubEpoch) into one int.
    /// Layout: year * 100 + season * 10 + sub (season 0..3, sub 0..2).
    /// </summary>
    public static int Pack(int year, int seasonIndex, int subEpoch)
    {
        seasonIndex = seasonIndex < 0 ? 0 : (seasonIndex > 3 ? 3 : seasonIndex);
        subEpoch = subEpoch < 0 ? 0 : (subEpoch > 2 ? 2 : subEpoch);
        return (year * 100) + (seasonIndex * 10) + subEpoch;
    }

    /// <summary>Two-arg pack — Winter/Summer style (sub-epoch 0).</summary>
    public static int Pack(int year, int seasonIndex) => Pack(year, seasonIndex, 0);

    public static void Unpack(int epoch, out int year, out int seasonIndex, out int subEpoch)
    {
        if (epoch < 0)
        {
            year = 0;
            seasonIndex = 0;
            subEpoch = 0;
            return;
        }

        year = epoch / 100;
        int mod100 = epoch % 100;
        seasonIndex = mod100 / 10;
        subEpoch = mod100 % 10;
        if (seasonIndex > 3) seasonIndex = 3;
        if (subEpoch > 2) subEpoch = 2;
    }

    public static void Unpack(int epoch, out int year, out int seasonIndex)
    {
        Unpack(epoch, out year, out seasonIndex, out _);
    }

    /// <summary>Day within the current month, 0 .. DaysPerMonth-1.</summary>
    public static int DayInMonth(IGameCalendar cal)
    {
        int dpm = cal.DaysPerMonth;
        if (dpm <= 0) dpm = 9;
        // IGameCalendar has no DayOfMonth; mirror GameCalendar: TotalDays % DaysPerMonth.
        int day = cal.DayOfYear % dpm;
        if (day < 0) day += dpm;
        return day;
    }

    /// <summary>
    /// 0=Winter, 1=Spring, 2=Summer, 3=Fall — identical cut points to
    /// <c>GameCalendar.GetSeason</c>.
    /// </summary>
    public static int SeasonIndexFromRel(float seasonRel)
    {
        seasonRel = GameMath.Mod(seasonRel, 1f);
        if (seasonRel < SeasonRelWinterEnd) return 0; // Winter
        if (seasonRel < SeasonRelSpringEnd) return 1; // Spring
        if (seasonRel < SeasonRelSummerEnd) return 2; // Summer
        if (seasonRel < SeasonRelWinterStart) return 3; // Fall
        return 0; // Winter (year wrap)
    }

    /// <summary>
    /// Early/mid/late within Spring or Fall (thirds of that season's YearRel span).
    /// Winter and Summer always return 0.
    /// </summary>
    public static int SubEpochFromRel(float seasonRel)
    {
        seasonRel = GameMath.Mod(seasonRel, 1f);
        int season = SeasonIndexFromRel(seasonRel);
        float start, end;
        switch (season)
        {
            case 1: // Spring
                start = SeasonRelWinterEnd;
                end = SeasonRelSpringEnd;
                break;
            case 3: // Fall
                start = SeasonRelSummerEnd;
                end = SeasonRelWinterStart;
                break;
            default:
                return 0;
        }

        float span = end - start;
        if (span <= 1e-6f) return 0;
        float t = (seasonRel - start) / span;
        if (t < 1f / 3f) return 0;
        if (t < 2f / 3f) return 1;
        return 2;
    }

    public static int SeasonIndex(IGameCalendar cal, BlockPos pos)
    {
        float srel;
        try
        {
            srel = cal.GetSeasonRel(pos);
        }
        catch
        {
            srel = cal.YearRel;
        }
        return SeasonIndexFromRel(srel);
    }

    /// <summary>Player-hemisphere season epoch (year + VS season + spring/fall sub).</summary>
    public static int FromCalendar(IGameCalendar cal) =>
        FromCalendar(cal, pos: null);

    public static int FromCalendar(IGameCalendar cal, BlockPos? pos)
    {
        BlockPos p = pos ?? new BlockPos(0);
        float srel;
        try
        {
            srel = cal.GetSeasonRel(p);
        }
        catch
        {
            srel = cal.YearRel;
        }
        return Pack(cal.Year, SeasonIndexFromRel(srel), SubEpochFromRel(srel));
    }

    /// <summary>Legacy name kept for log callers — returns season index 0..3.</summary>
    public static int MonthQuarter(IGameCalendar cal) =>
        SeasonIndex(cal, new BlockPos(0));

    /// <summary>Legacy alias.</summary>
    public static int MonthTransitionSlot(IGameCalendar cal) => MonthQuarter(cal);

    public static string Describe(int epoch)
    {
        Unpack(epoch, out int year, out int season, out int sub);
        string name = SeasonName(season);
        if ((season == 1 || season == 3) && sub > 0)
            return $"Y{year}-{name}{SubName(sub)}";
        return $"Y{year}-{name}";
    }

    public static string Describe(IGameCalendar cal)
    {
        float srel;
        try { srel = cal.YearRel; }
        catch { srel = 0f; }
        int season = SeasonIndexFromRel(srel);
        int sub = SubEpochFromRel(srel);
        string name = SeasonName(season);
        if ((season == 1 || season == 3) && sub > 0)
            name += SubName(sub);
        return $"Y{cal.Year}-M{cal.Month}-D{DayInMonth(cal)}-{name}";
    }

    public static string Describe(IGameCalendar cal, BlockPos pos)
    {
        int season = SeasonIndex(cal, pos);
        float srel = 0f;
        try { srel = cal.GetSeasonRel(pos); } catch { /* */ }
        int sub = SubEpochFromRel(srel);
        string name = SeasonName(season);
        if ((season == 1 || season == 3) && sub > 0)
            name += SubName(sub);
        return $"Y{cal.Year}-M{cal.Month}-D{DayInMonth(cal)}-{name}"
            + $"-srel{srel:0.###}";
    }

    public static string SeasonName(int seasonIndex) => seasonIndex switch
    {
        1 => "Spring",
        2 => "Summer",
        3 => "Fall",
        _ => "Winter",
    };

    static string SubName(int sub) => sub switch
    {
        1 => "Mid",
        2 => "Late",
        _ => "Early",
    };
}
