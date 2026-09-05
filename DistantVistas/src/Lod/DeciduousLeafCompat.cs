using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Soft bridge to Tritan20 Deciduous Trees (<c>deciduoustrees</c> / assembly
/// <c>deciduousleaves</c>). No compile-time reference: when the mod is absent every
/// call is a no-op and capture keeps every leaf.
///
/// Deciduous hides canopy without removing blocks. LOD capture reads block ids, so
/// without this bridge winter LOD kept a full canopy blob. Their
/// <c>LeafDormancy.ActionAt</c> returns Visible / Collapse / Fan:
/// Collapse = fully hidden leaf → omit from LOD; Fan = branch mesh on branchy leaves →
/// keep (wood/branch structure); wood and non-leaf branch blocks are never omitted.
/// Falling leaf particles are not terrain and stay out of LOD.
/// </summary>
public static class DeciduousLeafCompat
{
    public const string ModId = "deciduoustrees";

    static bool bound;
    static bool present;
    static MethodInfo? actionAt;
    static object? collapseAct;

    public static bool Present => present;

    /// <summary>Resolve Deciduous once per side. Safe when the mod is missing.</summary>
    public static void Bind(ICoreAPI api)
    {
        bound = true;
        present = false;
        actionAt = null;
        collapseAct = null;

        try
        {
            if (!api.ModLoader.IsModEnabled(ModId)) return;
        }
        catch
        {
            return;
        }

        Type? dormancy = null;
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                dormancy = asm.GetType("DecidousLeaves.LeafDormancy");
            }
            catch
            {
                continue;
            }
            if (dormancy != null) break;
        }

        if (dormancy == null) return;

        MethodInfo? method = dormancy.GetMethod(
            "ActionAt",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(AssetLocation), typeof(BlockPos), typeof(bool) },
            modifiers: null);
        if (method == null) return;

        Type? actEnum = dormancy.GetNestedType("LeafAct", BindingFlags.Public);
        if (actEnum == null || !actEnum.IsEnum) return;

        object? collapse = null;
        foreach (object value in Enum.GetValues(actEnum))
        {
            if (string.Equals(value.ToString(), "Collapse", StringComparison.Ordinal))
            {
                collapse = value;
                break;
            }
        }
        if (collapse == null) return;

        actionAt = method;
        collapseAct = collapse;
        present = true;
        api.Logger.Notification(
            "[DistantVistas] Deciduous Trees detected: dormant leaf cells omit from LOD; "
            + "Fan branchy leaves and wood stay.");
    }

    /// <summary>
    /// True when this block run should not enter the LOD column: a Deciduous leaf cell
    /// whose ActionAt is Collapse. Wood, Eco Machina branches, Fan branchy leaves, and
    /// every block when Deciduous is absent, return false.
    /// </summary>
    public static bool ShouldOmitLeafRun(Block block, BlockPos pos)
    {
        if (!bound || !present || actionAt == null || collapseAct == null) return false;
        if (block?.Code == null) return false;
        if (!IsDeciduousLeafBlock(block)) return false;

        bool isBranchy = block.Code.Path.Contains("branchy", StringComparison.Ordinal);
        try
        {
            object? act = actionAt.Invoke(null, new object[] { block.Code, pos, isBranchy });
            return act != null && collapseAct.Equals(act);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Leaf canopy Deciduous can hide. Wood trunks and non-leaf branch blocks are
    /// deliberately excluded so bare winter trees keep structure.
    /// </summary>
    public static bool IsDeciduousLeafBlock(Block block)
    {
        if (block.BlockMaterial == EnumBlockMaterial.Wood) return false;
        string? path = block.Code?.Path;
        if (path == null) return false;
        if (path.Contains("log", StringComparison.Ordinal)
            || path.Contains("trunk", StringComparison.Ordinal))
        {
            return false;
        }
        // Eco Machina / similar branch blocks — keep.
        if (path.Contains("branch", StringComparison.Ordinal)
            && !path.Contains("leaves", StringComparison.Ordinal))
        {
            return false;
        }
        return path.Contains("leaves", StringComparison.Ordinal)
            || (path.Contains("leaf", StringComparison.Ordinal)
                && !path.StartsWith("leaflitter", StringComparison.Ordinal));
    }

    /// <summary>True when the section palette names any leaf canopy entry.</summary>
    public static bool SectionHasLeafPalette(LodSection section, IWorldAccessor world)
    {
        for (int i = 0; i < section.Palette.Count; i++)
        {
            int id = section.Palette[i].BlockId;
            if (id <= 0 || id >= world.Blocks.Count) continue;
            Block? block = world.Blocks[id];
            if (block != null && IsDeciduousLeafBlock(block)) return true;
        }
        return false;
    }
}
