using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace DistantVistas;

/// <summary>
/// Covers the login visit sweep with the Distant Vistas splash (backdrop + title + progress bar)
/// and keeps <see cref="ScreenManager.loadingText"/> updated. During the sweep the client stays on
/// <see cref="GuiScreenRunningGame"/> so Ortho/AfterFinalComposition IRenderers present the splash
/// every frame; terrain flash is blocked by <see cref="LodLoginBakeWorldHide"/>. Harmony skips
/// <see cref="GuiScreenLoadingGame.RenderToDefaultFramebuffer"/> so async-sound waits cannot
/// block sweep ticks.
/// </summary>
public sealed class LodLoginBakeVanillaLoadingHold : IRenderer, IDisposable
{
    static readonly FieldInfo? CurrentScreenField = typeof(ScreenManager).GetField(
        "CurrentScreen", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    static readonly MethodInfo? LoadScreenNoLoadCallMethod = typeof(ScreenManager).GetMethod(
        "LoadScreenNoLoadCall",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(GuiScreen) },
        modifiers: null);

    readonly ICoreClientAPI capi;
    readonly LodLoginBakeSplashOverlay splashOverlay;

    GuiScreenRunningGame? runningScreen;
    ScreenManager? screenManager;
    string vanillaLine = "";
    string dvDetail = "";
    float lastFraction;
    bool active;
    bool loggedResolve;

    /// <summary>Fired at the start of each draw pass, before any overlay draw.</summary>
    public Action<float>? OnRenderPulse;

    public bool IsReady => splashOverlay.IsReady;
    public bool HasRendered => splashOverlay.HasRendered;
    /// <summary>Always false — vanilla loader draw is bypassed during sweep.</summary>
    public bool UsesVanillaScreen => false;

    public double RenderOrder => 1.5;
    public int RenderRange => 9998;

    public LodLoginBakeVanillaLoadingHold(ICoreClientAPI capi)
    {
        this.capi = capi;
        splashOverlay = new LodLoginBakeSplashOverlay(capi);
    }

    public void Show()
    {
        active = true;
        loggedResolve = false;
        vanillaLine = "";
        dvDetail = "";
        Resolve();
    }

    public void Hide()
    {
        active = false;
        splashOverlay.Hide();
    }

    public void SetProgress(float fraction, string detail)
    {
        lastFraction = Math.Clamp(fraction, 0f, 1f);
        dvDetail = detail ?? "";
        ApplyLoadingText();
        splashOverlay.SetProgress(lastFraction, FormatDetailWithPercent(dvDetail, lastFraction));
    }

    public void SetOverlayAlpha(float alpha) { }

    /// <summary>Present-path splash paint — Harmony Prefix on RunningGame/LoadingGame framebuffer.</summary>
    public void PaintSweepFrame() => splashOverlay.PaintSweepFrame();

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (!active && !LodLoginBakeSweepGate.SweepActive) return;
        if (LodLoginBakeSweepGate.CharacterUiBlocksSplash) return;

        bool drawPass = stage == EnumRenderStage.Ortho
            || stage == EnumRenderStage.AfterFinalComposition
            || stage == EnumRenderStage.Done;
        if (!drawPass) return;

        if (LodLoginBakeSweepGate.SplashGlAllowed
            && !splashOverlay.HasRendered)
            LodLoginBakeSweepGate.EnsureRunningGameRenderPath(capi);

        OnRenderPulse?.Invoke(deltaTime);

        EnsureRunningScreenCurrent();
        ApplyLoadingText();
        splashOverlay.OnRenderFrame(deltaTime, stage);
    }

    void EnsureRunningScreenCurrent()
    {
        if (screenManager == null || runningScreen == null) return;
        GuiScreen? current = CurrentScreenField?.GetValue(screenManager) as GuiScreen;
        if (current == runningScreen) return;
        TryActivateRunningScreen();
    }

    void ApplyLoadingText()
    {
        if (screenManager == null) return;

        if (string.IsNullOrWhiteSpace(vanillaLine))
            CaptureVanillaLine();

        screenManager.loadingText = FormatLoadingText(
            vanillaLine, FormatDetailWithPercent(dvDetail, lastFraction));
    }

    void CaptureVanillaLine()
    {
        if (screenManager == null) return;
        string current = screenManager.loadingText ?? "";
        if (string.IsNullOrWhiteSpace(current))
        {
            vanillaLine = "Loading…";
            return;
        }

        int sep = current.IndexOf(" — ", StringComparison.Ordinal);
        vanillaLine = sep > 0 ? current[..sep] : current;
    }

    static string FormatDetailWithPercent(string detail, float fraction)
    {
        int pct = (int)Math.Round(Math.Clamp(fraction, 0f, 1f) * 100);
        if (string.IsNullOrWhiteSpace(detail))
            return pct + "%";

        if (detail.Contains('%', StringComparison.Ordinal))
            return detail;

        return pct + "% — " + detail;
    }

    static string FormatLoadingText(string vanilla, string dv)
    {
        if (string.IsNullOrWhiteSpace(dv))
            return string.IsNullOrWhiteSpace(vanilla) ? "Loading…" : vanilla;
        if (string.IsNullOrWhiteSpace(vanilla) || vanilla == dv)
            return dv;
        if (vanilla.Contains(dv, StringComparison.Ordinal))
            return vanilla;
        return vanilla + " — " + dv;
    }

    void Resolve()
    {
        try
        {
            var clientMain = (ClientMain)capi.World;
            runningScreen = clientMain.ScreenRunningGame;
            screenManager = runningScreen.ScreenManager;

            CaptureVanillaLine();

            GuiScreen? current = CurrentScreenField?.GetValue(screenManager) as GuiScreen;
            if (current is GuiScreenLoadingGame)
            {
                if (!loggedResolve)
                {
                    loggedResolve = true;
                    capi.Logger.Notification(
                        "[DistantVistas] Login sweep: leaving deferred vanilla loader — async-sound draw would block sweep ticks.");
                }
            }

            if (!TryActivateRunningScreen())
            {
                capi.Logger.Warning(
                    "[DistantVistas] Login sweep: could not switch to running-game screen — splash overlay only.");
            }

            splashOverlay.Show();
            ApplyLoadingText();

            if (LodLoginBakeSweepGate.SplashGlAllowed)
                LodLoginBakeSweepGate.EnsureRunningGameRenderPath(capi);

            if (!loggedResolve)
            {
                loggedResolve = true;
                capi.Logger.Notification(
                    "[DistantVistas] Login sweep: DV splash loading cover + ScreenManager.loadingText (running-game screen, vanilla loader bypassed).");
            }
        }
        catch (Exception ex)
        {
            capi.Logger.Warning(
                "[DistantVistas] Login sweep: overlay setup failed ({0}) — splash cover only.",
                ex.Message);
            splashOverlay.Show();
        }
    }

    bool TryActivateRunningScreen()
    {
        if (screenManager == null || runningScreen == null) return false;

        try
        {
            if (LoadScreenNoLoadCallMethod == null) return false;
            LoadScreenNoLoadCallMethod.Invoke(screenManager, new object[] { runningScreen });
            return true;
        }
        catch (Exception ex)
        {
            capi.Logger.Warning(
                "[DistantVistas] Login sweep: LoadScreenNoLoadCall(running) failed ({0}).", ex.Message);
            return false;
        }
    }

    public void Dispose()
    {
        active = false;
        splashOverlay.Dispose();
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Ortho);
        capi.Event.UnregisterRenderer(this, EnumRenderStage.AfterFinalComposition);
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Done);
    }
}
