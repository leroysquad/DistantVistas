using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Login visit sweep splash: backdrop, arched title, progress bar, and status text.
/// When <paramref name="stockOnly"/> is true, draws a minimal stock Loading… layout instead.
/// </summary>
public sealed class LodLoginBakeScreenRenderer : IRenderer, IDisposable
{
    public static readonly AssetLocation BackdropAsset =
        new("distantvistas", "textures/gui/login-backdrop.png");

    /// <summary>Arched title: rainbow shape, single solid gold — not multicolor lettering.</summary>
    public static readonly AssetLocation TitleAsset =
        new("distantvistas", "textures/gui/login-title-rainbow.png");

    const string TitleText = "Distant Vistas";
    const float PanelW = 520f;
    const float PanelH = 156f;
    const float TitleGap = 28f;
    const float TitleMaxWidthFrac = 0.62f;
    const float TitleMaxWidthPx = 560f;
    const int ArchedTitleW = 560;
    const int ArchedTitleH = 150;
    static readonly float[] OpaqueCover = { 0.05f, 0.06f, 0.09f, 1f };
    static readonly float[] DarkFill = { 0.06f, 0.08f, 0.11f, 1f };
    static readonly float[] BarTrack = { 0.12f, 0.14f, 0.18f, 1f };
    static readonly float[] BarFill = { 0.28f, 0.55f, 0.82f, 1f };
    static readonly float[] PanelFill = { 0.10f, 0.12f, 0.16f, 0.96f };
    static readonly double[] Gold = { 0.95, 0.84, 0.48, 1.0 };

    /// <summary>Consecutive ortho frames that painted the opaque base.</summary>
    public const int RequiredHealthyFrames = 8;

    public int ConsecutiveOpaqueFrames { get; private set; }
    public bool HasEverPaintedOpaque { get; private set; }
    public bool IsOverlayHealthy => ConsecutiveOpaqueFrames >= RequiredHealthyFrames;

    readonly ICoreClientAPI capi;
    readonly LoadedTexture backdrop;
    readonly LoadedTexture titleImage;
    readonly LoadedTexture titleFallbackTex;
    readonly LoadedTexture percentTex;
    readonly LoadedTexture statusTex;
    readonly LoadedTexture headingTex;
    readonly Vec4f tint = new();
    readonly Dictionary<int, int> solidColorTextures = new();

    MeshRef? ownedQuad;
    int whiteTextureId;

    readonly bool stockOnly;
    bool active;
    bool gfxReady;
    bool backdropMissing;
    bool titleMissing;
    float fraction;
    float overlayAlpha = 1f;
    string status = "";
    string percentLabel = "0%";

    public LodLoginBakeScreenRenderer(ICoreClientAPI capi, bool stockOnly = false)
    {
        this.capi = capi;
        this.stockOnly = stockOnly;
        backdrop = new LoadedTexture(capi);
        titleImage = new LoadedTexture(capi);
        titleFallbackTex = new LoadedTexture(capi);
        percentTex = new LoadedTexture(capi);
        statusTex = new LoadedTexture(capi);
        headingTex = new LoadedTexture(capi);
        percentFont = CairoFont.WhiteDetailText().WithFontSize(18);
        statusFont = CairoFont.WhiteSmallText();
        headingFont = CairoFont.WhiteDetailText().WithFontSize(22);
    }

