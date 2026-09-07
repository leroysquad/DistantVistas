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

    public static LodScoutHostSystem? ClientInstance { get; private set; }

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
    }

    public override void Dispose()
    {
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
        }
        catch { }
    }

    public void RequestDown(long key)
    {
        if (clientChannel == null || !clientChannel.Connected) return;
        try { clientChannel.SendPacket(new ScoutAnchorDown { Key = key }); }
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
        int radius = Math.Clamp(msg.Radius, 1, 24);
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

        if (holds.Count >= 24 && !holds.ContainsKey(msg.Key))
            return;

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
                    OnLoaded = () =>
                    {
                        try { sapi.WorldManager.ForceSendChunkColumn(player, sendCx, sendCz, dim); }
                        catch { }
                    },
                });
            }
        }
    }

    void DropAnchor(IServerPlayer player, long key)
    {
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
    }

    void ReleasePlayer(IServerPlayer player, string uid)
    {
        if (sapi != null)
        {
            LodScoutViewerEntity.DespawnAll(sapi.World);
            var extra = new List<Entity>();
            foreach (Entity entity in sapi.World.LoadedEntities.Values)
            {
                if (entity is LodScoutViewerEntity)
                    extra.Add(entity);
            }
            var gone = new EntityDespawnData { Reason = EnumDespawnReason.Removed };
            for (int i = 0; i < extra.Count; i++)
            {
                try { sapi.World.DespawnEntity(extra[i], gone); } catch { }
            }
        }

        if (!holdsByPlayer.TryGetValue(uid, out Dictionary<long, ScoutHold>? holds))
            return;
        holdsByPlayer.Remove(uid);
        foreach (ScoutHold hold in holds.Values)
            ReleaseHoldColumns(player, hold, stillNeeded: null);
    }

    void ReleaseHoldColumns(IServerPlayer player, ScoutHold hold, Dictionary<long, ScoutHold>? stillNeeded)
    {
        if (sapi == null) return;
        int pcx = 0, pcz = 0, keepR = 8;
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
            keepR = Math.Max(8, (int)Math.Ceiling(vd / (double)GlobalConstants.ChunkSize) + 2);
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
}
