using Vintagestory.API.Client;

namespace DistantVistas;

/// <summary>
/// Coordinates the render-layer fullscreen cover and a deferred input-blocking HUD guard.
/// Visuals are drawn by <see cref="LodLoginBakeScreenRenderer"/> every frame.
/// </summary>
public sealed class LodLoginBakeOverlay : IDisposable
{
    readonly LodLoginBakeScreenRenderer screen;
    readonly LodLoginBakeInputGuard inputGuard;

    public LodLoginBakeOverlay(ICoreClientAPI capi, LodLoginBakeScreenRenderer screen)
    {
        this.screen = screen;
        inputGuard = new LodLoginBakeInputGuard(capi);
    }

    public Action? OnCancelRequested
    {
        get => inputGuard.OnCancelRequested;
        set => inputGuard.OnCancelRequested = value;
    }

    public void UpdateProgress(float fraction, string detail) =>
        screen.SetProgress(fraction, detail);

    public void Show()
    {
        screen.Active = true;
        screen.SetProgress(0f, "Starting…");
        inputGuard.RequestShow();
    }

    public void EnsureInputBlocked() => inputGuard.TryEnsureOpen();

    public void Hide()
    {
        screen.Active = false;
        inputGuard.RequestHide();
    }

    public void Dispose() => Hide();
}
