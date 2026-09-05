namespace DistantVistas.Checks;

/// <summary>
/// Login-bake arithmetic and eligibility without a live game world.
/// </summary>
public static class SeasonBakeChecks
{
    public static void Run(Check c)
    {
        MultiplyRgbIdentity(c);
        MultiplyRgbScalesChannels(c);
        FlagBakedSkipsLiveTintBand(c);
        SnowVoteMajority(c);
    }

    static void MultiplyRgbIdentity(Check c)
    {
        int white = unchecked((int)0xFFFFFFFF);
        int same = LodSeasonBake.MultiplyRgb(white, 1f, 1f, 1f);
        c.Eq(white, same, "identity tint leaves RGB unchanged");
    }

    static void MultiplyRgbScalesChannels(Check c)
    {
        int grey = unchecked((int)0xFF808080);
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
    }

    static void SnowVoteMajority(Check c)
    {
        c.False(new LodSeasonBake.SnowVote(0, 0).MajoritySnow, "no eligible cells => no majority snow");
        c.False(new LodSeasonBake.SnowVote(4, 2).MajoritySnow, "half snow is not a majority");
        c.True(new LodSeasonBake.SnowVote(4, 3).MajoritySnow, "three of four eligible counts as majority");
    }
}
