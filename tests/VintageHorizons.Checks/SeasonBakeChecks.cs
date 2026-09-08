using Vintagestory.API.Common;

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
        VisualTopStaysDistinctFromDirt(c);
        SnowDirtSpotsSurviveQuantize(c);
        ClassifyRejectsFalseSnow(c);
        FinishColumnPaintSeasonGround(c);
        FinishColumnPaintKeepsSnowAndCanopy(c);
        VisitFrostCanopyAndGround(c);
        CanopyGrayPathGate(c);
        CanopyGrayMottleDeterministic(c);
        CanopyGrayMixKeepsAutumn(c);
        SimdAfterGetColor(c);
        ExpireMissingTexGate(c);
        LoginBakeGetColorReduction(c);
    }

    static void LoginBakeGetColorReduction(Check c)
    {
        c.True(LodSurfaceMix.StackDeterminedByTopOnly(
                LodSurfaceMix.Kind.Snow, "snow-3", unchecked((int)0xFFEEEEEE), 0.5f),
            "snow cap stops stack after top GetColor");
        c.True(LodSurfaceMix.StackDeterminedByTopOnly(
                LodSurfaceMix.Kind.Plant, "pine-leaves-normal", unchecked((int)0xFF336622), 0.2f),
            "canopy leaves stop stack in autumn");
        c.False(LodSurfaceMix.StackDeterminedByTopOnly(
                LodSurfaceMix.Kind.Ground, "soil-low-normal", unchecked((int)0xFF886644), 0.2f),
            "ground columns still sample the stack for mix");
        c.Eq(
            LodBakeScratch.GetColorCacheKey(42, 128, 64, 256),
            LodBakeScratch.GetColorCacheKey(42, 131, 64, 263),
            "GetColor cache key shares an 8×8 climate tile");
        c.False(
            LodBakeScratch.GetColorCacheKey(42, 128, 64, 256)
                == LodBakeScratch.GetColorCacheKey(43, 128, 64, 256),
            "GetColor cache key varies by block id");
        LodBakeScratch.BeginOverlayGetColorCache();
        LodBakeScratch.RememberOverlayGetColor(7, 128, 64, 256, unchecked((int)0xFF112233));
        c.True(
            LodBakeScratch.TryGetOverlayGetColor(7, 128, 64, 256, out int overlayHit)
                && overlayHit == unchecked((int)0xFF112233),
            "overlay GetColor cache returns remembered rgb");
        c.False(
            LodBakeScratch.TryGetOverlayGetColor(7, 144, 64, 256, out _),
            "overlay cache miss on different 16×16 tile");
        LodBakeScratch.EndOverlayGetColorCache();
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
        c.Eq(2, LodPaletteEntry.FlagFrost, "FlagFrost is bit 2 for mesher UP extra-white");
        c.True((LodPaletteEntry.FlagFrost & LodPaletteEntry.FlagWater) == 0,
            "FlagFrost does not collide with FlagWater");
        c.True((LodPaletteEntry.FlagFrost & LodPaletteEntry.FlagSkip) == 0,
            "FlagFrost does not collide with FlagSkip");
        c.True((LodPaletteEntry.FlagFrost & LodPaletteEntry.FlagThin) == 0,
            "FlagFrost does not collide with FlagThin");
        c.True((LodPaletteEntry.FlagFrost & LodPaletteEntry.FlagBaked) == 0,
            "FlagFrost does not collide with FlagBaked");
        c.True((LodPaletteEntry.FlagBaked & LodPaletteEntry.FlagThin) == 0,
            "FlagBaked does not collide with FlagThin");
        c.Eq(LodPaletteEntry.FlagBaked | LodPaletteEntry.FlagFrost, LodPaletteEntry.VisitKeepMask,
            "Reclassify keeps bake and frost bits");
    }

    static void SnowVoteMajority(Check c)
    {
        c.False(new LodSeasonBake.SnowVote(0, 0).MajoritySnow, "no eligible cells => no majority snow");
        c.False(new LodSeasonBake.SnowVote(4, 2).MajoritySnow, "half snow is not a majority");
        c.True(new LodSeasonBake.SnowVote(4, 3).MajoritySnow, "three of four eligible counts as majority");
    }

    static void VisualTopStaysDistinctFromDirt(Check c)
    {
        int snow = LodSurfaceMix.Pack(230, 232, 236);
        int dirt = LodSurfaceMix.Pack(92, 68, 42);
        int buried = LodSurfaceMix.MixCamouflage(snow, 1f, dirt, 1f, 0, 0f);
        LodSurfaceMix.Unpack(snow, out int sr, out _, out _);
        LodSurfaceMix.Unpack(buried, out int mr, out _, out _);
        c.True(sr - mr > 40, "dirt mix still exists as the rejected bury path");
        c.Eq(0, LodSurfaceMix.BlurRadius, "neighbour blur is off so spots are not washed to cream");
    }

    static void SnowDirtSpotsSurviveQuantize(Check c)
    {
        int snow = LodSurfaceMix.Pack(230, 232, 236);
        int dirt = LodSurfaceMix.Pack(92, 68, 42);
        int qs = LodSurfaceMix.Quantize(snow);
        int qd = LodSurfaceMix.Quantize(dirt);
        c.True(qs != qd, "quantized snow and dirt stay two colours");
        LodSurfaceMix.Unpack(qs, out int r, out int g, out int b);
        c.True((r + g + b) / 3 >= 180, "quantized snow stays pale");
    }

    static void ClassifyRejectsFalseSnow(Check c)
    {
        c.Eq(LodSurfaceMix.Kind.Plant, Classify("tallgrass-tall", EnumBlockMaterial.Plant),
            "tallgrass is plant, never snow");
        c.Eq(LodSurfaceMix.Kind.Ground, Classify("soil-medium-normal", EnumBlockMaterial.Soil),
            "soil is ground, never snow");
        c.Eq(LodSurfaceMix.Kind.Ground, Classify("peat-farmland", EnumBlockMaterial.Soil),
            "peat is ground, never snow");
        c.Eq(LodSurfaceMix.Kind.Snow, Classify("snowlayer-4", EnumBlockMaterial.Snow),
            "snowlayer is snow");
        c.False(LodSurfaceMix.IsActualSnowTop(LodSurfaceMix.Kind.Snow, "snowlayer-1"),
            "snowlayer-1 is specks, not a snow plate");
        c.True(LodSurfaceMix.IsActualSnowTop(LodSurfaceMix.Kind.Snow, "snowlayer-4"),
            "thicker snowlayer is a snow plate");
        c.Eq(LodSurfaceMix.Kind.Snow, Classify("snowblock", EnumBlockMaterial.Stone),
            "snowblock is snow");
        c.Eq(LodSurfaceMix.Kind.Snow, Classify("glacierice", EnumBlockMaterial.Ice),
            "glacier ice is snow");
        c.True(LodSurfaceMix.IsSeasonGroundPath("tallgrass-eaten-free"),
            "winter no-snow tallgrass is season ground");
        c.False(LodSurfaceMix.IsActualSnowTop(LodSurfaceMix.Kind.Plant, "tallgrass-tall"),
            "tallgrass kind never counts as a snow sheet");
        c.False(LodSurfaceMix.IsActualSnowTop(LodSurfaceMix.Kind.Snow, "soil-medium-normal"),
            "soil path never counts as a snow sheet even if kind leaked");
        c.False(LodSeasonBake.KeepVisitSnowColor(Block("snowlayer-1", EnumBlockMaterial.Snow), false),
            "snowlayer-1 is not kept as a snow plate");
        c.True(LodSeasonBake.KeepVisitSnowColor(Block("snowlayer-4", EnumBlockMaterial.Snow), false),
            "thicker snowlayer keeps snow RGB");
        c.False(LodSeasonBake.KeepVisitSnowColor(Block("tallgrass-tall", EnumBlockMaterial.Plant), false),
            "tallgrass RGB is never kept as snow");
    }

    static void FinishColumnPaintSeasonGround(Check c)
    {
        int dirt = LodSurfaceMix.Pack(92, 78, 42);
        int olive = LodSurfaceMix.Pack(88, 102, 54);
        int pale = LodSurfaceMix.Pack(210, 214, 218);

        int summer = LodSurfaceMix.FinishColumnPaint(
            LodSurfaceMix.Kind.Plant, "tallgrass-tall", olive,
            snow: 0, snowW: 0f, ground: dirt, groundW: 1f, plant: olive, plantW: 1f,
            winter: 0f);
        // Tallgrass keeps live GetColor until DeepWinterCamouflageStart (not soil blend).
        c.Eq(olive, summer,
            "summer mix is live GetColor plant and ground");
        LodPaletteRepair.Channels(summer, out int sr, out int sg, out _, out _, out _);
        c.True(sg >= sr, "summer mix stays green-leaning");
        c.True(!LodPaletteRepair.IsSnowOrIceAlbedo(summer), "summer mix is not snow");

        int mix = LodSurfaceMix.FinishColumnPaint(
            LodSurfaceMix.Kind.Plant, "tallgrass-tall", olive,
            snow: LodSurfaceMix.Pack(230, 232, 236), snowW: 0f,
            ground: dirt, groundW: 1f, plant: olive, plantW: 1f,
            winter: 1f);
        c.Eq(LodSurfaceMix.MixSeasonGround(olive, dirt, 0, 0, 1f), mix,
            "winter no-snow mix is live GetColor weighted toward soil");
        LodPaletteRepair.Channels(mix, out int r, out int g, out int b, out int luma, out int chroma);
        c.True(luma < 160, "winter no-snow mix is not a pale snow plate");
        c.True(chroma > 16, "winter no-snow mix keeps brown-green chroma");
        c.True(b < g && b < r, "mix is earth-toned, not a blue-white snow plate");
        c.True(!LodPaletteRepair.IsSnowOrIceAlbedo(mix),
            "winter no-snow mix is not snow albedo");

        int paleMix = LodSurfaceMix.FinishColumnPaint(
            LodSurfaceMix.Kind.Plant, "tallgrass-tall", pale,
            snow: 0, snowW: 0f, ground: dirt, groundW: 1f, plant: pale, plantW: 1f,
            winter: 1f);
        c.Eq(dirt, paleMix, "pale climate grass is replaced by live soil, not kept as snow");

        int tex = LodSurfaceMix.Pack(110, 104, 68);
        int winterTex = LodSurfaceMix.MixSeasonGround(pale, dirt, tex, tex, 1f);
        c.Eq(tex, winterTex, "winter mix at 1 is the texture camouflage, not pale GetColor");
        LodPaletteRepair.Channels(winterTex, out _, out _, out _, out int texLuma, out int texChroma);
        c.True(texLuma < 160, "texture camouflage is not a pale snow plate");
        c.True(texChroma > 16, "texture camouflage keeps brown-olive chroma");
        c.True(!LodPaletteRepair.IsBrightCap(winterTex), "texture camouflage is not a bright cap");
        c.True(!LodPaletteRepair.IsSnowOrIceAlbedo(winterTex), "texture camouflage is not snow albedo");

        int summerTex = LodSurfaceMix.MixSeasonGround(olive, dirt, tex, tex, 0f);
        LodPaletteRepair.Channels(summerTex, out int str, out int stg, out _, out _, out _);
        c.True(stg >= str, "summer still leans green when texture is available");
        c.True(summerTex != winterTex, "summer and winter ground paint are different colours");

        int speck = LodSurfaceMix.FinishColumnPaint(
            LodSurfaceMix.Kind.Snow, "snowlayer-1", pale,
            snow: pale, snowW: 1f, ground: dirt, groundW: 1f, plant: olive, plantW: 1f,
            winter: 1f);
        c.Eq(LodSurfaceMix.MixSeasonGround(olive, dirt, 0, 0, 1f), speck,
            "snowlayer-1 falls through to season ground mix");

        float summerRel = 0.25f + LodSurfaceMix.SeasonEnumOffset;
        float winterRel = 0.75f + LodSurfaceMix.SeasonEnumOffset;
        c.Near(0, LodSurfaceMix.WinterAmount(summerRel), 1e-5, "calendar summer is not winter");
        c.Near(1, LodSurfaceMix.WinterAmount(winterRel), 1e-5, "calendar winter is full winter amount");
        c.True(LodSurfaceMix.WinterAmount(0.333f) < 0.12f, "May year-rel is late spring, not winter");
        c.Near(0.5, LodSurfaceMix.WinterAmount(0.50f + LodSurfaceMix.SeasonEnumOffset + 0.125f), 0.02,
            "mid-autumn interpolates winter amount");

        int frostGround = LodSurfaceMix.MixSeasonGround(olive, dirt, tex, tex, 1f, frost: 1f);
        int liveWinter = LodSurfaceMix.MixSeasonGround(olive, dirt, 0, 0, 1f);
        c.True(frostGround != tex, "frosted winter ground is not the manila texture plate");
        c.Eq(LodSeasonBake.MixTowardWhite(liveWinter, LodSeasonBake.GroundFrostAlpha), frostGround,
            "frost keeps live winter mix then a light white wash");
        LodPaletteRepair.Channels(frostGround, out _, out _, out _, out int frostLuma, out int frostChroma);
        c.True(frostLuma < 160, "frosted winter ground is not a white sheet");
        c.True(frostChroma > 16, "frosted winter ground keeps chroma");
        c.True(!LodPaletteRepair.IsSnowOrIceAlbedo(frostGround),
            "frosted winter ground is not snow albedo");

        int frostPaint = LodSurfaceMix.FinishColumnPaint(
            LodSurfaceMix.Kind.Plant, "tallgrass-tall", olive,
            snow: 0, snowW: 0f, ground: dirt, groundW: 1f, plant: olive, plantW: 1f,
            winter: 1f, texGround: tex, texPlant: tex, frost: 1f);
        c.Eq(frostGround, frostPaint, "FinishColumnPaint frost matches MixSeasonGround frost");
    }

    static void FinishColumnPaintKeepsSnowAndCanopy(Check c)
    {
        int snow = LodSurfaceMix.Pack(230, 232, 236);
        int dirt = LodSurfaceMix.Pack(92, 68, 42);
        int keptSnow = LodSurfaceMix.FinishColumnPaint(
            LodSurfaceMix.Kind.Snow, "snowlayer-4", snow,
            snow, 1f, dirt, 1f, 0, 0f);
        c.Eq(snow, keptSnow, "real snow top is not buried under dirt");

        int autumn = LodSurfaceMix.Pack(200, 110, 40);
        int keptLeaf = LodSurfaceMix.FinishColumnPaint(
            LodSurfaceMix.Kind.Plant, "leaves-grown-oak", autumn,
            0, 0f, dirt, 1f, autumn, 1f);
        c.Eq(autumn, keptLeaf, "tree canopy keeps live leaf colour");

        int bush = LodSurfaceMix.Pack(180, 90, 50);
        int keptBush = LodSurfaceMix.FinishColumnPaint(
            LodSurfaceMix.Kind.Plant, "berrybush-blueberry-ripe", bush,
            0, 0f, dirt, 1f, bush, 1f, winter: 0.5f);
        c.Eq(bush, keptBush, "berry bush keeps live GetColor through autumn");
    }

    static void VisitFrostCanopyAndGround(Check c)
    {
        LodSeasonBake.LiveWinterAmount = 1f;

        Block pine = Block("leaves-grown-pine", EnumBlockMaterial.Leaves, frostable: true);
        c.True(LodSeasonBake.IsFrostableCanopy(pine),
            "pine Leaves + Frostable is frostable canopy");
        Block oak = Block("leaves-grown-oak", EnumBlockMaterial.Leaves, frostable: true);
        c.True(LodSeasonBake.IsFrostableCanopy(oak),
            "oak Leaves + Frostable is frostable canopy");
        Block bush = Block("berrybush-blueberry-flowering", EnumBlockMaterial.Plant, frostable: true);
        c.True(LodSeasonBake.IsFrostableCanopy(bush),
            "frostable berry bush is season foliage canopy");
        c.True(LodCanopyGray.IsSeasonFoliagePath("berrybush-blueberry-flowering"),
            "berrybush path is season foliage");
        c.False(LodCanopyGray.IsVanillaTreeCanopyPath("berrybush-blueberry-flowering"),
            "bushes stay out of tree-crown gray path");
        Block grass = Block("tallgrass-tall", EnumBlockMaterial.Plant, frostable: true);
        c.False(LodSeasonBake.IsFrostableCanopy(grass),
            "tallgrass Plant is not frostable canopy");
        Block bareLeaf = Block("leaves-grown-oak", EnumBlockMaterial.Leaves, frostable: false);
        c.False(LodSeasonBake.IsFrostableCanopy(bareLeaf),
            "Leaves without Frostable is not frostable canopy");

        c.True(LodSeasonBake.ShouldFlagFrost(1f, pine, "leaves-grown-pine"),
            "full frost flags pine canopy");
        c.False(LodSeasonBake.ShouldFlagFrost(0.10f, pine, "leaves-grown-pine"),
            "below FrostFlagMin does not flag");
        c.False(LodSeasonBake.ShouldFlagFrost(1f, grass, "tallgrass-tall"),
            "tallgrass does not get FlagFrost");
        c.True(LodSeasonBake.ShouldFlagFrost(1f, Block("soil-medium-normal", EnumBlockMaterial.Soil),
                "leaves-grown-oak"),
            "stored soil with a canopy visual top still flags frost");
        c.True(LodSeasonBake.ShouldFlagFrost(1f, Block("soil-medium-normal", EnumBlockMaterial.Soil),
                "berrybush-blueberry-ripe"),
            "stored soil with a bush visual top still flags frost");

        LodSeasonBake.LiveWinterAmount = 0.5f;
        c.False(LodSeasonBake.SeasonAllowsFrost,
            "mid-autumn WinterAmount below FrostSeasonMin blocks frost");
        c.False(LodSeasonBake.ShouldFlagFrost(1f, pine, "leaves-grown-pine"),
            "mid-autumn does not FlagFrost");
        LodSeasonBake.LiveWinterAmount = 1f;

        byte mixed = LodSeasonBake.MixVisitBakeFlags(0, frost: true);
        c.Eq((byte)(LodPaletteEntry.FlagBaked | LodPaletteEntry.FlagFrost), mixed,
            "visit bake stores FlagBaked and FlagFrost together");
        c.Eq(LodPaletteEntry.FlagBaked, LodSeasonBake.MixVisitBakeFlags(LodPaletteEntry.FlagFrost, frost: false),
            "warm rebake clears FlagFrost and keeps FlagBaked");
        c.Eq(LodPaletteEntry.FlagThin,
            LodSeasonBake.ClearVisitBakeFlags(
                (byte)(LodPaletteEntry.FlagBaked | LodPaletteEntry.FlagFrost | LodPaletteEntry.FlagThin)),
            "clearing visit bits leaves live policy flags");

        int pineRgb = LodSurfaceMix.Pack(32, 31, 7);
        int side = LodSeasonBake.MixTowardWhite(pineRgb, LodSeasonBake.SideFrostAlpha);
        LodPaletteRepair.Channels(side, out int sr, out int sg, out int sb, out int sideLuma, out _);
        c.True(sg + 8 >= sr, "side frost still green/orange-leaning, not a white cube");
        c.True(sb < sg, "side frost is not a blue-white snow plate");
        c.True(sideLuma < 140, "side frost 0.32 stays darker than a frost crown");
        c.Eq(pineRgb, LodSeasonBake.MixTowardWhite(pineRgb, 0f), "mix 0 is identity");

        int top = LodSeasonBake.MixTowardWhite(side, LodSeasonBake.TopFrostExtra);
        LodPaletteRepair.Channels(top, out _, out _, out _, out int topLuma, out _);
        c.True(topLuma > sideLuma + 40, "mesher UP extra-white is paler than the stored side");

        float midAutumn = 0.50f + LodSurfaceMix.SeasonEnumOffset + 0.10f;
        c.True(LodSurfaceMix.WinterAmount(midAutumn) < LodSeasonBake.FrostSeasonMin,
            "mid-autumn WinterAmount stays under frost gate");
        float lateAutumn = 0.50f + LodSurfaceMix.SeasonEnumOffset + 0.20f;
        c.True(LodSurfaceMix.WinterAmount(lateAutumn) >= LodSeasonBake.FrostSeasonMin,
            "late autumn reaches frost gate");
    }

    static LodSurfaceMix.Kind Classify(string path, EnumBlockMaterial material) =>
        LodSurfaceMix.Classify(Block(path, material));

    static Block Block(string path, EnumBlockMaterial material, bool frostable = false) =>
        new()
        {
            BlockId = 1,
            BlockMaterial = material,
            Code = new AssetLocation("game", path),
            Frostable = frostable,
        };

    static void CanopyGrayPathGate(Check c)
    {
        c.True(LodCanopyGray.IsVanillaTreeCanopyPath("leaves-grown-oak"),
            "oak leaves are vanilla canopy");
        c.True(LodCanopyGray.IsVanillaTreeCanopyPath("leaves-grown-pine"),
            "pine needles are vanilla canopy");
        c.True(LodCanopyGray.IsVanillaTreeCanopyPath("leavesbranchy-grown-birch"),
            "branchy leaves are vanilla canopy");
        c.False(LodCanopyGray.IsVanillaTreeCanopyPath("tallgrass-eaten-free"),
            "tallgrass is not canopy gray");
        c.False(LodCanopyGray.IsVanillaTreeCanopyPath("snowlayer-up"),
            "snowlayer is not canopy gray");
        c.False(LodCanopyGray.IsVanillaTreeCanopyPath("soil-medium-none"),
            "ground is not canopy gray");
        c.False(LodCanopyGray.IsVanillaTreeCanopyPath("berrybush-blueberry-flowering"),
            "bushes are not canopy gray");
        c.False(LodCanopyGray.IsVanillaTreeCanopyPath("sapling-oak"),
            "saplings are not canopy gray");
        c.False(LodCanopyGray.IsVanillaTreeCanopyPath("fallenleaves"),
            "fallen leaves are not canopy gray");
    }

    static void CanopyGrayMottleDeterministic(Check c)
    {
        float a = LodCanopyGray.MottleAmount(12004, -512);
        float b = LodCanopyGray.MottleAmount(12004, -512);
        c.Eq(a, b, "same XZ always the same mottle");
        int hits = 0, misses = 0;
        for (int i = 0; i < 64; i++)
        {
            float m = LodCanopyGray.MottleAmount(1000 + i * 3, 2000 + i * 5);
            if (m >= LodCanopyGray.MottleThreshold) hits++;
            else misses++;
        }
        c.True(hits > 0 && misses > 0, "mottle is patterned, not a solid gray sheet");
        c.Eq(0f, LodCanopyGray.MixTowardGray(LodCanopyGray.MottleThreshold - 0.01f, 1f),
            "below-threshold cells keep season colour");
        float mix = LodCanopyGray.MixTowardGray(0.95f, 1f);
        c.True(mix >= LodCanopyGray.MixMin && mix <= LodCanopyGray.MixMax,
            "hit cells mix gray in the 0.38-0.70 band");
        c.Eq(0f, LodCanopyGray.MixTowardGray(0.95f, 0f),
            "warm months (no frost weight) do not gray");
    }

    static void CanopyGrayMixKeepsAutumn(Check c)
    {
        int autumn = LodSurfaceMix.Pack(200, 110, 40);
        c.Eq(autumn, LodCanopyGray.MixRgb(autumn, 0f), "mix 0 is identity");
        int gray = LodCanopyGray.MixRgb(autumn, 1f);
        LodSurfaceMix.Unpack(gray, out int gr, out int gg, out int gb);
        c.Eq(LodCanopyGray.GrayR, gr, "mix 1 red is gray");
        c.Eq(LodCanopyGray.GrayG, gg, "mix 1 green is gray");
        c.Eq(LodCanopyGray.GrayB, gb, "mix 1 blue is gray");
        int half = LodCanopyGray.MixRgb(autumn, 0.5f);
        LodSurfaceMix.Unpack(half, out int r, out int g, out int b);
        c.True(r > b + 20, "half-mix autumn still reads warm (R ahead of B)");
        c.True(g > 80 && g < 160, "half-mix is not a white frost sheet");
        LodPaletteRepair.Channels(half, out _, out _, out _, out int luma, out int chroma);
        c.True(luma < LodPaletteRepair.BrightCapLuma, "gray mix stays under bright-cap sanitize");
        c.True(chroma > 20, "gray mix keeps autumn chroma");
    }

    static void ExpireMissingTexGate(Check c)
    {
        Block dirt = Block("soil-medium-normal", EnumBlockMaterial.Soil);
        c.True(LodSeasonBake.RejectExpireMissingTex(dirt, 0),
            "expire no-map skips colour 0");
        int missingWhite = unchecked((int)0xFFFCFCFC);
        c.True(LodPaletteRepair.IsMissingTextureWhite(missingWhite),
            "unknown.png white is missing-tex");
        c.True(LodSeasonBake.RejectExpireMissingTex(dirt, missingWhite),
            "expire no-map does not write missing-tex white onto dirt");
        c.False(LodSeasonBake.RejectExpireMissingTex(dirt, LodSurfaceMix.Pack(92, 68, 42)),
            "expire no-map keeps a real soil sample");
        Block snow = Block("snowlayer-4", EnumBlockMaterial.Snow);
        c.False(LodSeasonBake.RejectExpireMissingTex(snow, missingWhite),
            "real snow plates may keep pale RGB without a map chunk");
        c.True(File.ReadAllText(Path.Combine(
                GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodSeasonBake.cs"))
                .Contains("RejectExpireMissingTex"),
            "visit bake gates expire no-map samples");
    }

    /// <summary>
    /// Post-GetColor SIMD (LodRgbSimd) must match the scalar kernels bit-for-bit.
    /// GetColor itself is never vectorized.
    /// </summary>
    static void SimdAfterGetColor(Check c)
    {
        c.True(LodRgbSimd.PathName is "avx2" or "vector128" or "vector" or "scalar",
            "SIMD path name is avx2, vector128, vector, or scalar");

        int[] steps = { 12, 1, 15 };
        int mismatches = 0;
        string? first = null;
        try
        {
            foreach (int step in steps)
            {
                int[] sample = { 0, 1, 6, 11, 12, 13, 127, 128, 243, 244, 250, 255 };
                foreach (int red in sample)
                foreach (int green in sample)
                foreach (int blue in sample)
                {
                    int packedPixel = LodSurfaceMix.Pack(red, green, blue);
                    LodRgbSimd.ForceScalar = true;
                    int scalar = LodRgbSimd.QuantizePacked(packedPixel, step);
                    LodRgbSimd.ForceScalar = false;
                    int simd = LodRgbSimd.QuantizePacked(packedPixel, step);
                    int viaMix = LodSurfaceMix.Quantize(packedPixel, step);
                    if (scalar != simd || scalar != viaMix)
                    {
                        mismatches++;
                        first ??= $"quantize step={step} rgb={red},{green},{blue} scalar=0x{scalar:X8} simd=0x{simd:X8} mix=0x{viaMix:X8}";
                    }
                }

                for (int v = 0; v <= 255; v++)
                {
                    int packedSweep = LodSurfaceMix.Pack(v, (v * 3) & 255, (v * 7) & 255);
                    LodRgbSimd.ForceScalar = true;
                    int scalar = LodRgbSimd.QuantizePacked(packedSweep, step);
                    LodRgbSimd.ForceScalar = false;
                    int simd = LodRgbSimd.QuantizePacked(packedSweep, step);
                    if (scalar != simd)
                    {
                        mismatches++;
                        first ??= $"quantize sweep v={v} step={step}";
                    }
                }
            }
        }
        finally
        {
            LodRgbSimd.ForceScalar = false;
        }
        c.Eq(0, mismatches, first == null ? "Quantize SIMD is bit-identical to scalar" : first);

        var rng = new Random(20260907);
        int[] spanSrc = new int[4096 + 3];
        for (int i = 0; i < spanSrc.Length; i++)
        {
            if (rng.Next(8) == 0) spanSrc[i] = 0;
            else spanSrc[i] = LodSurfaceMix.Pack(rng.Next(256), rng.Next(256), rng.Next(256));
        }
        int[] spanScalar = (int[])spanSrc.Clone();
        int[] spanSimd = (int[])spanSrc.Clone();
        try
        {
            LodRgbSimd.ForceScalar = true;
            LodRgbSimd.QuantizeSpan(spanScalar, LodSurfaceMix.QuantizeStep);
            LodRgbSimd.ForceScalar = false;
            LodRgbSimd.QuantizeSpan(spanSimd, LodSurfaceMix.QuantizeStep);
        }
        finally
        {
            LodRgbSimd.ForceScalar = false;
        }
        EqRgb(c, spanScalar, spanSimd, "QuantizeSpan SIMD is bit-identical to scalar");

        int n = 64 * 64;
        int[] packedPlanes = new int[n];
        int[] r = new int[n];
        int[] g = new int[n];
        int[] b = new int[n];
        int[] round = new int[n];
        for (int i = 0; i < n; i++)
            packedPlanes[i] = LodSurfaceMix.Pack((i * 13) & 255, (i * 29) & 255, (i * 47) & 255);
        try
        {
            LodRgbSimd.ForceScalar = false;
            LodRgbSimd.UnpackPlanes(packedPlanes, r, g, b);
            LodRgbSimd.PackPlanes(r, g, b, round);
        }
        finally
        {
            LodRgbSimd.ForceScalar = false;
        }
        EqRgb(c, packedPlanes, round, "UnpackPlanes/PackPlanes roundtrip");

        foreach (int gs in new[] { 1, 7, 8, 64 })
        foreach (int radius in new[] { 0, 1 })
        {
            FillRgbGrid(gs, 1000 + gs * 17 + radius, out int[] src, out byte[] mask);
            int cells = gs * gs;
            int[] scalar = new int[cells];
            int[] simd = new int[cells];
            int[] viaMix = new int[cells];
            try
            {
                LodRgbSimd.ForceScalar = true;
                LodRgbSimd.BlurLandOnce(src, mask, scalar, gs, radius);
                LodRgbSimd.ForceScalar = false;
                LodRgbSimd.BlurLandOnce(src, mask, simd, gs, radius);
                LodSurfaceMix.BlurLand(src, mask, viaMix, gs, radius);
            }
            finally
            {
                LodRgbSimd.ForceScalar = false;
            }
            EqRgb(c, scalar, simd, $"BlurLandOnce r={radius} gs={gs} SIMD vs scalar");

            int[] twoScalar = new int[cells];
            int[] scratch = new int[cells];
            LodRgbSimd.BlurLandOnceScalar(src, mask, scratch, gs, radius);
            LodRgbSimd.BlurLandOnceScalar(scratch, mask, twoScalar, gs, radius);
            EqRgb(c, twoScalar, viaMix, $"LodSurfaceMix.BlurLand r={radius} gs={gs} vs two scalar passes");
        }

        // Radius-0 land with packed 0 must force alpha 0xFF (Pack(Unpack(0))).
        int[] zsrc = { 0 };
        byte[] zmask = { 1 };
        int[] zdst = new int[1];
        LodRgbSimd.BlurLandOnceScalar(zsrc, zmask, zdst, 1, 0);
        c.Eq(LodSurfaceMix.Pack(0, 0, 0), zdst[0], "scalar radius-0 land of 0 packs alpha");
        try
        {
            LodRgbSimd.ForceScalar = false;
            LodRgbSimd.BlurLandOnce(zsrc, zmask, zdst, 1, 0);
        }
        finally
        {
            LodRgbSimd.ForceScalar = false;
        }
        c.Eq(LodSurfaceMix.Pack(0, 0, 0), zdst[0], "SIMD radius-0 land of 0 packs alpha");
    }

    static void FillRgbGrid(int gs, int seed, out int[] rgb, out byte[] mask)
    {
        var rng = new Random(seed);
        int n = gs * gs;
        rgb = new int[n];
        mask = new byte[n];
        for (int i = 0; i < n; i++)
        {
            int t = rng.Next(6);
            mask[i] = t switch
            {
                0 or 1 or 2 => (byte)1,
                3 or 4 => (byte)2,
                _ => (byte)0,
            };
            if (mask[i] == 0)
                rgb[i] = 0;
            else if (mask[i] == 1 && rng.Next(11) == 0)
                rgb[i] = 0;
            else
                rgb[i] = LodSurfaceMix.Pack(rng.Next(256), rng.Next(256), rng.Next(256));
        }
        if (n > 0)
        {
            mask[0] = 1;
            rgb[0] = 0;
        }
        if (n > 1)
        {
            mask[1] = 2;
            rgb[1] = LodSurfaceMix.Pack(10, 40, 180);
        }
    }

    static void EqRgb(Check c, int[] expected, int[] actual, string what)
    {
        if (expected.Length != actual.Length)
        {
            c.Eq(expected.Length, actual.Length, what + " length");
            return;
        }

        int diffs = 0;
        int first = -1;
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] == actual[i]) continue;
            if (first < 0) first = i;
            diffs++;
        }

        if (diffs == 0)
            c.Eq(0, 0, what);
        else
            c.Eq(0, diffs,
                $"{what} ({diffs} cells, first [{first}] expect 0x{expected[first]:X8} got 0x{actual[first]:X8})");
    }
}
