using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Thread-local scratch for login / explore visit bake. Login overlay and explore
/// bake run on the client main thread; scouts never call GetColor from worker threads.
/// Reusing <see cref="BlockPos"/> and column arrays cuts Gen0/LOH churn during the
/// ~4096-column GetColor pass per L0 stop.
/// </summary>
static class LodBakeScratch
{
    [ThreadStatic] static BlockPos? pos;
    [ThreadStatic] static Block?[]? tops;
    [ThreadStatic] static int[]? topY;
    [ThreadStatic] static bool[]? frostCol;

    public static BlockPos Pos(int x, int y, int z)
    {
        BlockPos p = pos ??= new BlockPos();
        p.Set(x, y, z);
        return p;
    }

    public static void RentColumnMeta(int cols, out Block?[] topBlocks, out int[] topYs, out bool[] frostFlags)
    {
        if (tops == null || tops.Length < cols)
        {
            tops = new Block?[cols];
            topY = new int[cols];
            frostCol = new bool[cols];
        }

        topBlocks = tops;
        topYs = topY!;
        frostFlags = frostCol!;
        Array.Clear(topBlocks, 0, cols);
        Array.Clear(topYs, 0, cols);
        Array.Clear(frostFlags, 0, cols);
    }
}
