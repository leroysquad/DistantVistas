using System.Collections.Generic;
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
    readonly Dictionary<long, UpRequestState> lastUpByKey = new();
    readonly LodScoutHostPressureState hostPressure = new();
    readonly LodServerChunkRequestGate requestGate = new(
        MaxPriorityLoadsInFlight,
        MaxPriorityLoadsPerTick,
        MaxForceSendPerTick,
        MaxPriorityLoadQueue,
        MaxForceSendQueue);
    long tickListenerId;
    long lastHostTelemetryMs;
    long lastHostStatusMs;
    long hostStatusSequence;
    bool lastHostStatusPressure;
    bool hostStatusSent;
    long holdAccepted;
    long holdReplaced;
    long holdRefused;
    long holdEvicted;
    long pendingUpDropped;
    long holdPeak;
    readonly HashSet<string> hostStatusUids = new(StringComparer.Ordinal);

    public static LodScoutHostSystem? ClientInstance { get; private set; }
    internal static LodScoutHostSystem? ServerInstance { get; private set; }

    /// <summary>Client MaxConcurrent. Far KeepLoaded spam is refused past this.</summary>
    /// <summary>16 scouts + residency pump + spare so hop/unlock is not refused as the 17th hold.</summary>
    public const int MaxConcurrentHolds = 18;
    /// <summary>KeepLoaded Chebyshev radius. Near scouts use this; far scouts send less.</summary>
    public const int MaxHoldRadiusChunks = 4;
    /// <summary>OnLoaded ForceSend budget so scouts do not dump columns in one tick.</summary>
    public const int MaxForceSendPerTick = 2;
    public const int MaxPendingUps = 32;
    /// <summary>
    /// LoadChunkColumnPriority per server tick. 16 scouts × 9×9 rings used to enqueue
    /// hundreds of FIFO entries in one HoldAnchor (1.0.44 chunkdb autosave stall).
    /// </summary>
    public const int MaxPriorityLoadsPerTick = 2;
    public const int MaxPriorityLoadsInFlight = 24;
    public const int MaxPriorityLoadQueue = 256;
    public const int MaxForceSendQueue = 256;
    public const long RequestUpCooldownMs = 1500;
    public const long HostStatusIntervalMs = 500;

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
            .RegisterMessageType<ScoutAnchorsClear>()
            .RegisterMessageType<ScoutHostStatus>()
            .SetMessageHandler<ScoutHostStatus>(OnHostStatus);
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        ServerInstance = this;
        serverChannel = api.Network.RegisterChannel(LodScoutNet.ChannelName)
            .RegisterMessageType<ScoutAnchorUp>()
            .RegisterMessageType<ScoutAnchorDown>()
            .RegisterMessageType<ScoutAnchorsClear>()
            .RegisterMessageType<ScoutHostStatus>()
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
        if (ReferenceEquals(ServerInstance, this))
            ServerInstance = null;
        hostPressure.Reset();
        requestGate.Clear();
        base.Dispose();
    }

    public bool ChannelConnected => clientChannel != null && clientChannel.Connected;
    public bool HostPressureActive => hostPressure.IsActive(
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        lastUpByKey.Count > 0);
    public int HostPriorityPending => hostPressure.PriorityPending;
    public int HostPriorityInFlight => hostPressure.PriorityInFlight;
    public int HostForceSendPending => hostPressure.ForceSendPending;
    public long HostOldestInFlightMs => hostPressure.OldestInFlightMs;

    public void RequestUp(
        long key, int cx, int cz, int radius, int dimension, double x, double y, double z,
        bool priority = false)
    {
        if (clientChannel == null || !clientChannel.Connected) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        hostPressure.BeginSession(now);
        if (lastUpByKey.TryGetValue(key, out UpRequestState? previous)
            && previous.Cx == cx
            && previous.Cz == cz
            && previous.Radius == radius
            && previous.Dimension == dimension
            && previous.Priority == priority
            && previous.SentMs > 0
            && now - previous.SentMs < RequestUpCooldownMs)
        {
            LodScoutSeqDiag.LogHostUp(
                key, cx, cz, radius, capped: false, pending: true, priority: priority);
            return;
        }

        var next = new UpRequestState
        {
            Cx = cx,
            Cz = cz,
            Radius = radius,
            Dimension = dimension,
            Priority = priority,
            SentMs = 0,
        };
        lastUpByKey[key] = next;
        if (HostPressureActive)
        {
            LodScoutSeqDiag.LogHostUp(
                key, cx, cz, radius, capped: true, pending: true, priority: priority);
            return;
        }
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
                Priority = priority,
            });
            next.SentMs = now;
            LodScoutSeqDiag.LogHostUp(key, cx, cz, radius, capped: false, pending: false, priority: priority);
        }
        catch { }
    }

    public void RequestDown(long key)
    {
        lastUpByKey.Remove(key);
        if (lastUpByKey.Count == 0)
            hostPressure.Reset();
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
        lastUpByKey.Clear();
        hostPressure.Reset();
        if (capi != null)
            LodScoutViewerEntity.DespawnAll(capi.World);

        if (clientChannel == null || !clientChannel.Connected) return;
        try { clientChannel.SendPacket(new ScoutAnchorsClear { Unused = true }); }
        catch { }
    }

    void OnHostStatus(ScoutHostStatus status)
    {
        hostPressure.Note(status, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
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

        if (holds.TryGetValue(msg.Key, out ScoutHold? existing)
            && existing.Cx == msg.Cx && existing.Cz == msg.Cz
            && existing.Radius == radius && existing.Dimension == dim)
            return;

        if (holds.Count >= MaxConcurrentHolds && !holds.ContainsKey(msg.Key))
        {
            if (msg.Priority)
            {
                holdEvicted++;
                TryEvictFarthestHold(player, holds);
            }
            if (holds.Count >= MaxConcurrentHolds && !holds.ContainsKey(msg.Key))
            {
                holdRefused++;
                EnqueuePendingUp(player.PlayerUID, msg);
                LodScoutSeqDiag.LogHostUp(msg.Key, msg.Cx, msg.Cz, radius, capped: true, pending: true, priority: msg.Priority);
                return;
            }
        }

        var hold = new ScoutHold { Key = msg.Key, Cx = msg.Cx, Cz = msg.Cz, Radius = radius, Dimension = dim };

        if (holds.TryGetValue(msg.Key, out ScoutHold? prev))
        {
            holdReplaced++;
            LodScoutViewerEntity.DespawnOne(sapi.World, prev.Viewer);
            holds.Remove(msg.Key);
            ReleaseHoldColumns(player, prev, stillNeeded: holds);
        }
        else
        {
            holdAccepted++;
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
        holdPeak = Math.Max(holdPeak, holds.Count);

        int pendingUps = pendingUpsByPlayer.TryGetValue(player.PlayerUID, out Queue<ScoutAnchorUp>? pq)
            ? pq.Count : 0;
        LodScoutSeqDiag.LogHostHold(
            msg.Key, holds.Count, pendingUps,
            requestGate.ForceSendPending,
            requestGate.PriorityPending + requestGate.PriorityInFlight);

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
                string owner = HoldOwner(player.PlayerUID, msg.Key);
                LodServerQueueDecision decision = QueuePriorityLoad(
                    owner,
                    cx,
                    cz,
                    dim,
                    keepLoaded: true,
                    onLoaded: () => QueueForceSend(
                        owner, player.PlayerUID, cx, cz, dim,
                        LodServerChunkWorkPriority.Login),
                    LodServerChunkWorkPriority.Login);
                if (decision == LodServerQueueDecision.Dropped)
                    hold.NeedsAdmissionRetry = true;
                else
                    hold.AdmittedColumns.Add(col);
            }
        }
    }

    void TryEvictFarthestHold(IServerPlayer player, Dictionary<long, ScoutHold> holds)
    {
        if (holds.Count == 0) return;
        int pcx = 0;
        int pcz = 0;
        try
        {
            EntityPos pos = player.Entity.Pos;
            pcx = (int)Math.Floor(pos.X / GlobalConstants.ChunkSize);
            pcz = (int)Math.Floor(pos.Z / GlobalConstants.ChunkSize);
        }
        catch { return; }

        long worstKey = 0;
        int worstChebyshev = -1;
        foreach (KeyValuePair<long, ScoutHold> kv in holds)
        {
            int chebyshev = Math.Max(Math.Abs(kv.Value.Cx - pcx), Math.Abs(kv.Value.Cz - pcz));
            if (chebyshev > worstChebyshev)
            {
                worstChebyshev = chebyshev;
                worstKey = kv.Key;
            }
        }

        if (worstKey != 0)
            DropAnchor(player, worstKey);
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
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        requestGate.BeginTick(now);
        bool allowBackground = holdsByPlayer.Count == 0;
        DrainPriorityLoads(now, allowBackground);
        RequeueMissingHoldColumns();
        DrainForceSends(allowBackground);
        requestGate.EndTick(now);
        SendHostStatusIfDue(now);
        DrainPendingUps();
        LogHostTelemetryIfDue();
    }

    void SendHostStatusIfDue(long now)
    {
        if (sapi == null || serverChannel == null)
            return;
        bool pressure = requestGate.PressureActive;
        bool edge = !hostStatusSent || pressure != lastHostStatusPressure;
        if (!edge && now - lastHostStatusMs < HostStatusIntervalMs)
            return;

        hostStatusUids.Clear();
        foreach (string uid in holdsByPlayer.Keys)
            hostStatusUids.Add(uid);
        foreach (string uid in pendingUpsByPlayer.Keys)
            hostStatusUids.Add(uid);
        if (hostStatusUids.Count == 0)
            return;

        var status = new ScoutHostStatus
        {
            Sequence = ++hostStatusSequence,
            Pressure = pressure,
            PriorityPending = requestGate.PriorityPending,
            PriorityInFlight = requestGate.PriorityInFlight,
            ForceSendPending = requestGate.ForceSendPending,
            OldestInFlightMs = requestGate.OldestInFlightAgeMs,
            PriorityCompleted = requestGate.PriorityCompleted,
            ServerTimeMs = now,
        };

        foreach (string uid in hostStatusUids)
        {
            IPlayer? raw = null;
            try { raw = sapi.World.PlayerByUid(uid); }
            catch { }
            if (raw is not IServerPlayer player)
                continue;
            try { serverChannel.SendPacket(status, player); }
            catch { }
        }

        hostStatusUids.Clear();
        lastHostStatusMs = now;
        lastHostStatusPressure = pressure;
        hostStatusSent = true;
    }

    void LogHostTelemetryIfDue()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (lastHostTelemetryMs != 0 && now - lastHostTelemetryMs < 1000)
            return;

        int holdCount = 0;
        int pendingCount = 0;
        foreach (Dictionary<long, ScoutHold> holds in holdsByPlayer.Values)
            holdCount += holds.Count;
        foreach (Queue<ScoutAnchorUp> pending in pendingUpsByPlayer.Values)
            pendingCount += pending.Count;

        if (holdCount == 0
            && pendingCount == 0
            && requestGate.PriorityPending == 0
            && requestGate.PriorityInFlight == 0
            && requestGate.ForceSendPending == 0)
            return;

        lastHostTelemetryMs = now;
        LodScoutSeqDiag.LogHostTelemetry(
            holdCount,
            holdPeak,
            columnRefs.Count,
            pendingCount,
            requestGate.PriorityPending + requestGate.PriorityInFlight,
            requestGate.PriorityPendingPeak + requestGate.PriorityInFlightPeak,
            requestGate.PriorityInFlight,
            requestGate.OldestInFlightAgeMs,
            requestGate.PressureActive,
            requestGate.PriorityCompleted,
            requestGate.ForceSendPending,
            requestGate.ForceSendPendingPeak,
            holdAccepted,
            holdReplaced,
            holdRefused,
            holdEvicted,
            pendingUpDropped,
            requestGate.PriorityEnqueued,
            requestGate.PriorityCoalesced,
            requestGate.PriorityDropped,
            requestGate.PriorityStarted,
            requestGate.Cancelled,
            requestGate.ForceSendEnqueued,
            requestGate.ForceSendCoalesced,
            requestGate.ForceSendDropped,
            requestGate.ForceSendStarted,
            requestGate.Cancelled);
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
        {
            q.Dequeue();
            pendingUpDropped++;
        }
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

    internal LodServerQueueDecision QueuePriorityLoad(
        string owner,
        int cx,
        int cz,
        int dim,
        bool keepLoaded,
        Action? onLoaded,
        LodServerChunkWorkPriority priority)
    {
        return requestGate.QueuePriority(
            owner,
            new LodServerChunkColumn(cx, cz, dim),
            keepLoaded,
            onLoaded,
            priority);
    }

    void RequeueMissingHoldColumns()
    {
        if (sapi == null) return;
        if (requestGate.PriorityPending >= MaxPriorityLoadQueue)
            return;

        foreach (KeyValuePair<string, Dictionary<long, ScoutHold>> pair in holdsByPlayer)
        {
            if (requestGate.PriorityPending >= MaxPriorityLoadQueue)
                break;
            if (pair.Value.Count == 0)
                continue;

            IPlayer? raw = null;
            try { raw = sapi.World.PlayerByUid(pair.Key); }
            catch { }
            if (raw is not IServerPlayer player)
                continue;

            foreach (ScoutHold hold in pair.Value.Values)
            {
                if (!hold.NeedsAdmissionRetry)
                    continue;
                if (requestGate.PriorityPending >= MaxPriorityLoadQueue)
                    break;

                string owner = HoldOwner(player.PlayerUID, hold.Key);
                bool stillMissing = false;
                for (int dz = -hold.Radius; dz <= hold.Radius; dz++)
                {
                    for (int dx = -hold.Radius; dx <= hold.Radius; dx++)
                    {
                        if (requestGate.PriorityPending >= MaxPriorityLoadQueue)
                        {
                            stillMissing = true;
                            break;
                        }
                        int cx = hold.Cx + dx;
                        int cz = hold.Cz + dz;
                        if (cx < 0 || cz < 0) continue;
                        long col = ColumnKey(cx, cz, hold.Dimension);
                        if (hold.AdmittedColumns.Contains(col))
                            continue;

                        LodServerQueueDecision decision = QueuePriorityLoad(
                            owner,
                            cx,
                            cz,
                            hold.Dimension,
                            keepLoaded: true,
                            onLoaded: () => QueueForceSend(
                                owner, player.PlayerUID, cx, cz, hold.Dimension,
                                LodServerChunkWorkPriority.Login),
                            LodServerChunkWorkPriority.Login);
                        if (decision == LodServerQueueDecision.Dropped)
                        {
                            stillMissing = true;
                            continue;
                        }
                        hold.AdmittedColumns.Add(col);
                    }
                }
                hold.NeedsAdmissionRetry = stillMissing;
            }
        }
    }

    void DrainPriorityLoads(long nowMs, bool allowBackground)
    {
        if (sapi == null) return;
        while (requestGate.TryStartPriority(nowMs, out LodServerPriorityStart start, allowBackground))
        {
            LodServerChunkColumn column = start.Column;
            try
            {
                sapi.WorldManager.LoadChunkColumnPriority(
                    column.Cx,
                    column.Cz,
                    new ChunkLoadOptions
                    {
                        KeepLoaded = start.KeepLoaded,
                        OnLoaded = () => CompletePriorityLoad(column),
                    });
            }
            catch
            {
                CompletePriorityLoad(column);
            }
        }
    }

    void CompletePriorityLoad(LodServerChunkColumn column)
    {
        if (sapi == null) return;
        try
        {
            sapi.Event.EnqueueMainThreadTask(
                () => SettlePriorityLoad(column),
                "dv-priority-loaded");
        }
        catch
        {
            // Teardown can reject a callback after the gate has already been cleared.
        }
    }

    void SettlePriorityLoad(LodServerChunkColumn column)
    {
        if (sapi == null) return;
        if (!requestGate.CompletePriority(
                column,
                out IReadOnlyList<Action> callbacks,
                out bool stillWanted,
                out bool keepLoaded))
            return;

        for (int i = 0; i < callbacks.Count; i++)
        {
            try { callbacks[i](); }
            catch { }
        }

        if (!stillWanted && keepLoaded && !ShouldKeepLoaded(column))
        {
            try { sapi.WorldManager.UnloadChunkColumn(column.Cx, column.Cz); }
            catch { }
        }
    }

    bool ShouldKeepLoaded(LodServerChunkColumn column)
    {
        long key = ColumnKey(column.Cx, column.Cz, column.Dimension);
        if (columnRefs.TryGetValue(key, out int refs) && refs > 0)
            return true;
        if (sapi == null)
            return false;

        IPlayer[]? players = sapi.World.AllOnlinePlayers;
        if (players == null)
            return false;
        for (int i = 0; i < players.Length; i++)
        {
            if (players[i] is not IServerPlayer player)
                continue;
            EntityPos? pos = player.Entity?.Pos;
            if (pos == null || pos.Dimension != column.Dimension)
                continue;

            int vd = 256;
            try { vd = player.WorldData.LastApprovedViewDistance; } catch { }
            if (vd <= 0)
            {
                try { vd = player.WorldData.DesiredViewDistance; } catch { }
            }
            if (vd <= 0) vd = 256;
            int keepR = Math.Max(
                MaxHoldRadiusChunks,
                (int)Math.Ceiling(vd / (double)GlobalConstants.ChunkSize) + 2);
            int pcx = (int)Math.Floor(pos.X / GlobalConstants.ChunkSize);
            int pcz = (int)Math.Floor(pos.Z / GlobalConstants.ChunkSize);
            if (Math.Max(Math.Abs(column.Cx - pcx), Math.Abs(column.Cz - pcz)) <= keepR)
                return true;
        }
        return false;
    }

    void QueueForceSend(
        string owner,
        string playerUid,
        int cx,
        int cz,
        int dim,
        LodServerChunkWorkPriority priority)
    {
        requestGate.QueueForceSend(
            owner,
            new LodServerForceSendColumn(playerUid, cx, cz, dim),
            priority);
    }

    void DrainForceSends(bool allowBackground)
    {
        if (sapi == null) return;
        while (requestGate.TryStartForceSend(out LodServerForceSendColumn work, allowBackground))
        {
            IPlayer? raw = null;
            try { raw = sapi.World.PlayerByUid(work.PlayerUid); }
            catch { }
            if (raw is not IServerPlayer player)
                continue;

            try { sapi.WorldManager.ForceSendChunkColumn(player, work.Cx, work.Cz, work.Dimension); }
            catch { }
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
        requestGate.CancelOwner(HoldOwner(player.PlayerUID, hold.Key));
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

    static string HoldOwner(string uid, long key) =>
        "scout:" + uid + ":" + key.ToString(System.Globalization.CultureInfo.InvariantCulture);

    sealed class ScoutHold
    {
        public long Key;
        public int Cx;
        public int Cz;
        public int Radius;
        public int Dimension;
        public LodScoutViewerEntity? Viewer;
        public bool NeedsAdmissionRetry;
        public readonly HashSet<long> AdmittedColumns = new();
    }

    sealed class UpRequestState
    {
        public int Cx;
        public int Cz;
        public int Radius;
        public int Dimension;
        public bool Priority;
        public long SentMs;
    }
}
