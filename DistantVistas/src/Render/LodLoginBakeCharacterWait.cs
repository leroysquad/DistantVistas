using Vintagestory.API.Client;

namespace DistantVistas;

/// <summary>
/// Defers the login visit sweep until vanilla character creation / class selection
/// is finished on a new-world first login.
/// </summary>
public static class LodLoginBakeCharacterWait
{
    /// <summary>
    /// True while the player still needs to finish character or class selection.
    /// </summary>
    public static bool IsPending(ICoreClientAPI capi)
    {
        if (HasProtectedDialogOpen(capi)) return true;
        return !capi.PlayerReadyFired;
    }

    /// <summary>Character/class dialogs must never be closed by the sweep.</summary>
    public static bool IsProtectedDialog(GuiDialog dlg)
    {
        if (dlg is GuiDialogCharacterBase) return true;

        string name = dlg.GetType().Name;
        return name.Contains("Character", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ClassSelect", StringComparison.OrdinalIgnoreCase)
            || name.Contains("SelectClass", StringComparison.OrdinalIgnoreCase)
            || name.Contains("PlayerClass", StringComparison.OrdinalIgnoreCase);
    }

    static bool HasProtectedDialogOpen(ICoreClientAPI capi)
    {
        foreach (object gui in capi.Gui.OpenedGuis)
        {
            if (gui is GuiDialog dlg && IsProtectedDialog(dlg))
                return true;
        }

        return false;
    }
}
