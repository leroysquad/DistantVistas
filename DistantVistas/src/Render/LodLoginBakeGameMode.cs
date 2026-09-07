using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DistantVistas;

/// <summary>
/// Switches the local player to creative for login-sweep fly teleports, then restores
/// the prior mode (survival, guest, etc.) on teardown.
/// </summary>
public sealed class LodLoginBakeGameMode
{
    readonly ICoreClientAPI capi;
    EnumGameMode? saved;
    bool applied;

    public LodLoginBakeGameMode(ICoreClientAPI capi) => this.capi = capi;

    public bool IsCreative => applied;

    public void EnsureCreative()
    {
        IWorldPlayerData data = capi.World.Player.WorldData;
        if (!applied)
        {
            saved = data.CurrentGameMode;
            applied = true;
        }

        if (data.CurrentGameMode == EnumGameMode.Creative) return;

        data.CurrentGameMode = EnumGameMode.Creative;
        capi.SendChatMessage("/gamemode creative");
    }

    public void Restore()
    {
        if (!applied || !saved.HasValue) return;

        try
        {
            EnumGameMode prior = saved.Value;
            capi.World.Player.WorldData.CurrentGameMode = prior;
            if (prior != EnumGameMode.Creative)
                capi.SendChatMessage("/gamemode " + ModeCommand(prior));
        }
        finally
        {
            saved = null;
            applied = false;
        }
    }

    static string ModeCommand(EnumGameMode mode) => mode switch
    {
        EnumGameMode.Survival => "survival",
        EnumGameMode.Guest => "guest",
        EnumGameMode.Spectator => "spectator",
        _ => "creative",
    };
}
