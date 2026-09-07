using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace DistantVistas;

/// <summary>
/// Overlay CaptureAllInputs does not stop ClientMain.OnMouseMove from accumulating
/// MouseDelta. UpdateCameraYawPitch then applies leftover delta to mouseYaw after
/// the overlay hides, a sudden look yank. Zero both the live and delayed deltas
/// together; zeroing only MouseDelta inverts the next vel and yanks the other way.
/// </summary>
public static class LodLoginBakeMouseDelta
{
    static long lastLogMs;

    public static void Drain(ICoreClientAPI capi)
    {
        if (capi?.World is not ClientMain main) return;

        double leftover = Math.Abs(main.MouseDeltaX) + Math.Abs(main.MouseDeltaY)
            + Math.Abs(main.DelayedMouseDeltaX) + Math.Abs(main.DelayedMouseDeltaY);
        main.MouseDeltaX = 0;
        main.MouseDeltaY = 0;
        main.DelayedMouseDeltaX = 0;
        main.DelayedMouseDeltaY = 0;
        if (leftover < 0.5) return;

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now - lastLogMs < 250) return;
        lastLogMs = now;

        // #region agent log
        try
        {
            System.IO.File.AppendAllText(
                @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                "{\"sessionId\":\"40cccb\",\"runId\":\"input-audio\",\"hypothesisId\":\"H-LOOK\",\"location\":\"LodLoginBakeMouseDelta\",\"message\":\"drain-delta\",\"data\":{\"leftover\":"
                + leftover.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                + "},\"timestamp\":" + now + "}\n");
        }
        catch
        {
        }
        // #endregion
    }
}
