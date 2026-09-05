using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// How a block is drawn, or whether it is drawn at all. Shared by both sides: a section
/// captured by a server and one captured by a client have to agree about what counts as
/// terrain, or the same ground would come out differently depending on who saw it first.
///
/// Which tint applies is a separate question, answered client-side by LodTintRegistry.
/// </summary>
public static class LodBlockPolicy
{
    public static byte FlagsFor(Block block)
    {
        if (block.BlockMaterial is EnumBlockMaterial.Water or EnumBlockMaterial.Lava)
        {
            return LodPaletteEntry.FlagWater;
        }

        // Frozen lakes stay a water surface. Glacier / packed ice is an opaque
        // snow-like layer (FlagSnow), not see-through water.
        if (block.BlockMaterial == EnumBlockMaterial.Ice)
        {
            return IsLakeIce(block) ? LodPaletteEntry.FlagWater : LodPaletteEntry.FlagSnow;
        }

        // Sparse, mostly-transparent ground cover. As solid LOD cubes these come out as
        // pale grey blobs, because their textures average toward the transparent pixels.
        // Not all of EnumBlockMaterial.Plant: skipping every plant was tried and
        // flattened the landscape, and dense cover like grass reads fine as solid colour.
        // The Plant guard also keeps ferntree (material Wood, an actual tree) opaque.
        // Leaf Litter (modid leaflitter, companion to Deciduous Trees) piles are the same
        // class: low plant mats. As FlagThin they keep the brown carpet through L0/L1
        // instead of solid leaf cubes.
        if (block.BlockMaterial == EnumBlockMaterial.Plant && IsThinGroundCover(block))
        {
            return LodPaletteEntry.FlagThin;
        }

        // Not terrain at all, so it never becomes geometry.
        if (block.BlockMaterial is EnumBlockMaterial.Fire or EnumBlockMaterial.Meta)
        {
            return LodPaletteEntry.FlagSkip;
        }

        // Real snow layer (atlas white). Painted winter frost on soil/grass is FlagBaked
        // colour only — do not set FlagSnow for that.
        if (IsSnowLayer(block)) return LodPaletteEntry.FlagSnow;

        return 0;
    }

    /// <summary>
    /// Block is an actual snow/ice layer you would see from above — not frost paint.
    /// </summary>
    public static bool IsSnowLayer(Block block)
    {
        if (block.BlockMaterial == EnumBlockMaterial.Snow) return true;
        if (block.BlockMaterial == EnumBlockMaterial.Ice) return !IsLakeIce(block);
        string? path = block.Code?.Path;
        if (path == null) return false;
        return path.StartsWith("snow", StringComparison.Ordinal)
            || path.StartsWith("glacier", StringComparison.Ordinal)
            || path.Contains("glacierice", StringComparison.Ordinal)
            || path.Contains("glacialice", StringComparison.Ordinal)
            || path.Contains("packedice", StringComparison.Ordinal);
    }

    /// <summary>
    /// How much real snow sits on this cell (0 = none, 1 = full snowblock / height-7).
    /// Driven by vanilla <c>snowlayer-1..7</c> height — the same depth you see up close.
    /// </summary>
    public static float SnowCover01(Block block)
    {
        if (!IsSnowLayer(block)) return 0f;
        if (block.BlockMaterial == EnumBlockMaterial.Ice && !IsLakeIce(block)) return 1f;
        string? path = block.Code?.Path;
        if (path == null) return 1f;
        if (path.StartsWith("snowblock", StringComparison.Ordinal)) return 1f;
        // snowlayer-N → N/7. Any layer still reads as snow from the sky.
        if (path.StartsWith("snowlayer-", StringComparison.Ordinal)
            && int.TryParse(path.AsSpan("snowlayer-".Length), out int h))
            return GameMath.Clamp(h / 7f, 1f / 7f, 1f);
        return 1f;
    }

    /// <summary>
    /// Snow and ice already carry their colour in the atlas. A climate slot
    /// on those blocks multiplies valley grass onto white/cyan albedo.
    /// </summary>
    public static bool IsClimateUntinted(Block block)
    {
        if (block.BlockMaterial == EnumBlockMaterial.Snow) return true;
        if (block.BlockMaterial == EnumBlockMaterial.Ice) return true;
        string? path = block.Code?.Path;
        if (path == null) return false;
        return path.StartsWith("snow", StringComparison.Ordinal)
            || path.StartsWith("glacier", StringComparison.Ordinal)
            || path.StartsWith("lakeice", StringComparison.Ordinal)
            || path.Contains("glacierice", StringComparison.Ordinal)
            || path.Contains("glacialice", StringComparison.Ordinal)
            || path.Contains("packedice", StringComparison.Ordinal);
    }

    static bool IsLakeIce(Block block)
    {
        string? path = block.Code?.Path;
        if (path == null) return true;
        return path.Contains("lakeice", StringComparison.Ordinal)
            || path.StartsWith("lake", StringComparison.Ordinal);
    }

    static readonly string[] ThinGroundCoverPrefixes = { "flower", "fern", "tallfern", "leaflitter" };

    static bool IsThinGroundCover(Block block)
    {
        string? path = block.Code?.Path;
        if (path == null) return false;

        foreach (string prefix in ThinGroundCoverPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
