using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// After a live visit capture: lock per-column season appearance into the palette.
///
/// Capture (while the player is teleported there) stores real block ids — snow layers,
/// green or snowy leaves, grass tops. Bake samples vanilla's own tinted colour at each
/// column top (or reproduces the live shader's split climate + season path as fallback),
/// then sets <see cref="LodPaletteEntry.FlagBaked"/> so near and far LOD keep that paint
/// until relog. Ground snow also follows a majority vote over snow-eligible captured
/// surface columns.
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
        if (LodPaletteRepair.IsRockLikeAlbedo(color) || LodPaletteRepair.IsSnowOrIceAlbedo(color))
            return false;
        if ((LodBlockPolicy.FlagsFor(block) & LodPaletteEntry.FlagWater) != 0) return false;
        if (block.EntityClass != null) return false;

        if (block.ClimateColorMapResolved != null || block.SeasonColorMapResolved != null)
            return true;

        return block.BlockMaterial == EnumBlockMaterial.Plant && plantTintFallback != null;
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

    public static SnowVote ComputeSnowVote(LodSection section, Block[] blocks, long sectionKey)
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
                if (entry.BlockId <= 0 || entry.BlockId >= blocks.Length) continue;
                Block block = blocks[entry.BlockId];
                if (!IsSnowEligibleGround(block)) continue;
                eligible++;
                if (ColumnSurfaceIsSnowy(block)) snowy++;
                break;
            }
        }
        return new SnowVote(eligible, snowy);
    }

    /// <summary>
    /// Vanilla's fully tinted face colour at a world position — the ground truth during
    /// the visit sweep when chunks are loaded.
    /// </summary>
    public static int SampleVanillaColor(ICoreClientAPI capi, Block block, int x, int y, int z)
    {
        try
        {
            var pos = new BlockPos(x, y, z);
            int color = block.GetColor(capi, pos);
            if (color != 0 && !LodPaletteRepair.NeedsColor(color))
                return color;
        }
        catch
        {
            // Fall back to manual tint reproduction.
        }
        return 0;
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

        if (groundSnowMajority && IsSnowEligibleGround(block))
            baked = BlendTowardSnow(baked, 0.72f);
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
            ClimateCondition? cl = world.BlockAccessor.GetClimateAt(new BlockPos(x, world.SeaLevel, z));
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

    static int BlendTowardSnow(int color, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        int sr = 210, sg = 215, sb = 220;
        int r = (int)((color & 0xFF) * (1f - amount) + sr * amount);
        int g = (int)(((color >> 8) & 0xFF) * (1f - amount) + sg * amount);
        int b = (int)(((color >> 16) & 0xFF) * (1f - amount) + sb * amount);
        return unchecked((int)0xFF000000) | Math.Clamp(b, 0, 255) << 16
            | Math.Clamp(g, 0, 255) << 8 | Math.Clamp(r, 0, 255);
    }

    /// <summary>
    /// Bake every tintable palette entry in a cached section. Returns how many colours changed.
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
            if (entry.BlockId <= 0 || entry.BlockId >= world.Blocks.Length) continue;
            Block block = world.Blocks[entry.BlockId];
            if (!section.TryFindPaletteTop(sectionKey, pid, out int x, out int y, out int z)) continue;

            (int untinted, LodUntintedShare share) = untintedOf(block);
            untinted = LodPaletteRepair.KeepCapturedColor(
                untinted, untinted, LodBlockPolicy.IsClimateUntinted(block));

            if (!CanBake(block, untinted, plantTintFallback))
            {
                if ((entry.Flags & LodPaletteEntry.FlagBaked) != 0)
                {
                    entry.Flags = (byte)(entry.Flags & ~LodPaletteEntry.FlagBaked);
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

            if (baked == entry.Color && (entry.Flags & LodPaletteEntry.FlagBaked) != 0
                && entry.TintSlot == LodTintRegistry.SlotNone)
            {
                continue;
            }

            entry.Color = baked;
            entry.Flags = (byte)(entry.Flags | LodPaletteEntry.FlagBaked);
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
}
