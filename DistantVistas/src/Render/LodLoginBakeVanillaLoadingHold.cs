using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace DistantVistas;

/// <summary>
/// Keeps the player on Vintage Story's native world-loading UI during the login visit
/// sweep by re-rendering <see cref="GuiScreenLoadingGame"/> and updating
/// <see cref="ScreenManager.loadingText"/> with sweep progress.
/// </summary>
public sealed class LodLoginBakeVanillaLoadingHold : IRenderer, IDisposable
{
    static readonly FieldInfo? CachedScreensField = typeof(ScreenManager).GetField(
        "CachedScreens", BindingFlags.Instance | BindingFlags.NonPublic);

    static readonly MethodInfo? LoadAndCacheScreenMethod = typeof(ScreenManager).GetMethod(
        "LoadAndCacheScreen",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(Type) },
        modifiers: null);

    readonly ICoreClientAPI capi;
    LodLoginBakeStockLoadingFallback? stockFallback;

    GuiScreenLoadingGame? loadingScreen;
    ScreenManager? screenManager;
    string detail = "";
    bool active;
    bool useStockFallback;
    bool loggedFirstPaint;
    bool loggedResolve;

    /// <summary>
    /// Fired at the start of each draw pass, before any blocking vanilla loading draw.
    /// </summary>
    public Action<float>? OnRenderPulse;

    public bool IsReady => loadingScreen != null || stockFallback?.IsReady == true;
    public bool HasRendered { get; private set; }

    public double RenderOrder => 1.5;
    public int RenderRange => 9998;

    public LodLoginBakeVanillaLoadingHold(ICoreClientAPI capi) => this.capi = capi;

    public void Show()
    {
        active = true;
        HasRendered = false;
        loggedFirstPaint = false;
        loggedResolve = false;
        useStockFallback = false;
        detail = "";
        stockFallback?.Hide();
        Resolve();
    }

    public void Hide()
    {
        active = false;
        stockFallback?.Hide();
    }

    public void SetProgress(float fraction, string detail)
    {
        this.detail = detail ?? "";
        ApplyLoadingText();
        stockFallback?.SetProgress(fraction, this.detail);
    }

    /// <summary>No-op — vanilla loading screen does not fade; release hides it.</summary>
    public void SetOverlayAlpha(float alpha) { }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (!active) return;

        // Pulse on Ortho only — never call blocking RenderToDefaultFramebuffer on this pass.
        // If vanilla draw blocks on async sound, AfterFinalComposition may stall for the rest
        // of the frame, but the next frame's Ortho pulse still advances the sweep.
        if (stage == EnumRenderStage.Ortho)
        {
            OnRenderPulse?.Invoke(deltaTime);
            if (!useStockFallback && loadingScreen != null)
                ApplyLoadingText();
            else
                stockFallback?.OnRenderFrame(deltaTime, stage);
            return;
        }

        if (stage != EnumRenderStage.AfterFinalComposition) return;

        if (!useStockFallback && loadingScreen != null)
        {
            try
            {
                ApplyLoadingText();
                loadingScreen.RenderToDefaultFramebuffer(deltaTime);
                HasRendered = true;
                if (!loggedFirstPaint)
                {
                    loggedFirstPaint = true;
                    capi.Logger.Notification(
                        "[DistantVistas] Login sweep: vanilla loading screen painted.");
                }
                return;
            }
            catch (Exception ex)
            {
                capi.Logger.Warning(
                    "[DistantVistas] Login sweep: vanilla loading draw failed ({0}) — stock fallback.",
                    ex.Message);
                useStockFallback = true;
                stockFallback ??= new LodLoginBakeStockLoadingFallback(capi);
                stockFallback.Show();
            }
        }

        stockFallback?.OnRenderFrame(deltaTime, stage);
        if (stockFallback?.HasRendered == true)
            HasRendered = true;
    }

    void ApplyLoadingText()
    {
        if (screenManager == null) return;
        screenManager.loadingText = string.IsNullOrWhiteSpace(detail)
            ? "Loading…"
            : detail;
    }

    void Resolve()
    {
        try
        {
            var clientMain = (ClientMain)capi.World;
            GuiScreenRunningGame running = clientMain.ScreenRunningGame;
            screenManager = running.ScreenManager;
            loadingScreen = FindCachedLoadingScreen(screenManager);
            if (loadingScreen == null)
            {
                capi.Logger.Warning(
                    "[DistantVistas] Login sweep: no cached vanilla loading screen — stock fallback.");
                useStockFallback = true;
                stockFallback = new LodLoginBakeStockLoadingFallback(capi);
                stockFallback.Show();
                capi.Logger.Notification(
                    "[DistantVistas] Login sweep: stock Loading… fallback (vanilla screen unavailable).");
                return;
            }

            ApplyLoadingText();

            if (!loggedResolve)
            {
                loggedResolve = true;
                capi.Logger.Notification(
                    "[DistantVistas] Login sweep: using vanilla world loading screen during visit bake.");
            }
            return;
        }
        catch (Exception ex)
        {
            capi.Logger.Warning(
                "[DistantVistas] Login sweep: could not attach to vanilla loading screen ({0}) — stock fallback.",
                ex.Message);
        }

        useStockFallback = true;
        stockFallback = new LodLoginBakeStockLoadingFallback(capi);
        stockFallback.Show();
        capi.Logger.Notification(
            "[DistantVistas] Login sweep: stock Loading… fallback (vanilla screen unavailable).");
    }

    static GuiScreenLoadingGame? FindCachedLoadingScreen(ScreenManager sm)
    {
        // Prefer the live cache dictionary — LoadAndCacheScreen can construct a fresh
        // GuiScreenLoadingGame and re-trigger async sound loading on empty cache.
        if (CachedScreensField?.GetValue(sm) is System.Collections.IDictionary dict)
        {
            Type loadingType = typeof(GuiScreenLoadingGame);
            foreach (System.Collections.DictionaryEntry entry in dict)
            {
                if (entry.Key is Type t && t == loadingType && entry.Value is GuiScreenLoadingGame loading)
                    return loading;
            }
        }

        return TryLoadAndCacheScreen(sm);
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
        stockFallback?.Dispose();
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Ortho);
        capi.Event.UnregisterRenderer(this, EnumRenderStage.AfterFinalComposition);
    }
}
