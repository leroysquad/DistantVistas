namespace DistantVistas.Checks;

public static class FrameBudgetChecks
{
    public static void Run(Check c)
    {
        LookBusyIsBusyNotStreaming(c);
        SitStillAllowsWork(c);
        SixteenBlockStillStreaming(c);
        SmallStepIsStepBusyNotStreaming(c);
        ExtraWorkPlayerBusy(c);
        CaptureAppliesBudgets(c);
        ForcedMeshAndCatchUp(c);
        FillNearestCappedDropsFar(c);
        FillNearestCappedKeepsFarWhenUnbanded(c);
        QueueMipOnStoreIndexIsOff(c);
        ArriveStreamingIsNotCameraBusy(c);
        WalkingDoesNotStarveMeshKeep(c);
        WalkingStartsKeepNotSeason(c);
        PruneWalkBudgetExists(c);
        ExtremeManagedHeapCountsAsPressure(c);
    }

    static void ExtremeManagedHeapCountsAsPressure(Check c)
    {
        LodMemoryBudget.Probe();
        long bar = LodMemoryBudget.ManagedPressureMb;
        long extreme = Math.Max(8_001, bar * 2);
        c.True(LodMemoryBudget.IsMemoryPressure(30, extreme),
            "heap at 2× soft bar counts toward pressure");
        c.False(LodMemoryBudget.IsMemoryPressure(30, Math.Max(1, bar / 4)),
            "well under the soft bar does not trip the extreme rule");
    }

    static void PruneWalkBudgetExists(Check c)
    {
        c.True(LodFrameBudget.PruneWalkKeyBudget > 0, "walk prune budget is positive");
        c.True(LodFrameBudget.PruneWalkKeyBudget < LodFrameBudget.WalkRenderDirtyCap,
            "walk prune budget is below the dirty remesh cap");
        c.True(LodMemoryBudget.MaxResidentSections > 0,
            "demand-resident section soft-cap is positive");
    }

    static void LookBusyIsBusyNotStreaming(Check c)
    {
        var look = new LodStreamingGate();
        look.Tick();
        look.NoteLook(0, 0);
        c.False(look.IsLookBusy, "first look sample is the baseline");
        c.False(look.IsBusy, "baseline look is not busy");

        look.NoteLook(1f, 0);
        c.True(look.IsLookBusy, "yaw past the deadzone is looking");
        c.True(look.IsBusy, "look-busy is busy");
        c.False(look.IsStreaming, "looking is not 16-block vanilla streaming");
        c.False(LodIdleRecapturePolicy.AllowExtraWork(
                look.IsStreaming, false, false, look.IsBusy),
            "look-busy extra work is 0");

        var sig = new LodStreamingGate();
        sig.Tick();
        sig.NoteFrameSignals(true, false, false);
        c.True(sig.IsLookBusy, "NoteFrameSignals copies look from the renderer");
        c.True(sig.IsBusy, "a copied look flag is busy");
        c.False(sig.IsStreaming, "a copied look flag is not IsStreaming");
    }

    static void SitStillAllowsWork(Check c)
    {
        var idle = new LodStreamingGate();
        idle.Tick();
        idle.NotePlayer("p", 0, 0);
        idle.NotePlayer("p", 0, 0);
        idle.NoteLook(0.1f, 0);
        idle.NoteLook(0.1f, 0);
        idle.NoteFrameMs(8f);
        c.False(idle.IsBusy, "sit still looking forward is not busy");
        c.True(LodIdleRecapturePolicy.AllowExtraWork(
                idle.IsStreaming, false, false, idle.IsBusy),
            "sit-still extra work is allowed");
        c.True(LodFrameBudget.AllowCatchUp(idle.IsBusy),
            "sit-still catch-up is allowed");
    }

    static void SixteenBlockStillStreaming(Check c)
    {
        var walk = new LodStreamingGate();
        walk.Tick();
        walk.NotePlayer("p", 0, 0);
        walk.NotePlayer("p", 16, 0);
        c.True(walk.IsStreaming, "16 blocks in one step is still IsStreaming");
        c.True(walk.IsWalkBusy, "16-block hold is walk-busy");
        c.True(walk.IsCameraBusy, "16-block walk is camera-busy");
        c.True(walk.IsBusy, "16-block streaming is busy");
        c.True(walk.IsStepBusy, "a 16-block step is also step-busy");
    }

