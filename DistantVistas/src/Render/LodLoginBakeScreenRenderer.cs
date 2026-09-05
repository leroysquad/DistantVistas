using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Login visit-sweep overlay (layout locked):
/// 1. Full-screen landscape backdrop
/// 2. Centered arched solid-gold title graphic above the panel
/// 3. Loading panel with progress % and status below
/// </summary>
public sealed class LodLoginBakeScreenRenderer : IRenderer, IDisposable
{
    public static readonly AssetLocation BackdropAsset =
        new("distantvistas", "textures/gui/login-backdrop.png");

    /// <summary>Arched title: rainbow shape, single solid gold — not multicolor lettering.</summary>
    public static readonly AssetLocation TitleAsset =
        new("distantvistas", "textures/gui/login-title-rainbow.png");

    const string TitleText = "Distant Vistas";
    const double PanelW = 520;
    const double PanelH = 156;
    const float TitleGap = 28f;
    const float TitleMaxWidthFrac = 0.62f;
    const float TitleMaxWidthPx = 560f;
    const int ArchedTitleW = 560;
    const int ArchedTitleH = 150;
    static readonly float[] DarkFill = { 0.06f, 0.08f, 0.11f, 1f };
    static readonly float[] BarTrack = { 0.12f, 0.14f, 0.18f, 1f };
    static readonly float[] BarFill = { 0.28f, 0.55f, 0.82f, 1f };
    static readonly float[] PanelFill = { 0.10f, 0.12f, 0.16f, 0.92f };
    static readonly double[] Gold = { 0.95, 0.84, 0.48, 1.0 };

    readonly ICoreClientAPI capi;
    readonly LoadedTexture backdrop;
    readonly LoadedTexture titleImage;
    readonly LoadedTexture titleFallbackTex;
    readonly LoadedTexture percentTex;
    readonly LoadedTexture statusTex;
    readonly Vec4f tint = new();
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
            titleW = Math.Min(w * TitleMaxWidthFrac, titleFallbackTex.Width);
            titleH = titleW * titleFallbackTex.Height / titleFallbackTex.Width;
        }

        float blockH = titleH > 0 ? titleH + TitleGap + PanelH : PanelH;
        float blockTop = (h - blockH) * 0.5f;
        float panelX = (w - PanelW) * 0.5f;
        float panelY = blockTop + titleH + (titleH > 0 ? TitleGap : 0);

        if (titleH > 0)
        {
            float titleX = (w - titleW) * 0.5f;
            int titleTexId = !titleMissing && titleImage.TextureId > 0
                ? titleImage.TextureId
                : titleFallbackTex.TextureId;
            rapi.Render2DTexture(titleTexId, titleX, blockTop, titleW, titleH, 204);
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

        DrawText(percentTex, panelX + PanelW - pad - percentTex.Width, panelY + 14f, 208);
        DrawText(statusTex, panelX + pad, panelY + 84f, 209);
    }

    void DrawBackdrop(float w, float h)
    {
        if (!backdropMissing && backdrop.TextureId > 0)
        {
            capi.Render.Render2DTexture(backdrop.TextureId, 0, 0, w, h, 200);
            tint.Set(0.04f, 0.05f, 0.08f, 0.35f);
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
    }

    void EnsureGraphicsLoaded()
    {
        if (gfxReady) return;
        backdropMissing = !TryLoadTexture(capi, BackdropAsset, ref backdrop);
        titleMissing = !TryLoadTexture(capi, TitleAsset, ref titleImage);
        if (titleMissing)
            BuildArchedGoldTitleFallback();
        gfxReady = true;
        RebuildText();
    }

    /// <summary>Dev fallback only — shipped builds should package <see cref="TitleAsset"/>.</summary>
    void BuildArchedGoldTitleFallback()
    {
        titleFallbackTex.Dispose();

        using ImageSurface surface = new(Format.Argb32, ArchedTitleW, ArchedTitleH);
        using Context ctx = new(surface);

        ctx.SetSourceRGBA(0, 0, 0, 0);
        ctx.Paint();

        CairoFont font = CairoFont.WhiteDetailText().WithFontSize(34);
        font.SetupContext(ctx);

        double cx = ArchedTitleW * 0.5;
        double cy = ArchedTitleH * 0.82;
        double radius = ArchedTitleW * 0.40;

        for (int i = 0; i < TitleText.Length; i++)
        {
            char c = TitleText[i];
            if (c == ' ') continue;

            double t = i / (TitleText.Length - 1.0);
            double angle = Math.PI * (1.0 - t);
            double x = cx + radius * Math.Cos(angle);
            double y = cy - radius * Math.Sin(angle);
            double rot = angle - Math.PI * 0.5;
            string glyph = c.ToString();
            TextExtents extents = ctx.TextExtents(glyph);

            ctx.Save();
            ctx.Translate(x, y);
            ctx.Rotate(rot);

            ctx.SetSourceRGBA(0, 0, 0, 0.5);
            ctx.MoveTo(-extents.XBearing - extents.Width * 0.5 + 1.5, -extents.YBearing + extents.Height * 0.5 + 1.5);
            ctx.TextPath(glyph);
            ctx.Fill();

            ctx.SetSourceRGBA(Gold[0], Gold[1], Gold[2], Gold[3]);
            ctx.MoveTo(-extents.XBearing - extents.Width * 0.5, -extents.YBearing + extents.Height * 0.5);
            ctx.TextPath(glyph);
            ctx.Fill();

            ctx.Restore();
        }

        int texId = capi.Gui.LoadCairoTexture(surface, true);
        titleFallbackTex.TextureId = texId;
        titleFallbackTex.Width = ArchedTitleW;
        titleFallbackTex.Height = ArchedTitleH;
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
