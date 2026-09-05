namespace DistantVistas;

/// <summary>
/// Coarse climate at XZ. Vanilla paints grass, leaves, and bushes from
/// GetClimateAt at that vertex. One keep-origin table painted the whole far
/// field, so the fade snapped green and mountain canopies stayed lime on
/// olive grass. Same sample for every vegetation slot on one hill; the slot
/// table still holds dirt-share dilution and per-species season maps.
/// </summary>
public sealed class LodClimateField
{
    public const int CellBlocks = 40;
    public const int MaxCells = 8192;

    public struct Sample
    {
        public float LowR, LowG, LowB, LowTemp;
        public float HighR, HighG, HighB, HighTemp;
        public bool Filled;
    }

    public static Sample Identity { get; } = new()
    {
        LowR = 1f, LowG = 1f, LowB = 1f, LowTemp = 128f,
        HighR = 1f, HighG = 1f, HighB = 1f, HighTemp = 128f,
        Filled = true
    };

    readonly Dictionary<long, Sample> cells = new();

    public int Count => cells.Count;

    public static long CellKey(int worldX, int worldZ)
    {
        int cx = FloorDiv(worldX, CellBlocks);
        int cz = FloorDiv(worldZ, CellBlocks);
        return ((long)cz << 32) ^ (uint)cx;
    }

    public void Put(int worldX, int worldZ, Sample sample)
    {
        sample.Filled = true;
        cells[CellKey(worldX, worldZ)] = sample;
    }

    public bool TryGet(int worldX, int worldZ, out Sample sample)
    {
        if (cells.TryGetValue(CellKey(worldX, worldZ), out sample) && sample.Filled)
            return true;
        sample = default;
        return false;
    }

    public Sample GetOrKeep(int worldX, int worldZ, in Sample keep) =>
        TryGet(worldX, worldZ, out Sample local) ? local : keep;

    public void Clear() => cells.Clear();

    /// <summary>
    /// Drop cells farther than <paramref name="maxDistBlocks"/> from the camera.
    /// Missing cells fall back to keep climate; filled far land is not rewritten.
    /// </summary>
    public int EvictFar(int camX, int camZ, int maxDistBlocks)
    {
        if (cells.Count == 0) return 0;
        int maxSq = maxDistBlocks <= 0 ? 0 : maxDistBlocks * maxDistBlocks;
        evictScratch.Clear();
        foreach (var kv in cells)
        {
            UnpackCellKey(kv.Key, out int cx, out int cz);
            int mx = cx * CellBlocks + CellBlocks / 2 - camX;
            int mz = cz * CellBlocks + CellBlocks / 2 - camZ;
            if (mx * mx + mz * mz > maxSq) evictScratch.Add(kv.Key);
        }
        foreach (long key in evictScratch) cells.Remove(key);
        return evictScratch.Count;
    }

    readonly List<long> evictScratch = new();

    public static void UnpackCellKey(long key, out int cx, out int cz)
    {
        cx = (int)(uint)key;
        cz = (int)(key >> 32);
    }

    /// <summary>
    /// Shift a keep-origin slot colour by the local/keep climate ratio.
    /// Grass and leaves at the same XZ share this sample.
    /// </summary>
    public static void ApplyLocalClimate(
        float slotR, float slotG, float slotB,
        float keepR, float keepG, float keepB,
        float localR, float localG, float localB,
        out float r, out float g, out float b)
    {
        r = slotR * SafeRatio(localR, keepR);
        g = slotG * SafeRatio(localG, keepG);
        b = slotB * SafeRatio(localB, keepB);
    }

    public static float SafeRatio(float local, float keep)
    {
        if (keep < 0.04f) return 1f;
        float t = local / keep;
        if (t < 0.25f) return 0.25f;
        if (t > 4f) return 4f;
        return t;
    }

    /// <summary>
    /// Bilinear XZ of four corners, then altitude blend. Matches lodterrain.vsh.
    /// </summary>
    public static void Bilinear(
        in Sample s00, in Sample s10, in Sample s01, in Sample s11,
        float u, float v, float yBlend,
        out float r, out float g, out float b, out float temp)
    {
        Sample a = LerpSample(s00, s10, u);
        Sample c = LerpSample(s01, s11, u);
        Sample xz = LerpSample(a, c, v);
        r = Lerp(xz.LowR, xz.HighR, yBlend);
        g = Lerp(xz.LowG, xz.HighG, yBlend);
        b = Lerp(xz.LowB, xz.HighB, yBlend);
        temp = Lerp(xz.LowTemp, xz.HighTemp, yBlend);
    }

    static Sample LerpSample(in Sample a, in Sample b, float v) => new()
    {
        LowR = Lerp(a.LowR, b.LowR, v),
        LowG = Lerp(a.LowG, b.LowG, v),
        LowB = Lerp(a.LowB, b.LowB, v),
        LowTemp = Lerp(a.LowTemp, b.LowTemp, v),
        HighR = Lerp(a.HighR, b.HighR, v),
        HighG = Lerp(a.HighG, b.HighG, v),
        HighB = Lerp(a.HighB, b.HighB, v),
        HighTemp = Lerp(a.HighTemp, b.HighTemp, v),
        Filled = true
    };

    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    static int FloorDiv(int a, int b)
    {
        int q = a / b;
        return ((a ^ b) < 0 && a % b != 0) ? q - 1 : q;
    }
}
