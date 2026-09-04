using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DistantVistas;

/// <summary>
/// Discover-bake: at capture (or budgeted season repaint) resolve the final grass-top
/// and foliage RGB through the game's own climate + season colour maps at that block's
/// lat/long/climate and calendar moment. Stored albedo is multiplied once; tint slot 0
/// and <see cref="LodPaletteEntry.FlagBaked"/> mean the shader must not re-tint.
/// Legacy untinted + live-slot sections keep the shader path until revisited or repaint.
/// </summary>
public static class LodSeasonBake
{
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

    /// <summary>
    /// Untinted atlas (or topsoil composite) times the vanilla map product at this X/Y/Z.
    /// </summary>
    public static int BakePaletteColor(
        IClientWorldAccessor world,
        Block block,
        int untintedColor,
        int x,
        int y,
        int z,
        LodUntintedShare share,
        Block? plantTintFallback)
    {
        SampleFinalTint(world, block, x, y, z, share, plantTintFallback, out float tr, out float tg, out float tb);
        return MultiplyRgb(untintedColor, tr, tg, tb);
    }

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

        int rgba = world.ApplyColorMapOnRgba(
            climate, season,
            unchecked((int)0xFFFFFFFF), x, y, z);

        r = ((rgba >> 16) & 0xFF) / 255f;
        g = ((rgba >> 8) & 0xFF) / 255f;
        b = (rgba & 0xFF) / 255f;
        LodTintRegistry.ClampTintAwayFromWhite(ref r, ref g, ref b);

        r = LodTopSoil.Dilute(share.R, r);
        g = LodTopSoil.Dilute(share.G, g);
        b = LodTopSoil.Dilute(share.B, b);
    }

    public static int MultiplyRgb(int color, float tr, float tg, float tb)
    {
        int ir = Math.Clamp((int)((color & 0xFF) * tr + 0.5f), 0, 255);
        int ig = Math.Clamp((int)(((color >> 8) & 0xFF) * tg + 0.5f), 0, 255);
        int ib = Math.Clamp((int)(((color >> 16) & 0xFF) * tb + 0.5f), 0, 255);
        return unchecked((int)0xFF000000) | ib << 16 | ig << 8 | ir;
    }

    /// <summary>
    /// Repaint baked entries after a calendar month change. <paramref name="untintedOf"/>
    /// must return the same stable untinted colour capture uses (atlas mean / topsoil composite).
    /// </summary>
    public static int RebakeSection(
        IClientWorldAccessor world,
        LodSection section,
        long sectionKey,
        Block? plantTintFallback,
        System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf)
    {
        int changed = 0;
        for (int pid = 0; pid < section.Palette.Count; pid++)
        {
            LodPaletteEntry entry = section.Palette[pid];
            if ((entry.Flags & LodPaletteEntry.FlagBaked) == 0) continue;
            if (entry.BlockId <= 0) continue;
            if (!section.TryFindPaletteTop(sectionKey, pid, out int x, out int y, out int z)) continue;

            Block block = world.Blocks[entry.BlockId];
            (int untinted, LodUntintedShare share) = untintedOf(block);
            int baked = BakePaletteColor(world, block, untinted, x, y, z, share, plantTintFallback);
            if (baked == entry.Color) continue;
            entry.Color = baked;
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

    /// <summary>
    /// True when a section still uses the pre-0.7.79 live-tint path (untinted palette +
    /// non-zero tint slot) and should be upgraded on load or budgeted repaint.
    /// </summary>
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
    /// Repaint baked entries and upgrade legacy live-tint rows to discover-baked palettes.
    /// </summary>
    public static int HealOrRepaintSection(
        IClientWorldAccessor world,
        LodSection section,
        long sectionKey,
        Block? plantTintFallback,
        System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf)
    {
        int changed = RebakeSection(world, section, sectionKey, plantTintFallback, untintedOf);
        changed += UpgradeLegacyEntries(world, section, sectionKey, plantTintFallback, untintedOf);
        return changed;
    }

    /// <summary>
    /// Discover-bake legacy caches that still carry a live tint slot. Runs on revisit and
    /// during the budgeted month repaint so players do not need to delete their cache.
    /// </summary>
    public static int UpgradeLegacyEntries(
        IClientWorldAccessor world,
        LodSection section,
        long sectionKey,
        Block? plantTintFallback,
        System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf)
    {
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

            int baked = BakePaletteColor(world, block, untinted, x, y, z, share, plantTintFallback);
            entry.Color = baked;
            entry.Flags = (byte)(entry.Flags | LodPaletteEntry.FlagBaked);
            entry.TintSlot = LodTintRegistry.SlotNone;
            section.Palette[pid] = entry;
            changed++;
        }
        if (changed > 0) section.InvalidatePaletteSnapshot();
        return changed;
    }
}
