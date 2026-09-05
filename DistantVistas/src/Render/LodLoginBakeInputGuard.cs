using Vintagestory.API.Client;

namespace DistantVistas;

/// <summary>
/// Input-only HUD blocker for the login visit sweep. Visuals live in
/// <see cref="LodLoginBakeScreenRenderer"/>; this guard never throws during
/// <c>LevelFinalize</c> and retries compose/open once the viewport is valid.
/// </summary>
public sealed class LodLoginBakeInputGuard : HudElement
{
    bool wantActive;

    public LodLoginBakeInputGuard(ICoreClientAPI capi) : base(capi) { }

    public override string ToggleKeyCombinationCode => "";

    public override bool PrefersUngrabbedMouse => false;

    public override bool DisableMouseGrab => false;

    public override double InputOrder => 0;

    public override double DrawOrder => 1.02;

    public override bool CaptureAllInputs() => wantActive && IsOpened();

    public override bool CaptureRawMouse() => wantActive && IsOpened();

    public override bool ShouldReceiveKeyboardEvents() => wantActive && IsOpened();

    public override bool ShouldReceiveMouseEvents() => wantActive && IsOpened();

    public override bool OnEscapePressed() => wantActive && IsOpened();

    public void RequestShow()
    {
        wantActive = true;
        TryEnsureOpen();
    }

    public void RequestHide()
    {
        wantActive = false;
        if (IsOpened()) TryClose();
    }

    /// <summary>Retry until compose succeeds — window bounds are often zero on LevelFinalize.</summary>
    public void TryEnsureOpen()
    {
        if (!wantActive || IsOpened()) return;
        if (!TryCompose()) return;
        try
        {
            TryOpen();
        }
        catch
        {
            // Render cover + HoldPlayerControls still block play if dialog open fails.
        }
    }

    bool TryCompose()
    {
        try
        {
            ElementBounds? bounds = SafeBounds();
            if (bounds == null) return false;

            SingleComposer?.Dispose();
            SingleComposer = capi.Gui
                .CreateCompo("dvistas-login-sweep-input", bounds)
                .Compose();
            return SingleComposer != null;
        }
        catch
        {
            return false;
        }
    }

    ElementBounds? SafeBounds()
    {
        float w = capi.Render.FrameWidth;
        float h = capi.Render.FrameHeight;
        if (w > 1f && h > 1f)
            return ElementBounds.Fixed(0, 0, w, h);

        try
        {
            ElementBounds window = capi.Gui.WindowBounds;
            if (window != null && window.OuterWidth > 1 && window.OuterHeight > 1)
                return ElementBounds.Fixed(0, 0, window.OuterWidth, window.OuterHeight);
        }
        catch
        {
            // WindowBounds can throw before the first frame.
        }

        return null;
    }
}
