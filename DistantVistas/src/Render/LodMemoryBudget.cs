namespace DistantVistas;

/// <summary>
/// Size the keep-circle from this PC's RAM. Mesh count alone is never a reason
/// to drop GPU meshes — only real FPS / working-set pressure may. When pressure
/// fires, the keep ring is 2× view distance (see PressureKeepScale).
/// </summary>
public static class LodMemoryBudget
{
    public const float MinKeepScale = 1.25f;
    public const float DefaultKeepScale = 2.0f;
    public const float MaxKeepScale = 3.0f;

    /// <summary>
    /// Pressure eviction may only drop L0/L1 outside this multiple of view distance.
    /// Inside the ring, visited land stays drawn.
    /// </summary>
    public const float PressureKeepScale = 2.0f;

    /// <summary>Soft hint for telemetry / companion yield — not a hard eviction cap.</summary>
    public static long TotalPhysicalBytes { get; private set; }
    public static float KeepScale { get; private set; } = DefaultKeepScale;
    public static int MaxResidentMeshes { get; private set; } = 2500;

    /// <summary>Managed heap (MB) above which memory can count toward pressure on this box.</summary>
    public static long ManagedPressureMb { get; private set; } = 24_000;

    /// <summary>Frame time (ms) sustained above this counts as FPS pressure (~30 FPS).</summary>
    public const double FramePressureMs = 33.0;

    /// <summary>Hitch spike (ms) that, with high managed memory, can tip into pressure.</summary>
    public const double HitchStormMs = 50.0;

    public static void Probe()
    {
        TotalPhysicalBytes = ReadTotalPhysical();
        double gb = TotalPhysicalBytes / (1024.0 * 1024.0 * 1024.0);
        if (gb <= 0) gb = 16;

        // KeepScale stays at 2× so the pin ring matches PressureKeepScale.
        // MaxResidentMeshes is only a soft telemetry / yield hint — never a hard drop.
        if (gb < 8)
        {
            KeepScale = 1.25f;
            MaxResidentMeshes = 800;
            ManagedPressureMb = 3_500;
        }
        else if (gb < 12)
        {
            KeepScale = 1.5f;
            MaxResidentMeshes = 1400;
            ManagedPressureMb = 6_000;
        }
        else if (gb < 20)
        {
            KeepScale = 2.0f;
            MaxResidentMeshes = 2500;
            ManagedPressureMb = 10_000;
        }
        else
        {
            // 64 GB boxes routinely hold many thousands of meshes; that is normal.
            KeepScale = PressureKeepScale;
            MaxResidentMeshes = 8000;
            // ~half of a 64 GB box before memory alone may count toward pressure.
            ManagedPressureMb = (long)(gb * 1024 * 0.55);
            if (ManagedPressureMb < 28_000) ManagedPressureMb = 28_000;
        }
    }

    /// <summary>
    /// Keep-circle scale for pinning. Never shrinks just because many meshes are live.
    /// </summary>
    public static float LiveKeepScale(int liveMeshCount)
    {
        _ = liveMeshCount;
        return KeepScale;
    }

    /// <summary>
    /// True when the player is actually hurting: bad frame time, and/or truly high
    /// managed memory (optionally with hitch spikes). Mesh count alone is never enough.
    /// </summary>
    public static bool IsUnderPressure(
        double recentAvgFrameMs,
        double recentP95FrameMs,
        long managedMb,
        int liveMeshCount)
    {
        bool frameBad = recentP95FrameMs >= FramePressureMs
            || recentAvgFrameMs >= FramePressureMs * 0.9;
        bool hitchStorm = recentP95FrameMs >= HitchStormMs;
        bool memHigh = managedMb >= ManagedPressureMb;

        if (frameBad) return true;
        if (memHigh && hitchStorm) return true;
        // Extreme managed memory alone on a potato can still need relief.
        if (memHigh && managedMb >= ManagedPressureMb + ManagedPressureMb / 4) return true;

        // Soft mesh signal only after frame time is already bad (caller may pass frameBad).
        _ = liveMeshCount;
        return false;
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
