using Vintagestory.API.Client;

namespace DistantVistas;

/// <summary>
/// Coordinates the render-layer fullscreen cover and an input-blocking dialog. Visuals are
/// drawn by <see cref="LodLoginBakeScreenRenderer"/> every frame; this dialog captures
/// keyboard, mouse and inventory shortcuts that movement multipliers cannot stop.
/// </summary>
public sealed class LodLoginBakeOverlay : GuiDialog, IDisposable
{
    readonly LodLoginBakeScreenRenderer screen;

    public LodLoginBakeOverlay(ICoreClientAPI capi, LodLoginBakeScreenRenderer screen) : base(capi)
    {
        this.screen = screen;
    }

    public override string ToggleKeyCombinationCode => "";

    public override bool PrefersUngrabbedMouse => false;

    public override bool DisableMouseGrab => false;

    public override double InputOrder => 0;

    public override double DrawOrder => 1.04;

    public override bool CaptureAllInputs() => true;

    public override bool CaptureRawMouse() => true;

    public override bool ShouldReceiveKeyboardEvents() => true;

    public override bool ShouldReceiveMouseEvents() => true;

    public override bool OnEscapePressed() => true;

    public void ComposeDialog()
    {
        ElementBounds full = ElementBounds.Fill.WithParent(capi.Gui.WindowBounds);
        SingleComposer = capi.Gui
            .CreateCompo("dvistas-login-sweep", capi.Gui.WindowBounds)
            .AddGameOverlay(full, new[] { 0.0, 0.0, 0.0, 0.0 })
            .Compose();
    }

    public void UpdateProgress(float fraction, string detail) =>
        screen.SetProgress(fraction, detail);

    public void Show()
    {
        screen.Active = true;
        screen.SetProgress(0f, "Starting…");
        if (IsOpened()) return;
        ComposeDialog();
        TryOpen();
    }

    public void Hide()
    {
        screen.Active = false;
        if (IsOpened()) TryClose();
    }

    public new void Dispose()
    {
        Hide();
        base.Dispose();
    }
}
