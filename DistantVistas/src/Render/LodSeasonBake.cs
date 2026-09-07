using System.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// After a live visit capture: lock per-column season appearance into the palette.
///
/// Capture (while the player is teleported there) stores real block ids — snow layers,
/// green or snowy leaves, grass tops. Bake mixes live ground, bush, and snow colours
/// into one camouflage RGB per column, blurs neighbours so the cell is not a
/// checkerboard of solids, then sets <see cref="LodPaletteEntry.FlagBaked"/>.
/// Shader <c>seasonRel</c> does not retint those pixels.
/// </summary>
public static class LodSeasonBake
{
    public readonly record struct SnowVote(int Eligible, int Snowy)
    {
        public bool MajoritySnow => Eligible > 0 && Snowy * 2 > Eligible;
    }

    public static bool CanBake(Block block, int color, Block? plantTintFallback)
    {
        if (LodBlockPolicy.IsClimateUntinted(block)) return false;
        if (LodPaletteRepair.IsRockLikeAlbedo(color)) return false;
        if (LodPaletteRepair.IsSnowOrIceAlbedo(color)
            && block.BlockMaterial != EnumBlockMaterial.Plant
            && block.SeasonColorMapResolved == null)
            return false;
        if ((LodBlockPolicy.FlagsFor(block) & LodPaletteEntry.FlagWater) != 0) return false;
        if (block.EntityClass != null) return false;

        if (block.ClimateColorMapResolved != null || block.SeasonColorMapResolved != null)
            return true;

        return block.BlockMaterial == EnumBlockMaterial.Plant && plantTintFallback != null;
    }

    /// <summary>
    /// Visit / explore bake may paint snow, glacier, and packed ice. <see cref="CanBake"/>
    /// refuses those so the shader-repro path does not tint white with climate grass.
    /// </summary>
    public static bool CanVisitBake(Block block, int color, Block? plantTintFallback)
    {
        if (ColumnSurfaceIsSnowy(block)) return true;
        if (LodBlockPolicy.IsClimateUntinted(block)
            && (LodBlockPolicy.FlagsFor(block) & LodPaletteEntry.FlagWater) == 0)
            return true;
        return CanBake(block, color, plantTintFallback);
    }

    /// <summary>
    /// Visual top of a loaded column: snow and extra canopy above the stored run / rain
    /// height. Returns false when the map chunk is not resident.
    /// </summary>
    public static bool TryResolveLiveSurface(
        IBlockAccessor acc,
        int x,
        int storedY,
        int z,
        int mapHeight,
        out Block? block,
        out int y)
    {
        block = null;
        y = storedY;
        if (!IsColumnMapLoaded(acc, x, z)) return false;

        int start = Math.Min(mapHeight - 1, Math.Max(storedY + 24, storedY));
        BlockPos pos = LodBakeScratch.Pos(x, start, z);
        for (int py = start; py >= 1; py--)
        {
            pos.Y = py;
            Block b = acc.GetBlock(pos);
            if (b == null || b.BlockId == 0) continue;
            byte flags = LodBlockPolicy.FlagsFor(b);
            if ((flags & LodPaletteEntry.FlagSkip) != 0) continue;
            block = b;
            y = py;
            return true;
        }

        return false;
    }

    public static bool IsSnowEligibleGround(Block block)
    {
        if (block.EntityClass != null) return false;
        byte flags = LodBlockPolicy.FlagsFor(block);
        if ((flags & (LodPaletteEntry.FlagWater | LodPaletteEntry.FlagSkip | LodPaletteEntry.FlagThin)) != 0)
            return false;
        if (LodBlockPolicy.IsClimateUntinted(block)) return false;
        if (block.BlockMaterial is EnumBlockMaterial.Plant) return false;
        string? path = block.Code?.Path;
        if (path == null) return false;
        return path.Contains("grass", StringComparison.Ordinal)
            || path.Contains("topsoil", StringComparison.Ordinal)
            || path.Contains("forestfloor", StringComparison.Ordinal)
            || path.Contains("peat", StringComparison.Ordinal);
    }

    public static bool ColumnSurfaceIsSnowy(Block block)
    {
        if (LodBlockPolicy.IsClimateUntinted(block)) return true;
        string? path = block.Code?.Path;
        if (path == null) return false;
        return path.StartsWith("snow", StringComparison.Ordinal);
    }

    public static SnowVote ComputeSnowVote(LodSection section, IList<Block> blocks, long sectionKey)
    {
        int eligible = 0, snowy = 0;
        int cols = LodSection.GridSize * LodSection.GridSize;
        for (int col = 0; col < cols; col++)
        {
            if (!section.Captured[col]) continue;
            foreach (ulong run in section.ColumnRuns(col))
            {
                int pid = LodSection.RunPaletteId(run);
                if (pid < 0 || pid >= section.Palette.Count) continue;
                LodPaletteEntry entry = section.Palette[pid];
                if (entry.BlockId <= 0 || entry.BlockId >= blocks.Count) continue;
                Block block = blocks[entry.BlockId];
                if (ColumnSurfaceIsSnowy(block))
                {
                    eligible++;
                    snowy++;
                    break;
                }
                if (!IsSnowEligibleGround(block)) continue;
                eligible++;
                break;
            }
        }
        return new SnowVote(eligible, snowy);
    }

    /// <summary>
    /// Expire leftover pass: GetColor stored tops even when the map chunk is not
    /// streamed. Hop budget stays ~64+16; unhopped resident FlagBaked must still
    /// take the new month. Live snow sampling is unchanged.
    /// </summary>
    internal static bool AllowExpireNoMapSample;

    /// <summary>Map chunk present for a single column — visit bake is per-column, not all-or-nothing.</summary>
    public static bool IsColumnMapLoaded(IBlockAccessor blockAccessor, int x, int z)
    {
        int chunkSize = GlobalConstants.ChunkSize;
        return LodCoveragePolicy.AllMapChunksLoaded(
            x, x + 1, z, z + 1, chunkSize,
            (cx, cz) => blockAccessor.GetMapChunk(cx, cz) != null);
    }

    /// <summary>
    /// Vanilla's fully tinted face colour at a world position — the ground truth during
    /// the visit sweep when chunks are loaded.
    /// </summary>
    public static int SampleVanillaColor(ICoreClientAPI capi, Block block, int x, int y, int z)
    {
        try
        {
            int color = block.GetColor(capi, LodBakeScratch.Pos(x, y, z));
            if (color != 0)
            {
                // Pure GetColor. FlagFrost + mesher apply the wash so early-spring
                // remesh thaws walls and crowns without rebaking every column.
                _ = ApplyVisitFrost(capi.World, block, LodBakeScratch.Pos(x, y, z), color);
                // #region agent log
                if (LodCanopyGray.IsSeasonFoliage(block))
                    FarCoverageDiag.NoteCanopySample(getColor: true, zero: false);
                // #endregion
                return color;
            }
            // #region agent log
            if (LodCanopyGray.IsSeasonFoliage(block))
                FarCoverageDiag.NoteCanopySample(getColor: false, zero: true);
            // #endregion
        }
        catch
        {
            // Fall back to manual tint reproduction.
            // #region agent log
            if (LodCanopyGray.IsSeasonFoliage(block))
                FarCoverageDiag.NoteCanopySample(getColor: false, zero: true);
            // #endregion
        }
        return 0;
    }

    /// <summary>
    /// Stable texture mean (<c>GetColorWithoutTint</c>). Grass overlay picks a
    /// random pixel per call; one sample is a square tile. The mean keeps winter
    /// specks as luma in the camouflage, not as Kind.Snow.
    /// </summary>
    public const int TextureMeanSamples = 8;

