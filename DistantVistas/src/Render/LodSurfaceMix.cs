using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Overlay and walk share this mix. Tree canopy and real snow keep the visual
/// top GetColor (climate + season maps — autumn orange/red, winter frost wash).
/// No-snow ground keeps live GetColor through summer and autumn. Texture
/// camouflage only kicks in deep winter without frost, so far LODs match the
/// near canvas instead of mud-brown plates. Neighbour blur is off.
/// </summary>
public static class LodSurfaceMix
{
    /// <summary>
    /// Paint pipeline generation for diagnostics / complete stamps.
    /// 9 = canopy GetColor at crown Y + low-ground mist / scout-fill era.
    /// From 1.0.24 a bump does NOT force a login visit-teleport; idle remesh
    /// and discover bake handle sticky empty-mesh / foliage. Season refresh
    /// stays on the ~30-day PlanSeasonExpired path.
    /// </summary>
    public const int PaintRevision = 9;

    public const int BlurRadius = 0;
    public const int QuantizeStep = 12;
    /// <summary>How many blocks down from the visual top we sample for the mix.</summary>
    public const int StackDepth = 16;

    [ThreadStatic] static int[]? rawMix;
    [ThreadStatic] static int[]? blurredMix;
    [ThreadStatic] static byte[]? mixMask;
    [ThreadStatic] static int[]? haloRaw;
    [ThreadStatic] static int[]? haloBlur;
    [ThreadStatic] static byte[]? haloMask;
    [ThreadStatic] static int[]? blurScratch;

    [ThreadStatic] public static string? ProbeTopPath;
    [ThreadStatic] public static Kind ProbeTopKind;
    [ThreadStatic] public static int ProbeTopRgb;
    [ThreadStatic] public static int ProbeSnowRgb;
    [ThreadStatic] public static int ProbeGroundRgb;
    [ThreadStatic] public static int ProbePlantRgb;
    [ThreadStatic] public static int ProbeMixRgb;
    [ThreadStatic] public static int ProbeTaken;
    [ThreadStatic] public static int ProbeSkipped;
    [ThreadStatic] public static int ProbeSnowLayers;
    [ThreadStatic] public static int ProbeGroundLayers;
    [ThreadStatic] public static int ProbePlantLayers;
    [ThreadStatic] public static float ProbeSeasonRel;
    [ThreadStatic] public static float ProbeWinter;
    [ThreadStatic] public static float ProbeFrostW;
    [ThreadStatic] public static int ProbeTexGroundRgb;
    [ThreadStatic] public static int ProbeTexPlantRgb;

    /// <summary>Same offset as <c>GameCalendar.GetSeason</c>.</summary>
    public const float SeasonEnumOffset = 0.21916668f;

    public static void Rent(int cols, out int[] raw, out int[] blurred, out byte[] mask)
    {
        if (rawMix == null || rawMix.Length < cols)
        {
            rawMix = new int[cols];
            blurredMix = new int[cols];
            mixMask = new byte[cols];
        }
        raw = rawMix;
        blurred = blurredMix!;
        mask = mixMask!;
        Array.Clear(raw, 0, cols);
        Array.Clear(blurred, 0, cols);
        Array.Clear(mask, 0, cols);
    }

    public enum Kind : byte { None, Snow, Ground, Plant, Water, Rock }

