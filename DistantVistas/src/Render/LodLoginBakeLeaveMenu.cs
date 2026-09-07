using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

namespace DistantVistas;

/// <summary>
/// Same leave-world path as the vanilla Escape menu "Leave world" button (1.22 SoftExit).
/// Restoring pose first is the caller's job so the save writes home, not the last visit hop.
/// </summary>
public static class LodLoginBakeLeaveMenu
{
    public static void Request(ICoreClientAPI capi)
    {
        if (capi.World is not ClientMain game) return;
        if (game.exitToMainMenu || game.exitToDisconnectScreen) return;

        game.SendLeave(0);
        game.exitReason = "leave world button pressed";
        game.DestroyGameSession(gotDisconnected: false, EnumExitMode.SoftExit);
    }
}
