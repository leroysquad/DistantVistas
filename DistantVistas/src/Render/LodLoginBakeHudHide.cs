using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace DistantVistas;

/// <summary>
/// Hides the vanilla HUD during the login visit sweep (same state as F4 in survival),
/// then restores the prior visibility on teardown.
/// </summary>
public sealed class LodLoginBakeHudHide
{
    static readonly FieldInfo? HideGuisField = typeof(ClientMain).GetField(
        "hideGuis", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    static readonly PropertyInfo? HideGuisProperty = typeof(ClientMain).GetProperty(
        "HideGuis", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

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
            TrySetHideGuis(true);
    }

    public void Restore()
    {
        if (!applied || !savedHidden.HasValue) return;

        try
        {
            bool wantHidden = savedHidden.Value;
            if (capi.HideGuis != wantHidden)
                TrySetHideGuis(wantHidden);
            if (capi.HideGuis != wantHidden)
            {
                // Toggle path can race with F4 / other .gui users — retry once.
                TrySetHideGuis(wantHidden);
                if (capi.HideGuis != wantHidden)
                {
                    capi.Logger.Warning(
                        "[DistantVistas] Login visit sweep: HUD restore mismatch (HideGuis={0}, wanted={1})",
                        capi.HideGuis, wantHidden);
                }
            }
        }
        finally
        {
            savedHidden = null;
            applied = false;
        }
    }

    void TrySetHideGuis(bool hidden)
    {
        if (TrySetHideGuisDirect(capi, hidden)) return;

        // Chat-toggle fallback when the engine field is unavailable or renamed.
        if (capi.HideGuis != hidden)
            capi.TriggerChatMessage(".gui");
    }

    static bool TrySetHideGuisDirect(ICoreClientAPI capi, bool hidden)
    {
        try
        {
            ClientMain clientMain = (ClientMain)capi.World;
            if (HideGuisProperty?.SetMethod != null)
            {
                HideGuisProperty.SetValue(clientMain, hidden);
                return capi.HideGuis == hidden;
            }

            if (HideGuisField?.FieldType == typeof(bool))
            {
                HideGuisField.SetValue(clientMain, hidden);
                return capi.HideGuis == hidden;
            }
        }
        catch
        {
            // Fall back to .gui toggle.
        }

        return false;
    }
}