    public static Kind Classify(Block block)
    {
        if (block.BlockId == 0) return Kind.None;
        if ((LodBlockPolicy.FlagsFor(block) & LodPaletteEntry.FlagWater) != 0)
            return Kind.Water;

        string? path = block.Code?.Path;
        // Grass / soil / peat never become snow, even if frost washed the RGB pale.
        if (IsSeasonGroundPath(path))
            return IsGroundPlantPath(path) ? Kind.Plant : Kind.Ground;

        if (LodSeasonBake.ColumnSurfaceIsSnowy(block) || LodBlockPolicy.IsClimateUntinted(block))
            return Kind.Snow;

        if (path != null)
        {
            if (Has(path, "leaves") || Has(path, "bush")
                || Has(path, "shrub") || Has(path, "fern") || Has(path, "flower")
                || Has(path, "sapling") || Has(path, "vine") || Has(path, "cattail")
                || Has(path, "reed") || Has(path, "needles"))
                return Kind.Plant;
        }

        if (block.BlockMaterial == EnumBlockMaterial.Leaves) return Kind.Plant;
        if (block.BlockMaterial == EnumBlockMaterial.Plant) return Kind.Plant;
        if (block.BlockMaterial is EnumBlockMaterial.Stone or EnumBlockMaterial.Ore
            or EnumBlockMaterial.Brick or EnumBlockMaterial.Ceramic or EnumBlockMaterial.Metal)
            return Kind.Rock;
        return Kind.Ground;
    }

