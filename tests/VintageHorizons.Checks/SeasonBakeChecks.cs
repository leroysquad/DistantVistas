namespace DistantVistas.Checks;

/// <summary>
/// Discover-bake arithmetic and eligibility without a live game world.
/// </summary>
public static class SeasonBakeChecks
{
    public static void Run(Check c)
    {
        MultiplyRgbIdentity(c);
        MultiplyRgbScalesChannels(c);
        FlagBakedSkipsLiveTintBand(c);
        BakedAlphaBand(c);
    }

    static void MultiplyRgbIdentity(Check c)
    {
        int white = unchecked((int)0xFFFFFFFF);
        int same = LodSeasonBake.MultiplyRgb(white, 1f, 1f, 1f);
        c.Eq(white, same, "identity tint leaves RGB unchanged");
    }

    static void MultiplyRgbScalesChannels(Check c)
    {
        int grey = 0xFF808080;
        int outc = LodSeasonBake.MultiplyRgb(grey, 0.5f, 1f, 0.25f);
        c.Eq(0x40, outc & 0xFF, "red channel scales");
        c.Eq(0x80, (outc >> 8) & 0xFF, "green channel unchanged at 1.0");
        c.Eq(0x20, (outc >> 16) & 0xFF, "blue channel scales");
    }

    static void FlagBakedSkipsLiveTintBand(Check c)
    {
        c.True(LodPaletteEntry.FlagBaked == 32, "FlagBaked is bit 32 for mesh alpha path");
        c.True((LodPaletteEntry.FlagBaked & LodPaletteEntry.FlagThin) == 0,
            "FlagBaked does not collide with FlagThin");
        c.Eq(LodMesher.BakedBase, LodTintRegistry.MaxSlots * 3,
            "baked band starts at alpha 192");
    }

    static void BakedAlphaBand(Check c)
    {
        c.True(LodSeasonBake.SectionNeedsLegacyHeal(LegacySection()),
            "live-tint slot without FlagBaked needs legacy heal");
        var baked = new LodSection();
        baked.FindOrAddPaletteEntry(1, 0x00407040, LodPaletteEntry.FlagBaked);
        c.False(LodSeasonBake.SectionNeedsLegacyHeal(baked),
            "FlagBaked rows are not legacy");
    }

    static LodSection LegacySection()
    {
        var s = new LodSection();
        s.FindOrAddPaletteEntry(1, 0x00A0B0C0, 0, tintSlot: 5);
        return s;
    }
}
