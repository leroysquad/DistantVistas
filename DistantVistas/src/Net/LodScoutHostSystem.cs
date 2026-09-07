using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using DistantVistas.Net;

namespace DistantVistas;

/// <summary>
/// Both-sides scout viewer: register the entity class, spawn a real entity at each
/// visit cell, and KeepLoaded + ForceSend chunk columns around it (WorldManager
/// auto-gen only follows real <see cref="IPlayer"/>s — VS has no dummy player).
/// Teardown Die + DespawnEntity + UnloadChunkColumn so nothing keeps meshing 4 km out.
/// </summary>
public sealed class LodScoutHostSystem : ModSystem
{
    ICoreClientAPI? capi;
    ICoreServerAPI? sapi;
    IClientNetworkChannel? clientChannel;
    IServerNetworkChannel? serverChannel;

    readonly Dictionary<string, Dictionary<long, ScoutHold>> holdsByPlayer = new();
    readonly Dictionary<long, int> columnRefs = new();
    readonly Dictionary<string, Queue<ScoutAnchorUp>> pendingUpsByPlayer = new();
    readonly Queue<ForceSendWork> forceSends = new();
    long tickListenerId;

    public static LodScoutHostSystem? ClientInstance { get; private set; }

    /// <summary>Client MaxConcurrent. Far KeepLoaded spam is refused past this.</summary>
    public const int MaxConcurrentHolds = 16;
    /// <summary>KeepLoaded Chebyshev radius. Near scouts use this; far scouts send less.</summary>
    public const int MaxHoldRadiusChunks = 4;
    /// <summary>OnLoaded ForceSend budget so 16 scouts do not dump hundreds of columns in one tick.</summary>
    public const int MaxForceSendPerTick = 48;
    public const int MaxPendingUps = 32;

    public override double ExecuteOrder() => 0.05;

    public override bool ShouldLoad(EnumAppSide forSide) => true;

    public override void Start(ICoreAPI api)
    {
        api.RegisterEntity(LodScoutViewerEntity.ClassName, typeof(LodScoutViewerEntity));
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        ClientInstance = this;
        clientChannel = api.Network.RegisterChannel(LodScoutNet.ChannelName)
            .RegisterMessageType<ScoutAnchorUp>()
            .RegisterMessageType<ScoutAnchorDown>()
            .RegisterMessageType<ScoutAnchorsClear>();
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        serverChannel = api.Network.RegisterChannel(LodScoutNet.ChannelName)
            .RegisterMessageType<ScoutAnchorUp>()
            .RegisterMessageType<ScoutAnchorDown>()
            .RegisterMessageType<ScoutAnchorsClear>()
            .SetMessageHandler<ScoutAnchorUp>(OnAnchorUp)
            .SetMessageHandler<ScoutAnchorDown>(OnAnchorDown)
            .SetMessageHandler<ScoutAnchorsClear>(OnAnchorsClear);

        api.Event.PlayerDisconnect += player =>
        {
            if (player?.PlayerUID != null)
                ReleasePlayer(player, player.PlayerUID);
        };
        tickListenerId = api.Event.RegisterGameTickListener(OnServerTick, 50);
    }

    public override void Dispose()
    {
        if (sapi != null && tickListenerId != 0)
        {
            try { sapi.Event.UnregisterGameTickListener(tickListenerId); } catch { }
            tickListenerId = 0;
        }
        if (ReferenceEquals(ClientInstance, this))
            ClientInstance = null;
        base.Dispose();
    }

    public bool ChannelConnected => clientChannel != null && clientChannel.Connected;

    public void RequestUp(long key, int cx, int cz, int radius, int dimension, double x, double y, double z)
    {
        if (clientChannel == null || !clientChannel.Connected) return;
        try
        {
            clientChannel.SendPacket(new ScoutAnchorUp
            {
                Key = key,
                Cx = cx,
                Cz = cz,
                Radius = radius,
                Dimension = dimension,
                X = x,
                Y = y,
                Z = z,
            });
            LodScoutSeqDiag.LogHostUp(key, cx, cz, radius, capped: false, pending: false);
        }
        catch { }
    }

