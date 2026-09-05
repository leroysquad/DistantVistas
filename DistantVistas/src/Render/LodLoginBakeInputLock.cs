using Vintagestory.API.Common;

namespace DistantVistas;

/// <summary>
/// Clears player movement/actions during the login visit sweep without indexing past
/// <see cref="EntityControls"/> bounds (VS 1.22.x throws on out-of-range enum slots).
/// </summary>
public static class LodLoginBakeInputLock
{
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
}
