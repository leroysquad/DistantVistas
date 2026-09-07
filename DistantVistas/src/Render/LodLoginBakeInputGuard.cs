using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace DistantVistas;

/// <summary>
/// Full-screen HUD overlay for the login visit sweep. Cairo splash art, progress bar,
/// and input capture — never OrthoMode, never present-path GL.
/// </summary>
public sealed class LodLoginBakeInputGuard : HudElement
{
    static readonly AssetLocation BackdropLoc = new("distantvistas", "gui/login-backdrop.png");
    static readonly double[] ProgressColor = { 0.46, 0.72, 0.38, 1.0 };

    bool wantActive;
    bool hasRendered;
    float progress;
    string detail = "Preparing…";
    int composedW;
    int composedH;
    ImageSurface? backdrop;
    bool backdropTried;
    int composeFailStreak;

    public Action? OnCancelRequested;

    public LodLoginBakeInputGuard(ICoreClientAPI capi) : base(capi) { }

    public override string ToggleKeyCombinationCode => "";

    public override EnumDialogType DialogType => EnumDialogType.HUD;

    // HUD dialogs do not increment ClientMain.DialogsOpened, so FPS look stays
    // grabbed unless DisableMouseGrab is true. PreferUngrabbed is Dialog-only in
    // UpdateFreeMouse; still set it while we are up.
    public override bool PrefersUngrabbedMouse => wantActive && IsOpened();

    public override bool DisableMouseGrab => wantActive && IsOpened();

    public override double InputOrder => 10.0;

    public override double DrawOrder => 1.02;

    public override bool CaptureAllInputs() => wantActive && IsOpened();

    public override bool CaptureRawMouse() => wantActive && IsOpened();

    public override bool ShouldReceiveKeyboardEvents() => wantActive && IsOpened();

    public override bool ShouldReceiveMouseEvents() => wantActive && IsOpened();

    public bool HasRendered => hasRendered && IsOpened();

    public bool IsReady => HasRendered;

    public override bool OnEscapePressed()
    {
        if (!wantActive || !IsOpened()) return false;
        OnCancelRequested?.Invoke();
        return true;
    }

    public void SetProgress(float fraction, string status)
    {
        progress = Math.Clamp(fraction, 0f, 1f);
        detail = string.IsNullOrEmpty(status) ? "Preparing…" : status;
        ApplyProgress();
    }

    public void SetOverlayAlpha(float alpha) { }

    public void RequestShow()
    {
        wantActive = true;
        TryEnsureOpen();
    }

    public void RequestHide()
    {
        LodLoginBakeInputLock.HoldLook(capi);
        wantActive = false;
        hasRendered = false;
        composeFailStreak = 0;
        if (IsOpened()) TryClose();
        RestoreLookNowAndNextTick();
    }

    public override void OnMouseMove(MouseEvent args)
    {
        if (wantActive && IsOpened())
        {
            LodLoginBakeInputLock.HoldLook(capi);
            args.Handled = true;
            return;
        }

        base.OnMouseMove(args);
    }

    public override void OnGuiClosed()
    {
        RestoreLookNowAndNextTick();
        ReleaseBackdrop();
        base.OnGuiClosed();
    }

    /// <summary>Retry until compose succeeds — window bounds are often zero on LevelFinalize.</summary>
    public void TryEnsureOpen()
    {
        if (!wantActive) return;
        if (NeedsCompose() && composeFailStreak >= 8) return;
        if (NeedsCompose() && !TryCompose())
        {
            return;
        }
        if (IsOpened())
        {
            hasRendered = true;
            return;
        }

        try
        {
            TryOpen();
            if (IsOpened())
                hasRendered = true;
        }
        catch
        {
            // HoldPlayerControls still blocks play if dialog open fails.
        }
    }

    public override void OnRenderGUI(float deltaTime)
    {
        if (wantActive)
        {
            LodLoginBakeInputLock.HoldLook(capi);
            if (NeedsCompose() && composeFailStreak < 8) TryCompose();
            if (!IsOpened()) TryEnsureOpen();
            else hasRendered = true;
        }

        base.OnRenderGUI(deltaTime);
    }

    void RestoreLookNowAndNextTick()
    {
        LodLoginBakeInputLock.RestoreLook(capi);
        try
        {
            capi.Event.EnqueueMainThreadTask(
                () => LodLoginBakeInputLock.RestoreLook(capi),
                "dv-look-drain");
        }
        catch
        {
        }
    }

    bool NeedsCompose()
    {
        ElementBounds? bounds = SafeBounds();
        if (bounds == null) return false;
        int w = (int)Math.Round(bounds.fixedWidth);
        int h = (int)Math.Round(bounds.fixedHeight);
        return SingleComposer == null || w != composedW || h != composedH;
    }

    bool TryCompose()
    {
        EnsureBackdrop();
        if (backdrop != null && TryComposeCore(useArt: true)) return true;
        return TryComposeCore(useArt: false);
    }

