using Vintagestory.API.Common;

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

        // Frozen lakes stay a water surface. Glacier / packed ice is opaque
        // ice, not a see-through lake; drawing it as water plus a grass
        // climate slot painted high ridges as green ice caps.
        if (block.BlockMaterial == EnumBlockMaterial.Ice)
        {
            return IsLakeIce(block) ? LodPaletteEntry.FlagWater : (byte)0;
        }

        // Sparse, mostly-transparent ground cover. As solid LOD cubes these come out as
        // pale grey blobs, because their textures average toward the transparent pixels.
        // Not all of EnumBlockMaterial.Plant: skipping every plant was tried and
        // flattened the landscape, and dense cover like grass reads fine as solid colour.
        // The Plant guard also keeps ferntree (material Wood, an actual tree) opaque.
        if (block.BlockMaterial == EnumBlockMaterial.Plant && IsThinGroundCover(block))
        {
            return LodPaletteEntry.FlagThin;
        }

        // Not terrain at all, so it never becomes geometry.
        if (block.BlockMaterial is EnumBlockMaterial.Fire or EnumBlockMaterial.Meta)
        {
            return LodPaletteEntry.FlagSkip;
        }

        return 0;
    }

    /// <summary>
    /// Snow and ice already carry their colour in the atlas. A climate slot
    /// on those blocks multiplies valley grass onto white/cyan albedo.
    /// </summary>
    public static bool IsClimateUntinted(Block block)
    {
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

    static readonly string[] ThinGroundCoverPrefixes = { "flower", "fern", "tallfern" };

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
