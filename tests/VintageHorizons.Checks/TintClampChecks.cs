namespace DistantVistas.Checks;

/// <summary>
/// High climate samples must not bleach greyscale stored colour to snow-white.
/// That was the far mountain-cap wash: rock sides (slot 0) stayed correct.
/// </summary>
public static class TintClampChecks
{
    public static void Run(Check c)
    {
        SnowWhiteIsPulledDown(c);
        OrdinaryGrassUnchanged(c);
        HighSampleStaysBelowSnowBand(c);
        SnowLikeHighCopiesLow(c);
        GreyValleyIsNotCopiedOntoHigh(c);
        RockLikeSkipsLiveTint(c);
        DullOliveGrassKeepsTint(c);
        ClampOnlyPullsBrightSamples(c);
    }

    static void SnowWhiteIsPulledDown(Check c)
    {
        float r = 1f, g = 1f, b = 1f;
        LodTintRegistry.ClampTintAwayFromWhite(ref r, ref g, ref b);
        c.Near(LodTintRegistry.MaxTintLuminance, (r + g + b) / 3.0, 0.0001,
            "a snow-white climate sample is pulled down to MaxTintLuminance");
        c.True(r < 0.8f && g < 0.8f && b < 0.8f, "no channel stays in the plastic-white band");
    }

    static void OrdinaryGrassUnchanged(Check c)
    {
        float r = 0.482f, g = 0.612f, b = 0.051f;
        float or = r, og = g, ob = b;
        LodTintRegistry.ClampTintAwayFromWhite(ref r, ref g, ref b);
        c.Eq(or, r, "ordinary grass red is unchanged");
        c.Eq(og, g, "ordinary grass green is unchanged");
        c.Eq(ob, b, "ordinary grass blue is unchanged");
    }

    static void HighSampleStaysBelowSnowBand(Check c)
    {
        c.True(LodTintRegistry.HighSampleOffsetBlocks < 320,
            "high sample is below the old snow-climate offset of 320");
        c.True(LodTintRegistry.HighSampleOffsetBlocks >= 80,
            "high sample still has enough lapse to cool mountain grass");
    }

    static void SnowLikeHighCopiesLow(Check c)
    {
        var low = new float[LodTintRegistry.MaxSlots * 4];
        var high = new float[LodTintRegistry.MaxSlots * 4];
        low[4] = 0.48f; low[5] = 0.61f; low[6] = 0.05f; low[7] = 1f;
        high[4] = 0.96f; high[5] = 0.97f; high[6] = 0.98f; high[7] = 1f;
        c.True(LodTintRegistry.IsSnowLikeTint(high[4], high[5], high[6]),
            "a snow-row climate sample is recognised");
        LodTintRegistry.ProtectHighTintFromSnow(low, high, slot: 1);
        c.Near(0.48, high[4], 0.0001, "snow-row high red copies the valley sample");
        c.Near(0.61, high[5], 0.0001, "snow-row high green copies the valley sample");
        c.Near(0.05, high[6], 0.0001, "snow-row high blue copies the valley sample");
    }

    static void GreyValleyIsNotCopiedOntoHigh(Check c)
    {
        var low = new float[LodTintRegistry.MaxSlots * 4];
        var high = new float[LodTintRegistry.MaxSlots * 4];
        low[4] = 0.65f; low[5] = 0.65f; low[6] = 0.65f; low[7] = 1f;
        high[4] = 0.96f; high[5] = 0.97f; high[6] = 0.98f; high[7] = 1f;
        LodTintRegistry.ProtectHighTintFromSnow(low, high, slot: 1);
        c.Near(0.96, high[4], 0.0001, "identity/grey valley is not copied onto a snow-row high slot");
    }

    static void RockLikeSkipsLiveTint(Check c)
    {
        c.True(LodPaletteRepair.IsRockLikeAlbedo(0x002F4A6B),
            "brown dirt/rock albedo is rock-like");
        c.False(LodPaletteRepair.IsRockLikeAlbedo(0x00A0A0A0),
            "greyscale grass waiting for a climate map is not rock-like");
        c.False(LodPaletteRepair.IsRockLikeAlbedo(unchecked((int)0x00FCFCFC)),
            "snow/missing-tex white is a bright cap, not rock-like");
    }

    static void DullOliveGrassKeepsTint(Check c)
    {
        // TrueScale / vanilla grass is dull olive, not chroma-0 grey. 0.7.19
        // chroma>=24 + mid luma treated this as rock and stripped the slot.
        int olive = 0x00649B8C; // R=140 G=155 B=100, chroma 55, G leads R
        c.False(LodPaletteRepair.IsRockLikeAlbedo(olive),
            "dull olive grass waiting for a colour map is not rock-like");
        int composite = 0x00626F80; // R=128 G=111 B=98, sparse grass+dirt mix
        c.False(LodPaletteRepair.IsRockLikeAlbedo(composite),
            "grass plus dirt composite still needs climate tint");
        c.True(LodTintRegistry.MaxTintLuminance > 0.70f,
            "tint clamp stays above the 0.65 grey crush");
        c.True(LodTintRegistry.MaxTintLuminance <= 0.78f + 0.0001f,
            "tint clamp is the 0.7.18 white pull, not brighter");
    }

    static void ClampOnlyPullsBrightSamples(Check c)
    {
        float r = 0.70f, g = 0.72f, b = 0.40f;
        float or = r, og = g, ob = b;
        LodTintRegistry.ClampTintAwayFromWhite(ref r, ref g, ref b);
        c.Eq(or, r, "a 0.61-luma green-brown is not pulled toward grey");
        c.Eq(og, g, "green channel of a sub-0.78 tint is unchanged");
        c.Eq(ob, b, "blue channel of a sub-0.78 tint is unchanged");
    }
}
