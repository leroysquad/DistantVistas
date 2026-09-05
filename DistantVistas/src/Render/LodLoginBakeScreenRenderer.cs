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
    /// <summary>Hook for supplied landscape backdrop (solid dark fill when missing).</summary>
    public static readonly AssetLocation BackdropAsset =
        new("distantvistas", "textures/login-backdrop.png");

    const double PanelW = 520;
    const double PanelH = 200;
    static readonly float[] DarkFill = { 0.06f, 0.08f, 0.11f, 1f };
    static readonly float[] BarTrack = { 0.12f, 0.14f, 0.18f, 1f };
    static readonly float[] BarFill = { 0.28f, 0.55f, 0.82f, 1f };
    static readonly float[] PanelFill = { 0.10f, 0.12f, 0.16f, 0.92f };

    readonly ICoreClientAPI capi;
    LoadedTexture backdrop;
    LoadedTexture titleTex;
    LoadedTexture percentTex;
    LoadedTexture statusTex;
    readonly Vec4f tint = new();
    readonly CairoFont titleFont;
    readonly CairoFont percentFont;
    readonly CairoFont statusFont;

    bool active;
    bool backdropReady;
    bool backdropMissing;
    float fraction;
    string status = "";
    string percentLabel = "0%";
    int whiteSubId = -1;

    public LodLoginBakeScreenRenderer(ICoreClientAPI capi)
    {
        this.capi = capi;
        backdrop = new LoadedTexture(capi);
        titleTex = new LoadedTexture(capi);
        percentTex = new LoadedTexture(capi);
        statusTex = new LoadedTexture(capi);
        titleFont = CairoFont.WhiteDetailText().WithFontSize(22);
        percentFont = CairoFont.WhiteDetailText().WithFontSize(18);
        statusFont = CairoFont.WhiteSmallText();
        TryLoadBackdrop();
    }

    public bool Active
    {
        get => active;
        set
        {
            if (active == value) return;
            active = value;
            if (value) TryLoadBackdrop();
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

        IRenderAPI rapi = capi.Render;
        float w = rapi.FrameWidth;
        float h = rapi.FrameHeight;
        if (w <= 0 || h <= 0) return;

        if (backdropReady && !backdropMissing)
        {
            rapi.Render2DTexture(backdrop.TextureId, 0, 0, w, h, 200);
            tint.Set(0.04f, 0.05f, 0.08f, 0.55f);
            DrawSolid(0, 0, w, h, 199, tint);
        }
        else
        {
            tint.Set(DarkFill[0], DarkFill[1], DarkFill[2], DarkFill[3]);
            DrawSolid(0, 0, w, h, 200, tint);
        }

        float panelX = (w - PanelW) / 2f;
        float panelY = (h - PanelH) / 2f;
        tint.Set(PanelFill[0], PanelFill[1], PanelFill[2], PanelFill[3]);
        DrawSolid(panelX, panelY, PanelW, PanelH, 201, tint);

        float pad = 20f;
        float barY = panelY + 88f;
        float barW = PanelW - pad * 2f;
        float barH = 18f;
        tint.Set(BarTrack[0], BarTrack[1], BarTrack[2], BarTrack[3]);
        DrawSolid(panelX + pad, barY, barW, barH, 202, tint);
        if (fraction > 0)
        {
            tint.Set(BarFill[0], BarFill[1], BarFill[2], BarFill[3]);
            DrawSolid(panelX + pad, barY, barW * fraction, barH, 203, tint);
        }

        float textX = panelX + pad;
        DrawText(titleTex, textX, panelY + 8f, 204);
        DrawText(percentTex, panelX + PanelW - pad - percentTex.Width, panelY + 10f, 204);
        DrawText(statusTex, textX, panelY + 118f, 205);
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
        // Vanilla routes missing block-colour textures to unknown.png at atlas subid 0.
        whiteSubId = 0;
        return whiteSubId;
    }

    void RebuildText()
    {
        capi.Gui.TextTexture.GenOrUpdateTextTexture("Distant Vistas", titleFont, ref titleTex);
        capi.Gui.TextTexture.GenOrUpdateTextTexture(percentLabel, percentFont, ref percentTex);
        capi.Gui.TextTexture.GenOrUpdateTextTexture(status, statusFont, ref statusTex);
    }

    void TryLoadBackdrop()
    {
        if (backdropReady) return;

        IAsset? asset = capi.Assets.TryGet(BackdropAsset);
        if (asset?.Data == null || asset.Data.Length == 0)
        {
            backdropMissing = true;
            backdropReady = true;
            return;
        }

        try
        {
            using BitmapExternal bmp = capi.Render.BitmapCreateFromPng(asset.Data);
            capi.Render.LoadTexture(bmp, ref backdrop);
            backdropMissing = backdrop.TextureId <= 0;
        }
        catch
        {
            backdropMissing = true;
        }

        backdropReady = true;
    }

    public void Dispose()
    {
        backdrop.Dispose();
        titleTex.Dispose();
        percentTex.Dispose();
        statusTex.Dispose();
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Ortho);
    }
}
