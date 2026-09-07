using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace DistantVistas;

/// <summary>
/// Login-overlay coverage without hopping the player: stagger real
/// <see cref="LodScoutViewerEntity"/> workers. Each viewer is a player-style
/// stream/render center at a visit cell. Near spawn waits for a drawable mesh;
/// the far ring releases after FlagBaked paint so overlay % can move.
/// </summary>
public sealed class LodLoginScoutFill
{
    public const int MaxConcurrent = 16;
    /// <summary>
    /// Spawn-solid scouts occupy these slots through mesh wait. Remaining slots
    /// run the far ring so overlay % is not stuck on 16 near tessellation waits.
    /// </summary>
    public const int MaxNearConcurrent = 8;
    public const int MaxFarConcurrent = 8;
    public const int MaxWaitTicks = 400;
    public const int MaxCaptureWaitTicks = 80;
    public const int MaxMeshWaitTicks = 120;
    public const int ChunkVisibleRadius = 2;
    public const int SweepRadiusChunks = 2;
    public const int SweepRowsPerCall = 1;
    public const int RevealGrowPerTick = 4;
    public const int RequestUpRetryTicks = 20;
    /// <summary>KeepLoaded / visible ring at spawn-solid visit cells.</summary>
    public const int NearRevealChunks = 4;
    /// <summary>Far visit cells only need the L0 footprint (64 blocks ≈ 2 chunks).</summary>
    public const int FarRevealChunks = 2;
    /// <summary>Pinned scouts re-partition this often; spawn still partitions once.</summary>
    public const int PartitioningEveryTicks = 8;
    /// <summary>
    /// Cap passed into Tick as chunkVisibleTarget. Must stay a neighbourhood,
    /// not the ~130-chunk onset disk.
    /// </summary>
    public const int LocalVisitRevealChunks = NearRevealChunks;

    readonly LodScoutEntity?[] slots = new LodScoutEntity[MaxConcurrent];
    readonly Queue<long> heldNear = new();
    readonly Queue<long> heldFar = new();
    readonly List<long> readyScratch = new(MaxConcurrent);
    int liveCount;

    public int LiveCount => liveCount;
    public int HeldCount => heldNear.Count + heldFar.Count;
    public int FinishedThisTick { get; private set; }
    public long? LastFinishedKey { get; private set; }

    public void Reset(ICoreClientAPI? capi = null)
    {
        DespawnLive(capi);
        LodScoutHostSystem.ClientInstance?.RequestClear();
        if (capi != null)
            LodScoutViewerEntity.DespawnAll(capi.World);
        for (int i = 0; i < slots.Length; i++) slots[i] = null;
        heldNear.Clear();
        heldFar.Clear();
        readyScratch.Clear();
        liveCount = 0;
        FinishedThisTick = 0;
        LastFinishedKey = null;
    }

    public bool HasWork => liveCount > 0 || HeldCount > 0;