    public static int SampleTextureMean(ICoreClientAPI capi, Block block, int x, int y, int z)
    {
        int id = block.BlockId;
        if (LodBakeScratch.TryGetSectionTextureMean(id, out int cached))
            return cached;

        int mean = 0;
        try
        {
            BlockPos pos = LodBakeScratch.Pos(x, y, z);
            long r = 0, g = 0, b = 0;
            int n = 0;
            for (int i = 0; i < TextureMeanSamples; i++)
            {
                int c = block.GetColorWithoutTint(capi, pos);
                if (c == 0) continue;
                r += c & 0xFF;
                g += (c >> 8) & 0xFF;
                b += (c >> 16) & 0xFF;
                n++;
            }
            mean = n == 0 ? 0 : LodSurfaceMix.Pack((int)(r / n), (int)(g / n), (int)(b / n));
        }
        catch
        {
            mean = 0;
        }

        LodBakeScratch.RememberSectionTextureMean(id, mean);
        return mean;
    }

    /// <summary>
    /// Stored wall colour: mix GetColor toward white by frostW * this.
    /// Pine 32,31,7 at full frost becomes ~103,103,86 (frosted green-gray).
    /// </summary>
    public const float SideFrostAlpha = 0.32f;

    /// <summary>
    /// Mesher UP faces extra-mix the stored side colour toward white.
    /// Combined top mix from original ≈ 0.80, readable frost crowns, not a snow cube.
    /// </summary>
    public const float TopFrostExtra = 0.70f;

    /// <summary>Minimum frost weight before FlagFrost is stored for the mesher.</summary>
    public const float FrostFlagMin = 0.15f;

    /// <summary>
    /// Light frost wash on live grass. Not a white sheet, and not the manila texture plate.
    /// </summary>
    public const float GroundFrostAlpha = 0.18f;

    /// <summary>
    /// Calendar winter amount (0 late spring/summer … 1 winter). Renderer sets
    /// this every draw. May must not keep baking or meshing frost-white LODs
    /// from cold climate samples alone.
    /// </summary>
    public static float LiveWinterAmount;

    /// <summary>
    /// Below this, no FlagFrost bake and no mesher frost wash. 0.75 ≈ late autumn
    /// (WinterAmount ramp ends near calendar winter). Mid-autumn stays GetColor only.
    /// </summary>
    public const float FrostSeasonMin = 0.75f;

    public static bool SeasonAllowsFrost => LiveWinterAmount >= FrostSeasonMin;

    /// <summary>
    /// Visit bake no longer mixes frost into stored RGB (PaintRevision 6+).
    /// FlagFrost marks climate frost; <see cref="LodMesher.FrostFaceColor"/>
    /// applies side + UP wash scaled by live winter so thaw remesh clears it.
    /// Kept as a probe hook so diagnostics still exercise the frost gate.
    /// </summary>
    public static int ApplyVisitFrost(IClientWorldAccessor world, Block block, BlockPos pos, int seasonRgb)
    {
        if (!SeasonAllowsFrost) return seasonRgb;
        if (seasonRgb == 0 || !IsFrostableCanopy(block)) return seasonRgb;
        if (!TryVisitFrostWeight(world, pos, out float w, out _, out _)) return seasonRgb;
        if (w <= 0f) return seasonRgb;
        return seasonRgb;
    }

    public static bool IsFrostableCanopy(Block? block)
    {
        if (block == null || !block.Frostable) return false;
        return LodCanopyGray.IsSeasonFoliage(block);
    }

    /// <summary>
    /// Walk capture may store soil while the live top is canopy/bush. Path is the
    /// visual top from <see cref="LodSurfaceMix.ProbeTopPath"/>.
    /// </summary>
    public static bool ShouldFlagFrost(float frostW, Block? block, string? path)
    {
        bool allows = SeasonAllowsFrost;
        bool flagged = allows
            && frostW >= FrostFlagMin
            && (IsFrostableCanopy(block) || LodCanopyGray.IsSeasonFoliagePath(path));
        // #region agent log
        ColorPathDiag.NoteBakeFrostGate(LiveWinterAmount, allows, flagged);
        // #endregion
        return flagged;
    }

    public static byte MixVisitBakeFlags(byte flags, bool frost)
    {
        flags = (byte)(flags | LodPaletteEntry.FlagBaked);
        if (frost) return (byte)(flags | LodPaletteEntry.FlagFrost);
        return (byte)(flags & ~LodPaletteEntry.FlagFrost);
    }

    public static byte ClearVisitBakeFlags(byte flags) =>
        (byte)(flags & ~LodPaletteEntry.VisitKeepMask);

    public static bool VisitColumnFrost(
        IClientWorldAccessor world, int x, int y, int z, Block? block, string? path)
    {
        TryVisitFrostWeight(world, LodBakeScratch.Pos(x, y, z), out float w, out _, out _);
        return ShouldFlagFrost(w, block, path);
    }

    public static int MixTowardWhite(int rgb, float mix)
    {
        if (rgb == 0 || mix <= 0f) return rgb;
        mix = Math.Clamp(mix, 0f, 1f);
        LodSurfaceMix.Unpack(rgb, out int r, out int g, out int b);
        return LodSurfaceMix.Pack(
            (int)(r * (1f - mix) + 255f * mix + 0.5f),
            (int)(g * (1f - mix) + 255f * mix + 0.5f),
            (int)(b * (1f - mix) + 255f * mix + 0.5f));
    }

