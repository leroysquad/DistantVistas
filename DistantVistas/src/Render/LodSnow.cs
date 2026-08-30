using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Alpine snow overlay only. A sea-level freeze line painted whole valleys plastic-white;
/// ground snow is captured snow blocks + seasonal tint.
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
