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

    /// <summary>
    /// Soft hint for demand-resident section heap. Never dumps SQLite into RAM on
    /// join; under mesh pressure only, cold spill may use a larger budget when over this.
    /// </summary>
    public static int MaxResidentSections { get; private set; } = 4000;

    /// <summary>Managed heap (MB) above which memory can count toward pressure on this box.</summary>
    public static long ManagedPressureMb { get; private set; } = 24_000;

    /// <summary>
    /// Enter frame pressure only when p95 is at/above this (~25 FPS). Raised from 33ms so
    /// normal ~30 FPS HD play does not permanently thrash.
    /// </summary>
    public const double FramePressureEnterP95Ms = 40.0;

    /// <summary>Enter when rolling average is at/above this.</summary>
    public const double FramePressureEnterAvgMs = 37.0;

    /// <summary>Clear hysteresis: p95 must stay below this (~40 FPS).</summary>
    public const double FramePressureClearP95Ms = 25.0;

    /// <summary>Clear hysteresis: average must stay below this.</summary>
    public const double FramePressureClearAvgMs = 28.0;

    /// <summary>How long (ms of wall/frame delta) enter signals must persist before pressure opens.</summary>
    public const double PressureEnterSustainMs = 1500.0;

    /// <summary>How long clear signals must persist before pressure closes.</summary>
    public const double PressureClearSustainMs = 2500.0;

    /// <summary>Hitch spike (ms) that, with high managed memory, can tip into pressure.</summary>
    public const double HitchStormMs = 50.0;
    // Legacy alias used by soft-mesh hint comparisons that want the enter bar.
    public const double FramePressureMs = FramePressureEnterP95Ms;

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
            MaxResidentSections = 1500;
            ManagedPressureMb = 3_500;
        }
        else if (gb < 12)
        {
            KeepScale = 1.5f;
            MaxResidentMeshes = 1400;
            MaxResidentSections = 2500;
            ManagedPressureMb = 6_000;
        }
        else if (gb < 20)
        {
            KeepScale = 2.0f;
            MaxResidentMeshes = 2500;
            MaxResidentSections = 3200;
            ManagedPressureMb = 4_500;
        }
        else
        {
            // 64 GB boxes routinely hold many thousands of meshes; that is normal.
            KeepScale = PressureKeepScale;
            MaxResidentMeshes = 8000;
            MaxResidentSections = 8000;
            // Soft managed bar: multi-GB heaps (telemetry ~4–6 GB) must still count
            // toward pressure so soft-cap spill can run. Was 28 GB — never fired.
            ManagedPressureMb = (long)(gb * 1024 * 0.12);
            if (ManagedPressureMb < 4_500) ManagedPressureMb = 4_500;
            if (ManagedPressureMb > 12_000) ManagedPressureMb = 12_000;
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
    /// <summary>True when frame times cross the enter bar (no hysteresis).</summary>
    public static bool IsFrameEnterSignal(double recentAvgFrameMs, double recentP95FrameMs)
    {
        return recentP95FrameMs >= FramePressureEnterP95Ms
            || recentAvgFrameMs >= FramePressureEnterAvgMs;
    }
    /// <summary>True when frame times are comfortably under the clear bar.</summary>
    public static bool IsFrameClearSignal(double recentAvgFrameMs, double recentP95FrameMs)
    {
        return recentP95FrameMs < FramePressureClearP95Ms
            && recentAvgFrameMs < FramePressureClearAvgMs;
    }
    /// <summary>
    /// Memory / hitch raw pressure (no hysteresis). Mesh count alone is never enough.
    /// </summary>
    public static bool IsMemoryPressure(double recentP95FrameMs, long managedMb)
    {
        bool hitchStorm = recentP95FrameMs >= HitchStormMs;
        bool memHigh = managedMb >= ManagedPressureMb;
        if (memHigh && hitchStorm) return true;
        if (memHigh && managedMb >= ManagedPressureMb + ManagedPressureMb / 4) return true;
        return false;
    }
    /// <summary>
    /// Instantaneous "hurting" signal without enter/clear hysteresis.
    /// Callers that need latch behaviour must sustain this via UpdateMeshPressure.
    /// Mesh count alone is never enough.
    /// </summary>
    public static bool IsUnderPressure(
        double recentAvgFrameMs,
        double recentP95FrameMs,
        long managedMb,
        int liveMeshCount)
    {
        _ = liveMeshCount;
        if (IsFrameEnterSignal(recentAvgFrameMs, recentP95FrameMs)) return true;
        if (IsMemoryPressure(recentP95FrameMs, managedMb)) return true;
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
