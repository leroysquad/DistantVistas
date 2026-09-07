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
        c.Eq(LodSurfaceMix.MixSeasonGround(olive, dirt, 0, 0, 0f), summer,
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
    }

    static void VisitFrostCanopyAndGround(Check c)
    {
        Block pine = Block("leaves-grown-pine", EnumBlockMaterial.Leaves, frostable: true);
        c.True(LodSeasonBake.IsFrostableCanopy(pine),
            "pine Leaves + Frostable is frostable canopy");
        Block oak = Block("leaves-grown-oak", EnumBlockMaterial.Leaves, frostable: true);
        c.True(LodSeasonBake.IsFrostableCanopy(oak),
            "oak Leaves + Frostable is frostable canopy");
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
}
