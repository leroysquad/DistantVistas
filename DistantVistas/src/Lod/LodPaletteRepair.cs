namespace DistantVistas;

/// <summary>
/// Filling in palette colours that are not there.
///
/// A capturing server has no texture atlas, so it stores 0 for every colour and the
/// receiving client fills them in (DESIGN.md §10.4). A client also PERSISTS what it
/// received, so anything that stopped the fill-in from happening was written to disk and
/// stayed there. That happened: a block code that failed to resolve used to be cached as
/// a failure for the rest of the session, so once a common code lost that race, every
/// foreign section installed afterwards was saved with its colours still at zero. Measured
/// on a real cache afterwards - 7 sections entirely uncoloured and 59 partly, on ordinary
/// ground like soil-low-normal, rock-slate and tallgrass-medium-free.
///
/// Zero is a safe sentinel for "never coloured". It is fully transparent black, which no
/// real block averages to, and it is exactly what the writer stores when it cannot answer.
///
/// Fixing the cause only helps caches that do not exist yet, so this runs over every
/// section as it loads and repairs what is already on disk.
/// </summary>
public static class LodPaletteRepair
{
    /// <summary>
    /// Never coloured, or coloured with the missing-texture / unknown.png near-white.
    /// Measured AvgColor of unknown.png is 0x00FCFCFC; HD themepacks can shift indices so
    /// a "usable" atlas slot still averages near-white. LOD must never paint that.
    /// Colour packing matches LodPaletteEntry (R in the low byte).
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
    /// Refresh position-independent colours after the palette algorithm changes.
    /// Null means the block's colour depends on captured world data and must be kept.
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

    /// <summary>
    /// Give every uncoloured entry a colour, and report how many were fixed.
    /// <paramref name="colorOf"/> takes a block id and returns a packed colour; it is the
    /// texture atlas on a client, and the reason this takes a delegate at all is that the
    /// atlas cannot exist in a headless check.
    /// </summary>
    public static int Fill(LodSection section, System.Func<int, int> colorOf)
    {
        int repaired = 0;

        for (int i = 0; i < section.Palette.Count; i++)
        {
            LodPaletteEntry entry = section.Palette[i];
            if (!NeedsColor(entry.Color)) continue;

            int color = colorOf(entry.BlockId);

            // A colour provider that itself answers 0 / near-white would leave the entry
            // unusable forever. Take grey/terrain instead: wrong, but finished and not a wash.
            entry.Color = Sanitize(color, UnknownBlockColor);
            section.Palette[i] = entry;
            repaired++;
        }

        return repaired;
    }

    /// <summary>
    /// What a block nothing can identify is drawn as: a mid grey that reads as
    /// unremarkable stone at distance. Deliberately not black, which is what these
    /// entries were by omission, and deliberately not magenta, which would shout about
    /// something the player cannot act on. The log carries the detail.
    /// </summary>
    public const int UnknownBlockColor = unchecked((int)0xFF8C8C8C);

    /// <summary>
    /// Sane grass/terrain stand-in when a block has no usable atlas colour at all.
    /// Mid olive-green reads as distant ground rather than fog/white wash.
    /// </summary>
    public const int TerrainFallbackColor = unchecked((int)0xFF2F6B3A);

    /// <summary>
    /// Pick a drawable colour: reject zero and missing-texture white, else grey/grass.
    /// </summary>
    public static int Sanitize(int color, int fallback = TerrainFallbackColor)
    {
        if (!NeedsColor(color)) return color;
        return NeedsColor(fallback) ? TerrainFallbackColor : fallback;
    }
}
