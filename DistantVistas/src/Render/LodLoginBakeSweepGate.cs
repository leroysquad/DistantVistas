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

    static readonly MethodInfo? LoadScreenNoLoadCallMethod = typeof(ScreenManager).GetMethod(
        "LoadScreenNoLoadCall",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(GuiScreen) },
        modifiers: null);

    /// <summary>Login visit sweep is armed or running.</summary>
    public static bool SweepActive { get; set; }

    /// <summary>Skip <see cref="GuiScreenRunningGame"/> world draws while the loader is held.</summary>
    public static bool SuppressRunningGameRender { get; set; }

    /// <summary><see cref="GuiScreenRunningGame.handOverRenderingToRunningGame"/> was blocked.</summary>
    public static bool HandoverDeferred { get; private set; }

    /// <summary>Harmony must allow the next handover invoke (completion path).</summary>
    internal static bool AllowHandoverPassThrough { get; private set; }

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
    public static void CompleteHandoverAndRelease(ICoreClientAPI capi, string reason) =>
        ClearHandoverDeferral(capi, reason, force: true);

    /// <summary>
    /// Clear handover deferral, run vanilla handover, and switch to the running-game screen.
    /// </summary>
    public static void ClearHandoverDeferral(ICoreClientAPI capi, string reason, bool force = false)
    {
        if (!HandoverDeferred && !force)
        {
            Release();
            return;
        }

        bool wasDeferred = HandoverDeferred;
        HandoverDeferred = false;
        SuppressRunningGameRender = false;
        SweepActive = false;

        AllowHandoverPassThrough = true;
        try
        {
            var clientMain = (ClientMain)capi.World;
            GuiScreenRunningGame running = clientMain.ScreenRunningGame;
            ScreenManager screenManager = running.ScreenManager;

            if (HandoverMethod != null)
                HandoverMethod.Invoke(running, null);
            else
                capi.Logger.Warning(
                    "[DistantVistas] Login sweep: handOverRenderingToRunningGame not found.");

            try
            {
                LoadScreenNoLoadCallMethod?.Invoke(screenManager, new object[] { running });
            }
            catch (Exception ex)
            {
                capi.Logger.Warning(
                    "[DistantVistas] Login sweep: LoadScreenNoLoadCall(running) failed ({0}).", ex.Message);
            }
        }
        catch (Exception ex)
        {
            capi.Logger.Warning(
                "[DistantVistas] Login sweep: handover to running game failed ({0}).", ex.Message);
        }
        finally
        {
            AllowHandoverPassThrough = false;
        }

        Release();

        capi.Logger.Notification(
            "[DistantVistas] Login sweep: handover deferral cleared — reason={0}{1}",
            reason,
            wasDeferred ? "" : " (screen forced)");
    }

    /// <inheritdoc cref="ClearHandoverDeferral"/>
    public static void CompleteHandover(ICoreClientAPI capi) =>
        ClearHandoverDeferral(capi, "legacy", force: true);
}
