namespace DistantVistas;

/// <summary>
/// Fill palette colours stored as 0 (server has no atlas) or unknown.png white.
/// Runs on load so old caches get fixed; new captures should already be coloured.
/// Unknown or missing-texture entries take a rock/dirt/grass neighbour from the
/// same section when one exists, never pure white.
/// </summary>
public static class LodPaletteRepair
{
    /// <summary>
    /// Never coloured, or near-white from unknown.png (0xFCFCFC). Packing: R in low byte.
    /// </summary>
    public static bool NeedsColor(int color) =>
        color == 0 || IsMissingTextureWhite(color) || IsMissingTextureSky(color);

    /// <summary>True when RGB is near-white (missing/unknown atlas sample).</summary>
    public static bool IsMissingTextureWhite(int color)
    {
        int r = color & 0xFF;
        int g = (color >> 8) & 0xFF;
        int b = (color >> 16) & 0xFF;
        return r >= 0xF0 && g >= 0xF0 && b >= 0xF0;
    }

    /// <summary>
    /// Isolated worlds without TrueScale resolve some leaves to unknown.png
    /// whose average is Farseer slate (about 0.26, 0.29, 0.45), not white.
    /// Dusty navy: R and G stay close, B ahead, mid luma. Cyan glacial ice
    /// is blue-ahead too but G tracks B and luma is higher; do not steal that
    /// into grass.
    /// </summary>
    public static bool IsMissingTextureSky(int color)
    {
        if (IsIceLikeAlbedo(color)) return false;
        Channels(color, out int r, out int g, out int b, out int luma, out int chroma);
        if (luma < 20 || luma > 130) return false;
        if (chroma < 12 || chroma > 96) return false;
        // Dusty slate, not cyan: R and G stay within a step of each other.
        int rg = r > g ? r - g : g - r;
        if (rg > 16) return false;
        return b >= r + 20 && b >= g + 20;
    }

    /// <summary>
    /// Pale cyan / glacial ice. G tracks B (not dusty R~G navy, not deep water
    /// with B far ahead of G). Bright snow is IsBrightCap instead.
    /// </summary>
    public static bool IsIceLikeAlbedo(int color)
    {
        Channels(color, out int r, out int g, out int b, out int luma, out int chroma);
        if (luma < 90 || luma > 230) return false;
        if (chroma < 16 || chroma > 80) return false;
        if (g < r + 8) return false;
        if (b < g + 8) return false;
        if (b > g + 40) return false;
        return true;
    }

    /// <summary>
    /// Stored snow or ice must keep its own colour, never a climate multiply
    /// that copies valley grass onto a high tint slot (green ice caps).
    /// </summary>
    public static bool IsSnowOrIceAlbedo(int color) =>
        IsBrightCap(color) || IsIceLikeAlbedo(color);

    /// <summary>
    /// White snow is meant to stay white. unknown.png is the same RGB as some
    /// snow, so Fill still treats default NeedsColor white as missing. Capture
    /// of a known snow/ice block passes keepBrightSnow and keeps the sample.
    /// Sky-missing-tex (Farseer slate) is still replaced.
    /// </summary>
    public static int KeepCapturedColor(int color, int fallback, bool snowOrIceBlock)
    {
        if (!snowOrIceBlock) return Sanitize(color, fallback);
        if (color == 0 || IsMissingTextureSky(color)) return Sanitize(color, fallback);
        return color;
    }

    /// <summary>
    /// Unpack RGB (R in the low byte) and a 0-255 luma / chroma pair.
    /// </summary>
    public static void Channels(int color, out int r, out int g, out int b, out int luma, out int chroma)
    {
        r = color & 0xFF;
        g = (color >> 8) & 0xFF;
        b = (color >> 16) & 0xFF;
        int mx = r > g ? r : g;
        if (b > mx) mx = b;
        int mn = r < g ? r : g;
        if (b < mn) mn = b;
        luma = (r + g + b) / 3;
        chroma = mx - mn;
    }

    /// <summary>
    /// Bright enough to read as a plastic-white LOD cap at coarse mip.
    /// 0.7.18 only treated unknown.png (all channels >= 0xF0). TrueScale and
    /// some server atlas samples sit around luma 200-239, so a lone light column
    /// still won Boyer-Moore and painted the whole parent cap. Real snow fields
    /// still pass when they cover 3 or 4 of the 2x2.
    /// </summary>
    public static bool IsBrightCap(int color)
    {
        Channels(color, out _, out _, out _, out int luma, out int chroma);
        // Low chroma + high luma: snow, chalk, missing-tex, HD near-white rock.
        return luma >= BrightCapLuma && chroma <= BrightCapChroma;
    }

