using Vintagestory.API.Client;

namespace DistantVistas;

/// <summary>
/// Hides the vanilla HUD during the login visit sweep via the <c>.gui</c> client command
/// (same toggle as F4 in survival), then restores the prior visibility on teardown.
/// </summary>
public sealed class LodLoginBakeHudHide
{
    readonly ICoreClientAPI capi;
    bool? savedHidden;
    bool applied;

    public LodLoginBakeHudHide(ICoreClientAPI capi) => this.capi = capi;

    public void EnsureHidden()
    {
        if (!applied)
        {
            savedHidden = capi.HideGuis;
            applied = true;
        }

        if (!capi.HideGuis)
            capi.TriggerChatMessage(".gui");
    }

    public void Restore()
    {
        if (!applied || !savedHidden.HasValue) return;

        try
        {
            bool wantHidden = savedHidden.Value;
            if (capi.HideGuis != wantHidden)
                capi.TriggerChatMessage(".gui");
        }
        finally
        {
            savedHidden = null;
            applied = false;
        }
    }
}
