using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DistantVistas;

/// <summary>
/// Pause-on-Start (modid <c>pauseonstart</c>) opens the escape menu after spawn so
/// AFK joins stay paused. Distant Vistas needs game ticks during the login overlay
/// (scout streams + bake). Force-unpause while the overlay is up, then put pause
/// back when the overlay finishes so Pause-on-Start still does its job.
/// </summary>
public static class LodPauseOnStartCompat
{
    public const string ModId = "pauseonstart";

    public static bool IsInstalled(ICoreAPI? api)
    {
        try { return api?.ModLoader.IsModEnabled(ModId) == true; }
        catch { return false; }
    }

    /// <summary>Clear pause so overlay scout/bake can tick. Safe when PoS is absent.</summary>
    public static void KeepUnpaused(ICoreClientAPI capi)
    {
        try { if (capi.IsGamePaused) capi.PauseGame(false); } catch { }
    }

    /// <summary>
    /// After a successful overlay (or skip-with-overlay-stolen-pause), restore PoS.
    /// No-op when Pause-on-Start is not installed. Esc-to-menu does not need this.
    /// </summary>
    public static void RestoreAfterLoginBake(ICoreClientAPI capi)
    {
        if (!IsInstalled(capi)) return;
        try { capi.PauseGame(true); } catch { }
        TryOpenIngameMenu(capi);
    }

    static void TryOpenIngameMenu(ICoreClientAPI capi)
    {
        try
        {
            foreach (GuiDialog dlg in capi.Gui.LoadedGuis)
            {
                string name = dlg.GetType().Name;
                if (name.IndexOf("IngameMenu", StringComparison.OrdinalIgnoreCase) < 0
                    && name.IndexOf("PauseMenu", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                dlg.TryOpen();
                return;
            }
        }
        catch
        {
        }
    }
}