    public void CopyLiveKeys(List<long> dest)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            LodScoutEntity? scout = slots[i];
            if (scout is { Live: true }) dest.Add(scout.Key);
        }
    }

    public void CopyHeldKeys(List<long> dest)
    {
        foreach (long key in heldNear) dest.Add(key);
        foreach (long key in heldFar) dest.Add(key);
    }

    public void NotifyPainted(long key)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            LodScoutEntity? scout = slots[i];
            if (scout is { Live: true } && scout.Key == key)
                scout.Painted = true;
        }
    }

    public bool IsLive(long key)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            LodScoutEntity? scout = slots[i];
            if (scout is { Live: true } && scout.Key == key)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Advance every empty slot from <paramref name="pending"/> so all
    /// <see cref="MaxConcurrent"/> workers run together. Mixes near mesh-wait
    /// and far paint-release so the overlay is not 16 spawn waits. Returns keys
    /// that finished capture this tick (ready to GetColor-paint).
    /// </summary>
    public List<long> Tick(
        ICoreClientAPI capi,
        LodPipeline pipeline,
        LodTerrainRenderer renderer,
        Queue<long> pending,
        HashSet<long> completedKeys,
        int chunkVisibleTarget,
        int onsetChunks,
        double pickupX,
        double pickupZ)
    {
        FinishedThisTick = 0;
        LastFinishedKey = null;
        readyScratch.Clear();
        int targetCap = Math.Min(
            LocalVisitRevealChunks,
            Math.Max(ChunkVisibleRadius, chunkVisibleTarget));

        int nearLive = 0;
        int farLive = 0;
        CountLiveMix(out nearLive, out farLive);

        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] is { Live: true }) continue;
            bool wantNear = PickWantNear(nearLive, farLive);
            long? key = TakeVisitKey(capi, pending, completedKeys, pickupX, pickupZ, wantNear);
            if (key == null)
                key = TakeVisitKey(capi, pending, completedKeys, pickupX, pickupZ, !wantNear);
            if (key == null) break;
            StartSlot(capi, i, key.Value, pickupX, pickupZ, onsetChunks, targetCap);
            if (slots[i] is { WaitForMesh: true }) nearLive++;
            else farLive++;
        }

        for (int i = 0; i < slots.Length; i++)
        {
            LodScoutEntity? scout = slots[i];
            if (scout is not { Live: true }) continue;
            long key = scout.Key;
            scout.Ticks++;
            HoldViewer(scout);

            int dim = capi.World.Player.Entity.Pos.Dimension;
            int target = Math.Min(
                scout.WaitForMesh ? NearRevealChunks : FarRevealChunks,
                ClampReveal(scout, pickupX, pickupZ, onsetChunks, targetCap));
            GrowReveal(capi, scout, dim, target);

            if (scout.Current == LodScoutEntity.Phase.WaitChunks)
            {
                int cx = scout.Cx;
                int cz = scout.Cz;
                // Near WaitChunks only. Paint/Mesh never SweepLoadedColumns or
                // forceRecapture — that recapture storm held 16 slots and blew GC.
                if (scout.WaitForMesh && scout.Ticks % 2 == 0)
                    pipeline.SweepLoadedColumns(
                        cx, cz, SweepRadiusChunks, forceRecapture: false,
                        rowsPerCall: SweepRowsPerCall, lane: LodPipeline.SweepLaneScout);

                if (scout.Ticks > 0 && scout.Ticks % RequestUpRetryTicks == 0)
                {
                    LodScoutHostSystem.ClientInstance?.RequestUp(
                        key, scout.Cx, scout.Cz, scout.HoldRadius, dim, scout.X, scout.Y, scout.Z);
                }

                bool loaded = LodLoginSweep.AllMapChunksLoaded(capi.World.BlockAccessor, key);
                if (!loaded)
                {
                    if (scout.Ticks < MaxWaitTicks)
                        continue;
                    // Do not bake missing-tex white. Miss audit / retry can pick this L0 up.
                    ReleaseSlot(capi, i);
                    continue;
                }

                pipeline.QueueL0SectionForce(key);
                scout.Current = LodScoutEntity.Phase.Capture;
                scout.Ticks = 0;
                continue;
            }

            if (scout.Current == LodScoutEntity.Phase.Capture)
            {
                if (!pipeline.IsL0SectionCaptureIdle(key)
                    && scout.Ticks < MaxCaptureWaitTicks)
                    continue;

                if (!scout.PaintQueued)
                {
                    readyScratch.Add(key);
                    LastFinishedKey = key;
                    FinishedThisTick++;
                    scout.PaintQueued = true;
                }

                scout.Current = LodScoutEntity.Phase.Paint;
                scout.Ticks = 0;
                continue;
            }

            if (scout.Current == LodScoutEntity.Phase.Paint)
            {
                if (!scout.Painted && scout.Ticks < MaxWaitTicks)
                    continue;
                if (scout.WaitForMesh)
                {
                    scout.Current = LodScoutEntity.Phase.Mesh;
                    scout.Ticks = 0;
                    continue;
                }
                ReleaseSlot(capi, i);
                continue;
            }

            if (scout.Current == LodScoutEntity.Phase.Mesh)
            {
                // Sticky empty tessellation claims are not drawable land. Do not
                // hold the scout slot for MaxMeshWaitTicks on a known-empty upload;
                // drain/stabilize still waits on HasDrawableMesh at spawn.
                bool meshed = renderer.HasDrawableMesh(key);
                bool emptyClaim = renderer.HasEmptyMeshClaim(key);
                if (!meshed && !emptyClaim && scout.Ticks < MaxMeshWaitTicks)
                    continue;
                ReleaseSlot(capi, i);
            }
        }

        liveCount = CountLive();
        return readyScratch;
    }

    void CountLiveMix(out int nearLive, out int farLive)
    {
        nearLive = 0;
        farLive = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            LodScoutEntity? scout = slots[i];
            if (scout is not { Live: true }) continue;
            if (scout.WaitForMesh) nearLive++;
            else farLive++;
        }
    }

    bool PickWantNear(int nearLive, int farLive)
    {
        bool nearRoom = nearLive < MaxNearConcurrent;
        bool farRoom = farLive < MaxFarConcurrent;
        if (nearRoom && farRoom)
            return nearLive <= farLive;
        if (nearRoom) return true;
        if (farRoom) return false;
        return nearLive < farLive;
    }

    long? TakeVisitKey(
        ICoreClientAPI capi,
        Queue<long> pending,
        HashSet<long> completedKeys,
        double pickupX,
        double pickupZ,
        bool wantNear)
    {
        Queue<long> held = wantNear ? heldNear : heldFar;
        while (held.Count > 0)
        {
            long key = held.Dequeue();
            if (!completedKeys.Contains(key))
                return key;
        }

        while (pending.Count > 0)
        {
            long key = pending.Dequeue();
            if (completedKeys.Contains(key)) continue;
            bool near = VisitIsNear(capi, key, pickupX, pickupZ);
            if (near == wantNear)
                return key;
            (near ? heldNear : heldFar).Enqueue(key);
        }

        return null;
    }

    static bool VisitIsNear(
        ICoreClientAPI capi,
        long key,
        double pickupX,
        double pickupZ)
    {
        var (x, _, z) = LodLoginSweep.VisitPosition(capi.World, key);
        double dx = x - pickupX;
        double dz = z - pickupZ;
        return dx * dx + dz * dz
            <= LodLoginBake.SpawnSolidRadiusBlocks * LodLoginBake.SpawnSolidRadiusBlocks;
    }

    static void HoldViewer(LodScoutEntity scout)
    {
        LodScoutViewerEntity? viewer = scout.Viewer;
        if (viewer == null) return;
        viewer.ServerPos.SetPos(scout.X, scout.Y, scout.Z);
        viewer.Pos.SetFrom(viewer.ServerPos);
        viewer.Pos.Motion.Set(0, 0, 0);
        viewer.ServerPos.Motion.Set(0, 0, 0);
        viewer.IsRendered = false;
        viewer.AlwaysActive = true;
        scout.PartitionTicks++;
        if (scout.PartitionTicks % PartitioningEveryTicks == 0)
            LodVsCompat.TryUpdatePartitioning(viewer);
    }

    static int ClampReveal(
        LodScoutEntity scout,
        double pickupX,
        double pickupZ,
        int onsetChunks,
        int targetCap)
    {
        double dx = scout.X - pickupX;
        double dz = scout.Z - pickupZ;
        int distChunks = (int)Math.Ceiling(Math.Sqrt(dx * dx + dz * dz) / GlobalConstants.ChunkSize);
        int room = Math.Max(ChunkVisibleRadius, onsetChunks - distChunks);
        int cap = Math.Min(targetCap, scout.WaitForMesh ? NearRevealChunks : FarRevealChunks);
        return Math.Min(cap, room);
    }

    static void GrowReveal(
        ICoreClientAPI capi,
        LodScoutEntity scout,
        int dim,
        int target)
    {
        if (scout.RevealRadius >= target)
            return;

        int before = scout.RevealRadius;
        scout.RevealRadius = Math.Min(target, scout.RevealRadius + RevealGrowPerTick);
        LodLoginBakePlayerMove.RequestChunkColumnRing(
            capi, scout.X, scout.Z, dim, before, scout.RevealRadius);
    }

    void StartSlot(
        ICoreClientAPI capi,
        int index,
        long key,
        double pickupX,
        double pickupZ,
        int onsetChunks,
        int targetCap)
    {
        var (x, y, z) = LodLoginSweep.VisitPosition(capi.World, key);
        double dx = x - pickupX;
        double dz = z - pickupZ;
        bool near = dx * dx + dz * dz <= LodLoginBake.SpawnSolidRadiusBlocks * LodLoginBake.SpawnSolidRadiusBlocks;
        var scout = new LodScoutEntity(key)
        {
            X = x,
            Y = y,
            Z = z,
            Cx = (int)Math.Floor(x / GlobalConstants.ChunkSize),
            Cz = (int)Math.Floor(z / GlobalConstants.ChunkSize),
            WaitForMesh = near,
        };

        try { scout.Viewer = LodScoutViewerEntity.SpawnAt(capi, key, x, y, z); }
        catch { scout.Viewer = null; }

        slots[index] = scout;
        liveCount = CountLive();
        int dim = capi.World.Player.Entity.Pos.Dimension;
        int radius = Math.Min(
            near ? NearRevealChunks : FarRevealChunks,
            ClampReveal(scout, pickupX, pickupZ, onsetChunks, targetCap));
        scout.HoldRadius = radius;
        LodLoginBakePlayerMove.RequestChunkColumnsVisible(capi, x, z, dim, ChunkVisibleRadius);
        LodScoutHostSystem.ClientInstance?.RequestUp(key, scout.Cx, scout.Cz, radius, dim, x, y, z);
    }

    void ReleaseSlot(ICoreClientAPI capi, int index)
    {
        LodScoutEntity? scout = slots[index];
        if (scout == null) return;
        LodScoutHostSystem.ClientInstance?.RequestDown(scout.Key);
        LodScoutViewerEntity.DespawnOne(capi.World, scout.Viewer);
        scout.Viewer = null;
        scout.Live = false;
        scout.Current = LodScoutEntity.Phase.Done;
        slots[index] = null;
    }

    void DespawnLive(ICoreClientAPI? capi)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            LodScoutEntity? scout = slots[i];
            if (scout == null) continue;
            LodScoutHostSystem.ClientInstance?.RequestDown(scout.Key);
            if (capi != null)
                LodScoutViewerEntity.DespawnOne(capi.World, scout.Viewer);
            scout.Viewer = null;
            scout.Live = false;
            slots[i] = null;
        }
    }

    int CountLive()
    {
        int n = 0;
        for (int i = 0; i < slots.Length; i++)
            if (slots[i] is { Live: true }) n++;
        return n;
    }
}
