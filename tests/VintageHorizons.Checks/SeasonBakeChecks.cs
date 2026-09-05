using Vintagestory.API.Common;

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
        BakedBandIsIdentity(c);
        FlagBakedSlottedNeedsHeal(c);
        BakeEpochPackRoundTrip(c);
        BakeEpochQuarterBounds(c);
        FrostMottleWorldLocked(c);
        CanopyFrostIsGrayNotSnow(c);
        FoliageLookMergesWinterAndSplitsSeasons(c);
        UnfrostedGreenLook(c);
        WantsGroundSnowFreezeLine(c);
        WantsInferredGroundSnowByMonth(c);
        CanHoldInferredSnowOnAnyGround(c);
        CoverGroundSnowPerColumn(c);
        CoverGroundSnowSkipsCanopy(c);
        CoverGroundSnowUnderFrostedCanopy(c);
        CoverGroundSnowGreenGrassVetoes(c);
        CoverGroundSnowUnderCanopyRock(c);
        CoverGroundSnowSandAndFarmland(c);
        CoverGroundSnowOneRockAmongDirt(c);
        CoverGroundSnowMeltsInferred(c);
        CoverGroundSnowMeltsRock(c);
        RecaptureLoadedSnowForMeltGate(c);
        CoverTrustsLoadedCaptureInMeltSeason(c);
        CoverVisitedJuneGrassStaysGrass(c);
        CoverVisitedJuneStripsLeftoverSnowlayer(c);
        CoverVisitedJuneSnowOverDirtShowsDirt(c);
        CoverUnvisitedJuneStillInfersAlpine(c);
        CoverDecemberStillInfersAfterCapture(c);
        CoverProvisionalJuneStillInfers(c);
        HasSnowSurfaceSeesInferred(c);
        PackedTempHelper(c);
        IdleSeasonPassSkipCurrentToken(c);
    }

    static void BakeEpochPackRoundTrip(Check c)
    {
        int epoch = LodSeasonBakeEpoch.Pack(1386, 3, 2);
        LodSeasonBakeEpoch.Unpack(epoch, out int y, out int season, out int sub);
        c.Eq(1386, y, "epoch year round-trips");
        c.Eq(3, season, "epoch season index round-trips");
        c.Eq(2, sub, "epoch sub-slot round-trips");
        c.True(LodSeasonBakeEpoch.Describe(epoch).Contains("Fall"),
            "epoch describe names the season");
    }

    static void BakeEpochQuarterBounds(Check c)
    {
        c.Eq(0, LodSeasonBakeEpoch.Pack(1, 1, -5) % 10, "negative sub-epoch clamps to 0");
        c.Eq(2, LodSeasonBakeEpoch.Pack(1, 1, 99) % 10, "oversize sub-epoch clamps to 2");
    }

    static void FrostMottleWorldLocked(Check c)
    {
        float a = LodSeasonBake.FrostMottle01(100, 200);
        float b = LodSeasonBake.FrostMottle01(100, 200);
        c.Eq(a, b, "frost mottle is a pure function of world XZ");
        c.True(a >= 0f && a <= 1f, "frost mottle stays in 0..1");

        c.Eq(63 / LodSeasonBake.FrostLandscapeStep, 64 / LodSeasonBake.FrostLandscapeStep,
            "40-block landscape cells straddle the 64-block L0 tile edge");
        int binL = LodSeasonBake.FrostMottleBin(63, 400);
        int binR = LodSeasonBake.FrostMottleBin(64, 400);
        c.True(Math.Abs(binL - binR) <= 2,
            "adjacent columns across a 64-boundary stay in nearby frost bins, not a plate flip");
        float edgeL = LodSeasonBake.FrostMottle01(63, 400);
        float edgeR = LodSeasonBake.FrostMottle01(64, 400);
        c.True(Math.Abs(edgeL - edgeR) < 0.22f,
            "bilinear landscape mottle is continuous across a 64-block edge");

        float a00 = LodSeasonBake.FrostMottle01(80, 400);
        float a10 = LodSeasonBake.FrostMottle01(81, 400);
        float a01 = LodSeasonBake.FrostMottle01(80, 401);
        c.True(a00 != a10 || a00 != a01,
            "3x3 inside one 40-cell is not a flat integer hash plate");
    }

    static void CanopyFrostIsGrayNotSnow(Check c)
    {
        c.Eq(35, LodSeasonBake.FrostBakeRevision, "frost bake revision is 35");
        c.True(LodSeasonBake.FrostMaxMixCanopy < 0.5f,
            "canopy frost mix stays well below a snow-hat wash");
        float lum = LodSeasonBake.RgbLum01(LodSeasonBake.FrostRgbCanopy);
        c.True(lum > 0.55f && lum < 0.82f,
            "canopy frost target is muted gray, not near-white snow");
    }

    static void FoliageLookMergesWinterAndSplitsSeasons(Check c)
    {
        int green = unchecked((int)0xFF308040);
        int greenTall = unchecked((int)0xFF50A060);
        int autumn = unchecked((int)0xFF2040D0);
        int grayA = LodSeasonBake.FrostRgbCanopy;
        int grayB = unchecked((int)0xFFC0BCB8);
        c.True(LodSeasonBake.SameFoliageLook(green, greenTall),
            "same-hue greens at different luminance share a look (no height cut)");
        c.False(LodSeasonBake.SameFoliageLook(green, autumn),
            "green vs autumn are different looks");
        c.True(LodSeasonBake.SameFoliageLook(grayA, grayB),
            "winter frost-grays share one look");
        c.False(LodSeasonBake.SameFoliageLook(green, grayA),
            "summer green does not merge onto winter gray");
    }

    static void UnfrostedGreenLook(Check c)
    {
        int green = unchecked((int)0xFF308040);
        int springMix = unchecked((int)0xFF609050);
        int gray = LodSeasonBake.FrostRgbCanopy;
        int autumn = unchecked((int)0xFF2040D0);
        int brownGrass = 0x00305070;
        c.True(LodSeasonBake.LooksUnfrostedGreen(green), "summer leaf green is unfrosted green");
        c.True(LodSeasonBake.LooksUnfrostedGreen(springMix), "partial spring green is still green");
        c.False(LodSeasonBake.LooksUnfrostedGreen(gray), "frost-gray canopy is not green");
        c.False(LodSeasonBake.LooksUnfrostedGreen(autumn), "autumn is not green");
        c.False(LodSeasonBake.LooksUnfrostedGreen(brownGrass),
            "winter brown grass does not veto snow");
        c.False(LodSeasonBake.CalendarMonthAllowsInventedGroundSnow(5),
            "May does not invent snow from missing climate");
        c.False(LodSeasonBake.CalendarMonthAllowsInventedGroundSnow(10),
            "October does not invent snow from missing climate");
        c.True(LodSeasonBake.CalendarMonthAllowsInventedGroundSnow(4),
            "April may still invent snow on far plates");
        c.True(LodSeasonBake.CalendarMonthAllowsInventedGroundSnow(12),
            "December still invents snow on far unloaded plates");
    }

    static void WantsGroundSnowFreezeLine(Check c)
    {
        c.True(LodSeasonBake.WantsGroundSnow(-4f, false),
            "air at/below freeze-line wants ground snow even outside calendar winter");
        c.True(LodSeasonBake.WantsGroundSnow(LodSeasonBake.FreezeLineStartC, true),
            "freeze-line start still wants snow");
        c.False(LodSeasonBake.WantsGroundSnow(12f, true),
            "warm air does not invent snow just because the calendar says winter");
        c.True(LodSeasonBake.WantsGroundSnow(float.NaN, true),
            "missing climate in winter still wants snow on far unloaded plates");
        c.False(LodSeasonBake.WantsGroundSnow(float.NaN, false),
            "missing climate outside winter does not invent snow");
    }

    static void WantsInferredGroundSnowByMonth(Check c)
    {
        c.True(LodSeasonBake.WantsInferredGroundSnow(-4f, false, 6),
            "June freeze-line at height still wants snow (alpine / high trees)");
        c.False(LodSeasonBake.WantsInferredGroundSnow(float.NaN, false, 6),
            "June missing climate does not invent a snow field");
        c.False(LodSeasonBake.WantsInferredGroundSnow(12f, true, 6),
            "warm June air does not want inferred snow");
        c.True(LodSeasonBake.WantsInferredGroundSnow(float.NaN, true, 12),
            "December missing climate still wants snow on far plates");
        c.True(LodSeasonBake.WantsInferredGroundSnow(-4f, true, 12),
            "December freeze-line still wants snow");
    }

    static void CoverGroundSnowPerColumn(Check c)
    {
        var s = new LodSection();
        byte grassFlags = (byte)(LodPaletteEntry.FlagBaked | LodPaletteEntry.FlagFrostGround);
        int grass = s.FindOrAddPaletteEntry(10, 0x00305070, grassFlags, tintSlot: 2);
        ulong[] dirt = { LodSection.PackRun(grass, 40, 1) };
        for (int col = 0; col < 4; col++) s.SetColumn(col, dirt);

        long key = LodWorld.SectionKey(0, 0, 0);
        // CaptureBlockPos L0: x = localX. Columns 0 and 1 want snow; 2 and 3 stay dirt.
        int changed = LodSeasonBake.CoverGroundSnowColumns(
            s, key, (x, y, z) => x < 2);

        c.Eq(0, changed, "Cover does not invent FlagSnow; far snow is the shader snowline");
        int snowed = 0, bare = 0;
        for (int col = 0; col < 4; col++)
        {
            ulong[] runs = s.ColumnRuns(col).ToArray();
            c.True(runs.Length >= 1, "covered column still has a surface run");
            byte flags = s.Palette[LodSection.RunPaletteId(runs[0])].Flags;
            bool isSnow = (flags & LodPaletteEntry.FlagSnow) != 0;
            c.False(isSnow, $"column {col} stays grass — Cover is not a season paintbrush");
            c.True((flags & LodPaletteEntry.FlagFrostGround) != 0, $"column {col} keeps the grass row");
            if (col < 2) snowed++;
            else bare++;
        }
        c.Eq(2, snowed, "wanting columns were counted");
        c.Eq(2, bare, "warm columns were counted");
        c.True(s.TryFindPaletteTop(key, grass, out _, out _, out _),
            "the grass palette row still has tops — one tallest sample must not erase snow columns");
    }

    static void CoverGroundSnowSkipsCanopy(Check c)
    {
        var s = new LodSection();
        byte grassFlags = (byte)(LodPaletteEntry.FlagBaked | LodPaletteEntry.FlagFrostGround);
        int grass = s.FindOrAddPaletteEntry(10, 0x00305070, grassFlags, tintSlot: 1);
        int leaf = s.FindOrAddPaletteEntry(20, unchecked((int)0xFF308040), LodPaletteEntry.FlagBaked);
        ulong[] canopy = { LodSection.PackRun(leaf, 52, 40), LodSection.PackRun(grass, 40, 1) };
        s.SetColumn(0, canopy);

        LodSeasonBake.CoverGroundSnowColumns(s, LodWorld.SectionKey(0, 0, 0), (_, _, _) => true);
        ulong[] runs = s.ColumnRuns(0).ToArray();
        byte topFlags = s.Palette[LodSection.RunPaletteId(runs[0])].Flags;
        c.Eq(leaf, LodSection.RunPaletteId(runs[0]), "canopy top stays the foliage row");
        c.Eq(0, topFlags & LodPaletteEntry.FlagSnow, "unsnowed leaves do not get snow hats");
        c.Eq(LodPaletteEntry.FlagBaked, topFlags, "foliage top stays FlagBaked");
        c.True(runs.Length >= 2, "canopy column still has the ground run");
        c.Eq(0, s.Palette[LodSection.RunPaletteId(runs[1])].Flags & LodPaletteEntry.FlagSnow,
            "green unfrosted canopy vetoes inferred ground snow");
    }

    static void CoverGroundSnowUnderFrostedCanopy(Check c)
    {
        var s = new LodSection();
        byte grassFlags = (byte)(LodPaletteEntry.FlagBaked | LodPaletteEntry.FlagFrostGround);
        int grass = s.FindOrAddPaletteEntry(10, 0x00305070, grassFlags, tintSlot: 1);
        int leaf = s.FindOrAddPaletteEntry(20, LodSeasonBake.FrostRgbCanopy, LodPaletteEntry.FlagBaked);
        s.SetColumn(0, new[] { LodSection.PackRun(leaf, 52, 40), LodSection.PackRun(grass, 40, 1) });

        LodSeasonBake.CoverGroundSnowColumns(s, LodWorld.SectionKey(0, 0, 0), (_, _, _) => true);
        ulong[] runs = s.ColumnRuns(0).ToArray();
        c.Eq(0, s.Palette[LodSection.RunPaletteId(runs[0])].Flags & LodPaletteEntry.FlagSnow,
            "frost-gray leaves still do not get snow hats");
        c.True((s.Palette[LodSection.RunPaletteId(runs[1])].Flags & LodPaletteEntry.FlagFrostGround) != 0,
            "frost-gray winter canopy does not get a Cover snow hat on the forest floor");
        c.Eq(0, s.Palette[LodSection.RunPaletteId(runs[1])].Flags & LodPaletteEntry.FlagSnow,
            "Cover does not invent FlagSnow on the forest floor");
    }

    static void CoverGroundSnowGreenGrassVetoes(Check c)
    {
        var s = new LodSection();
        byte grassFlags = (byte)(LodPaletteEntry.FlagBaked | LodPaletteEntry.FlagFrostGround);
        int grass = s.FindOrAddPaletteEntry(10, unchecked((int)0xFF308040), grassFlags, tintSlot: 1);
        s.SetColumn(0, new[] { LodSection.PackRun(grass, 40, 1) });

        LodSeasonBake.CoverGroundSnowColumns(s, LodWorld.SectionKey(0, 0, 0), (_, _, _) => true);
        c.Eq(0, s.Palette[LodSection.RunPaletteId(s.ColumnRuns(0)[0])].Flags & LodPaletteEntry.FlagSnow,
            "green unfrosted grass does not get inferred snow");
    }

    static void CanHoldInferredSnowOnAnyGround(Check c)
    {
        c.True(LodSeasonBake.CanHoldInferredSnow(Blk(EnumBlockMaterial.Stone, "rock-granite")),
            "stone holds inferred snow like vanilla snowlayer");
        c.True(LodSeasonBake.CanHoldInferredSnow(Blk(EnumBlockMaterial.Sand, "sand-granite")),
            "sand holds inferred snow");
        c.True(LodSeasonBake.CanHoldInferredSnow(Blk(EnumBlockMaterial.Gravel, "gravel-granite")),
            "gravel holds inferred snow");
        c.True(LodSeasonBake.CanHoldInferredSnow(Blk(EnumBlockMaterial.Soil, "farmland-dry-none")),
            "farmland is soil and holds inferred snow");
        c.True(LodSeasonBake.CanHoldInferredSnow(Blk(EnumBlockMaterial.Soil, "peat")),
            "peat holds inferred snow");
        c.True(LodSeasonBake.CanHoldInferredSnow(Blk(EnumBlockMaterial.Soil, "soil-medium-normal")),
            "soil still holds inferred snow");
        c.False(LodSeasonBake.CanHoldInferredSnow(Blk(EnumBlockMaterial.Wood, "log-grown-pine-ud")),
            "wood never gets inferred snow hats");
        c.False(LodSeasonBake.CanHoldInferredSnow(Blk(EnumBlockMaterial.Plant, "leaves-grown-birch-green")),
            "leaves stay frost-gray, not inferred snow");
        c.False(LodSeasonBake.CanHoldInferredSnow(Blk(EnumBlockMaterial.Snow, "snowlayer-1")),
            "real snowlayer is not inferred cover");
    }

    static Block Blk(EnumBlockMaterial material, string path) =>
        new()
        {
            BlockMaterial = material,
            Code = new AssetLocation("game", path),
        };

    static void CoverGroundSnowUnderCanopyRock(Check c)
    {
        var s = new LodSection();
        int rock = s.FindOrAddPaletteEntry(30, 0x00606060, 0);
        int leaf = s.FindOrAddPaletteEntry(20, unchecked((int)0xFF308040), LodPaletteEntry.FlagBaked);
        s.SetColumn(0, new[] { LodSection.PackRun(leaf, 52, 40), LodSection.PackRun(rock, 40, 1) });

        LodSeasonBake.CoverGroundSnowColumns(s, LodWorld.SectionKey(0, 0, 0), (_, _, _) => true);
        ulong[] runs = s.ColumnRuns(0).ToArray();
        byte topFlags = s.Palette[LodSection.RunPaletteId(runs[0])].Flags;
        c.Eq(leaf, LodSection.RunPaletteId(runs[0]), "canopy top stays the foliage row");
        c.Eq(0, topFlags & LodPaletteEntry.FlagSnow, "unsnowed leaves do not get snow hats");
        c.True(runs.Length >= 2, "canopy column still has the ground run");
        byte groundFlags = s.Palette[LodSection.RunPaletteId(runs[1])].Flags;
        c.Eq(0, groundFlags & LodPaletteEntry.FlagSnow,
            "green unfrosted canopy vetoes snow on rock the same as grass");
    }

    static void CoverGroundSnowSandAndFarmland(Check c)
    {
        var s = new LodSection();
        int sand = s.FindOrAddPaletteEntry(31, 0x00A0C0D0, 0);
        byte farmFlags = (byte)(LodPaletteEntry.FlagBaked | LodPaletteEntry.FlagFrostGround);
        int farm = s.FindOrAddPaletteEntry(32, 0x00304050, farmFlags, tintSlot: 1);
        s.SetColumn(0, new[] { LodSection.PackRun(sand, 40, 1) });
        s.SetColumn(1, new[] { LodSection.PackRun(farm, 40, 1) });

        LodSeasonBake.CoverGroundSnowColumns(s, LodWorld.SectionKey(0, 0, 0), (_, _, _) => true);
        c.Eq(0, s.Palette[LodSection.RunPaletteId(s.ColumnRuns(0)[0])].Flags
            & LodPaletteEntry.FlagSnow, "bare sand column is not Cover-invented snow");
        c.Eq(0, s.Palette[LodSection.RunPaletteId(s.ColumnRuns(1)[0])].Flags
            & LodPaletteEntry.FlagSnow, "farmland column is not Cover-invented snow");
    }

    static void CoverGroundSnowOneRockAmongDirt(Check c)
    {
        var s = new LodSection();
        byte grassFlags = (byte)(LodPaletteEntry.FlagBaked | LodPaletteEntry.FlagFrostGround);
        int grass = s.FindOrAddPaletteEntry(10, 0x00305070, grassFlags, tintSlot: 2);
        int rock = s.FindOrAddPaletteEntry(30, 0x00606060, 0);
        s.SetColumn(0, new[] { LodSection.PackRun(rock, 40, 1) });
        for (int col = 1; col < 4; col++)
            s.SetColumn(col, new[] { LodSection.PackRun(grass, 40, 1) });

        LodSeasonBake.CoverGroundSnowColumns(s, LodWorld.SectionKey(0, 0, 0), (_, _, _) => true);
        c.Eq(0, s.Palette[LodSection.RunPaletteId(s.ColumnRuns(0)[0])].Flags
            & LodPaletteEntry.FlagSnow,
            "the rock column is not Cover-invented snow");
        for (int col = 1; col < 4; col++)
        {
            c.Eq(0, s.Palette[LodSection.RunPaletteId(s.ColumnRuns(col)[0])].Flags
                & LodPaletteEntry.FlagSnow,
                $"grass column {col} is not Cover-invented snow");
        }
    }

    static void CoverGroundSnowMeltsInferred(Check c)
    {
        var s = new LodSection();
        byte grassFlags = (byte)(LodPaletteEntry.FlagBaked | LodPaletteEntry.FlagFrostGround);
        byte bin = (byte)LodSeasonBake.FrostMottleBin(0, 0);
        int grass = s.FindOrAddPaletteEntry(10, 0x00305070, grassFlags, tintSlot: bin);
        s.SetColumn(0, new[] { LodSection.PackRun(grass, 40, 1) });
        long key = LodWorld.SectionKey(0, 0, 0);
        SeedInferredSnow(s, grass, 0);
        byte covered = s.Palette[LodSection.RunPaletteId(s.ColumnRuns(0)[0])].Flags;
        c.True((covered & LodPaletteEntry.FlagSnow) != 0, "setup: leftover inferred FlagSnow on grass");

        LodSeasonBake.CoverGroundSnowColumns(s, key, (_, _, _) => false);
        ulong top = s.ColumnRuns(0)[0];
        int pid = LodSection.RunPaletteId(top);
        c.Eq(grass, pid, "melt restores the original grass frost row");
        c.Eq(0, s.Palette[pid].Flags & LodPaletteEntry.FlagSnow, "melted inferred snow is not FlagSnow");
        c.True((s.Palette[pid].Flags & LodPaletteEntry.FlagBaked) != 0, "melted ground is FlagBaked grass again");
    }

    static void CoverGroundSnowMeltsRock(Check c)
    {
        var s = new LodSection();
        int rock = s.FindOrAddPaletteEntry(30, 0x00606060, 0);
        s.SetColumn(0, new[] { LodSection.PackRun(rock, 40, 1) });
        long key = LodWorld.SectionKey(0, 0, 0);
        SeedInferredSnow(s, rock, 0);
        c.True((s.Palette[LodSection.RunPaletteId(s.ColumnRuns(0)[0])].Flags
            & LodPaletteEntry.FlagSnow) != 0, "setup: leftover inferred FlagSnow on rock");

        LodSeasonBake.CoverGroundSnowColumns(s, key, (_, _, _) => false);
        ulong top = s.ColumnRuns(0)[0];
        int pid = LodSection.RunPaletteId(top);
        c.Eq(rock, pid, "melt restores the original rock row, not a grass frost row");
        c.Eq(0, s.Palette[pid].Flags & LodPaletteEntry.FlagSnow, "melted rock is not FlagSnow");
        c.Eq(0, s.Palette[pid].Flags & LodPaletteEntry.FlagFrostGround,
            "melted rock is not FlagFrostGround");
    }

    static void RecaptureLoadedSnowForMeltGate(Check c)
    {
        c.True(LodSeasonBake.RecaptureLoadedSnowForMelt(6, true),
            "June snowed L0 recaptures while inferred or leftover snow remains");
        c.True(LodSeasonBake.RecaptureLoadedSnowForMelt(6, true),
            "June recaptures even if a visit token already matches this epoch");
        c.False(LodSeasonBake.RecaptureLoadedSnowForMelt(6, false),
            "June grass with no FlagSnow does not recapture");
        c.False(LodSeasonBake.RecaptureLoadedSnowForMelt(12, true),
            "December still skips recapture of a full snowed L0");
        c.True(LodSeasonBake.RecaptureLoadedSnowForMelt(6, false, pendingVisitRecapture: true),
            "June recaptures alpine land Cover stripped while far");
        c.False(LodSeasonBake.RecaptureLoadedSnowForMelt(6, false, pendingVisitRecapture: false),
            "June grass with no FlagSnow and no pending visit does not recapture");
        c.True(LodSeasonBake.SkipInferredSnowOnVisitedMelt(6, false),
            "May-Oct never invents FlagSnow on a non-provisional quadrant");
        c.False(LodSeasonBake.SkipInferredSnowOnVisitedMelt(6, true),
            "a peek/provisional quadrant is not a loaded visit");
        c.False(LodSeasonBake.SkipInferredSnowOnVisitedMelt(12, false),
            "December still infers after capture");
    }

    static void CoverTrustsLoadedCaptureInMeltSeason(Check c)
    {
        var s = new LodSection();
        byte grassFlags = (byte)(LodPaletteEntry.FlagBaked | LodPaletteEntry.FlagFrostGround);
        int grass = s.FindOrAddPaletteEntry(10, 0x00305070, grassFlags, tintSlot: 2);
        s.SetColumn(0, new[] { LodSection.PackRun(grass, 40, 1) });
        long key = LodWorld.SectionKey(0, 0, 0);
        SeedInferredSnow(s, grass, 0);
        c.True((s.Palette[LodSection.RunPaletteId(s.ColumnRuns(0)[0])].Flags
            & LodPaletteEntry.FlagSnow) != 0,
            "setup: leftover inferred snow on brown grass");

        LodSeasonBake.CoverGroundSnowColumns(
            s, key, (_, _, _) => true, world: null, loadedTruthEpoch: 42, calendarMonth: 6);
        c.Eq(0, s.Palette[LodSection.RunPaletteId(s.ColumnRuns(0)[0])].Flags
            & LodPaletteEntry.FlagSnow,
            "June after walking the chunk melts inferred snow and does not paint it back");
    }

    static void CoverVisitedJuneGrassStaysGrass(Check c)
    {
        var s = new LodSection();
        byte grassFlags = (byte)(LodPaletteEntry.FlagBaked | LodPaletteEntry.FlagFrostGround);
        int grass = s.FindOrAddPaletteEntry(10, 0x00305070, grassFlags, tintSlot: 2);
        s.SetColumn(0, new[] { LodSection.PackRun(grass, 40, 1) });
        LodSeasonBake.CoverGroundSnowColumns(
            s, LodWorld.SectionKey(0, 0, 0), (_, _, _) => true,
            world: null, loadedTruthEpoch: 0, calendarMonth: 6);
        c.Eq(0, s.Palette[LodSection.RunPaletteId(s.ColumnRuns(0)[0])].Flags
            & LodPaletteEntry.FlagSnow,
            "simulated June capture grass stays grass when Cover runs");
    }

    static void CoverVisitedJuneStripsLeftoverSnowlayer(Check c)
    {
        var s = new LodSection();
        int snow = s.FindOrAddPaletteEntry(99, 0x00FFFFFF, LodPaletteEntry.FlagSnow);
        s.SetColumn(0, new[] { LodSection.PackRun(snow, 40, 1) });
        LodSeasonBake.CoverGroundSnowColumns(
            s, LodWorld.SectionKey(0, 0, 0), (_, _, _) => true,
            world: null, loadedTruthEpoch: 0, calendarMonth: 6);
        c.False(s.HasSnowSurface() && s.ColumnRuns(0).Length > 0,
            "far June strips leftover snowlayer so LOD is not last winter's white");
        c.False(LodSeasonBake.SectionHasLeftoverSeasonalSnow(s, null),
            "no leftover snowlayer run remains after Cover");
        c.Eq(0, s.ColumnRuns(0).Length,
            "stripped snowlayer with nothing under it leaves the column empty");
    }

    static void CoverVisitedJuneSnowOverDirtShowsDirt(Check c)
    {
        var s = new LodSection();
        byte grassFlags = (byte)(LodPaletteEntry.FlagBaked | LodPaletteEntry.FlagFrostGround);
        int grass = s.FindOrAddPaletteEntry(10, 0x00305070, grassFlags, tintSlot: 2);
        int snow = s.FindOrAddPaletteEntry(99, 0x00FFFFFF, LodPaletteEntry.FlagSnow);
        s.SetColumn(0, new[]
        {
            LodSection.PackRun(snow, 41, 40),
            LodSection.PackRun(grass, 40, 1),
        });
        LodSeasonBake.CoverGroundSnowColumns(
            s, LodWorld.SectionKey(0, 0, 0), (_, _, _) => true,
            world: null, loadedTruthEpoch: 0, calendarMonth: 6);
        var runs = s.ColumnRuns(0);
        c.Eq(1, runs.Length, "snowlayer run is gone, dirt remains");
        c.Eq(0, s.Palette[LodSection.RunPaletteId(runs[0])].Flags & LodPaletteEntry.FlagSnow,
            "June far plate shows the dirt under leftover snowlayer");
    }

    static void CoverUnvisitedJuneStillInfersAlpine(Check c)
    {
        var s = new LodSection();
        byte grassFlags = (byte)(LodPaletteEntry.FlagBaked | LodPaletteEntry.FlagFrostGround);
        int grass = s.FindOrAddPaletteEntry(10, 0x00305070, grassFlags, tintSlot: 2);
        s.SetColumn(0, new[] { LodSection.PackRun(grass, 40, 1) });
        s.ProvisionalQuadrants = (byte)(1 << LodSection.QuadrantOf(0, 0));
        LodSeasonBake.CoverGroundSnowColumns(
            s, LodWorld.SectionKey(0, 0, 0), (_, _, _) => true,
            world: null, loadedTruthEpoch: 42, calendarMonth: 6);
        c.Eq(0, s.Palette[LodSection.RunPaletteId(s.ColumnRuns(0)[0])].Flags
            & LodPaletteEntry.FlagSnow,
            "provisional June freeze-line does not invent FlagSnow; far snow is the shader snowline");
    }

    static void CoverDecemberStillInfersAfterCapture(Check c)
    {
        var s = new LodSection();
        byte grassFlags = (byte)(LodPaletteEntry.FlagBaked | LodPaletteEntry.FlagFrostGround);
        int grass = s.FindOrAddPaletteEntry(10, 0x00305070, grassFlags, tintSlot: 2);
        s.SetColumn(0, new[] { LodSection.PackRun(grass, 40, 1) });
        s.LoadedCaptureLookToken = 42;
        LodSeasonBake.CoverGroundSnowColumns(
            s, LodWorld.SectionKey(0, 0, 0), (_, _, _) => true,
            world: null, loadedTruthEpoch: 42, calendarMonth: 12);
        c.Eq(0, s.Palette[LodSection.RunPaletteId(s.ColumnRuns(0)[0])].Flags
            & LodPaletteEntry.FlagSnow,
            "December does not invent FlagSnow after capture; far snow is the shader snowline");
    }

    static void CoverProvisionalJuneStillInfers(Check c)
    {
        var s = new LodSection();
        byte grassFlags = (byte)(LodPaletteEntry.FlagBaked | LodPaletteEntry.FlagFrostGround);
        int grass = s.FindOrAddPaletteEntry(10, 0x00305070, grassFlags, tintSlot: 2);
        s.SetColumn(0, new[] { LodSection.PackRun(grass, 40, 1) });
        s.ProvisionalQuadrants = (byte)(1 << LodSection.QuadrantOf(0, 0));
        s.LoadedCaptureLookToken = 42;
        LodSeasonBake.CoverGroundSnowColumns(
            s, LodWorld.SectionKey(0, 0, 0), (_, _, _) => true,
            world: null, loadedTruthEpoch: 42, calendarMonth: 6);
        c.Eq(0, s.Palette[LodSection.RunPaletteId(s.ColumnRuns(0)[0])].Flags
            & LodPaletteEntry.FlagSnow,
            "June peek/provisional plates do not invent FlagSnow");
    }

    static void HasSnowSurfaceSeesInferred(Check c)
    {
        var s = new LodSection();
        byte grassFlags = (byte)(LodPaletteEntry.FlagBaked | LodPaletteEntry.FlagFrostGround);
        int grass = s.FindOrAddPaletteEntry(10, 0x00305070, grassFlags, tintSlot: 2);
        s.SetColumn(0, new[] { LodSection.PackRun(grass, 40, 1) });
        c.False(s.HasSnowSurface(), "grass frost is not FlagSnow");
        c.False(s.QuadrantHasSnowSurface(0), "empty grass quadrant has no snow surface");
        SeedInferredSnow(s, grass, 0);
        c.True(s.HasSnowSurface(), "seeded inferred cover counts as a snow surface");
        c.True(s.HasInferredSnowSurface(), "inferred cover is FlagSnow plus FlagBaked");
        c.True(s.QuadrantHasSnowSurface(0), "inferred snow is per-quadrant");
        c.True(s.QuadrantHasInferredSnow(0), "Cover snow is FlagSnow plus FlagBaked per quadrant");
        c.False(s.QuadrantHasSnowSurface(3), "a snow-free quadrant does not recapture for a neighbour");
        c.False(s.QuadrantHasInferredSnow(3), "a snow-free quadrant has no inferred snow");
    }

    static void PackedTempHelper(Check c)
    {
        c.False(LodSeasonBake.WantsInferredGroundSnow(float.NaN, false, 5),
            "NaN in May does not invent FlagSnow");
        c.True(LodSeasonBake.WantsInferredGroundSnow(-8f, false, 6),
            "real freeze packed temp still wants alpine snow on provisional plates");
        c.False(LodSeasonBake.SkipInferredSnowOnVisitedMelt(6, true),
            "provisional June still allows Cover invent from that freeze reading");
    }

    static void IdleSeasonPassSkipCurrentToken(Check c)
    {
        var s = new LodSection();
        int grass = s.FindOrAddPaletteEntry(10, 0x00305070, 0, tintSlot: 2);
        s.SetColumn(0, new[] { LodSection.PackRun(grass, 40, 1) });
        s.SeasonLookToken = 7;
        c.False(LodSeasonBake.SectionNeedsIdleSeasonPass(s, 7, 6),
            "live-tint grass with no leftover snow skips the idle pass");
        c.False(LodSeasonBake.SectionNeedsIdleSeasonPass(s, 8, 6),
            "stale token alone does not remesh — live tint is uniforms");
        SeedInferredSnow(s, grass, 0);
        s.SeasonLookToken = 7;
        c.True(LodSeasonBake.SectionNeedsIdleSeasonPass(s, 7, 6),
            "inferred snow in June still needs an idle melt even if the token matches");
        c.False(LodSeasonBake.SectionNeedsIdleSeasonPass(s, 7, 12),
            "December inferred snow is shader snowline, not a melt skip");

        var leftover = new LodSection();
        int snow = leftover.FindOrAddPaletteEntry(99, 0x00FFFFFF, LodPaletteEntry.FlagSnow);
        leftover.SetColumn(0, new[] { LodSection.PackRun(snow, 40, 1) });
        leftover.SeasonLookToken = 7;
        c.True(LodSeasonBake.SectionNeedsIdleSeasonPass(leftover, 7, 6),
            "leftover snowlayer in June still needs Cover even if the token matches");
        c.False(LodSeasonBake.SectionNeedsIdleSeasonPass(leftover, 7, 12),
            "December leftover snowlayer is not a melt skip");
    }

    /// <summary>
    /// FlagBaked vegetation leftover unbakes to live tint. FlagSnow identity
    /// (real snow / ice) does not heal. Live-tint without FlagBaked is current.
    /// </summary>
    static void FlagBakedSlottedNeedsHeal(Check c)
    {
        var slotted = new LodSection();
        slotted.FindOrAddPaletteEntry(1, 0x00407040, LodPaletteEntry.FlagBaked, tintSlot: 5);
        c.True(LodSeasonBake.SectionHasBakedEntries(slotted),
            "FlagBaked slotted still joins the season epoch");
        c.True(LodSeasonBake.SectionNeedsLegacyHeal(slotted),
            "FlagBaked with a live season slot unbakes (seas/clim leftover)");

        var slotless = new LodSection();
        slotless.FindOrAddPaletteEntry(1, 0x00407040, LodPaletteEntry.FlagBaked);
        c.True(LodSeasonBake.SectionNeedsLegacyHeal(slotless),
            "FlagBaked SlotNone vegetation leftover unbakes to live tint");

        var snow = new LodSection();
        snow.FindOrAddPaletteEntry(2, 0x00FFFFFF, LodPaletteEntry.FlagSnow);
        c.False(LodSeasonBake.SectionNeedsLegacyHeal(snow),
            "FlagSnow identity does not heal");
    }

    /// <summary>
    /// Contract: FlagBaked albedo is climate×season×untinted; mesher emits bare
    /// BakedBase so the shader uses identity tint (no live seas/clim).
    /// </summary>
    static void BakedBandIsIdentity(Check c)
    {
        c.True(LodMesher.BakedBase + LodTintRegistry.MaxSlots - 1 <= 255,
            "baked band plus max slot still fits in a byte");
        c.Eq(LodMesher.BakedBase, (byte)(LodTintRegistry.MaxSlots * 3),
            "FlagBaked lands at bare BakedBase");
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
        c.Eq(LodMesher.BakedBase, LodTintRegistry.MaxSlots * 3,
            "baked band starts at alpha 192");
    }

    static void BakedAlphaBand(Check c)
    {
        c.False(LodSeasonBake.SectionNeedsLegacyHeal(LegacySection()),
            "live-tint slot without FlagBaked is current");
        var baked = new LodSection();
        baked.FindOrAddPaletteEntry(1, 0x00407040, LodPaletteEntry.FlagBaked);
        c.True(LodSeasonBake.SectionNeedsLegacyHeal(baked),
            "FlagBaked vegetation leftover unbakes to live tint");
        c.True(LodSeasonBake.SectionHasBakedEntries(baked),
            "FlagBaked section reports baked entries for leftover unbake");
        var snow = new LodSection();
        snow.FindOrAddPaletteEntry(2, 0x00FFFFFF, LodPaletteEntry.FlagSnow);
        c.False(LodSeasonBake.SectionNeedsLegacyHeal(snow),
            "FlagSnow identity does not heal");
    }

    static LodSection LegacySection()
    {
        var s = new LodSection();
        s.FindOrAddPaletteEntry(1, 0x00A0B0C0, 0, tintSlot: 5);
        return s;
    }

    /// <summary>
    /// Leftover Cover-inferred snow from old caches: FlagSnow|FlagBaked on the
    /// host block id, original opaque row kept so melt can restore it.
    /// </summary>
    public static void SeedInferredSnow(LodSection s, int hostPid, int col)
    {
        LodPaletteEntry host = s.Palette[hostPid];
        int snow = s.FindOrAddPaletteEntry(
            host.BlockId,
            unchecked((int)0xFFE8EEF4),
            (byte)(LodPaletteEntry.FlagSnow | LodPaletteEntry.FlagBaked));
        ulong[] runs = s.ColumnRuns(col).ToArray();
        if (runs.Length == 0)
        {
            s.SetColumn(col, new[] { LodSection.PackRun(snow, 40, 1) });
            return;
        }
        int last = runs.Length - 1;
        runs[last] = LodSection.PackRun(
            snow, LodSection.RunYTop(runs[last]), LodSection.RunYBottom(runs[last]));
        s.SetColumn(col, runs);
    }
}
