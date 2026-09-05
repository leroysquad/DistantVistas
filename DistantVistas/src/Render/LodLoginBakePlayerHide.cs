using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DistantVistas;

/// <summary>
/// Keeps the local player model out of view during teleports (first-person camera).
/// </summary>
public sealed class LodLoginBakePlayerHide
{
    readonly ICoreClientAPI capi;
    EnumCameraMode? savedCamera;
    bool applied;

    public LodLoginBakePlayerHide(ICoreClientAPI capi) => this.capi = capi;

    public void EnsureHidden()
    {
        if (!applied)
        {
            savedCamera = capi.Render.CameraType;
            applied = true;
        }

        if (capi.Render.CameraType != EnumCameraMode.FirstPerson)
            capi.Render.CameraType = EnumCameraMode.FirstPerson;
    }

    public void Restore()
    {
        if (!applied || !savedCamera.HasValue) return;

        try
        {
            capi.Render.CameraType = savedCamera.Value;
        }
        finally
        {
            savedCamera = null;
            applied = false;
        }
    }
}
