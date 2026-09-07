using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Cheap open-ocean LOD fill for login bootstrap: a few real sample visits establish
/// water colour/appearance; remaining open-ocean L0 cells get a flat stamped section so
/// far vistas show water instead of sky holes — without a full teleport+bake per cell.
/// </summary>
public static class LodLoginSweepOceanFill
{
    public const int DefaultSeabedDepth = 18;

    /// <summary>
    /// Stamp flat ocean sections for every open-ocean key using a visited sample template.
    /// </summary>
    public static int StampOpenOcean(
        ICoreClientAPI capi,
        LodPipeline pipeline,
        IReadOnlyList<long> fillKeys,
        IReadOnlyList<long> sampleKeys,
        IReadOnlySet<long> completedKeys)
    {
        if (fillKeys.Count == 0) return 0;

        LodSection? template = FindSampleTemplate(pipeline.World, sampleKeys, completedKeys);
        int sea = capi.World.SeaLevel;
        int stamped = 0;

        foreach (long key in fillKeys)
        {
            if (completedKeys.Contains(key)) continue;
            if (pipeline.World.Sections.TryGetValue(key, out LodSection? existing)
                && existing.CapturedColumns >= LodSection.GridSize * LodSection.GridSize / 2)
                continue;

            LodSection flat = BuildFlatOceanSection(capi, template, sea);
            pipeline.World.Sections[key] = flat;
            pipeline.World.ClassifySparseL0(key, flat);
            pipeline.World.MarkChanged(key);
            pipeline.InvalidateGpuMesh?.Invoke(key);
            stamped++;
        }

        if (stamped > 0)
            pipeline.DrainLoginPersistence(Math.Min(24, stamped));

        return stamped;
    }

    static LodSection? FindSampleTemplate(
        LodWorld world,
        IReadOnlyList<long> sampleKeys,
        IReadOnlySet<long> completedKeys)
    {
        foreach (long key in sampleKeys)
        {
            if (!completedKeys.Contains(key)) continue;
            if (world.Sections.TryGetValue(key, out LodSection? section)
                && section.CapturedColumns > 0)
                return section;
        }

        foreach (long key in sampleKeys)
        {
            if (world.Sections.TryGetValue(key, out LodSection? section)
                && section.CapturedColumns > 0)
                return section;
        }

        return null;
    }

    /// <summary>
    /// Uniform sea-surface section: one water run per column over a shallow seabed.
    /// Palette colours come from a visited sample when available.
    /// </summary>
    public static LodSection BuildFlatOceanSection(
        ICoreClientAPI capi,
        LodSection? template,
        int seaLevel,
        int seabedDepth = DefaultSeabedDepth)
    {
        int seabed = Math.Max(1, seaLevel - seabedDepth);
        ResolvePalette(template, capi, out int waterBlockId, out int waterColor, out byte waterFlags,
            out int bedBlockId, out int bedColor, out byte bedFlags);

        var section = new LodSection();
        int waterPid = section.FindOrAddPaletteEntry(waterBlockId, waterColor, waterFlags);
        int bedPid = section.FindOrAddPaletteEntry(bedBlockId, bedColor, bedFlags);
        ulong[] column =
        {
            LodSection.PackRun(waterPid, seaLevel, seabed),
            LodSection.PackRun(bedPid, seabed, 1),
        };

        int cols = LodSection.GridSize * LodSection.GridSize;
        for (int col = 0; col < cols; col++)
            section.SetColumn(col, column);

        return section;
    }

    static void ResolvePalette(
        LodSection? template,
        ICoreClientAPI capi,
        out int waterBlockId,
        out int waterColor,
        out byte waterFlags,
        out int bedBlockId,
        out int bedColor,
        out byte bedFlags)
    {
        waterBlockId = 0;
        waterColor = 0x00204880;
        waterFlags = LodPaletteEntry.FlagWater;
        bedBlockId = 0;
        bedColor = 0x00404048;
        bedFlags = 0;

        if (template != null)
        {
            if (TryFirstPalette(template, LodPaletteEntry.FlagWater,
                    out waterBlockId, out waterColor, out waterFlags))
            {
                if (!TryFirstPalette(template, 0, out bedBlockId, out bedColor, out bedFlags, skipWater: true))
                    TryResolveBedBlock(capi, out bedBlockId, out bedColor, out bedFlags);
                return;
            }
        }

        TryResolveWaterBlock(capi, out waterBlockId, out waterColor, out waterFlags);
        TryResolveBedBlock(capi, out bedBlockId, out bedColor, out bedFlags);
    }

    static bool TryFirstPalette(
        LodSection template,
        byte requiredFlags,
        out int blockId,
        out int color,
        out byte flags,
        bool skipWater = false)
    {
        for (int i = 0; i < template.Palette.Count; i++)
        {
            LodPaletteEntry e = template.Palette[i];
            if (requiredFlags != 0 && (e.Flags & requiredFlags) == 0) continue;
            if (skipWater && (e.Flags & LodPaletteEntry.FlagWater) != 0) continue;
            if (e.BlockId <= 0) continue;
            blockId = e.BlockId;
            color = e.Color;
            flags = e.Flags;
            return true;
        }

        blockId = 0;
        color = 0;
        flags = 0;
        return false;
    }

    static bool TryResolveWaterBlock(ICoreClientAPI capi, out int blockId, out int color, out byte flags)
    {
        foreach (Block block in capi.World.Blocks)
        {
            if (block?.Code == null) continue;
            if (LodBlockPolicy.FlagsFor(block) != LodPaletteEntry.FlagWater) continue;
            blockId = block.BlockId;
            color = block.GetColor(capi, new BlockPos());
            if (color == 0) color = 0x00204880;
            flags = LodPaletteEntry.FlagWater;
            return true;
        }

        blockId = 0;
        color = 0x00204880;
        flags = LodPaletteEntry.FlagWater;
        return false;
    }

    static bool TryResolveBedBlock(ICoreClientAPI capi, out int blockId, out int color, out byte flags)
    {
        foreach (Block block in capi.World.Blocks)
        {
            if (block?.Code == null) continue;
            if (block.BlockMaterial != EnumBlockMaterial.Stone
                && block.BlockMaterial != EnumBlockMaterial.Soil
                && block.BlockMaterial != EnumBlockMaterial.Gravel)
                continue;
            blockId = block.BlockId;
            color = block.GetColor(capi, new BlockPos());
            if (color == 0) color = 0x00404048;
            flags = 0;
            return true;
        }

        blockId = 0;
        color = 0x00404048;
        flags = 0;
        return false;
    }
}
