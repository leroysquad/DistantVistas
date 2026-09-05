namespace DistantVistas;

/// <summary>
/// FPS-first budgets. Snow, season, and recapture still finish — they wait until
/// the camera is quiet. Pure so the look/walk/hitch rule is checkable without a world.
/// </summary>
public static class LodFrameBudget
{
    /// <summary>Previous frame over this many ms is a hitch; catch-up stops.</summary>
    public const float FrameBusyMs = 16.5f;

    /// <summary>~1.7 degrees. Yaw/pitch past this is looking around.</summary>
    public const float LookDeadzoneRad = 0.03f;

    public const int LookHoldFrames = 12;
    public const int HitchHoldFrames = 8;
    public const int StepHoldFrames = 8;
    public const int LookBusyTicks = 12;
    public const int HitchBusyTicks = 8;
    public const int StepBusyTicks = 8;

    /// <summary>Any XZ step this large (~2 cm) cuts mesh burst. The 16-block gate stays on <see cref="LodStreamingGate"/>.</summary>
    public const double StepBlocks = 0.02;
    public static readonly double StepBlocksSq = StepBlocks * StepBlocks;

    public const int QuietForcedMeshStarts = 48;
    public const int BusyForcedMeshStarts = 4;
    /// <summary>First-mesh keep-circle coverage while walking. Hitch does not cut this.</summary>
    public const int KeepMeshStartsWalk = 4;
    /// <summary>While walking, drop remesh keys once dirty hits this so keep starts still run.</summary>
    public const int WalkRenderDirtyCap = 2048;
    /// <summary>
    /// Max RenderDirty keys examined per prune while walking / keep-origin shifting.
    /// Rotating cursor makes progress across frames; walkRequested keys are never pruned.
    /// </summary>
    public const int PruneWalkKeyBudget = 768;
    /// <summary>
    /// SelectNearestDirty / keep overlay: full scan only under this. Above it, a rotating
    /// examine budget avoids O(n) walkUs spikes when RenderDirty is huge.
    /// </summary>
    public const int SelectDirtyFullScanCap = 384;
    public const int SelectDirtyExamineBudget = 512;
    public const int QuietCaptureApplyBacklog = 8;
    public const int BusyCaptureApply = 1;
    public const int HitchCaptureApply = 0;
    public const int CaptureBacklogThreshold = 4;

    public const int QuietPalettePerTick = LodSeasonCatchUp.ResidentSectionsPerTick;
    public const int BusyPalettePerTick = 1;
    public const int HitchPalettePerTick = 0;

    public const int QuietPropagations = 48;
    /// <summary>Walking still has to mip parents or the trail behind you vanishes.</summary>
    public const int BusyPropagations = 4;

    /// <summary>Idle recapture / season-idle scratch: nearest N, never a full 6k sort.</summary>
    public const int ScratchCap = 96;

    /// <summary>Force-recapture and optional band filter. One L0 tile ring is 64; four tiles is 256.</summary>
    public const double ScratchBandBlocks = 256;

    public const int MapChunkRefreshIntervalFrames = 8;

    /// <summary>
    /// Join must not dump ApplyToParent keys into MipDirty. Demand-mesh / Cover when
    /// the walk actually wants the tile.
    /// </summary>
    public const bool QueueMipOnStoreIndex = false;

    public static float WrapAngle(float radians)
    {
        const float tau = MathF.PI * 2f;
        radians %= tau;
        if (radians > MathF.PI) radians -= tau;
        if (radians < -MathF.PI) radians += tau;
        return radians;
    }

    public static bool LookMoved(float prevYaw, float prevPitch, float yaw, float pitch)
    {
        float dy = WrapAngle(yaw - prevYaw);
        float dp = pitch - prevPitch;
        return dy * dy + dp * dp >= LookDeadzoneRad * LookDeadzoneRad;
    }

    public static bool FrameIsHitch(float lastFrameMs) =>
        lastFrameMs >= FrameBusyMs;

    public static int CaptureApplies(bool playerBusy, bool hitch, int backlog, bool stepping = false)
    {
        // Sit-hitch: 0. Walk-hitch: 1 so newly discovered land lands on the canvas.
        if (hitch && !stepping) return HitchCaptureApply;
        if (playerBusy) return BusyCaptureApply;
        return backlog >= CaptureBacklogThreshold ? QuietCaptureApplyBacklog : 1;
    }

    /// <summary>Snow/season burst waits. Walking is this, looking is this.</summary>
    public static bool StarveCatchUp(bool look, bool hitch, bool step) =>
        look || hitch || step;

    /// <summary>
    /// Looking around or a sit-hitch. Walking must still request keep/coarse meshes
    /// or the trail behind you vanishes.
    /// </summary>
    public static bool StarveMeshRequests(bool look, bool hitch, bool step) =>
        !step && (look || hitch);

    public static int ForcedMeshStarts(bool playerBusy) =>
        playerBusy ? BusyForcedMeshStarts : QuietForcedMeshStarts;

    /// <summary>
    /// Walking starts first-mesh keep-circle parents. Looking, sitting, or hitch
    /// without a step does not. Hitch from our own uploads must not starve this.
    /// </summary>
    public static int KeepMeshStarts(bool look, bool hitch, bool step)
    {
        _ = look;
        _ = hitch;
        return step ? KeepMeshStartsWalk : 0;
    }

    /// <summary>L0 remesh / recapture / SeasonForced. Zero while looking or walking.</summary>
    public static int FineMeshStarts(bool look, bool hitch, bool step, int quietBudget)
    {
        if (look || hitch || step) return 0;
        return quietBudget;
    }

    /// <summary>SeasonForced remesh of already-meshed land. Zero while looking or walking.</summary>
    public static int SeasonForcedStarts(bool look, bool hitch, bool step, int quietBudget)
    {
        if (look || hitch || step) return 0;
        return quietBudget;
    }

    public static int ResidentPaletteThisTick(bool playerBusy, bool hitch)
    {
        if (hitch) return HitchPalettePerTick;
        if (playerBusy) return BusyPalettePerTick;
        return QuietPalettePerTick;
    }

    public static int PropagationsThisTick(bool playerBusy, int mipDirty, int quietCatchUp, int quietNormal)
    {
        if (playerBusy) return BusyPropagations;
        return mipDirty > 16 ? quietCatchUp : quietNormal;
    }

    public static bool AllowCatchUp(bool playerBusy) => !playerBusy;
}