    bool TryComposeCore(bool useArt)
    {
        try
        {
            ElementBounds? root = SafeBounds();
            if (root == null) return false;

            double w = root.fixedWidth;
            double h = root.fixedHeight;
            ElementBounds bg = ElementBounds.Fixed(0, 0, w, h);
            ElementBounds title = ElementBounds.Fixed(40, h * 0.34, w - 80, 44);
            ElementBounds pct = ElementBounds.Fixed(40, h * 0.42, w - 80, 28);
            ElementBounds bar = ElementBounds.Fixed(40, h * 0.48, w - 80, 22);
            ElementBounds status = ElementBounds.Fixed(40, h * 0.54, w - 80, 90);
            ElementBounds hint = ElementBounds.Fixed(40, h * 0.74, w - 80, 28);

            CairoFont titleFont = CairoFont.WhiteMediumText().WithFontSize(30);
            CairoFont pctFont = CairoFont.WhiteMediumText().WithFontSize(22);
            CairoFont statusFont = CairoFont.WhiteSmallText();
            CairoFont hintFont = CairoFont.WhiteSmallText();

            SingleComposer?.Dispose();
            GuiComposer compo = capi.Gui.CreateCompo("dvistas-login-sweep", bg);
            if (useArt)
            {
                // Art path: no AddInset. The 0.08 inset crushed the landscape to near-black.
                compo.AddStaticCustomDraw(bg.FlatCopy(), PaintBackdrop);
            }
            else
            {
                compo.AddShadedDialogBG(bg.FlatCopy(), false, 0, 1f);
                compo.AddInset(bg.FlatCopy(), 2, 0.08f);
                compo.AddStaticText("Distant Vistas", titleFont, EnumTextOrientation.Center, title, "dv-title");
            }
            SingleComposer = compo
                .AddDynamicText("0%", pctFont, pct, "dv-pct")
                .AddStatbar(bar, ProgressColor, false, "dv-progress")
                .AddDynamicText(detail, statusFont, status, "dv-status")
                .AddStaticText("Esc to pause and return to the menu", hintFont, EnumTextOrientation.Center, hint, "dv-hint")
                .Compose();

            composedW = (int)Math.Round(w);
            composedH = (int)Math.Round(h);
            composeFailStreak = 0;
            ApplyProgress();
            return SingleComposer != null;
        }
        catch
        {
            composeFailStreak++;
            return false;
        }
    }

    void ApplyProgress()
    {
        GuiComposer? c = SingleComposer;
        if (c == null) return;
        try
        {
            c.GetStatbar("dv-progress")?.SetValue(progress * 100f);
            int pct = (int)Math.Round(progress * 100f);
            c.GetDynamicText("dv-pct")?.SetNewText($"{pct}%", autoHeight: false, forceRedraw: true);
            c.GetDynamicText("dv-status")?.SetNewText(detail, autoHeight: false, forceRedraw: true);
        }
        catch
        {
            // Composer can be mid-recompose while the sweep ticks.
        }
    }

    void EnsureBackdrop()
    {
        if (backdrop != null) return;
        try
        {
            IAsset? asset = capi.Assets.TryGet(BackdropLoc.Clone().WithPathPrefixOnce("textures/"));
            if (asset == null)
            {
                backdropTried = true;
                return;
            }

            backdrop = GuiElement.getImageSurfaceFromAsset(capi, BackdropLoc, 255);
            backdropTried = true;
        }
        catch
        {
            backdrop = null;
            backdropTried = true;
        }
    }

    void PaintBackdrop(Context ctx, ImageSurface surface, ElementBounds currentBounds)
    {
        float fw = (float)currentBounds.InnerWidth;
        float fh = (float)currentBounds.InnerHeight;
        double dx = currentBounds.drawX;
        double dy = currentBounds.drawY;

        ctx.SetSourceRGBA(0, 0, 0, 1);
        ctx.Rectangle(dx, dy, fw, fh);
        ctx.Fill();

        ImageSurface? src = backdrop;
        if (src == null || src.Width <= 0 || src.Height <= 0) return;

        (float x, float y, float dw, float dh) = LodLoginSplashLayout.CoverFit(fw, fh, src.Width, src.Height);
        if (dw <= 1f || dh <= 1f) return;

        try
        {
            ctx.Save();
            ctx.Translate(dx + x, dy + y);
            ctx.Scale(dw / src.Width, dh / src.Height);
            ctx.SetSourceSurface(src, 0, 0);
            ctx.Operator = Operator.Source;
            ctx.Rectangle(0, 0, src.Width, src.Height);
            ctx.Fill();
            ctx.Restore();
        }
        catch
        {
            try { ctx.Restore(); } catch { }
        }
    }

    void ReleaseBackdrop()
    {
        backdrop?.Dispose();
        backdrop = null;
        backdropTried = false;
    }

    ElementBounds? SafeBounds()
    {
        float w = capi.Render.FrameWidth;
        float h = capi.Render.FrameHeight;
        if (w > 1f && h > 1f)
        {
            float scale = Math.Max(RuntimeEnv.GUIScale, 0.5f);
            return ElementBounds.Fixed(0, 0, w / scale, h / scale);
        }

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