    public void RequestDown(long key)
    {
        if (clientChannel == null || !clientChannel.Connected) return;
        try
        {
            clientChannel.SendPacket(new ScoutAnchorDown { Key = key });
            LodScoutSeqDiag.LogHostDown(key, "client");
        }
        catch { }
    }

    public void RequestClear()
    {
        if (capi != null)
            LodScoutViewerEntity.DespawnAll(capi.World);

        if (clientChannel == null || !clientChannel.Connected) return;
        try { clientChannel.SendPacket(new ScoutAnchorsClear { Unused = true }); }
        catch { }
    }

    void OnAnchorUp(IServerPlayer fromPlayer, ScoutAnchorUp msg)
    {
        if (sapi == null || fromPlayer == null) return;
        sapi.Event.EnqueueMainThreadTask(
            () => HoldAnchor(fromPlayer, msg),
            "dv-scout-up");
    }

    void OnAnchorDown(IServerPlayer fromPlayer, ScoutAnchorDown msg)
    {
        if (sapi == null || fromPlayer == null) return;
        sapi.Event.EnqueueMainThreadTask(
            () => DropAnchor(fromPlayer, msg.Key),
            "dv-scout-down");
    }

    void OnAnchorsClear(IServerPlayer fromPlayer, ScoutAnchorsClear msg)
    {
        _ = msg;
        if (sapi == null || fromPlayer == null) return;
        sapi.Event.EnqueueMainThreadTask(
            () => ReleasePlayer(fromPlayer, fromPlayer.PlayerUID),
            "dv-scout-clear");
    }

