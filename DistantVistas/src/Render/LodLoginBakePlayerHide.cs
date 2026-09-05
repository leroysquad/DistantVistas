using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Hides the local player during the login visit sweep: first-person view, transparent
/// body tint, hidden FP hands, and sneak-style nametag suppression (same check vanilla
/// uses in <c>EntityBehaviorNameTag</c>).
///
/// Multiplayer limit: these are client-side toggles. Other players may still see your
/// body teleport if the server keeps broadcasting position — there is no dedicated
/// invisibility flag in the public API. Syncing <see cref="EntityControls.Sneak"/> is
/// best-effort and may hide your nametag remotely when the server accepts it.
/// </summary>
public sealed class LodLoginBakePlayerHide
{
    public const string HideFpHandsKey = "hideFpHands";

    /// <summary>ARGB white with alpha 0 — EntityShapeRenderer multiplies mesh colour by this.</summary>
    public static readonly int InvisibleRenderColor = ColorUtil.ColorFromRgba(255, 255, 255, 0);

    readonly ICoreClientAPI capi;
    EnumCameraMode? savedCamera;
    int? savedRenderColor;
    bool? savedHideFpHands;
    bool? savedControlsSneak;
    bool? savedServerSneak;
    bool applied;

    public LodLoginBakePlayerHide(ICoreClientAPI capi) => this.capi = capi;

    public void EnsureHidden()
    {
        EntityPlayer entity = capi.World.Player.Entity;
        if (!applied)
        {
            savedCamera = capi.Render.CameraType;
            savedRenderColor = entity.RenderColor;
            if (capi.Settings.Bool.Exists(HideFpHandsKey))
                savedHideFpHands = capi.Settings.Bool[HideFpHandsKey];
            savedControlsSneak = entity.Controls.Sneak;
            savedServerSneak = entity.ServerControls.Sneak;
            applied = true;
        }

        if (capi.Render.CameraType != EnumCameraMode.FirstPerson)
            capi.Render.CameraType = EnumCameraMode.FirstPerson;

        entity.RenderColor = InvisibleRenderColor;

        if (capi.Settings.Bool.Exists(HideFpHandsKey) && !capi.Settings.Bool[HideFpHandsKey])
            capi.Settings.Bool.Set(HideFpHandsKey, true, true);

        // Vanilla nametag renderer bails when ServerControls.Sneak is true (crouch).
        entity.ServerControls.Sneak = true;
        entity.Controls.Sneak = true;
    }

    public void Restore()
    {
        if (!applied) return;

        try
        {
            EntityPlayer entity = capi.World.Player.Entity;

            if (savedCamera.HasValue)
                capi.Render.CameraType = savedCamera.Value;

            if (savedRenderColor.HasValue)
                entity.RenderColor = savedRenderColor.Value;

            if (savedHideFpHands.HasValue && capi.Settings.Bool.Exists(HideFpHandsKey))
                capi.Settings.Bool.Set(HideFpHandsKey, savedHideFpHands.Value, true);

            if (savedServerSneak.HasValue)
                entity.ServerControls.Sneak = savedServerSneak.Value;

            if (savedControlsSneak.HasValue)
                entity.Controls.Sneak = savedControlsSneak.Value;
        }
        finally
        {
            savedCamera = null;
            savedRenderColor = null;
            savedHideFpHands = null;
            savedControlsSneak = null;
            savedServerSneak = null;
            applied = false;
        }
    }
}
