using Vintagestory.API.Common;

namespace DistantVistas.Checks;

/// <summary>
/// Soft Deciduous leaf classification without loading Tritan20's DLL. Omit semantics
/// stay false until Bind succeeds against a live LeafDormancy.ActionAt.
/// </summary>
public static class DeciduousLeafChecks
{
    public static void Run(Check c)
    {
        Classification(c);
        OmitWithoutBind(c);
    }

    static void Classification(Check c)
    {
        c.True(DeciduousLeafCompat.IsDeciduousLeafBlock(Plant("leaves-grown-birch-green")),
            "birch leaves are deciduous canopy");
        c.True(DeciduousLeafCompat.IsDeciduousLeafBlock(Plant("leavesbranchy-grown-oak")),
            "branchy oak leaves are deciduous canopy (Fan keeps them)");
        c.True(DeciduousLeafCompat.IsDeciduousLeafBlock(Plant("leaf-grown-maple")),
            "singular leaf-* canopy counts");

        c.False(DeciduousLeafCompat.IsDeciduousLeafBlock(Wood("log-grown-oak-ud")),
            "wood trunks are never omitted as leaves");
        c.False(DeciduousLeafCompat.IsDeciduousLeafBlock(Plant("leaflitter-3")),
            "Leaf Litter piles are ground cover, not canopy");
        c.False(DeciduousLeafCompat.IsDeciduousLeafBlock(Plant("branch-grown-oak")),
            "non-leaf branch blocks stay for winter structure");
    }

    static void OmitWithoutBind(Check c)
    {
        c.False(DeciduousLeafCompat.Present, "checks run without Deciduous assembled");
        c.False(
            DeciduousLeafCompat.ShouldOmitLeafRun(Plant("leaves-grown-birch-green"), new Vintagestory.API.MathTools.BlockPos(0, 100, 0)),
            "without Bind, no leaf run is omitted");
    }

    static Block Plant(string path) => new()
    {
        BlockMaterial = EnumBlockMaterial.Plant,
        Code = new AssetLocation("game", path),
    };

    static Block Wood(string path) => new()
    {
        BlockMaterial = EnumBlockMaterial.Wood,
        Code = new AssetLocation("game", path),
    };
}
