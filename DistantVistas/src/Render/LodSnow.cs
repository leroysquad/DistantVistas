using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Whether the LOD shader may paint a height-based snow overlay.
///
/// Vanilla winter in the foreground is snow *layers* on grass, dirt and stone - not a
/// white wash. The overlay used a freeze line from sea-level temperature; in winter that
/// line falls through the valley and every LOD top became plastic white against mixed
/// foreground snow. Overlay is alpine-only. Ground snow is captured snow blocks plus
/// live seasonal grass tint.
/// </summary>
public static class LodSnow
{
    public const float Disabled = 99999f;

    /// <summary>Freeze line must sit this far above sea before we paint altitude snow.</summary>
    public const float AlpineSlack = 32f;

    public static float OverlayY(int seaLevel, float tempAtSea, float tempAtSeaPlus150)
    {
        if (tempAtSea <= tempAtSeaPlus150) return Disabled;

        float lapsePerBlock = (tempAtSea - tempAtSeaPlus150) / 150f;
        float y = seaLevel + (tempAtSea - (-1f)) / lapsePerBlock;
        y = GameMath.Clamp(y, seaLevel - 64, Disabled);
        if (y < seaLevel + AlpineSlack) return Disabled;
        return y;
    }
}
