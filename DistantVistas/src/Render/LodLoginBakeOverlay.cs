using Vintagestory.API.Client;

namespace DistantVistas;

/// <summary>
/// Full-viewport join overlay while visited land is re-captured behind an opaque screen.
/// </summary>
public class LodLoginBakeOverlay : GuiDialog
{
    const string ProgressKey = "progress";
    const string StatusKey = "status";

    GuiElementStatbar? progressBar;

    public LodLoginBakeOverlay(ICoreClientAPI capi) : base(capi) { }

    public override string ToggleKeyCombinationCode => "";

    public override bool PrefersUngrabbedMouse => false;

    public void ComposeDialog()
    {
        ElementBounds dialogBounds = capi.Gui.WindowBounds;
        ElementBounds overlayBounds = ElementBounds.Fill.WithParent(dialogBounds);

        double panelW = 520;
        double panelH = 200;
        ElementBounds panelBounds = ElementBounds
            .Fixed((dialogBounds.InnerWidth - panelW) / 2, (dialogBounds.InnerHeight - panelH) / 2,
                panelW, panelH)
            .WithParent(dialogBounds);

        ElementBounds titleBounds = ElementBounds.Fixed(0, 0, panelW, 36).WithParent(panelBounds);
        ElementBounds subtitleBounds = ElementBounds.Fixed(0, 38, panelW, 28).WithParent(panelBounds);
        ElementBounds barBounds = ElementStdBounds.Statbar(EnumDialogArea.None, panelW - 40)
            .WithParent(panelBounds).WithFixedOffset(20, 88);
        ElementBounds detailBounds = ElementBounds.Fixed(0, 130, panelW, 48).WithParent(panelBounds);

        SingleComposer = capi.Gui
            .CreateCompo("dvistas-login-sweep", dialogBounds)
            .AddGameOverlay(overlayBounds, new[] { 0.06, 0.08, 0.11, 1.0 })
            .AddStaticText("Distant Vistas", CairoFont.WhiteDetailText(), titleBounds)
            .AddStaticText("Preparing visited land — capturing what is there this season.",
                CairoFont.WhiteSmallishText(), subtitleBounds)
            .AddStatbar(barBounds, new[] { 0.28, 0.55, 0.82 }, ProgressKey)
            .AddDynamicText("", CairoFont.WhiteSmallText(), detailBounds, StatusKey)
            .Compose();

        progressBar = SingleComposer.GetStatbar(ProgressKey);
        progressBar?.SetValue(0, 1);
        UpdateProgress(0f, "Starting…");
    }

    public void UpdateProgress(float fraction, string detail)
    {
        fraction = Math.Clamp(fraction, 0f, 1f);
        progressBar?.SetValue(fraction, 1);
        SingleComposer?.GetDynamicText(StatusKey)?.SetNewText(detail);
    }

    public void Show()
    {
        if (IsOpened()) return;
        ComposeDialog();
        base.TryOpen();
    }
}
