using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Path helpers for vanilla tree canopy. <see cref="ApplyTop"/> is identity:
/// mottled gray crowns made square patches. Leaves keep GetColor plus frost.
/// </summary>
public static class LodCanopyGray
{
    public const int GrayR = 152;
    public const int GrayG = 154;
    public const int GrayB = 158;
    public const float MottleThreshold = 0.48f;
    public const float MixMin = 0.38f;
    public const float MixMax = 0.70f;

    public static int GrayPacked => LodSurfaceMix.Pack(GrayR, GrayG, GrayB);

    static int dbgSamples;

    public static bool IsVanillaTreeCanopy(Block? block)
    {
        if (block == null || block.BlockId == 0) return false;
        string? path = block.Code?.Path;
        if (IsExcludedPath(path)) return false;
        if (block.BlockMaterial == EnumBlockMaterial.Leaves) return true;
        return IsCanopyPath(path);
    }

    public static bool IsVanillaTreeCanopyPath(string? path)
    {
        if (IsExcludedPath(path)) return false;
        return IsCanopyPath(path);
    }

    /// <summary>
    /// Tree leaves and berry bushes that take live GetColor + FlagFrost.
    /// Tallgrass / flowers stay on the plant ground path (no frost canopy bit).
    /// </summary>
    public static bool IsSeasonFoliage(Block? block)
    {
        if (block == null || block.BlockId == 0) return false;
        string? path = block.Code?.Path;
        if (IsExcludedSeasonFoliagePath(path)) return false;
        if (block.BlockMaterial == EnumBlockMaterial.Leaves) return true;
        return IsCanopyPath(path) || IsBushPath(path);
    }

    public static bool IsSeasonFoliagePath(string? path)
    {
        if (IsExcludedSeasonFoliagePath(path)) return false;
        return IsCanopyPath(path) || IsBushPath(path);
    }

    public static bool IsBushPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return Has(path, "bush") || Has(path, "shrub");
    }

    public static bool IsExcludedPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return Has(path, "tallgrass")
            || Has(path, "snowlayer")
            || Has(path, "fallen")
            || Has(path, "sapling")
            || Has(path, "bush")
            || Has(path, "shrub")
            || IsFlowerPath(path)
            || Has(path, "fern")
            || Has(path, "vine")
            || Has(path, "cattail")
            || Has(path, "reed");
    }

    /// <summary>Like <see cref="IsExcludedPath"/> but bushes and shrubs are foliage.</summary>
    public static bool IsExcludedSeasonFoliagePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return Has(path, "tallgrass")
            || Has(path, "snowlayer")
            || Has(path, "fallen")
            || Has(path, "sapling")
            || IsFlowerPath(path)
            || Has(path, "fern")
            || Has(path, "vine")
            || Has(path, "cattail")
            || Has(path, "reed");
    }

    /// <summary>
    /// Vanilla flower-* codes, not berrybush-*-flowering (Contains "flower" alone matches that).
    /// </summary>
    public static bool IsFlowerPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return Has(path, "flower") && !Has(path, "flowering");
    }

    public static bool IsCanopyPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return Has(path, "leaves")
            || Has(path, "pine")
            || Has(path, "conifer")
            || Has(path, "needles");
    }

    /// <summary>
    /// Visual crown of this column: no canopy one or two blocks above.
    /// Unloaded neighbours read as air and count as crown so overlay leftovers
    /// still mottled the same way as a walk remesh.
    /// </summary>
    public static bool IsCanopyCrown(IBlockAccessor acc, int x, int y, int z)
    {
        return !CanopyAt(acc, x, y + 1, z) && !CanopyAt(acc, x, y + 2, z);
    }

    /// <summary>
    /// 4-block blobs plus per-cell noise. Same (x,z) always the same amount.
    /// </summary>
    public static float MottleAmount(int x, int z)
    {
        float blob = Hash01(x >> 2, z >> 2);
        float cell = Hash01(x, z);
        return blob * 0.65f + cell * 0.35f;
    }

    public static float MixTowardGray(float mottle, float frostWeight)
    {
        if (mottle < MottleThreshold || frostWeight <= 0f) return 0f;
        float span = 1f - MottleThreshold;
        float t = span <= 0f ? 1f : Math.Clamp((mottle - MottleThreshold) / span, 0f, 1f);
        return (MixMin + t * (MixMax - MixMin)) * Math.Clamp(frostWeight, 0f, 1f);
    }

    public static int MixRgb(int seasonRgb, float mix)
    {
        if (mix <= 0f || seasonRgb == 0) return seasonRgb;
        LodSurfaceMix.Unpack(seasonRgb, out int r, out int g, out int b);
        return LodSurfaceMix.Pack(
            (int)(r * (1f - mix) + GrayR * mix + 0.5f),
            (int)(g * (1f - mix) + GrayG * mix + 0.5f),
            (int)(b * (1f - mix) + GrayB * mix + 0.5f));
    }

    public static int ApplyTop(IClientWorldAccessor world, Block block, BlockPos pos, int seasonRgb)
    {
        // Reverted: mottled gray crowns made square tree-top patches. Leaves
        // keep live GetColor + frost from SampleVanillaColor.
        _ = world;
        _ = block;
        _ = pos;
        return seasonRgb;
    }

    static bool CanopyAt(IBlockAccessor acc, int x, int y, int z)
    {
        try
        {
            var pos = new BlockPos(x, y, z);
            return IsVanillaTreeCanopy(acc.GetBlock(pos));
        }
        catch
        {
            return false;
        }
    }

    static bool Has(string path, string token) =>
        path.Contains(token, StringComparison.Ordinal);

    static float Hash01(int x, int z)
    {
        unchecked
        {
            uint n = (uint)(x * 374761393 + z * 668265263);
            n = (n ^ (n >> 13)) * 1274126177u;
            n ^= n >> 16;
            return (n & 65535u) / 65535f;
        }
    }

    const bool AgentDiskLog = false;

    static void LogSample(
        Block block, BlockPos pos, int before, int after,
        float mottle, float frostW, float mix)
    {
        if (!AgentDiskLog) return;
        if (dbgSamples >= 12) return;
        dbgSamples++;
        LodSurfaceMix.Unpack(before, out int br, out int bg, out int bb);
        LodSurfaceMix.Unpack(after, out int ar, out int ag, out int ab);
        string path = (block.Code?.Path ?? "").Replace("\\", "/").Replace("\"", "'");
        string visit = (LodSeasonBake.DebugVisitKind ?? "unknown").Replace("\\", "/").Replace("\"", "'");
        try
        {
            File.AppendAllText(
                @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                "{\"sessionId\":\"40cccb\",\"runId\":\"gray-top-1\",\"hypothesisId\":\"H-GRAY-TOP\",\"location\":\"LodCanopyGray.ApplyTop\",\"message\":\"canopy-gray\",\"data\":{\"path\":\""
                + path + "\",\"visit\":\"" + visit
                + "\",\"mottle\":" + mottle.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"frostW\":" + frostW.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"mix\":" + mix.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"crown\":true"
                + ",\"beforeR\":" + br + ",\"beforeG\":" + bg + ",\"beforeB\":" + bb
                + ",\"afterR\":" + ar + ",\"afterG\":" + ag + ",\"afterB\":" + ab
                + ",\"x\":" + pos.X + ",\"y\":" + pos.Y + ",\"z\":" + pos.Z
                + "},\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n");
        }
        catch { }
    }
}
