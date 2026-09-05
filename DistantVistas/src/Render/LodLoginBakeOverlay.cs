using Cairo;
using Vintagestory.API.Client;

namespace DistantVistas;

/// <summary>
/// Full-screen join overlay shown while visited LOD land is season-baked at login.
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
        ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
        ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.WithParent(dialogBounds);

        ElementBounds titleBounds = ElementBounds.Fixed(0, 8, 420, 30);
        titleBounds.WithParent(bgBounds);

        ElementBounds textBounds = ElementBounds.Fixed(0, 40, 420, 30);
        textBounds.WithParent(bgBounds);

        ElementBounds barBounds = ElementStdBounds.Statbar(EnumDialogArea.None, 380);
        barBounds.WithParent(bgBounds).FixedYOffset = 80;

        ElementBounds detailBounds = ElementBounds.Fixed(0, 120, 420, 24);
        detailBounds.WithParent(bgBounds);

        SingleComposer = capi.Gui
            .CreateCompo("dvistas-login-bake", dialogBounds)
            .AddShadedDialogBG(bgBounds, true, 5, 0.92f)
            .AddStaticText("Distant Vistas is loading…", CairoFont.WhiteDetailText(), titleBounds)
            .AddStaticText("Painting visited land for the current season. This runs once per login.",
                CairoFont.WhiteSmallishText(), textBounds)
            .AddStatbar(barBounds, new[] { 0.35, 0.62, 0.88 }, ProgressKey)
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
