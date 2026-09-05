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
            frozen = true;
        }

        cal.CalendarSpeedMul = 0f;

        cal.RemoveTimeSpeedModifier(SpeedModifierKey);
        float speed = cal.SpeedOfTime;
        if (speed != 0f)
            cal.SetTimeSpeedModifier(SpeedModifierKey, -speed);
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
            frozen = false;
        }
    }
}
