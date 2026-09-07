using System.Buffers;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Thread-local scratch for login / explore visit bake. Login overlay and explore
/// bake run on the client main thread; scouts never call GetColor from worker threads.
/// Reusing <see cref="BlockPos"/> and pooling column arrays cuts Gen0/LOH churn during
/// the ~4096-column GetColor pass per L0 stop.
/// See docs/plans/login-bake-efficiency.md (ArrayPool / Span scratch).
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
            if (tops != null)
                ArrayPool<Block?>.Shared.Return(tops, clearArray: true);
            if (topY != null)
                ArrayPool<int>.Shared.Return(topY, clearArray: true);
            if (frostCol != null)
                ArrayPool<bool>.Shared.Return(frostCol, clearArray: true);
            tops = ArrayPool<Block?>.Shared.Rent(cols);
            topY = ArrayPool<int>.Shared.Rent(cols);
            frostCol = ArrayPool<bool>.Shared.Rent(cols);
        }

        topBlocks = tops;
        topYs = topY!;
        frostFlags = frostCol!;
        Array.Clear(topBlocks, 0, cols);
        Array.Clear(topYs, 0, cols);
        Array.Clear(frostFlags, 0, cols);
    }
}
