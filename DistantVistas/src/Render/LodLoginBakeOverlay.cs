using Vintagestory.API.Client;

namespace DistantVistas;

/// <summary>
/// Coordinates vanilla world-loading UI and input blocking during the login visit sweep.
/// </summary>
public sealed class LodLoginBakeOverlay : IDisposable
{
    readonly LodLoginBakeVanillaLoadingHold vanillaLoading;
    readonly LodLoginBakeInputGuard inputGuard;

    public LodLoginBakeOverlay(ICoreClientAPI capi, LodLoginBakeVanillaLoadingHold vanillaLoading)
    {
        this.vanillaLoading = vanillaLoading;
        inputGuard = new LodLoginBakeInputGuard(capi);
    }

    public Action? OnCancelRequested
    {
        get => inputGuard.OnCancelRequested;
        set => inputGuard.OnCancelRequested = value;
    }

    public bool IsReady => vanillaLoading.IsReady;
    public bool HasRendered => vanillaLoading.HasRendered;

    public void UpdateProgress(float fraction, string detail) =>
        vanillaLoading.SetProgress(fraction, detail);

    public void SetOverlayAlpha(float alpha) =>
        vanillaLoading.SetOverlayAlpha(alpha);

    public void Show()
    {
        vanillaLoading.Show();
        inputGuard.RequestShow();
    }

    public void EnsureInputBlocked() => inputGuard.TryEnsureOpen();

    public void Hide()
    {
        vanillaLoading.Hide();
        inputGuard.RequestHide();
    }

    public void Dispose() => Hide();
}
