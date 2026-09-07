using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Overlay writes vanilla view to a frontier-relative stream disk (at least spawn-solid
/// 1024, then finishedRadius + lead, capped at 2048), then restores the player's slider.
/// Applied VD steps one chunk / 5s so chunkdb can pause for autosave (1.0.44 flood).
/// Visit / Farseer math stays on the 750 onset baseline so the FlagBaked disk remains 4075.
/// Never holds 1000. Never restores 750, overlay stream, leftover 1000, or a maxed ~1536 as original.
/// </summary>
public sealed class LodLoginBakeViewBoost
{
    /// <summary>VS client hard ceiling for view distance (blocks).</summary>
    public const int MaxVanillaViewDistance = 2048;

    /// <summary>Floor if the player has no desired view set.</summary>
    public const int SweepMinViewDistanceBlocks = 256;

    /// <summary>
    /// Farseer / visit-disk baseline (blocks). <c>4.5 × 750 + 700 = 4075</c>.
    /// Not the vanilla stream radius — see <see cref="MinOverlayStreamBlocks"/>.
    /// </summary>
    public const int SweepBoostViewDistanceBlocks = 750;

    /// <summary>
    /// Floor for overlay vanilla stream (spawn-solid). 1.0.43 held this constant and
    /// cliffed at finished≈639 (~913 blocks, ~0.89 of 1024).
    /// </summary>
    public const int MinOverlayStreamBlocks = 1024;

    /// <summary>Deprecated alias of <see cref="MinOverlayStreamBlocks"/> (1.0.43 constant).</summary>
    public const int SweepStreamViewDistanceBlocks = MinOverlayStreamBlocks;

    /// <summary>
    /// Keep stream ahead of the filled disk so the next annulus sits in the useful
    /// interior, not the ~11% dead rim (683/750 and 913/1024 playtests).
    /// </summary>
    public const int StreamLeadBlocks = 256;

    /// <summary>Playtest useful fraction of overlay VD (358/750 and 639/1024 ≈ 0.89).</summary>
    public const double StreamUsefulFillRatio = 0.88;

    /// <summary>
    /// One vanilla chunk per grow. 1.0.44 jumped 1024→1184 in one write and flooded
    /// RequestChunkColumnsQueue until chunkdbthread could not pause for autosave.
    /// </summary>
    public const int StreamGrowStepBlocks = 32;

    /// <summary>Wall-clock pause between stream steps so chunkdb can drain.</summary>
    public const int StreamGrowDwellMs = 5000;

    /// <summary>
    /// Overlay tick gap that means the integrated server is hitching (SP playtest
    /// died at ~3.2s ticks). Hold stream growth until ticks recover.
    /// </summary>
    public const int StreamGrowHitchPressureMs = 800;

    /// <summary>Old overlay hold. Never write this. Never treat it as the player's slider.</summary>
    public const int LegacySweepHoldBlocks = 1000;

    /// <summary>ClientSettings key for the graphics view-distance slider.</summary>
    public const string ViewDistanceSettingKey = "viewDistance";

    /// <summary>Lower overdraw during sweep = LOD draws closer under vanilla for wider cover.</summary>
    public const float SweepOverdrawStart = 0.35f;

    readonly ICoreClientAPI capi;
    readonly LodTerrainRenderer renderer;

    int? savedDesiredViewDistance;
    int? savedClientViewDistance;
    int? savedLastApprovedViewDistance;
    int savedFarViewDistanceCap;
    float savedOverdrawStart;
    bool applied;
    int boostedViewDistance;
    bool loggedBoost;
    int desiredStreamBlocks;
    long lastEnsureMs;
    long lastGrowMs;
    int lastHitchMs;
    bool hitchPressure;

    public LodLoginBakeViewBoost(ICoreClientAPI capi, LodTerrainRenderer renderer)
    {
        this.capi = capi;
        this.renderer = renderer;
    }

    public int BoostedViewDistanceBlocks => applied ? boostedViewDistance : 0;

    public int ChunkSweepRadiusChunks
    {
        get
        {
            // Capture sweep of columns scouts already streamed, out to Farseer
            // onset (gray tent + black tips). Does not tessellate vanilla that far.
            int blocks = SweepVisitRadiusBlocks;
            int cs = GlobalConstants.ChunkSize;
            return Math.Max(4, (int)Math.Ceiling(blocks / (double)cs) + 2);
        }
    }

    /// <summary>
    /// Vanilla SetChunkColumnVisible around the real player during overlay.
    /// Follows the live overlay stream so cliff L0s stay inside client view-cull.
    /// The FlagBaked 4075 disk is scout visit coverage, not a 4 km tessellation storm.
    /// </summary>
    public int SpawnStreamRadiusChunks
    {
        get
        {
            int cs = GlobalConstants.ChunkSize;
            int blocks = LiveStreamViewDistanceBlocks;
            return Math.Max(4, (int)Math.Ceiling(blocks / (double)cs) + 2);
        }
    }

    /// <summary>Live overlay vanilla stream (blocks), or the 1024 floor before boost applies.</summary>
    public int LiveStreamViewDistanceBlocks =>
        applied && boostedViewDistance > 0 ? boostedViewDistance : MinOverlayStreamBlocks;

    /// <summary>Frontier-relative target; applied stream steps toward this.</summary>
    public int DesiredStreamViewDistanceBlocks =>
        desiredStreamBlocks > 0 ? desiredStreamBlocks : MinOverlayStreamBlocks;

    public int LastHitchMs => lastHitchMs;

    public bool HitchPressure => hitchPressure;

    /// <summary>Spawn-solid Chebyshev radius in chunks — overlay SetChunkColumnVisible cap.</summary>
    public static int SpawnSolidStreamChunks()
    {
        int cs = GlobalConstants.ChunkSize;
        if (cs < 1) cs = 32;
        return Math.Max(4, (int)Math.Ceiling(MinOverlayStreamBlocks / (double)cs) + 2);
    }

    /// <summary>
    /// Step overlay vanilla VD one chunk at a time with dwell, never the full
    /// <see cref="OverlayStreamBlocks"/> jump (1.0.44 1024→1184 FIFO flood).
    /// </summary>
    public static int StepAppliedStream(
        int applied,
        int desired,
        long nowMs,
        long lastGrowMs,
        bool hitchPressure)
    {
        if (applied <= 0)
            return MinOverlayStreamBlocks;
        if (desired <= applied)
            return applied;
        if (hitchPressure)
            return applied;
        if (lastGrowMs > 0 && nowMs - lastGrowMs < StreamGrowDwellMs)
            return applied;
        int next = applied + StreamGrowStepBlocks;
        if (next > desired)
            next = desired;
        return GameMath.Clamp(next, MinOverlayStreamBlocks, MaxVanillaViewDistance);
    }

    /// <summary>
    /// Overlay vanilla / LastApproved view for this finished count: at least spawn-solid,
    /// then max(finishedRadius + lead, finishedRadius / usefulFill), snapped to chunk size,
    /// capped at the engine 2048 ceiling (not the 4075 visit disk).
    /// </summary>
    public static int OverlayStreamBlocks(int finishedL0)
    {
        int finishedR = LodLoginScoutFill.FinishedToRadiusBlocks(finishedL0);
        int byLead = finishedR + StreamLeadBlocks;
        int byRatio = finishedR <= 0
            ? MinOverlayStreamBlocks
            : (int)Math.Ceiling(finishedR / StreamUsefulFillRatio);
        int want = Math.Max(MinOverlayStreamBlocks, Math.Max(byLead, byRatio));
        int cs = GlobalConstants.ChunkSize;
        if (cs < 1) cs = 32;
        want = (int)Math.Ceiling(want / (double)cs) * cs;
        return GameMath.Clamp(want, MinOverlayStreamBlocks, MaxVanillaViewDistance);
    }

    /// <summary>
    /// Login visit disk in blocks: graphics hold × horizon onset so FlagBaked
    /// land meets the Farseer silhouette (~4.5× VD + 700), not a thin 750 ring
    /// and not empty sky past that rim.
    /// </summary>
    public static int SweepVisitRadiusBlocks =>
        (int)Math.Ceiling(LodCoveragePolicy.HorizonDrawDistance(SweepBoostViewDistanceBlocks));

    public int ChunkVisibleRadius
    {
        get
        {
            // Scout ring clamp / visit onset. Stream to Farseer onset + 700 so
            // local scout rings do not grow past the silhouette into empty sky.
            int blocks = SweepVisitRadiusBlocks;
            int cs = GlobalConstants.ChunkSize;
            return Math.Max(4, (int)Math.Ceiling(blocks / (double)cs) + 2);
        }
    }

    /// <summary>750 visit baseline, 1000 leftover, or any overlay stream 1024–2048. Never restore as original.</summary>
    public static bool IsSweepHoldValue(int blocks) =>
        blocks == SweepBoostViewDistanceBlocks
        || blocks == LegacySweepHoldBlocks
        || (blocks >= MinOverlayStreamBlocks && blocks <= MaxVanillaViewDistance);

    /// <summary>
    /// A graphics slider the player actually set. Scan hold (750), old hold (1000),
    /// and the maxed leftover (~1536 / 1500) are never original.
    /// </summary>
    public static bool IsPlayerViewDistance(int blocks) =>
        blocks > 0 && blocks < SweepBoostViewDistanceBlocks;

    /// <summary>
    /// Prefer a stored player slider over live. Refuse 750, 1000, and maxed leftovers.
    /// </summary>
    public static bool TryPickPlayerView(int live, int stored, out int player)
    {
        if (IsPlayerViewDistance(stored))
        {
            player = stored;
            return true;
        }

        if (IsPlayerViewDistance(live))
        {
            player = live;
            return true;
        }

        player = 0;
        return false;
    }

    /// <summary>
    /// If a previous overlay crashed or vanilla left 750/1000/1536 on the slider, write
    /// the saved player view back before play or before the next hold.
    /// Only a live slider below 750 is remembered as original.
    /// </summary>
    public static void RecoverPlayerViewIfNeeded(ICoreClientAPI capi)
    {
        if (capi.World?.Player?.WorldData == null) return;

        int live = ReadClientViewDistance(capi, 0);
        int liveDesired = 0;
        int liveApproved = 0;
        try { liveDesired = capi.World.Player.WorldData.DesiredViewDistance; } catch { }
        try { liveApproved = capi.World.Player.WorldData.LastApprovedViewDistance; } catch { }

        LodLoginBakeViewHoldStore? store = LodLoginBakeViewHoldStore.TryLoad(capi);
        if (IsPlayerViewDistance(live) && (store == null || !store.Armed))
        {
            store ??= new LodLoginBakeViewHoldStore();
            store.Armed = false;
            store.Slider = live;
            store.Desired = IsPlayerViewDistance(liveDesired) ? liveDesired : live;
            if (liveApproved > 0) store.Approved = liveApproved;
            store.Save(capi);
        }

        if (store == null) return;
        if (!store.Armed && IsPlayerViewDistance(live)) return;
        if (!TryPickPlayerView(live, store.Slider, out int slider)) return;

        WritePlayerView(capi, slider, store.Desired, store.Approved);
        store.Armed = false;
        store.Save(capi);
        capi.Logger.Notification(
            "[DistantVistas] Restored graphics view slider {0} (was {1}) from before the login visit.",
            slider, live);
        AgentViewLog("view-recover", slider, live, store.Desired, store.Approved, store.Armed);
    }

    public void EnsureBoosted(int finishedL0 = 0)
    {
        IWorldPlayerData data = capi.World.Player.WorldData;
        desiredStreamBlocks = OverlayStreamBlocks(finishedL0);
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lastHitchMs = lastEnsureMs == 0
            ? 0
            : (int)Math.Min(int.MaxValue, nowMs - lastEnsureMs);
        lastEnsureMs = nowMs;
        hitchPressure = lastHitchMs >= StreamGrowHitchPressureMs;

        int approvedNow = 0;
        try { approvedNow = data.LastApprovedViewDistance; } catch { }

        int target;
        if (!applied)
        {
            int clientNow = ReadClientViewDistance(capi, MinOverlayStreamBlocks);
            CapturePlayerView(data, clientNow, approvedNow);
            applied = true;
            target = MinOverlayStreamBlocks;
            lastGrowMs = nowMs;
        }
        else
        {
            int current = boostedViewDistance > 0 ? boostedViewDistance : MinOverlayStreamBlocks;
            target = StepAppliedStream(current, desiredStreamBlocks, nowMs, lastGrowMs, hitchPressure);
        }

        int sliderNow = ReadClientViewDistance(capi, target);
        if (sliderNow != target)
            WriteClientViewDistance(capi, target);

        int previousStream = boostedViewDistance;

        if (data.DesiredViewDistance != target)
        {
            data.DesiredViewDistance = target;
            LodVsCompat.TryUpdatePartitioning(capi.World.Player.Entity);
        }

        if (data.LastApprovedViewDistance > 0 && data.LastApprovedViewDistance < target)
            data.LastApprovedViewDistance = target;
        else if (data.LastApprovedViewDistance > 0 && data.LastApprovedViewDistance > target
                 && target >= MinOverlayStreamBlocks)
            data.LastApprovedViewDistance = target;

        boostedViewDistance = target;

        if (target > previousStream && target > MinOverlayStreamBlocks)
        {
            lastGrowMs = nowMs;
            LodScoutSeqDiag.LogStreamGrow(
                finishedL0, target, desiredStreamBlocks, lastHitchMs, hitchPressure);
            if (previousStream > 0)
            {
                capi.Logger.Notification(
                    "[DistantVistas] Overlay stream grew {0}→{1} (desired {2}) at finished {3} (filled radius {4}).",
                    previousStream,
                    target,
                    desiredStreamBlocks,
                    finishedL0,
                    LodLoginScoutFill.FinishedToRadiusBlocks(finishedL0));
            }
        }
        else if (target > previousStream && previousStream > 0)
            lastGrowMs = nowMs;

        if (renderer.FarViewDistanceCap != 0)
            renderer.FarViewDistanceCap = 0;

        float overdraw = GameMath.Clamp(SweepOverdrawStart, 0.15f, 0.95f);
        if (Math.Abs(renderer.OverdrawStart - overdraw) > 0.001f)
            renderer.OverdrawStart = overdraw;

        if (!loggedBoost)
        {
            loggedBoost = true;
            capi.Logger.Notification(
                "[DistantVistas] Login sweep view hold: slider {0}→{1}, DesiredViewDistance {2}→{3}, LastApproved {4}→{5}, FarViewDistanceCap {6}→0, OverdrawStart {7:0.00}→{8:0.00}, chunk sweep radius {9}.",
                savedClientViewDistance ?? sliderNow,
                ReadClientViewDistance(capi, target),
                savedDesiredViewDistance ?? target,
                target,
                savedLastApprovedViewDistance ?? approvedNow,
                data.LastApprovedViewDistance,
                savedFarViewDistanceCap,
                savedOverdrawStart,
                overdraw,
                ChunkSweepRadiusChunks);
            AgentViewLog(
                "view-boost-apply",
                savedClientViewDistance ?? -1,
                target,
                savedDesiredViewDistance ?? -1,
                savedLastApprovedViewDistance ?? -1,
                true);
        }

        try { renderer.ApplyZFar(); } catch { }
    }

    public void Restore()
    {
        if (!applied) return;

        int before = 0;
        int after = 0;
        int saved = savedDesiredViewDistance ?? -1;
        int lastApproved = 0;
        int sliderBefore = ReadClientViewDistance(capi, -1);
        int sliderAfter = sliderBefore;
        try { before = capi.World.Player.WorldData.DesiredViewDistance; } catch { }
        try { lastApproved = capi.World.Player.WorldData.LastApprovedViewDistance; } catch { }

        try
        {
            ApplySavedPlayerView();
            renderer.FarViewDistanceCap = savedFarViewDistanceCap;
            renderer.OverdrawStart = savedOverdrawStart;
            try { renderer.ApplyZFar(); } catch { }
            try { after = capi.World.Player.WorldData.DesiredViewDistance; } catch { }
            sliderAfter = ReadClientViewDistance(capi, sliderBefore);
            LodLoginBakeInputLock.RestoreLook(capi);
            DisarmStore();
        }
        finally
        {
            AgentViewLog(
                "view-boost-restore",
                savedClientViewDistance ?? -1,
                sliderAfter,
                after,
                savedLastApprovedViewDistance ?? -1,
                false);
            boostedViewDistance = 0;
            applied = false;
            loggedBoost = false;
            desiredStreamBlocks = 0;
            lastEnsureMs = 0;
            lastGrowMs = 0;
            lastHitchMs = 0;
            hitchPressure = false;
        }

        _ = before;
        _ = lastApproved;
        _ = sliderBefore;
        _ = saved;
    }

    /// <summary>
    /// Pose restore / RequestMode can copy the slider again. Write the saved player
    /// view a second time after that, without treating 750 as original.
    /// </summary>
    public void ReassertPlayerView()
    {
        if (savedClientViewDistance == null && savedDesiredViewDistance == null)
        {
            RecoverPlayerViewIfNeeded(capi);
            return;
        }

        ApplySavedPlayerView();
        renderer.OverdrawStart = savedOverdrawStart;
        renderer.FarViewDistanceCap = savedFarViewDistanceCap;
        try { renderer.ApplyZFar(); } catch { }
    }

    void CapturePlayerView(IWorldPlayerData data, int clientNow, int approvedNow)
    {
        LodLoginBakeViewHoldStore? store = LodLoginBakeViewHoldStore.TryLoad(capi);
        int storedSlider = store?.Slider ?? 0;
        int storedDesired = store?.Desired ?? 0;
        int storedApproved = store?.Approved ?? 0;

        if (TryPickPlayerView(clientNow, storedSlider, out int slider))
            savedClientViewDistance = slider;

        int liveDesired = data.DesiredViewDistance;
        if (TryPickPlayerView(liveDesired, storedDesired, out int desired))
            savedDesiredViewDistance = desired;
        else if (savedClientViewDistance.HasValue)
            savedDesiredViewDistance = savedClientViewDistance;

        savedLastApprovedViewDistance = storedApproved > 0 ? storedApproved : approvedNow;
        savedFarViewDistanceCap = renderer.FarViewDistanceCap;
        savedOverdrawStart = renderer.OverdrawStart;

        if (savedClientViewDistance is int keep)
        {
            var next = new LodLoginBakeViewHoldStore
            {
                Armed = true,
                Slider = keep,
                Desired = savedDesiredViewDistance ?? keep,
                Approved = savedLastApprovedViewDistance ?? 0,
            };
            next.Save(capi);
        }
    }

    void ApplySavedPlayerView()
    {
        try
        {
            IWorldPlayerData data = capi.World.Player.WorldData;
            if (savedClientViewDistance.HasValue && IsPlayerViewDistance(savedClientViewDistance.Value))
                WriteClientViewDistance(capi, savedClientViewDistance.Value);

            int desired = savedDesiredViewDistance ?? savedClientViewDistance ?? 0;
            if (IsPlayerViewDistance(desired))
            {
                data.DesiredViewDistance = desired;
                LodVsCompat.TryUpdatePartitioning(capi.World.Player.Entity);
            }

            if (savedLastApprovedViewDistance.HasValue && savedLastApprovedViewDistance.Value > 0)
                data.LastApprovedViewDistance = savedLastApprovedViewDistance.Value;
        }
        catch { }
    }

    void DisarmStore()
    {
        LodLoginBakeViewHoldStore? store = LodLoginBakeViewHoldStore.TryLoad(capi)
            ?? new LodLoginBakeViewHoldStore();
        if (savedClientViewDistance is int slider && IsPlayerViewDistance(slider))
            store.Slider = slider;
        if (savedDesiredViewDistance is int desired && IsPlayerViewDistance(desired))
            store.Desired = desired;
        if (savedLastApprovedViewDistance is int approved && approved > 0)
            store.Approved = approved;
        store.Armed = false;
        store.Save(capi);
    }

    static void WritePlayerView(ICoreClientAPI capi, int slider, int desired, int approved)
    {
        if (IsPlayerViewDistance(slider))
            WriteClientViewDistance(capi, slider);

        try
        {
            IWorldPlayerData data = capi.World.Player.WorldData;
            int wantDesired = IsPlayerViewDistance(desired) ? desired : slider;
            if (IsPlayerViewDistance(wantDesired))
            {
                data.DesiredViewDistance = wantDesired;
                LodVsCompat.TryUpdatePartitioning(capi.World.Player.Entity);
            }

            if (approved > 0)
                data.LastApprovedViewDistance = approved;
        }
        catch { }
    }

    static int ReadClientViewDistance(ICoreClientAPI capi, int fallback)
    {
        try
        {
            ISettingsClass<int> ints = capi.Settings.Int;
            if (ints.Exists(ViewDistanceSettingKey))
                return ints[ViewDistanceSettingKey];
        }
        catch { }
        return fallback;
    }

    static void WriteClientViewDistance(ICoreClientAPI capi, int blocks)
    {
        // Never persist leftover 1000. Overlay streams (1024–2048) write on purpose;
        // player sliders are always below the 750 visit baseline.
        if (blocks == LegacySweepHoldBlocks)
            return;
        try
        {
            ISettingsClass<int> ints = capi.Settings.Int;
            ints.Set(ViewDistanceSettingKey, blocks, true);
        }
        catch { }
    }

    static void AgentViewLog(string message, int slider, int now, int desired, int approved, bool armed)
    {
        try
        {
            File.AppendAllText(
                @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                "{\"sessionId\":\"40cccb\",\"runId\":\"vd-750\",\"hypothesisId\":\"H-VD\",\"location\":\"LodLoginBakeViewBoost\",\"message\":\""
                + message
                + "\",\"data\":{\"slider\":" + slider
                + ",\"now\":" + now
                + ",\"desired\":" + desired
                + ",\"approved\":" + approved
                + ",\"armed\":" + (armed ? "true" : "false")
                + ",\"holdFloor\":" + MinOverlayStreamBlocks
                + ",\"visitHold\":" + SweepBoostViewDistanceBlocks
                + "},\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n");
        }
        catch { }
    }

    internal static int ResolveBoostViewDistance(IWorldPlayerData data)
    {
        _ = data;
        return OverlayStreamBlocks(0);
    }
}