    static void SmallStepIsStepBusyNotStreaming(Check c)
    {
        var step = new LodStreamingGate();
        step.Tick();
        step.NotePlayer("p", 0, 0);
        step.NotePlayer("p", 1, 0);
        c.False(step.IsStreaming, "a 1-block step is not 16-block streaming");
        c.True(step.IsStepBusy, "a 1-block step is step-busy");
        c.True(step.IsBusy, "step-busy is busy");
        c.False(LodIdleRecapturePolicy.AllowExtraWork(
                step.IsStreaming, false, false, step.IsBusy),
            "a small step still yields extra work");
    }

    static void ExtraWorkPlayerBusy(Check c)
    {
        c.False(LodIdleRecapturePolicy.AllowExtraWork(false, false, false, true),
            "playerBusy blocks extra work");
        c.True(LodIdleRecapturePolicy.AllowExtraWork(false, false, false, false),
            "quiet idle with playerBusy false may do extra work");
    }

    static void CaptureAppliesBudgets(Check c)
    {
        c.Eq(0, LodFrameBudget.CaptureApplies(true, true, 99, stepping: false),
            "sit hitch applies 0");
        c.Eq(1, LodFrameBudget.CaptureApplies(true, true, 99, stepping: true),
            "walk hitch still applies 1");
        c.Eq(1, LodFrameBudget.CaptureApplies(true, false, 99),
            "playerBusy applies 1");
        c.Eq(8, LodFrameBudget.CaptureApplies(false, false, 4),
            "idle backlog at the threshold applies 8");
        c.Eq(8, LodFrameBudget.CaptureApplies(false, false, 5),
            "idle backlog above the threshold applies 8");
        c.Eq(1, LodFrameBudget.CaptureApplies(false, false, 0),
            "idle with no backlog applies 1");
    }

    static void ForcedMeshAndCatchUp(Check c)
    {
        c.Eq(4, LodFrameBudget.ForcedMeshStarts(true),
            "busy forced mesh starts are 4");
        c.Eq(48, LodFrameBudget.ForcedMeshStarts(false),
            "quiet forced mesh starts stay 48");
        c.Eq(4, LodFrameBudget.PropagationsThisTick(true, 100, 48, 4),
            "busy propagation budget is 4 so walking can mip parents");
        c.Eq(48, LodFrameBudget.PropagationsThisTick(false, 17, 48, 4),
            "quiet catch-up propagation stays 48");
        c.False(LodFrameBudget.AllowCatchUp(true), "busy forbids catch-up");
        c.True(LodFrameBudget.AllowCatchUp(false), "quiet allows catch-up");
        c.True(LodSeasonCatchUp.ResidentSectionsPerTick >= 16,
            "busy palettes live in LodFrameBudget, not a lowered ResidentSectionsPerTick");
        c.Eq(0, LodSeasonCatchUp.ColdLoadsPerTick,
            "cold loads per tick stay 0");
    }

    static void FillNearestCappedDropsFar(Check c)
    {
        long near = LodWorld.SectionKey(0, 0, 0);
        long mid = LodWorld.SectionKey(0, 2, 0);
        long far = LodWorld.SectionKey(0, 8, 0);
        var dest = new List<(long Key, double DistSq)>();
        var visited = new HashSet<long>();
        LodSeasonIdleOrder.FillNearestCapped(
            dest, new[] { far, mid, near }, visited, px: 32, pz: 32,
            cap: 2, maxDistBlocks: 0);
        c.Eq(2, dest.Count, "cap drops far keys");
        c.Eq(near, dest[0].Key, "closest section is first, not insertion order");
        c.Eq(mid, dest[1].Key, "mid distance is second");
        c.True(dest.Count != 3, "does not require dest.Count == all keys");
        c.True(dest[0].DistSq < dest[1].DistSq, "nearest distSq is strictly closer than mid");

        var band = new List<(long Key, double DistSq)>();
        LodSeasonIdleOrder.FillNearestCapped(
            band, new[] { far, mid, near }, visited, px: 32, pz: 32,
            cap: 96, maxDistBlocks: 256);
        c.True(band.Count < 3, "256-block ring drops the far key");
        c.Eq(near, band[0].Key, "band fill still puts closest first");
    }

