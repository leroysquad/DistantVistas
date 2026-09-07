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
    float? savedSpeedOfTime;
    double? anchoredTotalHours;
    long freezeStartMs;
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
            savedSpeedOfTime = cal.SpeedOfTime;
            anchoredTotalHours = cal.TotalHours;
            freezeStartMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            frozen = true;
            capi.Logger.Notification("[DistantVistas] Login visit sweep: time frozen");
        }

        cal.CalendarSpeedMul = 0f;
        ZeroSpeedOfTime(cal);
        // Do not rewind TotalHours every pulse. That parks the client ~1 hour
        // behind the server; vanilla then catch-up-simulates it on restore
        // ("daytime drifted 66 mins") and the main thread dies after land.
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
        float? speedOfTime = savedSpeedOfTime;
        double? anchored = anchoredTotalHours;
        long startedMs = freezeStartMs;

        try
        {
            IGameCalendar? cal = TryGetCalendar();
            if (cal != null)
            {
                double hoursBefore = cal.TotalHours;
                float jump = TryJumpHeldHours(cal, anchored, speedOfTime, speedMul, startedMs);
                double hoursAfter = cal.TotalHours;
                long elapsedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startedMs;
                double heldGap = anchored.HasValue ? Math.Abs(hoursBefore - anchored.Value) : -1;
                // #region agent log
                try
                {
                    System.IO.File.AppendAllText(
                        @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                        "{\"sessionId\":\"40cccb\",\"hypothesisId\":\"H1\",\"location\":\"LodLoginBakeTimeFreeze.Restore\",\"message\":\"time-restore-hours\",\"data\":{\"hoursBefore\":" + hoursBefore.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"hoursAfter\":" + hoursAfter.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"heldGap\":" + heldGap.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"jump\":" + jump.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"elapsedMs\":" + elapsedMs + "},\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n");
                }
                catch { }
                // #endregion
                capi.Logger.Notification(
                    "[DistantVistas] Login visit sweep: time restore hours before={0:0.000} after={1:0.000} heldGap={2:0.00} jump={3:0.00} elapsedMs={4}",
                    hoursBefore, hoursAfter, heldGap, jump, elapsedMs);
                TryRemoveSpeedModifier(cal);
                TryRestoreCalendarSpeedMul(cal, speedMul);
            }
        }
        finally
        {
            savedCalendarSpeedMul = null;
            savedSpeedOfTime = null;
            anchoredTotalHours = null;
            freezeStartMs = 0;
            frozen = false;
            capi.Logger.Notification("[DistantVistas] Login visit sweep: time restored");
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

    float TryJumpHeldHours(
        IGameCalendar cal,
        double? anchored,
        float? speedOfTime,
        float? speedMul,
        long startedMs)
    {
        if (!anchored.HasValue) return 0f;

        double hoursNow = cal.TotalHours;
        long elapsedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startedMs;
        double heldGap = Math.Abs(hoursNow - anchored.Value);
        float jump = 0f;
        // Clock still sitting on the freeze hour after a long sweep: jump it
        // so vanilla does not simulate the missing game-minutes on the main thread.
        if (heldGap < 0.25 && elapsedMs > 5000)
        {
            float speed = speedOfTime.GetValueOrDefault(60f);
            float mul = speedMul.GetValueOrDefault(1f);
            jump = (float)(elapsedMs / 1000.0 * speed * mul / 3600.0);
            if (jump > 0.05f)
                cal.Add(jump);
            else
                jump = 0f;
        }
        return jump;
    }
}
