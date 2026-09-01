namespace DistantVistas;

/// <summary>
/// Size the keep-circle from this PC's RAM so a huge visited cache cannot
/// pin more GPU meshes than the machine can relaunch with.
/// </summary>
public static class LodMemoryBudget
{
    public const float MinKeepScale = 1.25f;
    public const float DefaultKeepScale = 2.0f;
    public const float MaxKeepScale = 3.0f;

    public static long TotalPhysicalBytes { get; private set; }
    public static float KeepScale { get; private set; } = DefaultKeepScale;
    public static int MaxResidentMeshes { get; private set; } = 2500;

    public static void Probe()
    {
        TotalPhysicalBytes = ReadTotalPhysical();
        double gb = TotalPhysicalBytes / (1024.0 * 1024.0 * 1024.0);
        if (gb <= 0) gb = 16;

        if (gb < 8)
        {
            KeepScale = 1.25f;
            MaxResidentMeshes = 800;
        }
        else if (gb < 12)
        {
            KeepScale = 1.5f;
            MaxResidentMeshes = 1400;
        }
        else if (gb < 20)
        {
            KeepScale = 2.0f;
            MaxResidentMeshes = 2500;
        }
        else if (gb < 32)
        {
            KeepScale = 2.5f;
            MaxResidentMeshes = 4000;
        }
        else
        {
            KeepScale = 3.0f;
            MaxResidentMeshes = 6000;
        }
    }

    /// <summary>
    /// If live GPU meshes are over budget, shrink the keep-circle toward
    /// MinKeepScale. Never smaller than vanilla view distance * 1.25.
    /// </summary>
    public static float LiveKeepScale(int liveMeshCount)
    {
        if (liveMeshCount <= MaxResidentMeshes) return KeepScale;
        float over = liveMeshCount / (float)MaxResidentMeshes;
        return Math.Clamp(KeepScale / over, MinKeepScale, KeepScale);
    }

    static long ReadTotalPhysical()
    {
        try
        {
            var info = GC.GetGCMemoryInfo();
            if (info.TotalAvailableMemoryBytes > 0) return info.TotalAvailableMemoryBytes;
        }
        catch
        {
        }
        return 16L * 1024 * 1024 * 1024;
    }
}
