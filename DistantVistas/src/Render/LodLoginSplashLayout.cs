namespace DistantVistas;

/// <summary>
/// Resolution-independent layout for the login splash. Backdrop art may be authored at
/// 1920×1080 (or any size); the frame can be ultrawide (e.g. 2560×1369). Cover-fit
/// scales the texture to fill the frame without assuming a fixed aspect ratio.
/// </summary>
static class LodLoginSplashLayout
{
    /// <summary>
    /// Scale texture to cover the frame (crop overflow), centered — same as CSS object-fit: cover.
    /// </summary>
    public static (float X, float Y, float Width, float Height) CoverFit(
        float frameW, float frameH, float texW, float texH)
    {
        if (frameW <= 0 || frameH <= 0) return (0, 0, 0, 0);
        if (texW <= 0 || texH <= 0) return (0, 0, frameW, frameH);

        float scale = Math.Max(frameW / texW, frameH / texH);
        float dw = texW * scale;
        float dh = texH * scale;
        return ((frameW - dw) * 0.5f, (frameH - dh) * 0.5f, dw, dh);
    }

    /// <summary>Tile size for full-screen solid quads when a single large quad fails.</summary>
    public const float OpaqueTileSize = 512f;
}
