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

    /// <summary>
    /// Unfreezes time: removes our speed modifier, restores saved calendar speed,
    /// clears the TotalHours anchor, and logs. Each step is independent so a partial
    /// teardown failure cannot leave the world frozen.
    /// </summary>
    public void Restore()
    {
        if (!frozen) return;

        float? speedMul = savedCalendarSpeedMul;

        try
        {
            IGameCalendar? cal = TryGetCalendar();
            if (cal != null)
            {
                TryRemoveSpeedModifier(cal);
                TryRestoreCalendarSpeedMul(cal, speedMul);
            }
        }
        finally
        {
            savedCalendarSpeedMul = null;
            anchoredTotalHours = null;
            frozen = false;
            capi.Logger.Notification("[DistantVistas] Login visit sweep: time restored.");
        }
    }

    IGameCalendar? TryGetCalendar()
    {
        try
        {
            return capi.World?.Calendar;
        }
        catch (Exception ex)
        {
            capi.Logger.Warning(
                "[DistantVistas] Login visit sweep: calendar unavailable during time restore ({0}).",
                ex.Message);
            return null;
        }
    }

    void TryRemoveSpeedModifier(IGameCalendar cal)
    {
        try
        {
            cal.RemoveTimeSpeedModifier(SpeedModifierKey);
        }
        catch (Exception ex)
        {
            capi.Logger.Warning(
                "[DistantVistas] Login visit sweep: SpeedOfTime modifier remove failed ({0}).",
                ex.Message);
        }
    }

    void TryRestoreCalendarSpeedMul(IGameCalendar cal, float? speedMul)
    {
        if (!speedMul.HasValue) return;

        try
        {
            cal.CalendarSpeedMul = speedMul.Value;
        }
        catch (Exception ex)
        {
            capi.Logger.Warning(
                "[DistantVistas] Login visit sweep: CalendarSpeedMul restore failed ({0}).",
                ex.Message);
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
