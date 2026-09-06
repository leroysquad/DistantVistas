using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Temporarily raises vanilla view distance and DV cover knobs during the login visit
/// sweep so each stop streams and captures as much terrain as the PC/server allows,
/// then restores the player's prior settings on finish, Esc, or cancel.
/// </summary>
public sealed class LodLoginBakeViewBoost
{
    /// <summary>VS client hard ceiling for view distance (blocks).</summary>
    public const int MaxVanillaViewDistance = 2048;

    /// <summary>Floor during login sweep — wide column sweep / batch bake per stop.</summary>
    public const int SweepMinViewDistanceBlocks = 2000;

    /// <summary>Lower overdraw during sweep = LOD draws closer under vanilla for wider cover.</summary>
    public const float SweepOverdrawStart = 0.35f;

    readonly ICoreClientAPI capi;
    readonly LodTerrainRenderer renderer;

    int? savedDesiredViewDistance;
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
            int vd = applied ? boostedViewDistance : (int)renderer.LiveViewDistance;
            int cs = GlobalConstants.ChunkSize;
            return Math.Max(4, (int)Math.Ceiling(vd / (double)cs) + 2);
        }
    }

    public int ChunkVisibleRadius
    {
        get
        {
            int vd = applied ? boostedViewDistance : (int)renderer.LiveViewDistance;
            int cs = GlobalConstants.ChunkSize;
            return GameMath.Clamp((int)Math.Ceiling(vd / (double)cs / 4.0), 2, 16);
        }
    }

    public void EnsureBoosted()
    {
        IWorldPlayerData data = capi.World.Player.WorldData;
        int target = ResolveBoostViewDistance(data);
        if (!applied)
        {
            savedDesiredViewDistance = data.DesiredViewDistance;
            savedFarViewDistanceCap = renderer.FarViewDistanceCap;
            savedOverdrawStart = renderer.OverdrawStart;
            applied = true;
        }

        if (data.DesiredViewDistance != target)
        {
            data.DesiredViewDistance = target;
            capi.World.Player.Entity.UpdatePartitioning();
        }

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
                "[DistantVistas] Login sweep view boost: vanilla {0}→{1} blocks, FarViewDistanceCap {2}→0 (unlimited), OverdrawStart {3:0.00}→{4:0.00}, chunk sweep radius {5}.",
                savedDesiredViewDistance ?? target,
                target,
                savedFarViewDistanceCap,
                savedOverdrawStart,
                overdraw,
                ChunkSweepRadiusChunks);
        }

        try { renderer.ApplyZFar(); } catch { }
    }

    public void Restore()
    {
        if (!applied) return;

        try
        {
            IWorldPlayerData data = capi.World.Player.WorldData;
            if (savedDesiredViewDistance.HasValue)
            {
                data.DesiredViewDistance = savedDesiredViewDistance.Value;
                capi.World.Player.Entity.UpdatePartitioning();
            }

            renderer.FarViewDistanceCap = savedFarViewDistanceCap;
            renderer.OverdrawStart = savedOverdrawStart;
            try { renderer.ApplyZFar(); } catch { }
        }
        finally
        {
            savedDesiredViewDistance = null;
            boostedViewDistance = 0;
            applied = false;
            loggedBoost = false;
        }
    }

    internal static int ResolveBoostViewDistance(IWorldPlayerData data)
    {
        int ceiling = data.LastApprovedViewDistance > 0
            ? data.LastApprovedViewDistance
            : MaxVanillaViewDistance;
        ceiling = GameMath.Clamp(ceiling, 128, MaxVanillaViewDistance);
        int boosted = Math.Max(data.DesiredViewDistance, ceiling);
        boosted = Math.Max(boosted, SweepMinViewDistanceBlocks);
        return GameMath.Clamp(boosted, 128, MaxVanillaViewDistance);
    }
}
