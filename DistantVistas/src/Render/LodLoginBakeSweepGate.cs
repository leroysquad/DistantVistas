namespace DistantVistas;

/// <summary>
/// Cross-cutting flags for the login visit sweep (Harmony patches, render suppress).
/// </summary>
public static class LodLoginBakeSweepGate
{
    /// <summary>Login visit sweep is armed or running.</summary>
    public static bool SweepActive { get; set; }

    /// <summary>Skip <see cref="GuiScreenRunningGame"/> world draws while the loader is held.</summary>
    public static bool SuppressRunningGameRender { get; set; }

    public static void Arm()
    {
        SweepActive = true;
        SuppressRunningGameRender = true;
    }

    public static void Release()
    {
        SweepActive = false;
        SuppressRunningGameRender = false;
    }
}
