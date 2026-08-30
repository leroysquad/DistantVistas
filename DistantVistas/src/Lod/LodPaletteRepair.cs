namespace DistantVistas;

/// <summary>
/// Fill palette colours stored as 0 (server has no atlas) or unknown.png white.
/// Runs on load so old caches get fixed; new captures should already be coloured.
/// </summary>
public static class LodPaletteRepair
{
    /// <summary>
    /// Never coloured, or near-white from unknown.png (0xFCFCFC). Packing: R in low byte.
    /// </summary>
    public static bool NeedsColor(int color) => color == 0 || IsMissingTextureWhite(color);

    /// <summary>True when RGB is near-white (missing/unknown atlas sample).</summary>
    public static bool IsMissingTextureWhite(int color)
    {
        int r = color & 0xFF;
        int g = (color >> 8) & 0xFF;
        int b = (color >> 16) & 0xFF;
        return r >= 0xF0 && g >= 0xF0 && b >= 0xF0;
    }

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

            int color = Sanitize(provided.Value, UnknownBlockColor);
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

            // If colorOf also returns 0/white, fall back so we don't loop forever.
            entry.Color = Sanitize(color, UnknownBlockColor);
            section.Palette[i] = entry;
            repaired++;
        }

        return repaired;
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