    /// <summary>
    /// Winter no-snow ground (soil, tallgrass, peat, forest floor). White specks
    /// in the texture are not a snow sheet.
    /// </summary>
    public static bool IsSeasonGroundPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (path.StartsWith("snow", StringComparison.Ordinal)
            || Has(path, "glacier")
            || Has(path, "packedice")
            || Has(path, "lakeice"))
            return false;
        return Has(path, "tallgrass") || Has(path, "soil") || Has(path, "topsoil")
            || Has(path, "forestfloor") || Has(path, "peat") || Has(path, "farmland")
            || Has(path, "gravel") || Has(path, "sand") || Has(path, "dirt")
            || Has(path, "mud") || Has(path, "clay")
            || (Has(path, "grass") && !Has(path, "leaves"));
    }

    public static bool IsGroundPlantPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return Has(path, "tallgrass") || Has(path, "fern") || Has(path, "flower")
            || Has(path, "cattail") || Has(path, "reed")
            || (Has(path, "grass") && !Has(path, "soil") && !Has(path, "leaves")
                && !Has(path, "topsoil") && !Has(path, "forestfloor"));
    }

    /// <summary>
    /// Shared overlay + walk paint. Canopy and real snow keep the top GetColor.
    /// Season-tinted plants (leaves already handled) and ground keep live GetColor
    /// through autumn. Deep-winter no-frost ground may use texture camouflage.
    /// </summary>
    public static int FinishColumnPaint(
        Kind topKind,
        string? topPath,
        int topRgb,
        int snow,
        float snowW,
        int ground,
        float groundW,
        int plant,
        float plantW,
        float winter = 0f,
        int texGround = 0,
        int texPlant = 0,
        float frost = 0f)
    {
        _ = snow;
        _ = snowW;
        _ = groundW;
        _ = plantW;
        if (topKind == Kind.Water && topRgb != 0) return topRgb;

        if (IsActualSnowTop(topKind, topPath) && topRgb != 0)
            return topRgb;

        // Tree / bush crowns: exact GetColor (season orange/red). Frost wash is mesher-only.
        if (topRgb != 0 && LodCanopyGray.IsSeasonFoliagePath(topPath))
            return topRgb;
        if (topRgb != 0 && topKind == Kind.Plant
            && (topPath == null || LodCanopyGray.IsCanopyPath(topPath) || LodCanopyGray.IsBushPath(topPath)))
            return topRgb;

        // Other plants (tallgrass, ferns): keep live GetColor until deep winter.
        // Autumn chroma is on GetColor; texture mean has no season map.
        if (topKind == Kind.Plant && topRgb != 0 && winter < DeepWinterCamouflageStart)
            return topRgb;

        int mixed = MixSeasonGround(plant, ground, texPlant, texGround, winter, frost);
        return mixed != 0 ? mixed : topRgb;
    }

    /// <summary>
    /// One-block season mix when the column stack is not streamed (expire
    /// leftover). Canopy and plants keep live GetColor through autumn. Ground
    /// uses the same MixSeasonGround rules as overlay/walk.
    /// </summary>
    public static int MixVisitBlock(ICoreClientAPI capi, Block block, int x, int y, int z, int liveRgb)
    {
        if (liveRgb == 0) return 0;
        string? path = block.Code?.Path;
        Kind k = Classify(block);
        if (k == Kind.Water) return liveRgb;
        if (IsActualSnowTop(k, path) && liveRgb != 0) return liveRgb;
        // Prefer material-aware foliage (Leaves) over path-only — crown vs ground.
        if (LodCanopyGray.IsSeasonFoliage(block) && liveRgb != 0) return liveRgb;
        float winter = WinterAmount(ReadSeasonRel(capi, x, y, z));
        if (k == Kind.Plant && winter < DeepWinterCamouflageStart) return liveRgb;
        int tex = LodSeasonBake.SampleTextureMean(capi, block, x, y, z);
        float frost = 0f;
        try
        {
            LodSeasonBake.TryVisitFrostWeight(capi.World, LodBakeScratch.Pos(x, y, z), out frost, out _, out _);
        }
        catch { }
        int mixed = MixSeasonGround(liveRgb, liveRgb, tex, tex, winter, frost);
        return mixed != 0 ? mixed : liveRgb;
    }

    /// <summary>
    /// snowlayer-1 is a 2-voxel dusting. From far away that is texture specks,
    /// not a snow sheet. Thicker layers stay snow plates.
    /// </summary>
    public static bool IsThinSnowSpeckPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return path.Equals("snowlayer-1", StringComparison.Ordinal)
            || path.StartsWith("snowlayer-1-", StringComparison.Ordinal);
    }

    public static bool IsActualSnowTop(Kind kind, string? path)
    {
        if (IsSeasonGroundPath(path)) return false;
        if (IsThinSnowSpeckPath(path)) return false;
        if (kind != Kind.Snow) return false;
        if (string.IsNullOrEmpty(path)) return true;
        return path.StartsWith("snow", StringComparison.Ordinal)
            || Has(path, "glacier")
            || Has(path, "packedice");
    }

    /// <summary>
    /// Presence recipe, not an equal average. Snow-with-dirt stays cream; dirt-with-bushes
    /// stays brown-green. Equal RGB average turns both into mud and neighbours flip tiles.
    /// </summary>
    public static int MixCamouflage(int snow, float snowW, int ground, float groundW, int plant, float plantW)
    {
        bool hs = snowW > 0.001f;
        bool hg = groundW > 0.001f;
        bool hp = plantW > 0.001f;
        if (!hs && !hg && !hp) return 0;

        float sw, gw, pw;
        if (hs && hg && hp) { sw = 0.45f; gw = 0.35f; pw = 0.20f; }
        else if (hs && hg) { sw = 0.58f; gw = 0.42f; pw = 0f; }
        else if (hs && hp) { sw = 0.62f; gw = 0f; pw = 0.38f; }
        else if (hg && hp) { sw = 0f; gw = 0.70f; pw = 0.30f; }
        else if (hs) { sw = 1f; gw = 0f; pw = 0f; }
        else if (hg) { sw = 0f; gw = 1f; pw = 0f; }
        else { sw = 0f; gw = 0f; pw = 1f; }

        return Weighted(snow, sw, ground, gw, plant, pw);
    }

    /// <summary>
    /// 0 at year start maps to late winter after <see cref="SeasonEnumOffset"/>.
    /// 0 = spring start, 0.25 = summer, 0.5 = autumn, 0.75 = winter.
    /// </summary>
    public static float SeasonCycle(float seasonRel)
    {
        float c = seasonRel - SeasonEnumOffset;
        return c - MathF.Floor(c);
    }

    /// <summary>
    /// 0 = late spring and summer (bright green live GetColor). 1 = winter.
    /// Autumn ramps 0→1. Early spring thaws 1→0 before May.
    /// Texture camouflage only uses the high end — see <see cref="DeepWinterCamouflageStart"/>.
    /// </summary>
    public static float WinterAmount(float seasonRel)
    {
        float c = SeasonCycle(seasonRel);
        if (c >= 0.75f) return 1f;
        if (c >= 0.50f) return (c - 0.50f) / 0.25f;
        if (c >= 0.25f) return 0f;
        const float thaw = 0.12f;
        return c >= thaw ? 0f : 1f - c / thaw;
    }

    /// <summary>
    /// WinterAmount at/above this may lerp ground toward untinted texture mean.
    /// Below it, live GetColor (climate + season) is authoritative — that is what
    /// carries autumn orange/red and frosted grass. Was 0, which muddied autumn.
    /// </summary>
    public const float DeepWinterCamouflageStart = 0.88f;

    public static void SeasonGroundWeights(float winter, out float groundW, out float plantW)
    {
        winter = Math.Clamp(winter, 0f, 1f);
        plantW = 0.82f + (0.32f - 0.82f) * winter;
        groundW = 1f - plantW;
    }

    /// <summary>
    /// No-snow ground paint. Live GetColor carries climate + season (green → orange
    /// → red → frost). Texture mean is only the deep-winter speck camouflage when
    /// there is no frost sheet. Frost keeps live GetColor plus a light wash.
    /// </summary>
    public static int MixSeasonGround(
        int livePlant, int liveGround, int texPlant, int texGround, float winter, float frost = 0f)
    {
        winter = Math.Clamp(winter, 0f, 1f);
        frost = Math.Clamp(frost, 0f, 1f);
        int plantLive = PreferLandColor(livePlant, texPlant, liveGround, texGround);
        int groundLive = PreferLandColor(liveGround, texGround, livePlant, texPlant);
        int plantTex = texPlant != 0 ? texPlant : plantLive;
        int groundTex = texGround != 0 ? texGround : groundLive;

        // Keep season GetColor through autumn and most of winter. Only deep winter
        // without frost pulls toward untinted texture (brown/tan speck plates).
        float texPull = 0f;
        if (frost < 0.05f && winter > DeepWinterCamouflageStart)
            texPull = (winter - DeepWinterCamouflageStart) / (1f - DeepWinterCamouflageStart);

        int plant = LerpRgb(plantLive, plantTex, texPull);
        int ground = LerpRgb(groundLive, groundTex, texPull);
        if (plant == 0 && ground == 0) return 0;
        int mixed;
        if (plant == 0) mixed = ground;
        else if (ground == 0) mixed = plant;
        else
        {
            SeasonGroundWeights(winter, out float gw, out float pw);
            mixed = Weighted(0, 0f, ground, gw, plant, pw);
        }
        // frost weight is already season-gated at TryVisitFrostWeight; do not
        // also require LiveWinterAmount here (unit tests and bake pass frost:1).
        if (frost > 0f && mixed != 0)
            mixed = LodSeasonBake.MixTowardWhite(mixed, frost * LodSeasonBake.GroundFrostAlpha);
        return mixed;
    }

    public static bool IsPaleSheet(int color)
    {
        if (color == 0) return false;
        if (LodPaletteRepair.IsSnowOrIceAlbedo(color) || LodPaletteRepair.IsBrightCap(color))
            return true;
        LodPaletteRepair.Channels(color, out _, out _, out _, out int luma, out int chroma);
        return luma >= 150 && chroma <= 56;
    }

    public static int LerpRgb(int a, int b, float t)
    {
        if (a == 0) return b;
        if (b == 0) return a;
        t = Math.Clamp(t, 0f, 1f);
        Unpack(a, out int ar, out int ag, out int ab);
        Unpack(b, out int br, out int bg, out int bb);
        return Pack(
            (int)(ar + (br - ar) * t + 0.5f),
            (int)(ag + (bg - ag) * t + 0.5f),
            (int)(ab + (bb - ab) * t + 0.5f));
    }

    public static bool IsCanopyPlant(Kind k, string? path) =>
        k == Kind.Plant && LodCanopyGray.IsSeasonFoliagePath(path);

    public static float ReadSeasonRel(ICoreClientAPI capi, int x, int y, int z)
    {
        try
        {
            return capi.World.Calendar.GetSeasonRel(LodBakeScratch.Pos(x, y, z));
        }
        catch
        {
            return 0.5f;
        }
    }

    static int PreferLandColor(int live, int tex, int otherLive, int otherTex)
    {
        if (live != 0 && !IsPaleSheet(live)) return live;
        if (tex != 0 && !IsPaleSheet(tex)) return tex;
        if (otherTex != 0 && !IsPaleSheet(otherTex)) return otherTex;
        if (otherLive != 0 && !IsPaleSheet(otherLive)) return otherLive;
        if (tex != 0) return tex;
        return live;
    }

    public static int Quantize(int color, int step = QuantizeStep)
    {
        if (color == 0 || step <= 1) return color;
        Unpack(color, out int r, out int g, out int b);
        return Pack(Snap(r, step), Snap(g, step), Snap(b, step));
    }

    /// <summary>
    /// Same blur as <see cref="BlurLand"/>, but edge columns also see live stacks
    /// in neighbouring L0 cells so two chunks of the same patch share one mix.
    /// </summary>
    public static void BlurWithHalo(
        ICoreClientAPI capi,
        IBlockAccessor acc,
        int mapHeight,
        int originX,
        int originZ,
        int[] src,
        byte[] mask,
        int[] dst,
        int gs,
        int radius)
    {
        int h = gs + radius * 2;
        int haloN = h * h;
        if (haloRaw == null || haloRaw.Length < haloN)
        {
            haloRaw = new int[haloN];
            haloBlur = new int[haloN];
            haloMask = new byte[haloN];
        }
        int[] hRaw = haloRaw;
        int[] hBlur = haloBlur!;
        byte[] hMask = haloMask!;
        Array.Clear(hRaw, 0, haloN);
        Array.Clear(hBlur, 0, haloN);
        Array.Clear(hMask, 0, haloN);

        for (int hz = 0; hz < h; hz++)
        {
            for (int hx = 0; hx < h; hx++)
            {
                int cx = hx - radius;
                int cz = hz - radius;
                int hi = hz * h + hx;
                if ((uint)cx < (uint)gs && (uint)cz < (uint)gs)
                {
                    int i = cz * gs + cx;
                    hRaw[hi] = src[i];
                    hMask[hi] = mask[i];
                    continue;
                }

                int wx = originX + cx;
                int wz = originZ + cz;
                if (!LodSeasonBake.IsColumnMapLoaded(acc, wx, wz)) continue;
                if (!LodSeasonBake.TryResolveLiveSurface(
                        acc, wx, mapHeight - 2, wz, mapHeight, out _, out int sy))
                    continue;
                int mix = SampleColumnStack(capi, acc, wx, sy, wz, out bool water, out _);
                if (mix == 0) continue;
                hRaw[hi] = mix;
                hMask[hi] = water ? (byte)2 : (byte)1;
            }
        }

        BlurLand(hRaw, hMask, hBlur, h, radius);
        for (int cz = 0; cz < gs; cz++)
        {
            for (int cx = 0; cx < gs; cx++)
            {
                dst[cz * gs + cx] = hBlur[(cz + radius) * h + (cx + radius)];
            }
        }
    }

    /// <summary>
    /// Two box-blur passes over land mixes. One pass still leaves a 1-block
    /// snow/dirt flip as two close colours that quantize into a checkerboard.
    /// Water copies through. Empty stays empty.
    /// </summary>
    public static void BlurLand(int[] src, byte[] mask, int[] dst, int gs, int radius)
    {
        int n = gs * gs;
        if (blurScratch == null || blurScratch.Length < n)
            blurScratch = new int[n];
        BlurLandOnce(src, mask, blurScratch, gs, radius);
        BlurLandOnce(blurScratch, mask, dst, gs, radius);
    }

    static void BlurLandOnce(int[] src, byte[] mask, int[] dst, int gs, int radius)
    {
        for (int cz = 0; cz < gs; cz++)
        {
            for (int cx = 0; cx < gs; cx++)
            {
                int i = cz * gs + cx;
                byte m = mask[i];
                if (m != 1)
                {
                    dst[i] = m == 2 ? src[i] : 0;
                    continue;
                }

                long r = 0, g = 0, b = 0, n = 0;
                int z0 = cz - radius, z1 = cz + radius;
                int x0 = cx - radius, x1 = cx + radius;
                if (z0 < 0) z0 = 0;
                if (x0 < 0) x0 = 0;
                if (z1 >= gs) z1 = gs - 1;
                if (x1 >= gs) x1 = gs - 1;
                for (int nz = z0; nz <= z1; nz++)
                {
                    int row = nz * gs;
                    for (int nx = x0; nx <= x1; nx++)
                    {
                        int j = row + nx;
                        if (mask[j] != 1) continue;
                        Unpack(src[j], out int sr, out int sg, out int sb);
                        r += sr;
                        g += sg;
                        b += sb;
                        n++;
                    }
                }

                dst[i] = n == 0 ? src[i] : Pack((int)(r / n), (int)(g / n), (int)(b / n));
            }
        }
    }

    public static int SampleColumnStack(
        ICoreClientAPI capi,
        IBlockAccessor acc,
        int x,
        int startY,
        int z,
        out bool water,
        out bool hasSnow)
    {
        water = false;
        hasSnow = false;
        float sr = 0, sg = 0, sb = 0, sw = 0;
        float gr = 0, gg = 0, gb = 0, gw = 0;
        float pr = 0, pg = 0, pb = 0, pw = 0;
        float tgr = 0, tgg = 0, tgb = 0, tgw = 0;
        float tpr = 0, tpg = 0, tpb = 0, tpw = 0;
        int taken = 0;
        int skipped = 0;
        int snowLayers = 0, groundLayers = 0, plantLayers = 0;
        bool gotGround = false;
        ProbeTopPath = null;
        ProbeTopKind = Kind.None;
        ProbeTopRgb = 0;
        ProbeSeasonRel = ReadSeasonRel(capi, x, startY, z);
        ProbeWinter = WinterAmount(ProbeSeasonRel);
        ProbeFrostW = 0f;
        ProbeTexGroundRgb = 0;
        ProbeTexPlantRgb = 0;
        BlockPos pos = LodBakeScratch.Pos(x, startY, z);
        try
        {
            if (capi.World is IClientWorldAccessor world)
                LodSeasonBake.TryVisitFrostWeight(world, pos, out ProbeFrostW, out _, out _);
        }
        catch { }

        for (int py = startY; py >= 1 && taken < StackDepth; py--)
        {
            pos.Y = py;
            Block b = acc.GetBlock(pos);
            if (b == null || b.BlockId == 0) continue;
            if ((LodBlockPolicy.FlagsFor(b) & LodPaletteEntry.FlagSkip) != 0)
            {
                skipped++;
                continue;
            }

            Kind k = Classify(b);
            if (k == Kind.Water)
            {
                if (taken == 0)
                {
                    water = true;
                    int wcol = LodSeasonBake.SampleVanillaColor(capi, b, x, py, z);
                    ProbeTopPath = b.Code?.Path;
                    ProbeTopKind = Kind.Water;
                    ProbeTopRgb = wcol;
                    ProbeMixRgb = wcol;
                    ProbeTaken = 1;
                    ProbeSkipped = skipped;
                    ProbeSnowLayers = 0;
                    ProbeGroundLayers = 0;
                    ProbePlantLayers = 0;
                    return wcol;
                }
                break;
            }

            int rgb = LodSeasonBake.SampleVanillaColor(capi, b, x, py, z);
            if (rgb == 0) continue;
            taken++;
            if (taken == 1)
            {
                ProbeTopPath = b.Code?.Path;
                ProbeTopKind = k;
                ProbeTopRgb = rgb;
            }
            if (k == Kind.Snow) snowLayers++;
            else if (k == Kind.Plant) plantLayers++;
            else groundLayers++;
            Unpack(rgb, out int cr, out int cg, out int cb);
            float w = k switch
            {
                Kind.Snow => taken == 1 ? 1.6f : 0.8f,
                Kind.Plant => 0.85f,
                Kind.Ground => 1.5f,
                Kind.Rock => gotGround ? 0.2f : 1.0f,
                _ => 0.7f,
            };

            if (k == Kind.Snow)
            {
                sr += cr * w; sg += cg * w; sb += cb * w; sw += w;
                hasSnow = true;
            }
            else if (k == Kind.Plant)
            {
                pr += cr * w; pg += cg * w; pb += cb * w; pw += w;
            }
            else
            {
                gr += cr * w; gg += cg * w; gb += cb * w; gw += w;
            }

            if (k != Kind.Snow && !IsCanopyPlant(k, b.Code?.Path))
            {
                int texRgb = LodSeasonBake.SampleTextureMean(capi, b, x, py, z);
                if (texRgb != 0)
                {
                    Unpack(texRgb, out int tr, out int tg, out int tb);
                    if (k == Kind.Plant)
                    {
                        tpr += tr * w; tpg += tg * w; tpb += tb * w; tpw += w;
                    }
                    else
                    {
                        tgr += tr * w; tgg += tg * w; tgb += tb * w; tgw += w;
                    }
                }
            }

            if (k is Kind.Ground or Kind.Rock)
            {
                if (gotGround) break;
                gotGround = true;
            }
        }

        int snow = sw > 0f ? Pack((int)(sr / sw), (int)(sg / sw), (int)(sb / sw)) : 0;
        int ground = gw > 0f ? Pack((int)(gr / gw), (int)(gg / gw), (int)(gb / gw)) : 0;
        int plant = pw > 0f ? Pack((int)(pr / pw), (int)(pg / pw), (int)(pb / pw)) : 0;
        ProbeTexGroundRgb = tgw > 0f ? Pack((int)(tgr / tgw), (int)(tgg / tgw), (int)(tgb / tgw)) : 0;
        ProbeTexPlantRgb = tpw > 0f ? Pack((int)(tpr / tpw), (int)(tpg / tpw), (int)(tpb / tpw)) : 0;
        int mix = FinishColumnPaint(
            ProbeTopKind, ProbeTopPath, ProbeTopRgb, snow, sw, ground, gw, plant, pw,
            ProbeWinter, ProbeTexGroundRgb, ProbeTexPlantRgb, ProbeFrostW);
        ProbeSnowRgb = snow;
        ProbeGroundRgb = ground;
        ProbePlantRgb = plant;
        ProbeMixRgb = mix;
        ProbeTaken = taken;
        ProbeSkipped = skipped;
        ProbeSnowLayers = snowLayers;
        ProbeGroundLayers = groundLayers;
        ProbePlantLayers = plantLayers;
        return mix;
    }

    public static int Pack(int r, int g, int b) =>
        unchecked((int)0xFF000000)
        | Math.Clamp(b, 0, 255) << 16
        | Math.Clamp(g, 0, 255) << 8
        | Math.Clamp(r, 0, 255);

    public static void Unpack(int color, out int r, out int g, out int b)
    {
        r = color & 0xFF;
        g = (color >> 8) & 0xFF;
        b = (color >> 16) & 0xFF;
    }

    static int Weighted(int snow, float sw, int ground, float gw, int plant, float pw)
    {
        Unpack(snow, out int sr, out int sg, out int sb);
        Unpack(ground, out int gr, out int gg, out int gb);
        Unpack(plant, out int pr, out int pg, out int pb);
        return Pack(
            (int)(sr * sw + gr * gw + pr * pw + 0.5f),
            (int)(sg * sw + gg * gw + pg * pw + 0.5f),
            (int)(sb * sw + gb * gw + pb * pw + 0.5f));
    }

    static int Snap(int v, int step)
    {
        int q = ((v + step / 2) / step) * step;
        return q > 255 ? 255 : q;
    }

    static bool Has(string path, string token) =>
        path.Contains(token, StringComparison.Ordinal);
}