    void HoldAnchor(IServerPlayer player, ScoutAnchorUp msg)
    {
        if (sapi == null) return;
        int radius = Math.Clamp(msg.Radius, 1, MaxHoldRadiusChunks);
        int dim = msg.Dimension;
        try
        {
            EntityPos pos = player.Entity.Pos;
            int pcx = (int)Math.Floor(pos.X / GlobalConstants.ChunkSize);
            int pcz = (int)Math.Floor(pos.Z / GlobalConstants.ChunkSize);
            // Onset disk is ~4075/32 ≈ 127 chunks. Reject far-away KeepLoaded spam.
            if (Math.Max(Math.Abs(msg.Cx - pcx), Math.Abs(msg.Cz - pcz)) > 160)
                return;
        }
        catch { return; }

        if (!holdsByPlayer.TryGetValue(player.PlayerUID, out Dictionary<long, ScoutHold>? holds))
        {
            holds = new Dictionary<long, ScoutHold>();
            holdsByPlayer[player.PlayerUID] = holds;
        }

        if (holds.Count >= MaxConcurrentHolds && !holds.ContainsKey(msg.Key))
        {
            EnqueuePendingUp(player.PlayerUID, msg);
            LodScoutSeqDiag.LogHostUp(msg.Key, msg.Cx, msg.Cz, radius, capped: true, pending: true);
            return;
        }

        var hold = new ScoutHold { Key = msg.Key, Cx = msg.Cx, Cz = msg.Cz, Radius = radius, Dimension = dim };

        if (holds.TryGetValue(msg.Key, out ScoutHold? prev))
        {
            LodScoutViewerEntity.DespawnOne(sapi.World, prev.Viewer);
            ReleaseHoldColumns(player, prev, stillNeeded: holds);
        }

        double x = msg.X, y = msg.Y, z = msg.Z;
        if (Math.Abs(x) < 0.01 && Math.Abs(z) < 0.01)
        {
            x = (msg.Cx + 0.5) * GlobalConstants.ChunkSize;
            z = (msg.Cz + 0.5) * GlobalConstants.ChunkSize;
            y = sapi.World.SeaLevel + 2;
        }

        try { hold.Viewer = LodScoutViewerEntity.SpawnAt(sapi, msg.Key, x, y, z); }
        catch { hold.Viewer = null; }

        holds[msg.Key] = hold;

        int pendingUps = pendingUpsByPlayer.TryGetValue(player.PlayerUID, out Queue<ScoutAnchorUp>? pq)
            ? pq.Count : 0;
        LodScoutSeqDiag.LogHostHold(msg.Key, holds.Count, pendingUps, forceSends.Count);

        for (int dz = -radius; dz <= radius; dz++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                int cx = msg.Cx + dx;
                int cz = msg.Cz + dz;
                if (cx < 0 || cz < 0) continue;
                long col = ColumnKey(cx, cz, dim);
                columnRefs.TryGetValue(col, out int n);
                columnRefs[col] = n + 1;
                int sendCx = cx;
                int sendCz = cz;
                sapi.WorldManager.LoadChunkColumnPriority(cx, cz, new ChunkLoadOptions
                {
                    KeepLoaded = true,
                    OnLoaded = () => EnqueueForceSend(player, sendCx, sendCz, dim),
                });
            }
        }
    }

    void DropAnchor(IServerPlayer player, long key)
    {
        RemovePendingUp(player.PlayerUID, key);
        if (!holdsByPlayer.TryGetValue(player.PlayerUID, out Dictionary<long, ScoutHold>? holds))
            return;
        if (!holds.TryGetValue(key, out ScoutHold? hold))
            return;
        holds.Remove(key);
        if (sapi != null)
            LodScoutViewerEntity.DespawnOne(sapi.World, hold.Viewer);
        hold.Viewer = null;
        ReleaseHoldColumns(player, hold, stillNeeded: holds);
        if (holds.Count == 0)
            holdsByPlayer.Remove(player.PlayerUID);
        DrainPendingUpsFor(player);
    }

    void OnServerTick(float dt)
    {
        _ = dt;
        DrainForceSends();
        DrainPendingUps();
    }

    void EnqueuePendingUp(string uid, ScoutAnchorUp msg)
    {
        if (!pendingUpsByPlayer.TryGetValue(uid, out Queue<ScoutAnchorUp>? q))
        {
            q = new Queue<ScoutAnchorUp>();
            pendingUpsByPlayer[uid] = q;
        }

        int n = q.Count;
        for (int i = 0; i < n; i++)
        {
            ScoutAnchorUp existing = q.Dequeue();
            if (existing.Key != msg.Key)
                q.Enqueue(existing);
        }

        if (q.Count >= MaxPendingUps)
            q.Dequeue();
        q.Enqueue(msg);
    }

    void RemovePendingUp(string uid, long key)
    {
        if (!pendingUpsByPlayer.TryGetValue(uid, out Queue<ScoutAnchorUp>? q) || q.Count == 0)
            return;
        int n = q.Count;
        for (int i = 0; i < n; i++)
        {
            ScoutAnchorUp existing = q.Dequeue();
            if (existing.Key != key)
                q.Enqueue(existing);
        }
        if (q.Count == 0)
            pendingUpsByPlayer.Remove(uid);
    }

    void DrainPendingUps()
    {
        if (sapi == null) return;
        var uids = new List<string>(pendingUpsByPlayer.Keys);
        for (int i = 0; i < uids.Count; i++)
        {
            string uid = uids[i];
            IPlayer? raw = null;
            try { raw = sapi.World.PlayerByUid(uid); }
            catch { }
            if (raw is not IServerPlayer player)
            {
                pendingUpsByPlayer.Remove(uid);
                continue;
            }
            DrainPendingUpsFor(player);
        }
    }

    void DrainPendingUpsFor(IServerPlayer player)
    {
        if (!pendingUpsByPlayer.TryGetValue(player.PlayerUID, out Queue<ScoutAnchorUp>? q))
            return;
        holdsByPlayer.TryGetValue(player.PlayerUID, out Dictionary<long, ScoutHold>? holds);

        while (q.Count > 0)
        {
            int live = holds?.Count ?? 0;
            if (live >= MaxConcurrentHolds)
                break;
            ScoutAnchorUp msg = q.Dequeue();
            HoldAnchor(player, msg);
            holdsByPlayer.TryGetValue(player.PlayerUID, out holds);
        }

        if (q.Count == 0)
            pendingUpsByPlayer.Remove(player.PlayerUID);
    }

    void EnqueueForceSend(IServerPlayer player, int cx, int cz, int dim)
    {
        if (forceSends.Count >= 2048)
            DrainForceSends(extra: MaxForceSendPerTick);
        forceSends.Enqueue(new ForceSendWork { Player = player, Cx = cx, Cz = cz, Dim = dim });
    }

    void DrainForceSends(int extra = 0)
    {
        if (sapi == null) return;
        int budget = MaxForceSendPerTick + extra;
        int n = 0;
        while (n < budget && forceSends.Count > 0)
        {
            ForceSendWork work = forceSends.Dequeue();
            try { sapi.WorldManager.ForceSendChunkColumn(work.Player, work.Cx, work.Cz, work.Dim); }
            catch { }
            n++;
        }
    }

    void ReleasePlayer(IServerPlayer player, string uid)
    {
        if (sapi != null)
        {
            LodScoutViewerEntity.DespawnAll(sapi.World);
            var extra = new List<Entity>();
            IDictionary<long, Entity>? loaded = LodVsCompat.TryGetLoadedEntities(sapi.World);
            if (loaded != null)
            {
                foreach (Entity entity in loaded.Values)
                {
                    if (entity is LodScoutViewerEntity)
                        extra.Add(entity);
                }
            }
            var gone = new EntityDespawnData { Reason = EnumDespawnReason.Removed };
            for (int i = 0; i < extra.Count; i++)
            {
                try { sapi.World.DespawnEntity(extra[i], gone); } catch { }
            }
        }

        pendingUpsByPlayer.Remove(uid);
        if (!holdsByPlayer.TryGetValue(uid, out Dictionary<long, ScoutHold>? holds))
            return;
        holdsByPlayer.Remove(uid);
        foreach (ScoutHold hold in holds.Values)
            ReleaseHoldColumns(player, hold, stillNeeded: null);
    }

    void ReleaseHoldColumns(IServerPlayer player, ScoutHold hold, Dictionary<long, ScoutHold>? stillNeeded)
    {
        if (sapi == null) return;
        int pcx = 0, pcz = 0, keepR = MaxHoldRadiusChunks;
        try
        {
            EntityPos pos = player.Entity.Pos;
            pcx = (int)Math.Floor(pos.X / GlobalConstants.ChunkSize);
            pcz = (int)Math.Floor(pos.Z / GlobalConstants.ChunkSize);
            int vd = 256;
            try { vd = player.WorldData.LastApprovedViewDistance; } catch { }
            if (vd <= 0)
            {
                try { vd = player.WorldData.DesiredViewDistance; } catch { }
            }
            if (vd <= 0) vd = 256;
            keepR = Math.Max(MaxHoldRadiusChunks, (int)Math.Ceiling(vd / (double)GlobalConstants.ChunkSize) + 2);
        }
        catch { }

        for (int dz = -hold.Radius; dz <= hold.Radius; dz++)
        {
            for (int dx = -hold.Radius; dx <= hold.Radius; dx++)
            {
                int cx = hold.Cx + dx;
                int cz = hold.Cz + dz;
                if (cx < 0 || cz < 0) continue;
                long col = ColumnKey(cx, cz, hold.Dimension);
                if (!columnRefs.TryGetValue(col, out int n))
                    continue;
                n--;
                if (n > 0)
                {
                    columnRefs[col] = n;
                    continue;
                }
                columnRefs.Remove(col);

                if (stillNeeded != null && StillHeld(stillNeeded, cx, cz, hold.Dimension))
                    continue;

                int chebyshev = Math.Max(Math.Abs(cx - pcx), Math.Abs(cz - pcz));
                if (chebyshev <= keepR)
                    continue;

                try { sapi.WorldManager.UnloadChunkColumn(cx, cz); }
                catch { }
            }
        }
    }

    static bool StillHeld(Dictionary<long, ScoutHold> holds, int cx, int cz, int dim)
    {
        foreach (ScoutHold hold in holds.Values)
        {
            if (hold.Dimension != dim) continue;
            if (Math.Abs(hold.Cx - cx) <= hold.Radius && Math.Abs(hold.Cz - cz) <= hold.Radius)
                return true;
        }
        return false;
    }

    static long ColumnKey(int cx, int cz, int dim) =>
        ((long)dim << 42) ^ ((long)cx << 21) ^ (uint)cz;

    sealed class ScoutHold
    {
        public long Key;
        public int Cx;
        public int Cz;
        public int Radius;
        public int Dimension;
        public LodScoutViewerEntity? Viewer;
    }

    sealed class ForceSendWork
    {
        public IServerPlayer Player = null!;
        public int Cx;
        public int Cz;
        public int Dim;
    }
}
