using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace DistantVistas;

/// <summary>
/// Temporary player-style render/stream anchor. Vintage Story auto-generates and
/// tessellates around <see cref="EntityPlayer"/> / <see cref="IPlayer"/> — there is
/// no dummy-player API, and a token that only calls <c>SetChunkColumnVisible</c> is
/// not a center. Each login scout is a real <see cref="Entity"/> parked on a visit
/// cell (not a subclass of <see cref="EntityPlayer"/> — that type is tied to an
/// <see cref="IPlayer"/>). Player-like far presence: <see cref="AllowOutsideLoadedRange"/>,
/// <see cref="AlwaysActive"/>, <see cref="StoreWithChunk"/> false (players are stored
/// separately). Server WorldManager still only auto-loads around real players, so the
/// host KeepLoaded + ForceSend-s the neighbourhood to the baker. Then fully despawned.
/// </summary>
public sealed class LodScoutViewerEntity : Entity
{
    public const string ClassName = "LodScoutViewer";
    public static readonly AssetLocation TypeCode = new("distantvistas", "scoutviewer");

    public long VisitKey;

    public override bool IsInteractable => false;

    /// <summary>Stay until <see cref="Die"/>; then the engine may remove us.</summary>
    public override bool ShouldDespawn => !Alive;

    /// <summary>
    /// Players (and what they ride) are stored separately. Scouts must not persist
    /// into a chunk save if overlay teardown races an autosave.
    /// </summary>
    public override bool StoreWithChunk => false;

    /// <summary>
    /// Sit on a far column before that column is loaded — the default culls
    /// non-players outside the loaded range.
    /// </summary>
    public override bool AllowOutsideLoadedRange => true;

    public override void Initialize(EntityProperties properties, ICoreAPI api, long chunkIndex3d)
    {
        AlwaysActive = true;
        base.Initialize(properties, api, chunkIndex3d);
        IsRendered = false;
        Pos.Motion.Set(0, 0, 0);
        ServerPos.Motion.Set(0, 0, 0);
    }

    public override void OnGameTick(float dt)
    {
        Pos.Motion.Set(0, 0, 0);
        ServerPos.Motion.Set(0, 0, 0);
        Pos.SetFrom(ServerPos);
        IsRendered = false;
        AlwaysActive = true;
    }

    public override bool ShouldReceiveDamage(DamageSource damageSource, float damage) => false;

    public override bool CanCollect(Entity byEntity) => false;

    /// <summary>
    /// Spawn a viewer at the visit cell on this side (client and/or server).
    /// The real player stays at pickup.
    /// </summary>
    public static LodScoutViewerEntity? SpawnAt(
        ICoreAPI api,
        long visitKey,
        double x,
        double y,
        double z)
    {
        if (api?.World == null) return null;

        EntityProperties? type = api.World.GetEntityType(TypeCode);
        Entity entity;
        try
        {
            if (type != null)
                entity = api.ClassRegistry.CreateEntity(type);
            else
                entity = api.ClassRegistry.CreateEntity(ClassName);
        }
        catch
        {
            return null;
        }

        if (entity is not LodScoutViewerEntity viewer)
            return null;

        viewer.VisitKey = visitKey;
        viewer.AlwaysActive = true;
        viewer.ServerPos.SetPos(x, y, z);
        viewer.Pos.SetFrom(viewer.ServerPos);
        viewer.Pos.Motion.Set(0, 0, 0);
        viewer.ServerPos.Motion.Set(0, 0, 0);
        viewer.IsRendered = false;

        if (type != null)
        {
            try { viewer.Initialize(type, api, 0); }
            catch { }
        }

        viewer.AlwaysActive = true;
        viewer.IsRendered = false;
        try { api.World.SpawnPriorityEntity(viewer); }
        catch
        {
            try { api.World.SpawnEntity(viewer); }
            catch { }
        }

        viewer.IsRendered = false;
        viewer.AlwaysActive = true;
        LodVsCompat.TryUpdatePartitioning(viewer);
        LodVsCompat.TryIndexLoadedEntity(api.World, viewer);
        string side = api.Side == EnumAppSide.Server ? "server" : "client";
        LodScoutSeqDiag.LogViewerSpawn(side, visitKey, x, y, z);
        return viewer;
    }

    public static void DespawnOne(IWorldAccessor? world, Entity? entity)
    {
        if (entity == null) return;
        long visitKey = entity is LodScoutViewerEntity scout ? scout.VisitKey : 0;
        long id = entity.EntityId;
        try
        {
            entity.Die(EnumDespawnReason.Removed);
        }
        catch { }

        if (world == null) return;

        if (world is IServerWorldAccessor server)
        {
            try
            {
                server.DespawnEntity(entity, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
            }
            catch { }
        }

        LodVsCompat.TryRemoveLoadedEntity(world, id);
        if (visitKey != 0)
        {
            string side = world is IServerWorldAccessor ? "server" : "client";
            LodScoutSeqDiag.LogViewerDespawn(side, visitKey, 1);
        }
    }

    [ThreadStatic] static List<Entity>? despawnScratch;

    /// <summary>
    /// Tear down every scout viewer. Login overlay end, Esc, fail, and world-leave
    /// must leave the real player as the only render/stream center.
    /// </summary>
    public static int DespawnAll(IWorldAccessor world)
    {
        IDictionary<long, Entity>? loaded = LodVsCompat.TryGetLoadedEntities(world);
        if (loaded == null) return 0;
        List<Entity> doomed = despawnScratch ??= new List<Entity>(LodLoginScoutFill.MaxConcurrent);
        doomed.Clear();
        foreach (Entity entity in loaded.Values)
        {
            if (entity is LodScoutViewerEntity)
                doomed.Add(entity);
        }

        for (int i = 0; i < doomed.Count; i++)
            DespawnOne(world, doomed[i]);
        return doomed.Count;
    }
}
