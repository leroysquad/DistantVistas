using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Holds vanilla graphics view at 750 for the login visit scan and bake, then
/// writes back the slider the player had before Distant Vistas touched it.
/// Never holds 1000. Never restores 750, leftover 1000, or a maxed ~1536/1500 as original.
/// </summary>
public sealed class LodLoginBakeViewBoost
{
    /// <summary>VS client hard ceiling for view distance (blocks).</summary>
    public const int MaxVanillaViewDistance = 2048;

    /// <summary>Floor if the player has no desired view set.</summary>
    public const int SweepMinViewDistanceBlocks = 256;

    /// <summary>
    /// Login visit holds vanilla graphics view at this distance (blocks), then restores
    /// the player's slider. Must write <c>ClientSettings.viewDistance</c> — DesiredViewDistance
    /// alone is overwritten from the slider every RequestMode.
    /// </summary>
    public const int SweepBoostViewDistanceBlocks = 750;

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
    /// The 750-hold disk keeps spawn-local chunks solid. The FlagBaked 4075 disk
    /// is scout visit coverage, not a 4 km tessellation storm.
    /// </summary>
    public int SpawnStreamRadiusChunks
    {
        get
        {
            int cs = GlobalConstants.ChunkSize;
            return Math.Max(4, (int)Math.Ceiling(SweepBoostViewDistanceBlocks / (double)cs) + 2);
        }
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

    /// <summary>750 (this hold) or 1000 (old hold). Not a value we may restore as original.</summary>
    public static bool IsSweepHoldValue(int blocks) =>
        blocks == SweepBoostViewDistanceBlocks || blocks == LegacySweepHoldBlocks;

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

    public void EnsureBoosted()
    {
        IWorldPlayerData data = capi.World.Player.WorldData;
        int target = ResolveBoostViewDistance(data);
        int clientNow = ReadClientViewDistance(capi, target);
        int approvedNow = 0;
        try { approvedNow = data.LastApprovedViewDistance; } catch { }

        if (!applied)
        {
            CapturePlayerView(data, clientNow, approvedNow);
            applied = true;
        }

        if (clientNow != target)
            WriteClientViewDistance(capi, target);

        if (data.DesiredViewDistance != target)
        {
            data.DesiredViewDistance = target;
            capi.World.Player.Entity.UpdatePartitioning();
        }

        if (data.LastApprovedViewDistance > 0 && data.LastApprovedViewDistance < target)
            data.LastApprovedViewDistance = target;

        boostedViewDistance = target;

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
                savedClientViewDistance ?? clientNow,
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
                capi.World.Player.Entity.UpdatePartitioning();
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
                capi.World.Player.Entity.UpdatePartitioning();
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
        if (IsSweepHoldValue(blocks) && blocks != SweepBoostViewDistanceBlocks)
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
                + ",\"hold\":" + SweepBoostViewDistanceBlocks
                + "},\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n");
        }
        catch { }
    }

    internal static int ResolveBoostViewDistance(IWorldPlayerData data)
    {
        _ = data;
        return GameMath.Clamp(SweepBoostViewDistanceBlocks, SweepMinViewDistanceBlocks, MaxVanillaViewDistance);
    }
}
