using System.Globalization;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DistantVistas;

/// <summary>
/// Clears player movement/actions during the login visit sweep without indexing past
/// <see cref="EntityControls"/> bounds (VS 1.22.x throws on out-of-range enum slots).
/// Also drains leftover FPS-look deltas: the splash HUD does not count as a Dialog, so
/// the mouse stays grabbed and ClientMain.OnMouseMove keeps adding MouseDeltaX/Y.
/// </summary>
public static class LodLoginBakeInputLock
{
    public static bool OverlayLookLocked { get; private set; }

    const BindingFlags WorldFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

    static readonly string[] LookDeltaNames =
    {
        "MouseDeltaX",
        "MouseDeltaY",
        "DelayedMouseDeltaX",
        "DelayedMouseDeltaY",
    };

    static readonly EnumEntityAction[] ClearableActions =
    {
        EnumEntityAction.Forward,
        EnumEntityAction.Backward,
        EnumEntityAction.Left,
        EnumEntityAction.Right,
        EnumEntityAction.Jump,
        EnumEntityAction.Sprint,
        EnumEntityAction.Glide,
        EnumEntityAction.FloorSit,
        EnumEntityAction.LeftMouseDown,
        EnumEntityAction.RightMouseDown,
        EnumEntityAction.Up,
        EnumEntityAction.Down,
        EnumEntityAction.CtrlKey,
        EnumEntityAction.ShiftKey,
        EnumEntityAction.InWorldLeftMouseDown,
        EnumEntityAction.InWorldRightMouseDown,
    };

    public static void Apply(EntityControls controls)
    {
        controls.StopAllMovement();
        controls.MovespeedMultiplier = 0f;
        controls.WalkVector.Set(0, 0, 0);
        controls.FlyVector.Set(0, 0, 0);
        controls.IsFlying = true;
        controls.NoClip = true;
        controls.Gliding = false;
        controls.DetachedMode = false;
        controls.IsClimbing = false;
        controls.IsAiming = false;

        controls.Forward = false;
        controls.Backward = false;
        controls.Left = false;
        controls.Right = false;
        controls.Jump = false;
        controls.Sprint = false;
        controls.Gliding = false;
        controls.FloorSitting = false;
        controls.Up = false;
        controls.Down = false;
        controls.LeftMouseDown = false;
        controls.RightMouseDown = false;
        controls.CtrlKey = false;
        controls.ShiftKey = false;

        foreach (EnumEntityAction action in ClearableActions)
            TrySet(controls, action, false);
    }

    /// <summary>
    /// Sweep lock left NoClip + flying on. Movespeed alone is not an unlock.
    /// </summary>
    public static void Release(EntityControls controls)
    {
        controls.StopAllMovement();
        controls.MovespeedMultiplier = 1f;
        controls.WalkVector.Set(0, 0, 0);
        controls.FlyVector.Set(0, 0, 0);
        controls.IsFlying = false;
        controls.NoClip = false;
        controls.Gliding = false;
        controls.DetachedMode = false;
        controls.IsClimbing = false;
        controls.IsAiming = false;
        foreach (EnumEntityAction action in ClearableActions)
            TrySet(controls, action, false);
    }

    /// <summary>
    /// While the splash is up: stop FPS look applying, and keep look deltas at zero
    /// so they cannot dump into yaw when the overlay closes.
    /// </summary>
    public static void HoldLook(ICoreClientAPI capi)
    {
        if (capi?.World == null) return;
        OverlayLookLocked = true;
        SetAllowCameraControl(capi, false);
        DrainLookDeltas(capi);
    }

    /// <summary>
    /// Overlay finished: drop leftover grab deltas, turn camera control back on, and
    /// resync yaw/pitch from the entity pose the sweep locked.
    /// </summary>
    public static void RestoreLook(ICoreClientAPI capi)
    {
        if (capi?.World == null) return;
        OverlayLookLocked = false;
        DrainLookDeltas(capi);
        SetAllowCameraControl(capi, true);
        ResyncYawPitch(capi);
        DrainLookDeltas(capi);
        AgentLookLog(capi);
    }

    public static void DrainLookDeltas(ICoreClientAPI capi)
    {
        if (capi?.World == null) return;
        LodLoginBakeMouseDelta.Drain(capi);
        object world = capi.World;
        foreach (string name in LookDeltaNames)
            ZeroNumeric(world, name);
    }

    static void SetAllowCameraControl(ICoreClientAPI capi, bool value)
    {
        object? world = capi.World;
        if (world == null) return;
        try
        {
            FieldInfo? field = world.GetType().GetField("AllowCameraControl", WorldFlags);
            if (field != null && field.FieldType == typeof(bool))
                field.SetValue(world, value);
        }
        catch
        {
        }
    }

    static void ResyncYawPitch(ICoreClientAPI capi)
    {
        try
        {
            var entity = capi.World?.Player?.Entity;
            if (entity == null || capi.Input == null) return;
            capi.Input.MouseYaw = entity.Pos.Yaw;
            capi.Input.MousePitch = entity.Pos.Pitch;
        }
        catch
        {
        }
    }

    static void ZeroNumeric(object target, string name)
    {
        try
        {
            Type type = target.GetType();
            FieldInfo? field = type.GetField(name, WorldFlags);
            if (field != null)
            {
                field.SetValue(target, ZeroOf(field.FieldType));
                return;
            }

            PropertyInfo? prop = type.GetProperty(name, WorldFlags);
            if (prop != null && prop.CanWrite)
                prop.SetValue(target, ZeroOf(prop.PropertyType));
        }
        catch
        {
        }
    }

    static object? ZeroOf(Type type)
    {
        if (type == typeof(double)) return 0d;
        if (type == typeof(float)) return 0f;
        if (type == typeof(int)) return 0;
        if (type == typeof(long)) return 0L;
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    static void TrySet(EntityControls controls, EnumEntityAction action, bool value)
    {
        if (!IsKnownAction(action)) return;
        try
        {
            controls[action] = value;
        }
        catch (IndexOutOfRangeException)
        {
            // Enum value exists but this VS build has no backing slot — skip.
        }
    }

    static bool IsKnownAction(EnumEntityAction action) =>
        action != EnumEntityAction.None && Enum.IsDefined(typeof(EnumEntityAction), action);

    static void AgentLookLog(ICoreClientAPI capi)
    {
        // #region agent log
        try
        {
            float yaw = 0f, pitch = 0f;
            try
            {
                yaw = capi.Input.MouseYaw;
                pitch = capi.Input.MousePitch;
            }
            catch
            {
            }

            System.IO.File.AppendAllText(
                @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                "{\"sessionId\":\"40cccb\",\"runId\":\"audio-2x\",\"hypothesisId\":\"H-LOOK\",\"location\":\"LodLoginBakeInputLock.RestoreLook\",\"message\":\"restore-look\",\"data\":{\"yaw\":"
                + yaw.ToString("0.###", CultureInfo.InvariantCulture)
                + ",\"pitch\":" + pitch.ToString("0.###", CultureInfo.InvariantCulture)
                + "},\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n");
        }
        catch
        {
        }
        // #endregion
    }
}
