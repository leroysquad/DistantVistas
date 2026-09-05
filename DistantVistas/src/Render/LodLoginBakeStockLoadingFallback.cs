using Vintagestory.API.Client;

namespace DistantVistas;

/// <summary>
/// Minimal stock-looking loading cover when <see cref="GuiScreenLoadingGame"/> cannot be
/// re-rendered. Dark fill + "Loading…" + status — not the custom Distant Vistas splash.
/// </summary>
public sealed class LodLoginBakeStockLoadingFallback : IRenderer, IDisposable
{
    readonly ICoreClientAPI capi;
    readonly LodLoginBakeScreenRenderer stock;
    bool registered;

    public bool IsReady => stock.Active;
    public bool HasRendered => stock.HasEverPaintedOpaque;

    public double RenderOrder => 3.0;
    public int RenderRange => 9998;

    public LodLoginBakeStockLoadingFallback(ICoreClientAPI capi)
    {
        this.capi = capi;
        stock = new LodLoginBakeScreenRenderer(capi, stockOnly: true);
    }

    public void Show()
    {
        EnsureRegistered();
        stock.Active = true;
        stock.PrepareImmediate();
        stock.SetProgress(0f, "Loading…");
    }

    public void Hide()
    {
        stock.Active = false;
    }

    public void SetProgress(float fraction, string detail) =>
        stock.SetProgress(fraction, detail);

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage) =>
        stock.OnRenderFrame(deltaTime, stage);

    void EnsureRegistered()
    {
        if (registered) return;
        registered = true;
        capi.Event.RegisterRenderer(this, EnumRenderStage.Ortho, "distantvistas-login-stock-fallback");
        capi.Event.RegisterRenderer(this, EnumRenderStage.AfterFinalComposition,
            "distantvistas-login-stock-fallback-final");
    }

    public void Dispose()
    {
        stock.Dispose();
        if (!registered) return;
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Ortho);
        capi.Event.UnregisterRenderer(this, EnumRenderStage.AfterFinalComposition);
    }
}
