using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Full-viewport ortho overlay that paints every frame above the 3D world. GuiDialog alone
/// is unreliable for hiding teleports; this cannot be skipped by the GUI composer.
/// </summary>
public sealed class LodLoginBakeScreenRenderer : IRenderer, IDisposable
{
    /// <summary>Landscape backdrop stretched to the viewport (solid dark fill when missing).</summary>
    public static readonly AssetLocation BackdropAsset =
        new("distantvistas", "textures/gui/login-backdrop.png");

    /// <summary>Arched single-color "Distant Vistas" title above the loading panel.</summary>
    public static readonly AssetLocation TitleAsset =
        new("distantvistas", "textures/gui/login-title-rainbow.png");

    const double PanelW = 520;
    const double PanelH = 156;
    const float TitleGap = 28f;
    const float TitleMaxWidthFrac = 0.62f;
    const float TitleMaxWidthPx = 560f;
    static readonly float[] DarkFill = { 0.06f, 0.08f, 0.11f, 1f };
    static readonly float[] BarTrack = { 0.12f, 0.14f, 0.18f, 1f };
    static readonly float[] BarFill = { 0.28f, 0.55f, 0.82f, 1f };
    static readonly float[] PanelFill = { 0.10f, 0.12f, 0.16f, 0.92f };
    static readonly double[] TitleFallbackColor = { 0.95, 0.86, 0.62, 1.0 };

    readonly ICoreClientAPI capi;
    readonly LoadedTexture backdrop;
    readonly LoadedTexture titleImage;
    readonly LoadedTexture titleFallbackTex;
    readonly LoadedTexture percentTex;
    readonly LoadedTexture statusTex;
    readonly Vec4f tint = new();
    readonly CairoFont titleFallbackFont;
    readonly CairoFont percentFont;
    readonly CairoFont statusFont;

    bool active;
    bool gfxReady;
    bool backdropMissing;
    bool titleMissing;
    float fraction;
    string status = "";
    string percentLabel = "0%";
    int whiteSubId = -1;

    public LodLoginBakeScreenRenderer(ICoreClientAPI capi)
    {
        this.capi = capi;
        backdrop = new LoadedTexture(capi);
        titleImage = new LoadedTexture(capi);
        titleFallbackTex = new LoadedTexture(capi);
        percentTex = new LoadedTexture(capi);
        statusTex = new LoadedTexture(capi);
        titleFallbackFont = CairoFont.WhiteDetailText()
            .WithFontSize(28)
            .WithColor(TitleFallbackColor);
        percentFont = CairoFont.WhiteDetailText().WithFontSize(18);
        statusFont = CairoFont.WhiteSmallText();
    }

    public bool Active
    {
        get => active;
        set
        {
            if (active == value) return;
            active = value;
            if (value)
            {
                gfxReady = false;
                EnsureGraphicsLoaded();
            }
        }
    }

    public double RenderOrder => 1.03;
    public int RenderRange => 9999;

