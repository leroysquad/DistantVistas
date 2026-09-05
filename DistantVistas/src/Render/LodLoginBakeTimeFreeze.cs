using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DistantVistas;

/// <summary>
/// Freezes calendar/day progression and in-world clock speed during the login visit sweep,
/// then restores the prior calendar speed multiplier exactly.
/// </summary>
public sealed class LodLoginBakeTimeFreeze
{
    public const string SpeedModifierKey = "distantvistas-loginbake";

    readonly ICoreClientAPI capi;
    float? savedCalendarSpeedMul;
    double? anchoredTotalHours;
    bool frozen;

    public LodLoginBakeTimeFreeze(ICoreClientAPI capi) => this.capi = capi;

    public bool IsFrozen => frozen;

    /// <summary>First call saves settings; later calls keep time frozen if something changed them.</summary>
    public void EnsureFrozen()
    {
        IGameCalendar cal = capi.World.Calendar;
        if (!frozen)
        {
            savedCalendarSpeedMul = cal.CalendarSpeedMul;
            anchoredTotalHours = cal.TotalHours;
            frozen = true;
        }

        cal.CalendarSpeedMul = 0f;
        ZeroSpeedOfTime(cal);
        AnchorTotalHours(cal);
    }

    public void Restore()
    {
        if (!frozen) return;

        try
        {
            IGameCalendar cal = capi.World.Calendar;
            cal.RemoveTimeSpeedModifier(SpeedModifierKey);
            if (savedCalendarSpeedMul.HasValue)
                cal.CalendarSpeedMul = savedCalendarSpeedMul.Value;
        }
        finally
        {
            savedCalendarSpeedMul = null;
            anchoredTotalHours = null;
            frozen = false;
        }
    }

    void ZeroSpeedOfTime(IGameCalendar cal)
    {
        cal.RemoveTimeSpeedModifier(SpeedModifierKey);
        float speed = cal.SpeedOfTime;
        if (speed != 0f)
            cal.SetTimeSpeedModifier(SpeedModifierKey, -speed);
    }

    void AnchorTotalHours(IGameCalendar cal)
    {
        if (!anchoredTotalHours.HasValue) return;

        double drift = anchoredTotalHours.Value - cal.TotalHours;
        if (Math.Abs(drift) > 1e-6)
            cal.Add((float)drift);
    }
}