    /// <summary>
    /// Same units as colormap.vsh: heretemp = DescaleTemperature(NowValues) / 255,
    /// w = clamp((0.333 - heretemp) * 15, 0, 1). NowValues already includes season.
    /// </summary>
    public static bool TryVisitFrostWeight(
        IClientWorldAccessor world,
        BlockPos pos,
        out float weight,
        out float tempC,
        out float hereTemp)
    {
        weight = 0f;
        tempC = 999f;
        hereTemp = 1f;
        try
        {
            ClimateCondition? cl = world.BlockAccessor.GetClimateAt(pos, EnumGetClimateMode.NowValues);
            if (cl == null) return false;
            tempC = cl.Temperature;
            hereTemp = Climate.DescaleTemperature(tempC) / 255f;
            if (hereTemp < 0.333f)
                weight = Math.Clamp((0.333f - hereTemp) * 15f, 0f, 1f);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static int BakePaletteColor(
        ICoreClientAPI capi,
        IClientWorldAccessor world,
        Block block,
        int untintedColor,
        int x,
        int y,
        int z,
        LodUntintedShare share,
        Block? plantTintFallback,
        bool groundSnowMajority = false)
    {
        int baked = SampleVanillaColor(capi, block, x, y, z);
        if (baked == 0)
        {
            SampleFinalTint(world, block, x, y, z, share, plantTintFallback, out float tr, out float tg, out float tb);
            baked = MultiplyRgb(untintedColor, tr, tg, tb);
        }

        _ = groundSnowMajority;
        return baked;
    }

    /// <summary>
    /// Reproduce the live shader's split climate table + season mix when GetColor is
    /// unavailable. Climate and season are never sampled together on white.
    /// </summary>
    public static void SampleFinalTint(
        IClientWorldAccessor world,
        Block block,
        int x,
        int y,
        int z,
        LodUntintedShare share,
        Block? plantTintFallback,
        out float r,
        out float g,
        out float b)
    {
        Block sample = block;
        if (sample.ClimateColorMapResolved == null && sample.SeasonColorMapResolved == null
            && sample.BlockMaterial == EnumBlockMaterial.Plant && plantTintFallback != null)
        {
            sample = plantTintFallback;
        }

        string? climate = sample.ClimateColorMapResolved != null ? sample.ClimateColorMap : null;
        string? season = sample.SeasonColorMapResolved != null ? sample.SeasonColorMap : null;

        if (climate == null && season == null)
        {
            r = g = b = 1f;
            return;
        }

        if (climate != null)
        {
            int yHigh = GameMath.Clamp(y + LodTintRegistry.HighSampleOffsetBlocks, 0, world.BlockAccessor.MapSizeY - 1);
            float tintBlend = GameMath.Clamp(
                (y - world.SeaLevel) / (float)LodTintRegistry.HighSampleOffsetBlocks, 0f, 1f);

            SampleClimateMap(world, climate, x, y, z, out float lr, out float lg, out float lb);
            SampleClimateMap(world, climate, x, yHigh, z, out float hr, out float hg, out float hb);

            if (LodTintRegistry.IsSnowLikeTint(hr, hg, hb) && y > world.SeaLevel + 8)
            {
                SampleClimateMap(world, climate, x, world.SeaLevel, z, out float slr, out float slg, out float slb);
                if (!LodTintRegistry.IsSnowLikeTint(slr, slg, slb))
                {
                    hr = slr; hg = slg; hb = slb;
                }
            }

            r = lr + (hr - lr) * tintBlend;
            g = lg + (hg - lg) * tintBlend;
            b = lb + (hb - lb) * tintBlend;
            LodTintRegistry.ClampTintAwayFromWhite(ref r, ref g, ref b);
        }
        else
        {
            r = g = b = 1f;
        }

        r = LodTopSoil.Dilute(share.R, r);
        g = LodTopSoil.Dilute(share.G, g);
        b = LodTopSoil.Dilute(share.B, b);

        if (season != null)
        {
            SampleSeasonMap(world, season, x, y, z, out float sr, out float sg, out float sb);
            sr = LodTopSoil.Dilute(share.R, sr);
            sg = LodTopSoil.Dilute(share.G, sg);
            sb = LodTopSoil.Dilute(share.B, sb);

            float temp = 128f;
            ClimateCondition? cl = world.BlockAccessor.GetClimateAt(LodBakeScratch.Pos(x, world.SeaLevel, z));
            if (cl != null)
                temp = LodTintRegistry.UnscaledTempByteFromCelsius(cl.WorldGenTemperature);
            float amt = LodTintRegistry.SeasonWeightFromTempByte(temp);

            r += (sr - r) * amt;
            g += (sg - g) * amt;
            b += (sb - b) * amt;
        }
    }

    static void SampleClimateMap(
        IClientWorldAccessor world, string climate,
        int x, int y, int z, out float r, out float g, out float b)
    {
        int rgba = world.ApplyColorMapOnRgba(
            climate, (string?)null,
            unchecked((int)0xFFFFFFFF), x, y, z);
        r = ((rgba >> 16) & 0xFF) / 255f;
        g = ((rgba >> 8) & 0xFF) / 255f;
        b = (rgba & 0xFF) / 255f;
        LodTintRegistry.ClampTintAwayFromWhite(ref r, ref g, ref b);
    }

    static void SampleSeasonMap(
        IClientWorldAccessor world, string season,
        int x, int y, int z, out float r, out float g, out float b)
    {
        int rgba = world.ApplyColorMapOnRgba(
            (string?)null, season,
            unchecked((int)0xFFFFFFFF), x, y, z);
        r = ((rgba >> 16) & 0xFF) / 255f;
        g = ((rgba >> 8) & 0xFF) / 255f;
        b = (rgba & 0xFF) / 255f;
        LodTintRegistry.ClampTintAwayFromWhite(ref r, ref g, ref b);
    }

    public static int MultiplyRgb(int color, float tr, float tg, float tb)
    {
        int ir = Math.Clamp((int)((color & 0xFF) * tr + 0.5f), 0, 255);
        int ig = Math.Clamp((int)(((color >> 8) & 0xFF) * tg + 0.5f), 0, 255);
        int ib = Math.Clamp((int)(((color >> 16) & 0xFF) * tb + 0.5f), 0, 255);
        return unchecked((int)0xFF000000) | ib << 16 | ig << 8 | ir;
    }

    /// <summary>
    /// Keep stored snow/ice/water RGB. Pale grass is not snow: never key this
    /// off <see cref="LodPaletteRepair.IsSnowOrIceAlbedo"/>. snowlayer-1 is
    /// texture specks, so Sanitize may still replace a leftover white plate.
    /// </summary>
    public static bool KeepVisitSnowColor(Block block, bool waterColumn)
    {
        if (waterColumn) return true;
        if (LodSurfaceMix.IsThinSnowSpeckPath(block.Code?.Path)) return false;
        return ColumnSurfaceIsSnowy(block);
    }

    /// <summary>
    public struct VisitBakeTally
    {
        public int Changed;
        public int GetColor;
        public int PaleKept;
        public int Zero;
        public int Leaves;
        public int Snow;
    }

    static int dbgMixSections;
    static int dbgSnowProbes;
    static int dbgPlantProbes;
    static int dbgFrostLeaves;
    static int dbgFrostKeep;
    static int dbgCanopy;
    static int dbgWalkWhite;
    static int dbgWalkKeep;
    // #region agent log
    internal static string DebugVisitKind = "unknown";
    // #endregion

    /// <summary>
    /// Login / explore visit bake: lock a camouflage RGB from the live stack
    /// via <see cref="LodSurfaceMix.FinishColumnPaint"/> (ground + plants this
    /// season, real snow and canopy tops kept). Neighbour blur is off.
    /// </summary>
    public static int BakeSectionFromVisit(
        ICoreClientAPI capi,
        LodSection section,
        long sectionKey,
        Block? plantTintFallback,
        System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf) =>
        BakeSectionFromVisit(capi, section, sectionKey, plantTintFallback, untintedOf, out _);

    public static int BakeSectionFromVisit(
        ICoreClientAPI capi,
        LodSection section,
        long sectionKey,
        Block? plantTintFallback,
        System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf,
        out VisitBakeTally tally)
    {
        tally = default;
        _ = plantTintFallback;
        IClientWorldAccessor world = capi.World;
        int changed = 0;
        int gs = LodSection.GridSize;
        int cols = gs * gs;
        int mapH = world.BlockAccessor.MapSizeY;
        LodSurfaceMix.Rent(cols, out int[] raw, out int[] blurred, out byte[] mask);
        LodBakeScratch.RentColumnMeta(cols, out Block?[] tops, out int[] topY, out bool[] frostCol);
        LodBakeScratch.BeginSectionTextureMeans();
        try
        {
            changed = BakeSectionFromVisitBody(
                capi, world, section, sectionKey, untintedOf,
                cols, mapH, raw, blurred, mask, tops, topY, frostCol, ref tally);
        }
        finally
        {
            LodBakeScratch.EndSectionTextureMeans();
        }
        return changed;
    }

    static int BakeSectionFromVisitBody(
        ICoreClientAPI capi,
        IClientWorldAccessor world,
        LodSection section,
        long sectionKey,
        System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf,
        int cols,
        int mapH,
        int[] raw,
        int[] blurred,
        byte[] mask,
        Block?[] tops,
        int[] topY,
        bool[] frostCol,
        ref VisitBakeTally tally)
    {
        int changed = 0;
        int nLand = 0, nSnowTop = 0, nPlantTop = 0, nGroundTop = 0;
        long sumTopR = 0, sumMixR = 0, sumBlurR = 0, sumFinalR = 0;
        int nKeepChanged = 0, nSkipped = 0;

        for (int col = 0; col < cols; col++)
        {
            if (!section.Captured[col]) continue;
            if (!section.TryGetTopRun(col, out ulong topRun)) continue;

            int pid = LodSection.RunPaletteId(topRun);
            if (pid < 0 || pid >= section.Palette.Count) continue;

            LodPaletteEntry entry = section.Palette[pid];
            if (entry.BlockId <= 0 || entry.BlockId >= world.Blocks.Count) continue;

            Block block = world.Blocks[entry.BlockId];
            (int x, int y, int z) = LodPipeline.CaptureBlockPos(sectionKey, col, topRun);
            if (TryResolveLiveSurface(
                    world.BlockAccessor, x, y, z, mapH,
                    out Block? live, out int liveY)
                && live != null)
            {
                block = live;
                y = liveY;
            }

            if (ColumnSurfaceIsSnowy(block)) tally.Snow++;
            if (block.BlockMaterial == EnumBlockMaterial.Plant
                || (block.Code?.Path?.Contains("leaves", StringComparison.Ordinal) ?? false))
                tally.Leaves++;

            if (!IsColumnMapLoaded(world.BlockAccessor, x, z))
            {
                if (!AllowExpireNoMapSample) continue;
                int expireColor = SampleVanillaColor(capi, block, x, y, z);
                if (expireColor == 0)
                {
                    tally.Zero++;
                    continue;
                }
                expireColor = LodSurfaceMix.MixVisitBlock(capi, block, x, y, z, expireColor);
                tops[col] = block;
                topY[col] = y;
                frostCol[col] = VisitColumnFrost(world, x, y, z, block, block.Code?.Path);
                raw[col] = expireColor;
                mask[col] = (byte)1;
                nLand++;
                LodSurfaceMix.Unpack(expireColor, out int expireR, out _, out _);
                sumTopR += expireR;
                sumMixR += expireR;
                continue;
            }

            tops[col] = block;
            topY[col] = y;
            int mix = LodSurfaceMix.SampleColumnStack(
                capi, world.BlockAccessor, x, y, z, out bool water, out bool hasSnow);
            if (mix == 0)
            {
                tally.Zero++;
                continue;
            }

            frostCol[col] = ShouldFlagFrost(
                LodSurfaceMix.ProbeFrostW, block, LodSurfaceMix.ProbeTopPath);
            raw[col] = mix;
            mask[col] = water ? (byte)2 : (byte)1;
            if (hasSnow) tally.PaleKept++;
            if (water) continue;

            nLand++;
            LodSurfaceMix.Unpack(LodSurfaceMix.ProbeTopRgb, out int topR, out _, out _);
            LodSurfaceMix.Unpack(mix, out int mixR, out _, out _);
            sumTopR += topR;
            sumMixR += mixR;
            nSkipped += LodSurfaceMix.ProbeSkipped;
            var tk = LodSurfaceMix.ProbeTopKind;
            if (tk == LodSurfaceMix.Kind.Snow) nSnowTop++;
            else if (tk == LodSurfaceMix.Kind.Plant) nPlantTop++;
            else nGroundTop++;

            // #region agent log
            bool wantSnow = tk == LodSurfaceMix.Kind.Snow && dbgSnowProbes < 4;
            bool wantPlant = tk == LodSurfaceMix.Kind.Plant && dbgPlantProbes < 4;
            string probePath = LodSurfaceMix.ProbeTopPath ?? "";
            bool frostLeaf = dbgFrostLeaves < 8 && FrostLeafPath(probePath);
            if (wantSnow || wantPlant)
            {
                if (wantSnow) dbgSnowProbes++;
                if (wantPlant) dbgPlantProbes++;
                AgentMixLog(wantPlant ? "H-B" : "H-A", "LodSeasonBake.BakeSectionFromVisit",
                    wantPlant ? "plant-stack" : "snow-stack",
                    "{\"path\":\"" + probePath.Replace("\\", "/").Replace("\"", "'")
                    + "\",\"kind\":" + (int)tk
                    + ",\"visit\":\"" + DebugVisitKind.Replace("\\", "/").Replace("\"", "'")
                    + "\",\"topRgb\":" + LodSurfaceMix.ProbeTopRgb
                    + ",\"snowRgb\":" + LodSurfaceMix.ProbeSnowRgb
                    + ",\"groundRgb\":" + LodSurfaceMix.ProbeGroundRgb
                    + ",\"plantRgb\":" + LodSurfaceMix.ProbePlantRgb
                    + ",\"texGroundRgb\":" + LodSurfaceMix.ProbeTexGroundRgb
                    + ",\"texPlantRgb\":" + LodSurfaceMix.ProbeTexPlantRgb
                    + ",\"mixRgb\":" + LodSurfaceMix.ProbeMixRgb
                    + ",\"taken\":" + LodSurfaceMix.ProbeTaken
                    + ",\"skipped\":" + LodSurfaceMix.ProbeSkipped
                    + ",\"snowL\":" + LodSurfaceMix.ProbeSnowLayers
                    + ",\"groundL\":" + LodSurfaceMix.ProbeGroundLayers
                    + ",\"plantL\":" + LodSurfaceMix.ProbePlantLayers
                    + ",\"seasonRel\":" + LodSurfaceMix.ProbeSeasonRel.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"winter\":" + LodSurfaceMix.ProbeWinter.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"x\":" + x + ",\"y\":" + y + ",\"z\":" + z + "}");
            }
            if (frostLeaf)
            {
                dbgFrostLeaves++;
                int noTint = 0;
                try { noTint = block.GetColorWithoutTint(capi, new BlockPos(x, y, z)); }
                catch { }
                LodPaletteRepair.Channels(LodSurfaceMix.ProbeTopRgb, out int tr, out int tg, out int tb, out int tLuma, out int tChroma);
                LodPaletteRepair.Channels(noTint, out int nr, out int ng, out int nb, out int nLuma, out _);
                var leafPos = new BlockPos(x, y, z);
                ClimateCondition? nowCl = null;
                try { nowCl = world.BlockAccessor.GetClimateAt(leafPos, EnumGetClimateMode.NowValues); }
                catch { }
                float tempC = nowCl?.Temperature ?? 999f;
                float worldGenC = nowCl?.WorldGenTemperature ?? 999f;
                float seasonRel = 0f;
                int dayOfYear = 0;
                try
                {
                    seasonRel = world.Calendar.GetSeasonRel(leafPos);
                    dayOfYear = world.Calendar.DayOfYear;
                }
                catch { }
                bool frostAble = block.Frostable;
                float frostW = 0f;
                float hereTemp = 1f;
                TryVisitFrostWeight(world, leafPos, out frostW, out tempC, out hereTemp);
                int rawSeason = 0;
                try { rawSeason = block.GetColor(capi, leafPos); } catch { }
                LodPaletteRepair.Channels(rawSeason, out int gr, out int gg, out int gb, out int gLuma, out int gChroma);
                AgentFrostLog("H-LEAF-GETCOLOR", "LodSeasonBake.BakeSectionFromVisit", "leaf-sample",
                    "{\"path\":\"" + probePath.Replace("\\", "/").Replace("\"", "'")
                    + "\",\"kind\":" + (int)tk
                    + ",\"visit\":\"" + DebugVisitKind.Replace("\\", "/").Replace("\"", "'")
                    + "\",\"getR\":" + gr + ",\"getG\":" + gg + ",\"getB\":" + gb
                    + ",\"getLuma\":" + gLuma + ",\"getChroma\":" + gChroma
                    + ",\"topR\":" + tr + ",\"topG\":" + tg + ",\"topB\":" + tb
                    + ",\"topLuma\":" + tLuma + ",\"topChroma\":" + tChroma
                    + ",\"frostApplied\":" + (LodSurfaceMix.ProbeTopRgb != rawSeason ? "true" : "false")
                    + ",\"missingW\":" + (LodPaletteRepair.IsMissingTextureWhite(LodSurfaceMix.ProbeTopRgb) ? "true" : "false")
                    + ",\"brightCap\":" + (LodPaletteRepair.IsBrightCap(LodSurfaceMix.ProbeTopRgb) ? "true" : "false")
                    + ",\"snowAlbedo\":" + (LodPaletteRepair.IsSnowOrIceAlbedo(LodSurfaceMix.ProbeTopRgb) ? "true" : "false")
                    + ",\"noTintR\":" + nr + ",\"noTintG\":" + ng + ",\"noTintB\":" + nb
                    + ",\"noTintLuma\":" + nLuma
                    + ",\"seasonMap\":\"" + (block.SeasonColorMap ?? "").Replace("\\", "/").Replace("\"", "'")
                    + "\",\"climateMap\":\"" + (block.ClimateColorMap ?? "").Replace("\\", "/").Replace("\"", "'")
                    + "\",\"frostAble\":" + (frostAble ? "true" : "false")
                    + ",\"tempC\":" + tempC.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"worldGenC\":" + worldGenC.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"hereTemp\":" + hereTemp.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"frostW\":" + frostW.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"seasonRel\":" + seasonRel.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"dayOfYear\":" + dayOfYear
                    + ",\"snowL\":" + LodSurfaceMix.ProbeSnowLayers
                    + ",\"plantL\":" + LodSurfaceMix.ProbePlantLayers
                    + ",\"groundL\":" + LodSurfaceMix.ProbeGroundLayers
                    + ",\"x\":" + x + ",\"y\":" + y + ",\"z\":" + z + "}");
            }
            if (dbgCanopy < 8 && VanillaCanopyPath(probePath))
            {
                dbgCanopy++;
                SampleFinalTint(world, block, x, y, z, LodUntintedShare.None, plantTintFallback,
                    out float sr, out float sg, out float sb);
                int tintR = (int)(sr * 255f + 0.5f);
                int tintG = (int)(sg * 255f + 0.5f);
                int tintB = (int)(sb * 255f + 0.5f);
                int rawSeason = 0;
                try { rawSeason = block.GetColor(capi, new BlockPos(x, y, z)); } catch { }
                LodPaletteRepair.Channels(rawSeason, out int gr, out int gg, out int gb, out int gLuma, out int gChroma);
                LodPaletteRepair.Channels(LodSurfaceMix.ProbeTopRgb, out int tr, out int tg, out int tb, out int tLuma, out _);
                float seasonRel = 0f;
                int dayOfYear = 0;
                try
                {
                    seasonRel = world.Calendar.GetSeasonRel(new BlockPos(x, y, z));
                    dayOfYear = world.Calendar.DayOfYear;
                }
                catch { }
                float mott = LodCanopyGray.MottleAmount(x, z);
                float frostWg = 0f;
                bool crown = false;
                try
                {
                    crown = LodCanopyGray.IsCanopyCrown(world.BlockAccessor, x, y, z);
                    TryVisitFrostWeight(world, new BlockPos(x, y, z), out frostWg, out _, out _);
                }
                catch { }
                float mixAmt = LodCanopyGray.MixTowardGray(mott, frostWg);
                AgentFrostLog("H-MAY7", "LodSeasonBake.BakeSectionFromVisit", "canopy-sample",
                    "{\"path\":\"" + probePath.Replace("\\", "/").Replace("\"", "'")
                    + "\",\"visit\":\"" + DebugVisitKind.Replace("\\", "/").Replace("\"", "'")
                    + "\",\"getR\":" + gr + ",\"getG\":" + gg + ",\"getB\":" + gb
                    + ",\"getLuma\":" + gLuma + ",\"getChroma\":" + gChroma
                    + ",\"topR\":" + tr + ",\"topG\":" + tg + ",\"topB\":" + tb
                    + ",\"topLuma\":" + tLuma
                    + ",\"tintR\":" + tintR + ",\"tintG\":" + tintG + ",\"tintB\":" + tintB
                    + ",\"seasonMap\":\"" + (block.SeasonColorMap ?? "").Replace("\\", "/").Replace("\"", "'")
                    + "\",\"climateMap\":\"" + (block.ClimateColorMap ?? "").Replace("\\", "/").Replace("\"", "'")
                    + "\",\"seasonRel\":" + seasonRel.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"dayOfYear\":" + dayOfYear
                    + ",\"mottle\":" + mott.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"frostW\":" + frostWg.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"grayMix\":" + mixAmt.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"crown\":" + (crown ? "true" : "false")
                    + ",\"canopyPath\":" + (LodCanopyGray.IsVanillaTreeCanopyPath(probePath) ? "true" : "false")
                    + ",\"snowL\":" + LodSurfaceMix.ProbeSnowLayers
                    + ",\"x\":" + x + ",\"y\":" + y + ",\"z\":" + z + "}");
            }
            if (DebugVisitKind == "walk" && dbgWalkWhite < 8)
            {
                LodPaletteRepair.Channels(LodSurfaceMix.ProbeTopRgb, out int wr, out int wg, out int wb, out int wLuma, out _);
                LodPaletteRepair.Channels(mix, out int mxr, out int mxg, out int mxb, out int mxLuma, out _);
                bool kindSnow = tk == LodSurfaceMix.Kind.Snow;
                bool snowAlbedo = LodPaletteRepair.IsSnowOrIceAlbedo(LodSurfaceMix.ProbeTopRgb)
                    || LodPaletteRepair.IsSnowOrIceAlbedo(mix);
                bool pale = wLuma >= 160 || mxLuma >= 160;
                if (kindSnow || snowAlbedo || pale
                    || LodPaletteRepair.IsBrightCap(mix)
                    || LodPaletteRepair.IsMissingTextureWhite(mix))
                {
                    dbgWalkWhite++;
                    AgentFrostLog("H-WHITE-1", "LodSeasonBake.BakeSectionFromVisit", "walk-white-sample",
                        "{\"path\":\"" + probePath.Replace("\\", "/").Replace("\"", "'")
                        + "\",\"visit\":\"walk\""
                        + ",\"kind\":" + (int)tk
                        + ",\"kindSnow\":" + (kindSnow ? "true" : "false")
                        + ",\"columnSnowy\":" + (ColumnSurfaceIsSnowy(block) ? "true" : "false")
                        + ",\"climateUntinted\":" + (LodBlockPolicy.IsClimateUntinted(block) ? "true" : "false")
                        + ",\"blendToward\":false"
                        + ",\"snowL\":" + LodSurfaceMix.ProbeSnowLayers
                        + ",\"plantL\":" + LodSurfaceMix.ProbePlantLayers
                        + ",\"groundL\":" + LodSurfaceMix.ProbeGroundLayers
                        + ",\"topR\":" + wr + ",\"topG\":" + wg + ",\"topB\":" + wb
                        + ",\"topLuma\":" + wLuma
                        + ",\"mixR\":" + mxr + ",\"mixG\":" + mxg + ",\"mixB\":" + mxb
                        + ",\"mixLuma\":" + mxLuma
                        + ",\"texGroundRgb\":" + LodSurfaceMix.ProbeTexGroundRgb
                        + ",\"texPlantRgb\":" + LodSurfaceMix.ProbeTexPlantRgb
                        + ",\"seasonRel\":" + LodSurfaceMix.ProbeSeasonRel.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ",\"winter\":" + LodSurfaceMix.ProbeWinter.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ",\"snowAlbedo\":" + (snowAlbedo ? "true" : "false")
                        + ",\"brightCap\":" + (LodPaletteRepair.IsBrightCap(mix) ? "true" : "false")
                        + ",\"missingW\":" + (LodPaletteRepair.IsMissingTextureWhite(mix) ? "true" : "false")
                        + ",\"x\":" + x + ",\"y\":" + y + ",\"z\":" + z + "}");
                }
            }
            // #endregion
        }

        int originX = LodWorld.KeySx(sectionKey) * LodSection.SectionBlocks;
        int originZ = LodWorld.KeySz(sectionKey) * LodSection.SectionBlocks;
        LodSurfaceMix.BlurWithHalo(
            capi, world.BlockAccessor, mapH, originX, originZ,
            raw, mask, blurred, gs, LodSurfaceMix.BlurRadius);

        for (int col = 0; col < cols; col++)
        {
            if (mask[col] == 0) continue;
            Block? block = tops[col];
            if (block == null) continue;
            if (!section.TryGetTopRun(col, out ulong topRun)) continue;

            int pid = LodSection.RunPaletteId(topRun);
            if (pid < 0 || pid >= section.Palette.Count) continue;
            LodPaletteEntry entry = section.Palette[pid];

            (int untinted, _) = untintedOf(block);
            untinted = LodPaletteRepair.KeepCapturedColor(
                untinted, untinted, LodBlockPolicy.IsClimateUntinted(block));

            int baked = blurred[col];
            if (baked == 0)
            {
                tally.Zero++;
                byte liveFlags = ClearVisitBakeFlags(entry.Flags);
                int livePid = section.FindOrAddPaletteEntry(
                    block.BlockId, entry.Color, liveFlags, entry.TintSlot);
                if (livePid != pid && section.TrySetTopRunPaletteId(col, livePid))
                    changed++;
                continue;
            }

            tally.GetColor++;
            int beforeKeep = baked;
            if (mask[col] == 1)
            {
                LodSurfaceMix.Unpack(blurred[col], out int blurR, out _, out _);
                sumBlurR += blurR;
            }
            baked = LodPaletteRepair.KeepCapturedColor(
                baked, untinted, KeepVisitSnowColor(block, mask[col] == 2));
            if (baked != beforeKeep) nKeepChanged++;
            // #region agent log
            if (dbgFrostKeep < 8 && FrostLeafPath(block.Code?.Path))
            {
                dbgFrostKeep++;
                LodPaletteRepair.Channels(beforeKeep, out int br, out int bg, out int bb, out int bLuma, out int bChroma);
                LodPaletteRepair.Channels(baked, out int ar, out int ag, out int ab, out int aLuma, out _);
                LodPaletteRepair.Channels(untinted, out int ur, out int ug, out int ub, out int uLuma, out _);
                bool keepSnow = KeepVisitSnowColor(block, mask[col] == 2);
                AgentFrostLog("H-LEAF-SANITIZE", "LodSeasonBake.BakeSectionFromVisit", "leaf-keep",
                    "{\"path\":\"" + (block.Code?.Path ?? "").Replace("\\", "/").Replace("\"", "'")
                    + "\",\"beforeR\":" + br + ",\"beforeG\":" + bg + ",\"beforeB\":" + bb
                    + ",\"beforeLuma\":" + bLuma + ",\"beforeChroma\":" + bChroma
                    + ",\"afterR\":" + ar + ",\"afterG\":" + ag + ",\"afterB\":" + ab
                    + ",\"afterLuma\":" + aLuma
                    + ",\"untintedR\":" + ur + ",\"untintedG\":" + ug + ",\"untintedB\":" + ub
                    + ",\"untintedLuma\":" + uLuma
                    + ",\"swapped\":" + (baked != beforeKeep ? "true" : "false")
                    + ",\"keepSnow\":" + (keepSnow ? "true" : "false")
                    + ",\"missingW\":" + (LodPaletteRepair.IsMissingTextureWhite(beforeKeep) ? "true" : "false")
                    + ",\"brightCap\":" + (LodPaletteRepair.IsBrightCap(beforeKeep) ? "true" : "false") + "}");
            }
            if (DebugVisitKind == "walk" && dbgWalkKeep < 8)
            {
                LodPaletteRepair.Channels(beforeKeep, out int br, out int bg, out int bb, out int bLuma, out _);
                LodPaletteRepair.Channels(baked, out int ar, out int ag, out int ab, out int aLuma, out _);
                bool snowAlbedo = LodPaletteRepair.IsSnowOrIceAlbedo(beforeKeep)
                    || LodPaletteRepair.IsSnowOrIceAlbedo(baked);
                if (baked != beforeKeep || snowAlbedo || aLuma >= 160 || bLuma >= 160)
                {
                    dbgWalkKeep++;
                    AgentFrostLog("H-WHITE-3", "LodSeasonBake.BakeSectionFromVisit", "walk-white-keep",
                        "{\"path\":\"" + (block.Code?.Path ?? "").Replace("\\", "/").Replace("\"", "'")
                        + "\",\"visit\":\"walk\""
                        + ",\"keepChanged\":" + (baked != beforeKeep ? "true" : "false")
                        + ",\"keepSnow\":" + (mask[col] == 2 || LodBlockPolicy.IsClimateUntinted(block)
                            || LodPaletteRepair.IsSnowOrIceAlbedo(beforeKeep) ? "true" : "false")
                        + ",\"snowAlbedo\":" + (snowAlbedo ? "true" : "false")
                        + ",\"brightCap\":" + (LodPaletteRepair.IsBrightCap(baked) ? "true" : "false")
                        + ",\"missingW\":" + (LodPaletteRepair.IsMissingTextureWhite(baked) ? "true" : "false")
                        + ",\"beforeR\":" + br + ",\"beforeG\":" + bg + ",\"beforeB\":" + bb
                        + ",\"beforeLuma\":" + bLuma
                        + ",\"afterR\":" + ar + ",\"afterG\":" + ag + ",\"afterB\":" + ab
                        + ",\"afterLuma\":" + aLuma + "}");
                }
            }
            // #endregion
            LodSurfaceMix.Unpack(baked, out int finR, out _, out _);
            sumFinalR += finR;

            byte bakedFlags = MixVisitBakeFlags(entry.Flags, frostCol[col]);
            int targetPid = section.FindOrAddPaletteEntry(
                block.BlockId, baked, bakedFlags, LodTintRegistry.SlotNone);

            if (targetPid != pid)
            {
                if (section.TrySetTopRunPaletteId(col, targetPid))
                    changed++;
                continue;
            }

            if (entry.Color != baked
                || entry.TintSlot != LodTintRegistry.SlotNone
                || entry.Flags != bakedFlags)
            {
                entry.Color = baked;
                entry.Flags = bakedFlags;
                entry.TintSlot = LodTintRegistry.SlotNone;
                section.Palette[pid] = entry;
                changed++;
            }
        }

        if (changed > 0) section.InvalidatePaletteSnapshot();
        tally.Changed = changed;
        // #region agent log
        if (nLand > 0 && dbgMixSections < 16)
        {
            dbgMixSections++;
            AgentMixLog("H-A", "LodSeasonBake.BakeSectionFromVisit", "section-mix",
                "{\"nLand\":" + nLand
                + ",\"snowTop\":" + nSnowTop
                + ",\"plantTop\":" + nPlantTop
                + ",\"groundTop\":" + nGroundTop
                + ",\"meanTopR\":" + (sumTopR / nLand)
                + ",\"meanMixR\":" + (sumMixR / nLand)
                + ",\"meanBlurR\":" + (sumBlurR / nLand)
                + ",\"meanFinalR\":" + (sumFinalR / nLand)
                + ",\"keepChanged\":" + nKeepChanged
                + ",\"skipped\":" + nSkipped
                + ",\"changed\":" + changed
                + ",\"key\":" + sectionKey + "}");
        }
        // #endregion
        return changed;
    }

    /// <summary>
    /// Walk/discover visit bake: paint at most <paramref name="maxColumns"/> captured
    /// tops starting at <paramref name="startCol"/>, under a wall-clock budget.
    /// Writes raw column colours (no neighbour blur) so work can split across ticks.
    /// Incomplete means "continue next tick from <paramref name="nextCol"/>" — the
    /// section mesh stays drawn; only season GetColor is elongated. Login sweep still
    /// uses the full <see cref="BakeSectionFromVisit"/> path with blur.
    /// </summary>
    public static int BakeSectionFromVisitChunked(
        ICoreClientAPI capi,
        LodSection section,
        long sectionKey,
        Block? plantTintFallback,
        System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf,
        int startCol,
        int maxColumns,
        double maxMs,
        out int nextCol,
        out bool complete)
    {
        _ = plantTintFallback;
        nextCol = startCol;
        complete = false;
        if (maxColumns <= 0 || maxMs <= 0) return 0;

        IClientWorldAccessor world = capi.World;
        int gs = LodSection.GridSize;
        int cols = gs * gs;
        if (startCol < 0) startCol = 0;
        if (startCol >= cols)
        {
            complete = true;
            nextCol = cols;
            return 0;
        }

        int mapH = world.BlockAccessor.MapSizeY;
        int painted = 0;
        long deadline = Stopwatch.GetTimestamp()
            + (long)(Stopwatch.Frequency * maxMs / 1000.0);

        LodBakeScratch.BeginSectionTextureMeans();
        try
        {
            return BakeSectionFromVisitChunkedBody(
                capi, world, section, sectionKey, untintedOf,
                startCol, maxColumns, cols, mapH, deadline,
                out nextCol, out complete, ref painted);
        }
        finally
        {
            LodBakeScratch.EndSectionTextureMeans();
        }
    }

    static int BakeSectionFromVisitChunkedBody(
        ICoreClientAPI capi,
        IClientWorldAccessor world,
        LodSection section,
        long sectionKey,
        System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf,
        int startCol,
        int maxColumns,
        int cols,
        int mapH,
        long deadline,
        out int nextCol,
        out bool complete,
        ref int painted)
    {
        int changed = 0;
        complete = false;
        nextCol = startCol;

        for (int col = startCol; col < cols; col++)
        {
            // Stop *before* advancing past this column so incomplete resume
            // revisits it — never skip a Captured top just because the budget ended.
            if (painted >= maxColumns)
            {
                nextCol = col;
                if (changed > 0) section.InvalidatePaletteSnapshot();
                return changed;
            }
            if ((painted & 15) == 0 && Stopwatch.GetTimestamp() >= deadline)
            {
                nextCol = col;
                if (changed > 0) section.InvalidatePaletteSnapshot();
                return changed;
            }

            if (!section.Captured[col]) continue;
            if (!section.TryGetTopRun(col, out ulong topRun)) continue;

            int pid = LodSection.RunPaletteId(topRun);
            if (pid < 0 || pid >= section.Palette.Count) continue;

            LodPaletteEntry entry = section.Palette[pid];
            if (entry.BlockId <= 0 || entry.BlockId >= world.Blocks.Count) continue;

            Block block = world.Blocks[entry.BlockId];
            (int x, int y, int z) = LodPipeline.CaptureBlockPos(sectionKey, col, topRun);
            if (TryResolveLiveSurface(
                    world.BlockAccessor, x, y, z, mapH,
                    out Block? live, out int liveY)
                && live != null)
            {
                block = live;
                y = liveY;
            }

            if (!IsColumnMapLoaded(world.BlockAccessor, x, z))
                continue;

            int mix = LodSurfaceMix.SampleColumnStack(
                capi, world.BlockAccessor, x, y, z, out bool water, out _);
            if (mix == 0) continue;

            bool frost = ShouldFlagFrost(
                LodSurfaceMix.ProbeFrostW, block, LodSurfaceMix.ProbeTopPath);
            (int untinted, _) = untintedOf(block);
            untinted = LodPaletteRepair.KeepCapturedColor(
                untinted, untinted, LodBlockPolicy.IsClimateUntinted(block));
            mix = LodPaletteRepair.KeepCapturedColor(
                mix, untinted, KeepVisitSnowColor(block, water));

            byte bakedFlags = MixVisitBakeFlags(entry.Flags, frost);
            int targetPid = section.FindOrAddPaletteEntry(
                block.BlockId, mix, bakedFlags, LodTintRegistry.SlotNone);

            if (targetPid != pid)
            {
                if (section.TrySetTopRunPaletteId(col, targetPid))
                    changed++;
            }
            else if (entry.Color != mix
                || entry.TintSlot != LodTintRegistry.SlotNone
                || entry.Flags != bakedFlags)
            {
                entry.Color = mix;
                entry.Flags = bakedFlags;
                entry.TintSlot = LodTintRegistry.SlotNone;
                section.Palette[pid] = entry;
                changed++;
            }

            painted++;
        }

        nextCol = cols;
        complete = true;
        if (changed > 0) section.InvalidatePaletteSnapshot();
        return changed;
    }

    // #region agent log
    static bool FrostLeafPath(string? path) =>
        path != null
        && (path.Contains("leaves", StringComparison.Ordinal)
            || path.Contains("frosted", StringComparison.Ordinal)
            || path.Contains("bush", StringComparison.Ordinal)
            || path.Contains("pine", StringComparison.Ordinal)
            || path.Contains("conifer", StringComparison.Ordinal)
            || path.Contains("tallgrass", StringComparison.Ordinal));

    static bool VanillaCanopyPath(string? path) =>
        path != null
        && (path.Contains("leaves", StringComparison.Ordinal)
            || path.Contains("pine", StringComparison.Ordinal)
            || path.Contains("conifer", StringComparison.Ordinal));

    internal static void FlagBakedLuma(LodSection section, out int nBaked, out int meanLuma, out int nPale)
    {
        nBaked = 0;
        meanLuma = 0;
        nPale = 0;
        long sum = 0;
        for (int i = 0; i < section.Palette.Count; i++)
        {
            LodPaletteEntry e = section.Palette[i];
            if ((e.Flags & LodPaletteEntry.FlagBaked) == 0) continue;
            LodPaletteRepair.Channels(e.Color, out _, out _, out _, out int luma, out _);
            nBaked++;
            sum += luma;
            if (luma >= 160) nPale++;
        }
        if (nBaked > 0) meanLuma = (int)(sum / nBaked);
    }

    const bool AgentDiskLog = false;

    static void AgentMixLog(string hypothesisId, string location, string message, string dataJson)
    {
        if (!AgentDiskLog) return;
        try
        {
            System.IO.File.AppendAllText(
                @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                "{\"sessionId\":\"40cccb\",\"runId\":\"cream-1\",\"hypothesisId\":\"" + hypothesisId
                + "\",\"location\":\"" + location + "\",\"message\":\"" + message
                + "\",\"data\":" + dataJson
                + ",\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n");
        }
        catch { }
    }

    static void AgentFrostLog(string hypothesisId, string location, string message, string dataJson)
    {
        if (!AgentDiskLog) return;
        try
        {
            System.IO.File.AppendAllText(
                @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                "{\"sessionId\":\"40cccb\",\"runId\":\"frost-1\",\"hypothesisId\":\"" + hypothesisId
                + "\",\"location\":\"" + location + "\",\"message\":\"" + message
                + "\",\"data\":" + dataJson
                + ",\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n");
        }
        catch { }
    }
    // #endregion

    /// <summary>
    /// Bake every tintable palette entry in a cached section. Returns how many colours changed.
    /// Uses shader reproduction when GetColor is unavailable (legacy / off-visit paths only).
    /// </summary>
    public static int BakeSection(
        ICoreClientAPI capi,
        LodSection section,
        long sectionKey,
        Block? plantTintFallback,
        System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf)
    {
        IClientWorldAccessor world = capi.World;
        SnowVote vote = ComputeSnowVote(section, world.Blocks, sectionKey);
        int changed = 0;
        for (int pid = 0; pid < section.Palette.Count; pid++)
        {
            LodPaletteEntry entry = section.Palette[pid];
            if (entry.BlockId <= 0 || entry.BlockId >= world.Blocks.Count) continue;
            Block block = world.Blocks[entry.BlockId];
            if (!section.TryFindPaletteTop(sectionKey, pid, out int x, out int y, out int z)) continue;

            (int untinted, LodUntintedShare share) = untintedOf(block);
            untinted = LodPaletteRepair.KeepCapturedColor(
                untinted, untinted, LodBlockPolicy.IsClimateUntinted(block));

            if (!CanBake(block, untinted, plantTintFallback))
            {
                byte cleared = ClearVisitBakeFlags(entry.Flags);
                if (cleared != entry.Flags)
                {
                    entry.Flags = cleared;
                    entry.TintSlot = 0;
                    section.Palette[pid] = entry;
                    changed++;
                }
                continue;
            }

            bool groundSnow = vote.MajoritySnow && IsSnowEligibleGround(block);
            int baked = BakePaletteColor(
                capi, world, block, untinted, x, y, z, share, plantTintFallback, groundSnow);
            baked = LodPaletteRepair.KeepCapturedColor(
                baked, untinted, LodBlockPolicy.IsClimateUntinted(block));

            byte bakedFlags = MixVisitBakeFlags(
                entry.Flags, VisitColumnFrost(world, x, y, z, block, block.Code?.Path));
            if (baked == entry.Color && entry.Flags == bakedFlags
                && entry.TintSlot == LodTintRegistry.SlotNone)
            {
                continue;
            }

            entry.Color = baked;
            entry.Flags = bakedFlags;
            entry.TintSlot = LodTintRegistry.SlotNone;
            section.Palette[pid] = entry;
            changed++;
        }

        if (changed > 0) section.InvalidatePaletteSnapshot();
        return changed;
    }

    public static bool SectionHasBakedEntries(LodSection section)
    {
        for (int i = 0; i < section.Palette.Count; i++)
        {
            if ((section.Palette[i].Flags & LodPaletteEntry.FlagBaked) != 0) return true;
        }
        return false;
    }

    public static bool SectionNeedsLoginBake(LodSection section)
    {
        for (int i = 0; i < section.Palette.Count; i++)
        {
            LodPaletteEntry entry = section.Palette[i];
            if (entry.BlockId <= 0) continue;
            if ((entry.Flags & LodPaletteEntry.FlagBaked) != 0) continue;
            if (entry.TintSlot != LodTintRegistry.SlotNone) return true;
        }
        return false;
    }

    /// <summary>Legacy live-tint rows still on disk (brown/manila plates on rejoin).</summary>
    public static bool SectionNeedsLegacyHeal(LodSection section)
    {
        for (int i = 0; i < section.Palette.Count; i++)
        {
            LodPaletteEntry e = section.Palette[i];
            if ((e.Flags & LodPaletteEntry.FlagBaked) != 0) continue;
            if (e.TintSlot != LodTintRegistry.SlotNone) return true;
        }
        return false;
    }

    /// <summary>
    /// Discover-bake legacy caches that still carry a live tint slot. Runs on disk load
    /// so old SQLite browns heal without a manual cache wipe (0.8.46 join refresh).
    /// </summary>
    public static int UpgradeLegacyEntries(
        ICoreClientAPI capi,
        LodSection section,
        long sectionKey,
        Block? plantTintFallback,
        System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf)
    {
        IClientWorldAccessor world = capi.World;
        int changed = 0;
        for (int pid = 0; pid < section.Palette.Count; pid++)
        {
            LodPaletteEntry entry = section.Palette[pid];
            if ((entry.Flags & LodPaletteEntry.FlagBaked) != 0) continue;
            if (entry.TintSlot == LodTintRegistry.SlotNone) continue;
            if (entry.BlockId <= 0) continue;
            if (!section.TryFindPaletteTop(sectionKey, pid, out int x, out int y, out int z)) continue;

            Block block = world.Blocks[entry.BlockId];
            (int untinted, LodUntintedShare share) = untintedOf(block);
            if (!CanBake(block, untinted, plantTintFallback)) continue;

            int baked = BakePaletteColor(
                capi, world, block, untinted, x, y, z, share, plantTintFallback);
            baked = LodPaletteRepair.KeepCapturedColor(
                baked, untinted, LodBlockPolicy.IsClimateUntinted(block));

            byte bakedFlags = MixVisitBakeFlags(
                entry.Flags, VisitColumnFrost(world, x, y, z, block, block.Code?.Path));
            if (baked == entry.Color && entry.Flags == bakedFlags
                && entry.TintSlot == LodTintRegistry.SlotNone)
            {
                continue;
            }

            entry.Color = baked;
            entry.Flags = bakedFlags;
            entry.TintSlot = LodTintRegistry.SlotNone;
            section.Palette[pid] = entry;
            changed++;
        }

        if (changed > 0) section.InvalidatePaletteSnapshot();
        return changed;
    }

    /// <summary>
    /// Outside the 30-day window: retint stored L0 inside the player-centered season
    /// disk. Canvas beyond that disk is left alone. Does not create or wipe sections.
    /// </summary>
    public static int RebakeDiskInPlace(
        ICoreClientAPI capi,
        LodWorld world,
        LodPipeline pipeline,
        Block? plantTintFallback,
        System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf,
        out int storedInDisk,
        out int loaded,
        out int paletteChanges,
        out int diskCells,
        out int elapsedMs)
    {
        storedInDisk = 0;
        loaded = 0;
        paletteChanges = 0;
        diskCells = 0;
        int changedSections = 0;
        var sw = Stopwatch.StartNew();
        EntityPos pos = capi.World.Player.Entity.Pos;
        int radius = LodLoginSweepBootstrap.EmptyCanvasBootstrapRadiusBlocks;
        double radiusSq = (double)radius * radius;
        int footprint = LodSection.SectionBlocks;
        int centerSx = (int)Math.Floor(pos.X / footprint);
        int centerSz = (int)Math.Floor(pos.Z / footprint);
        int cellRadius = LodLoginSweepBootstrap.BootstrapCellRadius(footprint);
        diskCells = CountDiskCells(centerSx, centerSz, cellRadius, footprint, pos.X, pos.Z, radiusSq);

        foreach (long key in LodLoginSweep.VisitedL0Keys(world))
        {
            double cx = LodWorld.KeySx(key) * footprint + footprint * 0.5;
            double cz = LodWorld.KeySz(key) * footprint + footprint * 0.5;
            double dx = cx - pos.X;
            double dz = cz - pos.Z;
            if (dx * dx + dz * dz > radiusSq) continue;
            storedInDisk++;
            LodSection? section;
            if (!world.Sections.TryGetValue(key, out section))
            {
                section = world.LoadFromStore?.Invoke(key);
                if (section != null) world.InstallLoaded(key, section);
            }
            if (section == null) continue;
            loaded++;
            int n = BakeSection(capi, section, key, plantTintFallback, untintedOf);
            if (n <= 0) continue;
            paletteChanges += n;
            changedSections++;
            world.MarkChanged(key);
            pipeline.InvalidateMipAncestors(key);
            world.RenderDirty.Add(key);
        }
        elapsedMs = (int)sw.ElapsedMilliseconds;
        return changedSections;
    }

    static int CountDiskCells(
        int centerSx, int centerSz, int cellRadius, int footprint,
        double originX, double originZ, double radiusSq)
    {
        int n = 0;
        for (int dsx = -cellRadius; dsx <= cellRadius; dsx++)
        {
            for (int dsz = -cellRadius; dsz <= cellRadius; dsz++)
            {
                double cx = (centerSx + dsx) * footprint + footprint * 0.5;
                double cz = (centerSz + dsz) * footprint + footprint * 0.5;
                double dx = cx - originX;
                double dz = cz - originZ;
                if (dx * dx + dz * dz <= radiusSq) n++;
            }
        }
        return n;
    }
}
