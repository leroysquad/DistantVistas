using DistantVistas;

namespace DistantVistas.Checks;

/// <summary>
/// Winter foreground is mixed snow layers and grass. A freeze line at or below the
/// valley must not turn every LOD top white.
/// </summary>
public static class SnowOverlayChecks
{
    public static void Run(Check c)
    {
        WinterValleyDisablesOverlay(c);
        AlpineKeepsOverlay(c);
        NoLapseDisables(c);
    }

    static void WinterValleyDisablesOverlay(Check c)
    {
        // Sea already below freezing, colder with height: raw line is below sea.
        float y = LodSnow.OverlayY(seaLevel: 110, tempAtSea: -5f, tempAtSeaPlus150: -15f);
        c.Eq(LodSnow.Disabled, y, "winter valley climate does not paint a world-wide snow sheet");
    }

    static void AlpineKeepsOverlay(Check c)
    {
        // Mild valley, freezing on the high sample: line is above sea + slack.
        float y = LodSnow.OverlayY(seaLevel: 110, tempAtSea: 8f, tempAtSeaPlus150: -4f);
        c.True(y < LodSnow.Disabled, "alpine freeze line still enables height snow");
        c.True(y >= 110 + LodSnow.AlpineSlack, "and it stays above the valley floor");
    }

    static void NoLapseDisables(Check c)
    {
        c.Eq(LodSnow.Disabled, LodSnow.OverlayY(110, 10f, 10f), "flat lapse disables overlay");
        c.Eq(LodSnow.Disabled, LodSnow.OverlayY(110, 5f, 12f), "inverted lapse disables overlay");
    }
}