    public const int BrightCapLuma = 200;
    public const int BrightCapChroma = 48;

    /// <summary>
    /// Stored albedo is already chromatic brown earth, not grass waiting for a
    /// climate map. 0.7.19 used chroma 24 and any mid luma, which ate TrueScale
    /// grass: those textures are dull olive, not pure grey, and they NEED the
    /// colour map. Skip only when red leads green by a clear margin and chroma
    /// is strong. Grey, olive, and grass+dirt mixes keep their tint slot.
    /// </summary>
    public static bool IsRockLikeAlbedo(int color)
    {
        if (IsBrightCap(color)) return false;
        Channels(color, out int r, out int g, out int b, out int luma, out int chroma);
        if (chroma < RockLikeChroma) return false;
        if (luma < 24 || luma > 170) return false;
        // Grass waiting for a map is grey or dull olive: G is close to or
        // ahead of R. Bare dirt/rock brown has red well ahead of green.
        if (r < g + 16) return false;
        return true;
    }

    public const int RockLikeChroma = 48;

    /// <summary>
    /// Refresh stable (position-independent) colours. Null = keep stored colour (chisels etc.).
    /// </summary>
    public static int RefreshStable(LodSection section, System.Func<int, int?> colorOf)
    {
        int refreshed = 0;

        for (int i = 0; i < section.Palette.Count; i++)
        {
            LodPaletteEntry entry = section.Palette[i];
            int? provided = colorOf(entry.BlockId);
            if (!provided.HasValue) continue;

            int color = Sanitize(provided.Value, NeighborTerrainColor(section, i));
            if (entry.Color == color) continue;

            entry.Color = color;
            section.Palette[i] = entry;
            refreshed++;
        }

        return refreshed;
    }

    /// <summary>Fill uncoloured entries. colorOf is the atlas on clients; a delegate so tests can run headless.</summary>
    public static int Fill(LodSection section, System.Func<int, int> colorOf)
    {
        int repaired = 0;

        for (int i = 0; i < section.Palette.Count; i++)
        {
            LodPaletteEntry entry = section.Palette[i];
            if (!NeedsColor(entry.Color)) continue;

            int color = colorOf(entry.BlockId);

            // If colorOf also returns 0/white, take a neighbour rock/dirt/grass sample
            // from this section. Never store pure white; that is the missing-tex wash.
            entry.Color = Sanitize(color, NeighborTerrainColor(section, i));
            section.Palette[i] = entry;
            repaired++;
        }

        return repaired;
    }

    /// <summary>
    /// A usable rock/dirt/grass colour already in this section. Unknown blocks on a
    /// foreign server have no atlas entry; painting them white made whole mountain
    /// subsections light up. A neighbour's earth tone is wrong-but-plausible.
    /// </summary>
    public static int NeighborTerrainColor(LodSection section, int skipIndex)
    {
        int best = 0;
        int bestScore = -1;
        for (int i = 0; i < section.Palette.Count; i++)
        {
            if (i == skipIndex) continue;
            LodPaletteEntry e = section.Palette[i];
            if (NeedsColor(e.Color)) continue;
            if ((e.Flags & (LodPaletteEntry.FlagWater | LodPaletteEntry.FlagThin | LodPaletteEntry.FlagSkip)) != 0)
                continue;

            int r = e.Color & 0xFF;
            int g = (e.Color >> 8) & 0xFF;
            int b = (e.Color >> 16) & 0xFF;
            int luma = (r + g + b) / 3;
            // Skip near-black and near-white; prefer mid-luma earth (dirt ~90, grass ~70-110, rock ~80-140).
            if (luma < 24 || luma > 200) continue;
            int score = 255 - Math.Abs(luma - 96);
            if (score > bestScore)
            {
                bestScore = score;
                best = e.Color;
            }
        }
        return best != 0 ? best : TerrainFallbackColor;
    }

    /// <summary>Unknown block: mid grey. Not black (old zero-colour bug) and not magenta.</summary>
    public const int UnknownBlockColor = unchecked((int)0xFF8C8C8C);

    /// <summary>Fallback when a block has no usable atlas colour.</summary>
    public const int TerrainFallbackColor = unchecked((int)0xFF2F6B3A);

    /// <summary>Reject zero / near-white; otherwise return color (or fallback).</summary>
    public static int Sanitize(int color, int fallback = TerrainFallbackColor)
    {
        if (!NeedsColor(color)) return color;
        return NeedsColor(fallback) ? TerrainFallbackColor : fallback;
    }
}
