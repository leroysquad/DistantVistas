using System.Buffers;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Thread-local scratch for login / explore visit bake. Login overlay and explore
/// bake run on the client main thread; scouts never call GetColor from worker threads.
/// Reusing <see cref="BlockPos"/> and pooling column arrays cuts Gen0/LOH churn during
/// the ~4096-column GetColor pass per L0 stop. Per-section texture-mean cache avoids
/// 8× <c>GetColorWithoutTint</c> on the same BlockId across 4096 columns.
/// See docs/plans/login-bake-efficiency.md (ArrayPool / Span scratch).
/// </summary>
static class LodBakeScratch
{
    [ThreadStatic] static BlockPos? pos;
    [ThreadStatic] static Block?[]? tops;
    [ThreadStatic] static int[]? topY;
    [ThreadStatic] static bool[]? frostCol;
    [ThreadStatic] static Dictionary<int, int>? texMeanByBlockId;
    [ThreadStatic] static Dictionary<long, int>? getColorByKey;
    [ThreadStatic] static int getColorCalls;
    [ThreadStatic] static int texMeanScope;

    /// <summary>Scalar GetColor calls this section bake (cache misses only).</summary>
    public static int SectionGetColorCalls => getColorCalls;

    /// <summary>
    /// 8×8 climate tile + block id + Y band. GetColor is stable within a tile for
    /// the same block type; reuses samples across the 4096-column L0 pass.
    /// </summary>
    public static long GetColorCacheKey(int blockId, int x, int y, int z) =>
        ((long)blockId << 32)
        | ((long)(x >> 3 & 0xFFFF) << 16)
        | (long)(z >> 3 & 0xFFFF)
        | ((long)(y & 0xFF) << 48);

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

    /// <summary>
    /// Open a per-section <see cref="LodSeasonBake.SampleTextureMean"/> cache keyed
    /// by BlockId. Grass overlay still averages 8 random pixels once; later columns
    /// of the same block reuse that mean.
    /// </summary>
    public static void BeginSectionTextureMeans()
    {
        texMeanByBlockId ??= new Dictionary<int, int>(128);
        texMeanByBlockId.Clear();
        getColorByKey ??= new Dictionary<long, int>(4096);
        getColorByKey.Clear();
        getColorCalls = 0;
        texMeanScope++;
    }

    public static void EndSectionTextureMeans()
    {
        if (texMeanScope > 0) texMeanScope--;
        texMeanByBlockId?.Clear();
        getColorByKey?.Clear();
        getColorCalls = 0;
    }

    public static void NoteGetColorCall() => getColorCalls++;

    public static bool TryGetSectionGetColor(int blockId, int x, int y, int z, out int rgb)
    {
        if (texMeanScope > 0 && getColorByKey != null)
            return getColorByKey.TryGetValue(GetColorCacheKey(blockId, x, y, z), out rgb);
        rgb = 0;
        return false;
    }

    public static void RememberSectionGetColor(int blockId, int x, int y, int z, int rgb)
    {
        if (texMeanScope <= 0 || rgb == 0) return;
        getColorByKey ??= new Dictionary<long, int>(4096);
        getColorByKey[GetColorCacheKey(blockId, x, y, z)] = rgb;
    }

    public static bool TryGetSectionTextureMean(int blockId, out int rgb)
    {
        if (texMeanScope > 0 && texMeanByBlockId != null)
            return texMeanByBlockId.TryGetValue(blockId, out rgb);
        rgb = 0;
        return false;
    }

    public static void RememberSectionTextureMean(int blockId, int rgb)
    {
        if (texMeanScope <= 0) return;
        texMeanByBlockId ??= new Dictionary<int, int>(128);
        texMeanByBlockId[blockId] = rgb;
    }
}