    static void FillNearestCappedKeepsFarWhenUnbanded(Check c)
    {
        long near = LodWorld.SectionKey(0, 0, 0);
        long mid = LodWorld.SectionKey(0, 2, 0);
        long far = LodWorld.SectionKey(0, 8, 0);
        var dest = new List<(long Key, double DistSq)>();
        var visited = new HashSet<long>();
        LodSeasonIdleOrder.FillNearestCapped(
            dest, new[] { far, mid, near }, visited, px: 32, pz: 32,
            cap: 96, maxDistBlocks: 0);
        c.Eq(3, dest.Count, "maxDist 0 keeps far keys inside the nearest-N cap");
        c.Eq(far, dest[2].Key, "far is last of nearest-N, not dropped by a ring");
    }

    static void ArriveStreamingIsNotCameraBusy(Check c)
    {
        var arrivals = new LodStreamingGate();
        arrivals.Tick();
        arrivals.NoteChunkArrival();
        arrivals.NoteChunkArrival();
        c.True(arrivals.IsStreaming, "two arrivals are IsStreaming");
        c.False(arrivals.IsCameraBusy, "chunk arrivals are not looking or walking");
        c.True(LodIdleRecapturePolicy.AllowLoadedRecapture(arrivals.IsCameraBusy, false),
            "sit-still recapture runs while vanilla chunks are still arriving");
        c.False(LodIdleRecapturePolicy.AllowExploredLoad(arrivals.IsCameraBusy, false, arrivals.IsStreaming),
            "arrivals still yield extra disk loads");
        c.True(LodIdleRecapturePolicy.AllowLoadedRecapture(false, false),
            "join epoch is not this gate: loaded recapture is allowed");
        c.False(LodIdleRecapturePolicy.AllowExtraWork(false, false, true),
            "join epoch still blocks extra explored loads");
    }

    static void WalkingDoesNotStarveMeshKeep(Check c)
    {
        c.False(LodFrameBudget.StarveMeshRequests(false, false, true),
            "walking does not starve keep/coarse mesh requests");
        c.True(LodFrameBudget.StarveMeshRequests(true, false, false),
            "looking still starves extra mesh requests");
        c.False(LodFrameBudget.StarveMeshRequests(true, false, true),
            "look plus walk still requests the trail");
        c.True(LodFrameBudget.StarveCatchUp(false, false, true),
            "walking still starves the 48-wide snow burst");
    }

    static void WalkingStartsKeepNotSeason(Check c)
    {
        c.Eq(4, LodFrameBudget.KeepMeshStarts(false, false, true),
            "walking starts keep-circle first-meshes");
        c.Eq(4, LodFrameBudget.KeepMeshStarts(true, true, true),
            "hitch from our own uploads does not starve keep while stepping");
        c.Eq(0, LodFrameBudget.KeepMeshStarts(true, false, false),
            "look-only does not start keep meshes");
        c.Eq(0, LodFrameBudget.FineMeshStarts(false, false, true, 16),
            "walking starts 0 FineMesh / recapture");
        c.Eq(0, LodFrameBudget.SeasonForcedStarts(false, false, true, 48),
            "walking starts 0 SeasonForced remesh");
        c.Eq(2048, LodFrameBudget.WalkRenderDirtyCap,
            "walking caps RenderDirty growth");
        c.Eq(16, LodFrameBudget.FineMeshStarts(false, false, false, 16),
            "sit-still FineMesh uses the quiet budget");
        c.Eq(48, LodFrameBudget.SeasonForcedStarts(false, false, false, 48),
            "sit-still SeasonForced uses the quiet budget");
    }

    static void QueueMipOnStoreIndexIsOff(Check c)
    {
        c.False(LodFrameBudget.QueueMipOnStoreIndex,
            "join must not dump ApplyToParent into MipDirty");
    }
}
