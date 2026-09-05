using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace DistantVistas;

/// <summary>
/// Cross-cutting flags for the login visit sweep (Harmony patches, render suppress,
/// deferred handover to <see cref="GuiScreenRunningGame"/>).
/// </summary>
public static class LodLoginBakeSweepGate
{
    static readonly MethodInfo? HandoverMethod = typeof(GuiScreenRunningGame).GetMethod(
        "handOverRenderingToRunningGame",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    /// <summary>Login visit sweep is armed or running.</summary>
    public static bool SweepActive { get; set; }

    /// <summary>Skip <see cref="GuiScreenRunningGame"/> world draws while the loader is held.</summary>
    public static bool SuppressRunningGameRender { get; set; }

    /// <summary><see cref="GuiScreenRunningGame.handOverRenderingToRunningGame"/> was blocked.</summary>
    public static bool HandoverDeferred { get; private set; }

    public static void Arm()
    {
        SweepActive = true;
        SuppressRunningGameRender = true;
    }

    public static void MarkHandoverDeferred() => HandoverDeferred = true;

    public static void Release()
    {
        SweepActive = false;
        SuppressRunningGameRender = false;
        HandoverDeferred = false;
    }

    /// <summary>Finish deferred handover (if any) then clear sweep flags.</summary>
    public static void CompleteHandoverAndRelease(ICoreClientAPI capi)
    {
        CompleteHandover(capi);
        Release();
    }

    /// <summary>
    /// Finish the deferred handover after the visit sweep completes or is skipped.
    /// </summary>
    public static void CompleteHandover(ICoreClientAPI capi)
    {
        if (!HandoverDeferred) return;

        HandoverDeferred = false;
        SuppressRunningGameRender = false;

        try
        {
            var clientMain = (ClientMain)capi.World;
            GuiScreenRunningGame running = clientMain.ScreenRunningGame;
            if (HandoverMethod == null)
            {
                capi.Logger.Warning(
                    "[DistantVistas] Login sweep: handOverRenderingToRunningGame not found — skipping handover.");
                return;
            }

            HandoverMethod.Invoke(running, null);
            capi.Logger.Notification(
                "[DistantVistas] Login sweep: handed rendering to running game.");
        }
        catch (Exception ex)
        {
            capi.Logger.Warning(
                "[DistantVistas] Login sweep: handover to running game failed ({0}).", ex.Message);
        }
    }
}