    readonly CairoFont percentFont;
    readonly CairoFont statusFont;
    readonly CairoFont headingFont;

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
                ConsecutiveOpaqueFrames = 0;
                HasEverPaintedOpaque = false;
                PrepareImmediate();
            }
            else
            {
                ConsecutiveOpaqueFrames = 0;
            }
        }
    }

    public double RenderOrder => stockOnly ? 3.0 : 2.0;
    public int RenderRange => 9999;

    public void SetProgress(float progress, string detail)
    {
        fraction = Math.Clamp(progress, 0f, 1f);
        status = detail ?? "";
        percentLabel = $"{(int)Math.Round(fraction * 100)}%";
        RebuildText();
    }

    /// <summary>Fade the overlay out on release (1 = opaque, 0 = hidden).</summary>
    public void SetOverlayAlpha(float alpha) =>
        overlayAlpha = Math.Clamp(alpha, 0f, 1f);

    /// <summary>Call as early as LevelFinalize allows so the first painted frame is already opaque.</summary>
    public void PrepareImmediate()
    {
        EnsureGraphicsLoaded();
        RebuildText();
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (!active || overlayAlpha <= 0f) return;

        bool orthoPass = stage == EnumRenderStage.Ortho;
        bool safetyPass = stage == EnumRenderStage.AfterFinalComposition;
        if (!orthoPass && !safetyPass) return;

        EnsureGraphicsLoaded();

        IRenderAPI rapi = capi.Render;
        float w = rapi.FrameWidth;
        float h = rapi.FrameHeight;
        if (w <= 0 || h <= 0)
        {
            if (orthoPass) ConsecutiveOpaqueFrames = 0;
            return;
        }

        bool drewOpaque = DrawFullFrame(w, h, orthoPass, countHealth: orthoPass || stockOnly);
        if (!drewOpaque && orthoPass)
            ConsecutiveOpaqueFrames = 0;
    }

    bool DrawFullFrame(float w, float h, bool orthoPass, bool countHealth)
    {
        bool drewOpaque = DrawOpaqueCover(w, h, orthoPass);
        if (!drewOpaque) return false;

        if (countHealth || stockOnly)
        {
            ConsecutiveOpaqueFrames++;
            if (!HasEverPaintedOpaque)
                HasEverPaintedOpaque = true;
        }

        if (stockOnly)
        {
            DrawStockLayout(w, h, orthoPass);
            return true;
        }

        DrawBackdrop(w, h, orthoPass);

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
            TryDrawTexture(titleTexId, titleX, blockTop, titleW, titleH, 204, orthoPass);
        }

        tint.Set(PanelFill[0], PanelFill[1], PanelFill[2], PanelFill[3] * overlayAlpha);
        DrawSolid(panelX, panelY, PanelW, PanelH, 205, tint, orthoPass);

        float pad = 20f;
        float barY = panelY + 52f;
        float barW = PanelW - pad * 2f;
        float barH = 18f;
        tint.Set(BarTrack[0], BarTrack[1], BarTrack[2], BarTrack[3] * overlayAlpha);
        DrawSolid(panelX + pad, barY, barW, barH, 206, tint, orthoPass);
        if (fraction > 0)
        {
            tint.Set(BarFill[0], BarFill[1], BarFill[2], BarFill[3] * overlayAlpha);
            DrawSolid(panelX + pad, barY, barW * fraction, barH, 207, tint, orthoPass);
        }

        if (overlayAlpha > 0.01f)
        {
            DrawText(percentTex, panelX + PanelW - pad - percentTex.Width, panelY + 14f, 209, orthoPass);
            DrawText(statusTex, panelX + pad, panelY + 84f, 210, orthoPass);
        }

        return true;
    }

    void DrawStockLayout(float w, float h, bool orthoPass)
    {
        tint.Set(DarkFill[0], DarkFill[1], DarkFill[2], DarkFill[3] * overlayAlpha);
        DrawSolid(0, 0, w, h, 200, tint, orthoPass);

        RebuildHeading();
        float headingY = h * 0.42f;
        if (headingTex.TextureId > 0)
            DrawText(headingTex, (w - headingTex.Width) * 0.5f, headingY, 204, orthoPass);

        float panelW = Math.Min(PanelW, w - 40f);
        float panelX = (w - panelW) * 0.5f;
        float panelY = headingY + 48f;
        tint.Set(PanelFill[0], PanelFill[1], PanelFill[2], PanelFill[3] * overlayAlpha);
        DrawSolid(panelX, panelY, panelW, PanelH, 205, tint, orthoPass);

        float pad = 20f;
        float barY = panelY + 52f;
        float barW = panelW - pad * 2f;
        tint.Set(BarTrack[0], BarTrack[1], BarTrack[2], BarTrack[3] * overlayAlpha);
        DrawSolid(panelX + pad, barY, barW, 18f, 206, tint, orthoPass);
        if (fraction > 0)
        {
            tint.Set(BarFill[0], BarFill[1], BarFill[2], BarFill[3] * overlayAlpha);
            DrawSolid(panelX + pad, barY, barW * fraction, 18f, 207, tint, orthoPass);
        }

        if (overlayAlpha > 0.01f)
        {
            DrawText(percentTex, panelX + panelW - pad - percentTex.Width, panelY + 14f, 208, orthoPass);
            DrawText(statusTex, panelX + pad, panelY + 84f, 209, orthoPass);
        }
    }

    void RebuildHeading()
    {
        LoadedTexture heading = headingTex;
        capi.Gui.TextTexture.GenOrUpdateTextTexture("Loading…", headingFont, ref heading);
    }

    bool DrawOpaqueCover(float w, float h, bool orthoPass)
    {
        tint.Set(OpaqueCover[0], OpaqueCover[1], OpaqueCover[2], OpaqueCover[3] * overlayAlpha);
        return TryDrawSolidColor(0, 0, w, h, 198, tint, orthoPass);
    }

    void DrawBackdrop(float w, float h, bool orthoPass)
    {
        if (!backdropMissing && backdrop.TextureId > 0
            && TryDrawTexture(backdrop.TextureId, 0, 0, w, h, 200, orthoPass))
        {
            tint.Set(0.04f, 0.05f, 0.08f, 0.45f * overlayAlpha);
            DrawSolid(0, 0, w, h, 199, tint, orthoPass);
            return;
        }

        tint.Set(DarkFill[0], DarkFill[1], DarkFill[2], DarkFill[3] * overlayAlpha);
        DrawSolid(0, 0, w, h, 200, tint, orthoPass);
    }

    void DrawSolid(float x, float y, float width, float height, float z, Vec4f color, bool orthoPass) =>
        TryDrawSolidColor(x, y, width, height, z, color, orthoPass);

    void DrawText(LoadedTexture tex, float x, float y, float z, bool orthoPass)
    {
        if (tex.TextureId <= 0 || tex.Width <= 0) return;
        TryDrawLoadedTexture(tex, x, y, z, orthoPass);
    }

    /// <summary>
    /// Draw order: ortho internal-quad tint path first (proven on <see cref="EnumRenderStage.Ortho"/>),
    /// then explicit <see cref="MeshRef"/> (required on <see cref="EnumRenderStage.AfterFinalComposition"/>).
    /// </summary>
    bool TryDrawTexture(int textureId, float x, float y, float width, float height, float z, bool orthoPass)
    {
        if (textureId <= 0 || width <= 0 || height <= 0) return false;
        if (orthoPass && TryDrawWithInternalQuad(textureId, x, y, width, height, z, null))
            return true;
        if (TryDrawWithExplicitQuad(textureId, x, y, width, height, z)) return true;
        return orthoPass && TryDrawWithInternalQuad(textureId, x, y, width, height, z, null);
    }

    bool TryDrawSolidColor(float x, float y, float width, float height, float z, Vec4f color, bool orthoPass)
    {
        if (width <= 0 || height <= 0 || color[3] <= 0.001f) return false;

        if (orthoPass)
        {
            int whiteId = EnsureWhiteTexture();
            if (whiteId > 0 && TryDrawWithInternalQuad(whiteId, x, y, width, height, z, color))
                return true;
        }

        int bakedId = SolidColorTextureId(color);
        if (bakedId > 0 && TryDrawWithExplicitQuad(bakedId, x, y, width, height, z))
            return true;

        if (!orthoPass) return false;

        int fallbackWhite = EnsureWhiteTexture();
        return fallbackWhite > 0
            && TryDrawWithInternalQuad(fallbackWhite, x, y, width, height, z, color);
    }

    bool TryDrawLoadedTexture(LoadedTexture tex, float x, float y, float z, bool orthoPass)
    {
        if (tex.TextureId <= 0) return false;
        if (TryDrawWithExplicitQuad(tex.TextureId, x, y, tex.Width, tex.Height, z))
            return true;

        if (!orthoPass) return false;

        try
        {
            capi.Render.Render2DLoadedTexture(tex, x, y, z);
            return true;
        }
        catch
        {
            return false;
        }
    }

    bool TryDrawWithExplicitQuad(int textureId, float x, float y, float width, float height, float z)
    {
        MeshRef? quad = ResolveQuadMesh();
        if (quad == null) return false;

        try
        {
            capi.Render.Render2DTexture(quad, textureId, x, y, width, height, z);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// VS internal GUI quad — valid on ortho, null on AfterFinalComposition (0.8.1 NRE).
    /// </summary>
    bool TryDrawWithInternalQuad(
        int textureId, float x, float y, float width, float height, float z, Vec4f? color)
    {
        try
        {
            if (color != null)
                capi.Render.Render2DTexture(textureId, x, y, width, height, z, color);
            else
                capi.Render.Render2DTexture(textureId, x, y, width, height, z);
            return true;
        }
        catch
        {
            return false;
        }
    }

    MeshRef? ResolveQuadMesh()
    {
        MeshRef? guiQuad = capi.Gui.QuadMeshRef;
        if (guiQuad != null && !guiQuad.Disposed) return guiQuad;

        if (ownedQuad != null && !ownedQuad.Disposed) return ownedQuad;

        try
        {
            MeshData data = QuadMeshUtil.GetQuad();
            ownedQuad = capi.Render.UploadMesh(data);
            return ownedQuad;
        }
        catch
        {
            return null;
        }
    }

    int EnsureWhiteTexture()
    {
        if (whiteTextureId > 0) return whiteTextureId;

        using ImageSurface surface = new(Format.Argb32, 2, 2);
        using Context ctx = new(surface);
        ctx.SetSourceRGBA(1, 1, 1, 1);
        ctx.Rectangle(0, 0, 2, 2);
        ctx.Fill();

        whiteTextureId = capi.Gui.LoadCairoTexture(surface, false);
        return whiteTextureId;
    }

    int SolidColorTextureId(Vec4f color)
    {
        int key = ColorUtil.ToRgba(
            (int)Math.Clamp(color[3] * 255f, 0f, 255f),
            (int)Math.Clamp(color[0] * 255f, 0f, 255f),
            (int)Math.Clamp(color[1] * 255f, 0f, 255f),
            (int)Math.Clamp(color[2] * 255f, 0f, 255f));
        if (solidColorTextures.TryGetValue(key, out int cached)) return cached;

        using ImageSurface surface = new(Format.Argb32, 2, 2);
        using Context ctx = new(surface);
        ctx.SetSourceRGBA(color[0], color[1], color[2], color[3]);
        ctx.Rectangle(0, 0, 2, 2);
        ctx.Fill();

        int texId = capi.Gui.LoadCairoTexture(surface, false);
        if (texId > 0)
            solidColorTextures[key] = texId;
        return texId;
    }

    void RebuildText()
    {
        LoadedTexture pct = percentTex;
        LoadedTexture stat = statusTex;
        capi.Gui.TextTexture.GenOrUpdateTextTexture(percentLabel, percentFont, ref pct);
        capi.Gui.TextTexture.GenOrUpdateTextTexture(status, statusFont, ref stat);
    }

    void EnsureGraphicsLoaded()
    {
        if (gfxReady) return;
        LoadedTexture bd = backdrop;
        LoadedTexture ti = titleImage;
        backdropMissing = !TryLoadTexture(capi, BackdropAsset, ref bd);
        titleMissing = !TryLoadTexture(capi, TitleAsset, ref ti);
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
        headingTex.Dispose();
        if (ownedQuad != null && !ownedQuad.Disposed)
            capi.Render.DeleteMesh(ownedQuad);
        ownedQuad = null;
        solidColorTextures.Clear();
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Ortho);
        capi.Event.UnregisterRenderer(this, EnumRenderStage.AfterFinalComposition);
    }
}
