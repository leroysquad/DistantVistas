using Vintagestory.API.Client;

namespace DistantVistas;

/// <summary>
/// Paints the Distant Vistas login visit sweep splash: landscape backdrop, arched title,
/// progress bar, and Preparing/Visiting/Finishing status. Drawn via
/// <see cref="LodLoginBakeVanillaLoadingHold"/> on ortho and after-final passes.
/// </summary>
public sealed class LodLoginBakeSplashOverlay : IDisposable
{
    readonly LodLoginBakeScreenRenderer splash;

    public bool IsReady => splash.Active;
    public bool HasRendered => splash.HasEverPaintedOpaque;

    public LodLoginBakeSplashOverlay(ICoreClientAPI capi)
    {
        splash = new LodLoginBakeScreenRenderer(capi, stockOnly: false);
    }

    public void Show()
    {
        splash.Active = true;
        splash.SetProgress(0f, "Loading…");
    }

    public void Hide() => splash.Active = false;

    public void SetProgress(float fraction, string detail) =>
        splash.SetProgress(fraction, detail);

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage) =>
        splash.OnRenderFrame(deltaTime, stage);

    /// <summary>Guaranteed present-path paint (OrthoMode + PerspectiveMode pair).</summary>
    public void PaintSweepFrame() => splash.PaintSweepFrame();

    public void Dispose() => splash.Dispose();
}
