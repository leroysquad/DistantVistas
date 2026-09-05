using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace DistantVistas;

/// <summary>
/// Keeps the player on Vintage Story's native world-loading UI during the login visit
/// sweep by re-activating <see cref="GuiScreenLoadingGame"/> on the
/// <see cref="ScreenManager"/> and appending Distant Vistas progress to
/// <see cref="ScreenManager.loadingText"/>.
/// </summary>
public sealed class LodLoginBakeVanillaLoadingHold : IRenderer, IDisposable
{
    static readonly FieldInfo? CachedScreensField = typeof(ScreenManager).GetField(
        "CachedScreens", BindingFlags.Instance | BindingFlags.NonPublic);

    static readonly FieldInfo? CurrentScreenField = typeof(ScreenManager).GetField(
        "CurrentScreen", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    static readonly MethodInfo? LoadAndCacheScreenMethod = typeof(ScreenManager).GetMethod(
        "LoadAndCacheScreen",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(Type) },
        modifiers: null);

    static readonly MethodInfo? LoadScreenMethod = typeof(ScreenManager).GetMethod(
        "LoadScreen",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(GuiScreen) },
        modifiers: null);

    static readonly MethodInfo? LoadScreenNoLoadCallMethod = typeof(ScreenManager).GetMethod(
        "LoadScreenNoLoadCall",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(GuiScreen) },
        modifiers: null);

    readonly ICoreClientAPI capi;
    LodLoginBakeStockLoadingFallback? stockFallback;

    GuiScreenLoadingGame? loadingScreen;
    GuiScreenRunningGame? runningScreen;
    ScreenManager? screenManager;
    string vanillaLine = "";
    string dvDetail = "";
    bool active;
    bool useStockFallback;
    bool screenHeld;
    bool loggedFirstPaint;
    bool loggedResolve;
    bool loggedReopen;

    /// <summary>Fired at the start of each draw pass, before any blocking vanilla draw.</summary>
    public Action<float>? OnRenderPulse;

    public bool IsReady => loadingScreen != null || stockFallback?.IsReady == true;
    public bool HasRendered { get; private set; }
    public bool UsesVanillaScreen => active && !useStockFallback && loadingScreen != null;

    public double RenderOrder => 1.5;
    public int RenderRange => 9998;

    public LodLoginBakeVanillaLoadingHold(ICoreClientAPI capi) => this.capi = capi;

    public void Show()
    {
        active = true;
        HasRendered = false;
        loggedFirstPaint = false;
        loggedResolve = false;
        loggedReopen = false;
        useStockFallback = false;
        screenHeld = false;
        vanillaLine = "";
        dvDetail = "";
        stockFallback?.Hide();
        Resolve();
    }

    public void Hide()
    {
        active = false;
        stockFallback?.Hide();
        RestoreRunningScreen();
    }

    public void SetProgress(float fraction, string detail)
    {
        dvDetail = detail ?? "";
        ApplyLoadingText();
        stockFallback?.SetProgress(fraction, dvDetail);
    }

    public void SetOverlayAlpha(float alpha) { }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (!active) return;

        bool drawPass = stage == EnumRenderStage.Ortho
            || stage == EnumRenderStage.AfterFinalComposition;
        if (!drawPass) return;

        OnRenderPulse?.Invoke(deltaTime);

        if (!useStockFallback && loadingScreen != null)
        {
            EnsureLoadingScreenCurrent();
            ApplyLoadingText();

            try
            {
                loadingScreen.RenderToDefaultFramebuffer(deltaTime);
                HasRendered = true;
                if (!loggedFirstPaint)
                {
                    loggedFirstPaint = true;
                    capi.Logger.Notification(
                        "[DistantVistas] Login sweep: vanilla loading screen painted.");
                }
            }
            catch (Exception ex)
            {
                capi.Logger.Warning(
                    "[DistantVistas] Login sweep: vanilla loading draw failed ({0}) — stock fallback.",
                    ex.Message);
                SwitchToStockFallback();
            }

            if (HasRendered) return;
        }

        stockFallback?.OnRenderFrame(deltaTime, stage);
        if (stockFallback?.HasRendered == true)
            HasRendered = true;
    }

    void EnsureLoadingScreenCurrent()
    {
        if (screenManager == null || loadingScreen == null) return;
        GuiScreen? current = CurrentScreenField?.GetValue(screenManager) as GuiScreen;
        if (current == loadingScreen) return;

        if (!loggedReopen)
        {
            loggedReopen = true;
            capi.Logger.Notification(
                "[DistantVistas] Login sweep: re-opening vanilla world loading screen.");
        }

        TryActivateLoadingScreen(fromCache: true);
    }

    void ApplyLoadingText()
    {
        if (screenManager == null) return;

        if (string.IsNullOrWhiteSpace(vanillaLine))
            CaptureVanillaLine();

        screenManager.loadingText = FormatLoadingText(vanillaLine, dvDetail);
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

            loadingScreen = FindCachedLoadingScreen(screenManager);
            bool created = false;
            if (loadingScreen == null)
            {
                loadingScreen = new GuiScreenLoadingGame(
                    screenManager, runningScreen.ParentScreen ?? runningScreen);
                created = true;
                capi.Logger.Notification(
                    "[DistantVistas] Login sweep: created vanilla world loading screen instance.");
            }

            if (!TryActivateLoadingScreen(fromCache: !created))
            {
                capi.Logger.Warning(
                    "[DistantVistas] Login sweep: could not activate vanilla loading screen — stock fallback.");
                SwitchToStockFallback();
                return;
            }

            CaptureVanillaLine();
            ApplyLoadingText();

            if (!loggedResolve)
            {
                loggedResolve = true;
                capi.Logger.Notification(
                    "[DistantVistas] Login sweep: holding vanilla world loading screen during visit bake.");
            }
        }
        catch (Exception ex)
        {
            capi.Logger.Warning(
                "[DistantVistas] Login sweep: could not attach to vanilla loading screen ({0}) — stock fallback.",
                ex.Message);
            SwitchToStockFallback();
        }
    }

    void SwitchToStockFallback()
    {
        useStockFallback = true;
        screenHeld = false;
        stockFallback ??= new LodLoginBakeStockLoadingFallback(capi);
        stockFallback.Show();
        capi.Logger.Notification(
            "[DistantVistas] Login sweep: stock Loading… fallback (opaque cover).");
    }

    bool TryActivateLoadingScreen(bool fromCache)
    {
        if (screenManager == null || loadingScreen == null) return false;

        try
        {
            MethodInfo? method = fromCache ? LoadScreenNoLoadCallMethod : LoadScreenMethod;
            method ??= LoadScreenNoLoadCallMethod ?? LoadScreenMethod;
            if (method == null) return false;

            method.Invoke(screenManager, new object[] { loadingScreen });
            screenHeld = true;
            return true;
        }
        catch (Exception ex)
        {
            capi.Logger.Warning(
                "[DistantVistas] Login sweep: LoadScreen failed ({0}).", ex.Message);
            return false;
        }
    }

    void RestoreRunningScreen()
    {
        if (!screenHeld || screenManager == null || runningScreen == null) return;
        try
        {
            LoadScreenNoLoadCallMethod?.Invoke(screenManager, new object[] { runningScreen });
        }
        catch
        {
            // Best-effort restore on release.
        }
        finally
        {
            screenHeld = false;
        }
    }

    static GuiScreenLoadingGame? FindCachedLoadingScreen(ScreenManager sm)
    {
        GuiScreenLoadingGame? fromApi = TryLoadAndCacheScreen(sm);
        if (fromApi != null) return fromApi;

        if (CachedScreensField?.GetValue(sm) is not System.Collections.IDictionary dict)
            return null;

        Type loadingType = typeof(GuiScreenLoadingGame);
        foreach (System.Collections.DictionaryEntry entry in dict)
        {
            if (entry.Key is Type t && t == loadingType && entry.Value is GuiScreenLoadingGame loading)
                return loading;
        }

        return null;
    }

    static GuiScreenLoadingGame? TryLoadAndCacheScreen(ScreenManager sm)
    {
        if (LoadAndCacheScreenMethod == null) return null;
        try
        {
            object? result = LoadAndCacheScreenMethod.Invoke(sm, new object[] { typeof(GuiScreenLoadingGame) });
            return result as GuiScreenLoadingGame;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        active = false;
        RestoreRunningScreen();
        stockFallback?.Dispose();
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Ortho);
        capi.Event.UnregisterRenderer(this, EnumRenderStage.AfterFinalComposition);
    }
}
