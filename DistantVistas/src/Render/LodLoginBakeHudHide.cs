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
    bool forcedHide;

    public LodLoginBakeHudHide(ICoreClientAPI capi) => this.capi = capi;

    public void EnsureHidden()
    {
        if (!applied)
        {
            savedHidden = capi.HideGuis;
            applied = true;
        }

        if (!capi.HideGuis)
        {
            if (TrySetHideGuis(true))
                forcedHide = true;
        }
    }

    public void Restore()
    {
        if (!applied || !savedHidden.HasValue) return;

        try
        {
            bool wantHidden = savedHidden.Value;
            if (!TryRestoreHideGuis(wantHidden))
            {
                capi.Logger.Warning(
                    "[DistantVistas] Login visit sweep: HUD restore mismatch (HideGuis={0}, wanted={1})",
                    capi.HideGuis, wantHidden);
            }
        }
        finally
        {
            savedHidden = null;
            applied = false;
            forcedHide = false;
        }
    }

    bool TryRestoreHideGuis(bool wantHidden)
    {
        if (capi.HideGuis == wantHidden) return true;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (TrySetHideGuis(wantHidden) && capi.HideGuis == wantHidden)
                return true;
        }

        // If we forced hide during sweep, prefer showing HUD when restore is ambiguous.
        if (forcedHide && !wantHidden && capi.HideGuis)
        {
            TrySetHideGuis(false);
            if (capi.HideGuis == false) return true;
        }

        return capi.HideGuis == wantHidden;
    }

    bool TrySetHideGuis(bool hidden)
    {
        if (capi.HideGuis == hidden) return true;
        if (TrySetHideGuisDirect(capi, hidden)) return capi.HideGuis == hidden;

        // Chat-toggle fallback when the engine field is unavailable or renamed.
        capi.TriggerChatMessage(".gui");
        return capi.HideGuis == hidden;
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
