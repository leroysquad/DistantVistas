using Vintagestory.API.Client;

namespace DistantVistas;

/// <summary>
/// Coordinates the login visit sweep overlay: one <see cref="HudElement"/> with Cairo
/// progress UI and input capture. Does not hijack the framebuffer present path.
/// </summary>
public sealed class LodLoginBakeOverlay : IDisposable
{
    readonly LodLoginBakeInputGuard inputGuard;

    public LodLoginBakeOverlay(ICoreClientAPI capi)
    {
        inputGuard = new LodLoginBakeInputGuard(capi);
    }

    public Action? OnCancelRequested
    {
        get => inputGuard.OnCancelRequested;
        set => inputGuard.OnCancelRequested = value;
    }

    public bool IsReady => inputGuard.IsReady;
    public bool HasRendered => inputGuard.HasRendered;

    public void UpdateProgress(float fraction, string detail) =>
        inputGuard.SetProgress(fraction, detail);

    public void SetOverlayAlpha(float alpha) =>
        inputGuard.SetOverlayAlpha(alpha);

    public void Show() => inputGuard.RequestShow();

    public void EnsureInputBlocked() => inputGuard.TryEnsureOpen();

    public void Hide()
    {
        inputGuard.RequestHide();
    }

    public void Dispose() => Hide();
}
