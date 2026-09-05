using DistantVistas;

namespace DistantVistas.Checks;

/// <summary>
/// Grass-covered ground is composited, not painted.
///
/// Vanilla's `chunktopsoil.fsh` draws it as `brownSoil * (1 - grass.a) + grass * grass.a`,
/// and colour-maps only the grass half. A LOD vertex carries one colour and the tint is a
/// plain multiply, so the mod stores the composite and dilutes the slot's tint by the share
/// that came from untinted dirt. That only reproduces the shader if one identity holds.
/// </summary>
public static class TopSoilColorChecks
{
    public static void Run(Check c)
    {
        TheDilutedTintReproducesVanillasShader(c);
        FullCoverageIsTheOrdinaryCase(c);
        Degenerate(c);
    }

    /// <summary>
    /// composite * (share + (1 - share) * tint) == soil * (1 - a) + grass * a * tint,
    /// for every channel, coverage and tint.
    /// </summary>
    static void TheDilutedTintReproducesVanillasShader(Check c)
    {
        float[] soils = { 106f, 85f, 61f, 84f, 58f, 31f, 0f, 255f };
        float[] grasses = { 149f, 151f, 127f, 148f, 149f, 129f, 255f, 0f };
        float[] coverages = { 146f / 255f, 128f / 255f, 87f / 255f, 0.25f, 0.5f, 1f };
        float[] tints = { 0.482f, 0.612f, 0.051f, 0.1f, 1f };

        int checks = 0;
        float worst = 0f;
        foreach (float soil in soils)
        foreach (float grass in grasses)
        foreach (float a in coverages)
        foreach (float tint in tints)
        {
            float composite = LodTopSoil.Composite(soil, grass, a);
            float share = LodTopSoil.UntintedShare(soil, grass, a);
            float got = composite * LodTopSoil.Dilute(share, tint);
            float want = soil * (1f - a) + grass * a * tint;
            worst = Math.Max(worst, Math.Abs(got - want));
            checks++;
        }

        c.True(worst < 0.01f,
            $"the diluted tint reproduces the top-soil shader across {checks} combinations (worst error {worst:F4})");
    }

    static void FullCoverageIsTheOrdinaryCase(Check c)
    {
        c.Eq(0f, LodTopSoil.UntintedShare(106f, 149f, coverage: 1f), "full coverage leaves nothing untinted");
        c.Eq(149f, LodTopSoil.Composite(106f, 149f, coverage: 1f), "and the composite is just the overlay");
        c.Eq(0.482f, LodTopSoil.Dilute(0f, 0.482f), "a zero share passes the sampled tint straight through");

        c.Eq(1f, LodTopSoil.UntintedShare(106f, 149f, coverage: 0f), "no coverage means nothing is tinted");
        c.Eq(1f, LodTopSoil.Dilute(1f, 0.482f), "and a full share ignores the tint entirely");
    }

    static void Degenerate(Check c)
    {
        c.Eq(0f, LodTopSoil.UntintedShare(0f, 0f, 0.5f), "a black block has no untinted share rather than a NaN");
        c.Eq(1f, LodTopSoil.GreenerCoverage(1f), "full coverage stays capped at 1");
        c.True(LodTopSoil.GreenerCoverage(0.5f) > 0.5f, "greener bias lifts mid coverage");

        foreach (float a in new[] { 0f, 0.001f, 0.999f, 1f })
        foreach (float soil in new[] { 0f, 1f, 255f })
        foreach (float grass in new[] { 0f, 1f, 255f })
        {
            float share = LodTopSoil.UntintedShare(soil, grass, a);
            c.True(share >= 0f && share <= 1f, $"share stays in 0..1 for soil={soil} grass={grass} a={a}");
        }
    }
}