    public void SetProgress(float progress, string detail)
    {
        fraction = Math.Clamp(progress, 0f, 1f);
        status = detail ?? "";
        percentLabel = $"{(int)Math.Round(fraction * 100)}%";
        RebuildText();
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (!active) return;

        EnsureGraphicsLoaded();

        IRenderAPI rapi = capi.Render;
        float w = rapi.FrameWidth;
        float h = rapi.FrameHeight;
        if (w <= 0 || h <= 0) return;

        DrawBackdrop(w, h);

        float titleW = 0;
        float titleH = 0;
        if (!titleMissing && titleImage.TextureId > 0 && titleImage.Width > 0)
        {
            titleW = Math.Min(w * TitleMaxWidthFrac, TitleMaxWidthPx);
            titleH = titleW * titleImage.Height / titleImage.Width;
        }
        else if (titleFallbackTex.TextureId > 0 && titleFallbackTex.Width > 0)
        {
            titleW = titleFallbackTex.Width;
            titleH = titleFallbackTex.Height;
        }

        float blockH = titleH > 0 ? titleH + TitleGap + PanelH : PanelH;
        float blockTop = (h - blockH) * 0.5f;
        float panelX = (w - PanelW) * 0.5f;
        float panelY = blockTop + titleH + (titleH > 0 ? TitleGap : 0);

        if (titleH > 0)
        {
            float titleX = (w - titleW) * 0.5f;
            if (!titleMissing && titleImage.TextureId > 0)
                rapi.Render2DTexture(titleImage.TextureId, titleX, blockTop, titleW, titleH, 204);
            else
                DrawText(titleFallbackTex, titleX, blockTop, 204);
        }

        tint.Set(PanelFill[0], PanelFill[1], PanelFill[2], PanelFill[3]);
        DrawSolid(panelX, panelY, PanelW, PanelH, 205, tint);

        float pad = 20f;
        float barY = panelY + 52f;
        float barW = PanelW - pad * 2f;
        float barH = 18f;
        tint.Set(BarTrack[0], BarTrack[1], BarTrack[2], BarTrack[3]);
        DrawSolid(panelX + pad, barY, barW, barH, 206, tint);
        if (fraction > 0)
        {
            tint.Set(BarFill[0], BarFill[1], BarFill[2], BarFill[3]);
            DrawSolid(panelX + pad, barY, barW * fraction, barH, 207, tint);
        }

        float textX = panelX + pad;
        DrawText(percentTex, panelX + PanelW - pad - percentTex.Width, panelY + 14f, 208);
        DrawText(statusTex, textX, panelY + 84f, 209);
    }

    void DrawBackdrop(float w, float h)
    {
        if (!backdropMissing && backdrop.TextureId > 0)
        {
            capi.Render.Render2DTexture(backdrop.TextureId, 0, 0, w, h, 200);
            tint.Set(0.04f, 0.05f, 0.08f, 0.45f);
            DrawSolid(0, 0, w, h, 199, tint);
            return;
        }

        tint.Set(DarkFill[0], DarkFill[1], DarkFill[2], DarkFill[3]);
        DrawSolid(0, 0, w, h, 200, tint);
    }

    void DrawSolid(float x, float y, float width, float height, float z, Vec4f color)
    {
        int subId = WhiteSubId();
        if (subId < 0) return;
        capi.Render.Render2DTexture(subId, x, y, width, height, z, color);
    }

    void DrawText(LoadedTexture tex, float x, float y, float z)
    {
        if (tex.TextureId <= 0 || tex.Width <= 0) return;
        capi.Render.Render2DLoadedTexture(tex, x, y, z);
    }

    int WhiteSubId()
    {
        if (whiteSubId >= 0) return whiteSubId;
        whiteSubId = 0;
        return whiteSubId;
    }

    void RebuildText()
    {
        capi.Gui.TextTexture.GenOrUpdateTextTexture(percentLabel, percentFont, ref percentTex);
        capi.Gui.TextTexture.GenOrUpdateTextTexture(status, statusFont, ref statusTex);
        if (titleMissing)
            capi.Gui.TextTexture.GenOrUpdateTextTexture("Distant Vistas", titleFallbackFont, ref titleFallbackTex);
    }

    void EnsureGraphicsLoaded()
    {
        if (gfxReady) return;
        backdropMissing = !TryLoadTexture(capi, BackdropAsset, ref backdrop);
        titleMissing = !TryLoadTexture(capi, TitleAsset, ref titleImage);
        if (titleMissing)
            capi.Gui.TextTexture.GenOrUpdateTextTexture("Distant Vistas", titleFallbackFont, ref titleFallbackTex);
        gfxReady = true;
        RebuildText();
    }

    static bool TryLoadTexture(ICoreClientAPI capi, AssetLocation path, ref LoadedTexture into)
    {
        IAsset? asset = capi.Assets.TryGet(path);
        if (asset?.Data == null || asset.Data.Length == 0) return false;

        try
        {
            using BitmapExternal bmp = capi.Render.BitmapCreateFromPng(asset.Data);
            capi.Render.LoadTexture(bmp, ref into);
            return into.TextureId > 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        backdrop.Dispose();
        titleImage.Dispose();
        titleFallbackTex.Dispose();
        percentTex.Dispose();
        statusTex.Dispose();
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Ortho);
    }
}
