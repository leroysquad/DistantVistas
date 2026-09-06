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

    /// <summary>
    /// Vanilla block/item/entity texture atlases finished GPU compose (StageC).
    /// Splash must not create or bind textures until this is true — StageB runs
    /// BindTexture on worker threads and races main-thread splash GL (TrueScale HD).
    /// </summary>
    public static bool TextureAtlasesReady { get; private set; }

    static int textureAtlasStageCCount;

    /// <summary>Called from Harmony after each <c>FinaliseTextureAtlas_StageC</c> completes.</summary>
    public static void NotifyAtlasStageCComplete()
    {
        int n = Interlocked.Increment(ref textureAtlasStageCCount);
        if (n >= 3)
            TextureAtlasesReady = true;
    }

    /// <summary>Reset at client start; cleared while atlases recompose during load.</summary>
    public static void ResetTextureAtlasGate()
    {
        textureAtlasStageCCount = 0;
        TextureAtlasesReady = false;
    }

    /// <summary>Character/class selection is open — splash GL must stay off.</summary>
    public static bool CharacterUiBlocksSplash { get; set; }

    /// <summary>All conditions for splash texture create/bind/draw on the main thread.</summary>
    public static bool SplashGlAllowed =>
        SweepActive && TextureAtlasesReady && !CharacterUiBlocksSplash;

    public static void BlockSplashForCharacterUi() => CharacterUiBlocksSplash = true;

    public static void UnblockSplashForCharacterUi() => CharacterUiBlocksSplash = false;

    /// <summary>
    /// Legacy flag: when true, Harmony skips RunningGame RenderToPrimary/post passes.
    /// Login sweep no longer arms this (0.8.25+) — world flash is blocked via
    /// <see cref="LodLoginBakeWorldHide"/> while the present pipeline still runs so
    /// Ortho/AfterFinalComposition splash IRenderers can paint and present.
    /// </summary>
    public static bool SuppressRunningGameRender { get; set; }

    /// <summary><see cref="GuiScreenRunningGame.handOverRenderingToRunningGame"/> was blocked.</summary>
    public static bool HandoverDeferred { get; private set; }

    /// <summary>Harmony must allow the next handover invoke (completion path).</summary>
    internal static bool AllowHandoverPassThrough { get; private set; }

    public static void Arm()
    {
        // Sweep owns handover again — stop character-wait pass-through.
        AllowHandoverPassThrough = false;
        SweepActive = true;
        // Do NOT suppress RunningGame render — skipping RenderToPrimary blanks the
        // framebuffer present path so Ortho splash never reaches the screen (black).
        SuppressRunningGameRender = false;
    }

    /// <summary>
    /// While waiting on a real character/class dialog, release any blocked handover so
    /// vanilla can enter RunningGame / show the UI, and keep pass-through on until
    /// <see cref="Arm"/> starts the visit sweep.
    /// </summary>
    public static void AllowHandoverWhileCharacterPending(ICoreClientAPI capi)
    {
        if (HandoverDeferred)
            ClearHandoverDeferral(capi, "character-wait", force: true);
        AllowHandoverPassThrough = true;
    }

    public static void MarkHandoverDeferred() => HandoverDeferred = true;

    /// <summary>
    /// Complete deferred handover and switch to RunningGame without clearing
    /// <see cref="SweepActive"/> — required so RenderToDefaultFramebuffer presents
    /// after char-create while the sweep overlay is armed.
    /// </summary>
    public static void EnsureRunningGameRenderPath(ICoreClientAPI capi)
    {
        if (!SweepActive) return;

        AllowHandoverPassThrough = true;
        try
        {
            var clientMain = (ClientMain)capi.World;
            GuiScreenRunningGame? running = clientMain.ScreenRunningGame;
            if (running == null) return;

            if (HandoverDeferred && HandoverMethod != null)
                HandoverMethod.Invoke(running, null);
            HandoverDeferred = false;

            LoadScreenNoLoadCallMethod?.Invoke(running.ScreenManager, new object[] { running });
        }
        catch (Exception ex)
        {
            capi.Logger.Warning(
                "[DistantVistas] Login sweep: EnsureRunningGameRenderPath failed ({0}).", ex.Message);
        }
        finally
        {
            AllowHandoverPassThrough = false;
        }
    }

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
        // Clear hold flags first so a failed screen attach cannot leave Loading… stuck.
        HandoverDeferred = false;
        SuppressRunningGameRender = false;
        SweepActive = false;

        AllowHandoverPassThrough = true;
        try
        {
            TrySetLoadingText(capi, "Loading… — Finishing 100%");

            var clientMain = (ClientMain)capi.World;
            GuiScreenRunningGame? running = clientMain.ScreenRunningGame;
            if (running == null)
            {
                capi.Logger.Warning(
                    "[DistantVistas] Login sweep: ScreenRunningGame null — flags cleared (reason={0}).",
                    reason);
            }
            else
            {
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
                        "[DistantVistas] Login sweep: LoadScreenNoLoadCall(running) failed ({0}).",
                        ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            capi.Logger.Warning(
                "[DistantVistas] Login sweep: handover to running game failed ({0}) — flags already cleared.",
                ex.Message);
            TryForceRunningScreen(capi);
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

    static void TrySetLoadingText(ICoreClientAPI capi, string text)
    {
        try
        {
            var clientMain = (ClientMain)capi.World;
            GuiScreenRunningGame? running = clientMain.ScreenRunningGame;
            if (running?.ScreenManager != null)
                running.ScreenManager.loadingText = text;
        }
        catch
        {
            // Best-effort status only.
        }
    }

    static void TryForceRunningScreen(ICoreClientAPI capi)
    {
        try
        {
            var clientMain = (ClientMain)capi.World;
            GuiScreenRunningGame? running = clientMain.ScreenRunningGame;
            if (running == null) return;
            LoadScreenNoLoadCallMethod?.Invoke(running.ScreenManager, new object[] { running });
        }
        catch
        {
            // Flags already cleared; player can Esc to menu.
        }
    }

    /// <inheritdoc cref="ClearHandoverDeferral"/>
    public static void CompleteHandover(ICoreClientAPI capi) =>
        ClearHandoverDeferral(capi, "legacy", force: true);
}
