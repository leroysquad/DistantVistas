using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace DistantVistas;

/// <summary>
/// Login-overlay coverage without hopping the player: stagger real
/// <see cref="LodScoutViewerEntity"/> workers. Each viewer is a player-style
/// stream/render center at a visit cell (server KeepLoaded + ForceSend, client
/// SetChunkColumnVisible around the entity). Capture + GetColor + LOD mesh, then
/// despawn. Overlay stays up; Esc cancel still owned by <see cref="LodLoginBake"/>.
/// </summary>
public sealed class LodLoginScoutFill
{
    public const int MaxConcurrent = 6;
    public const int MaxWaitTicks = 400;
    public const int MaxCaptureWaitTicks = 80;
    public const int MaxMeshWaitTicks = 120;
    public const int ChunkVisibleRadius = 2;
    public const int SweepRadiusChunks = 3;
    public const int SweepRowsPerCall = 2;
    public const int RevealGrowPerTick = 4;
    /// <summary>
    /// Local streamed neighbourhood around a scout/visit cell (chunks).
    /// Spawn-centered vanilla stream stays at the 750-hold so FPS does not
    /// tessellate 4 km of real chunks. FlagBaked coverage is the visit disk.
    /// </summary>
    public const int LocalVisitRevealChunks = 8;

    readonly LodScoutEntity?[] slots = new LodScoutEntity[MaxConcurrent];
    int liveCount;

    public int LiveCount => liveCount;
    public int FinishedThisTick { get; private set; }
    public long? LastFinishedKey { get; private set; }

    public void Reset(ICoreClientAPI? capi = null)
    {
        DespawnLive(capi);
        LodScoutHostSystem.ClientInstance?.RequestClear();
        if (capi != null)
            LodScoutViewerEntity.DespawnAll(capi.World);
        for (int i = 0; i < slots.Length; i++) slots[i] = null;
        liveCount = 0;
        FinishedThisTick = 0;
        LastFinishedKey = null;
    }

    public bool HasWork => liveCount > 0;

    public void CopyLiveKeys(List<long> dest)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            LodScoutEntity? scout = slots[i];
            if (scout is { Live: true }) dest.Add(scout.Key);
        }
    }

    /// <summary>
    /// Advance active scouts and pull new keys from <paramref name="pending"/>.
    /// Each scout is a viewer entity at the visit cell. Returns keys that finished
    /// capture this tick (ready to GetColor-paint). Viewers stay until mesh exists.
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
        var ready = new List<long>(MaxConcurrent);
        int targetCap = Math.Min(
            LocalVisitRevealChunks,
            Math.Max(ChunkVisibleRadius, chunkVisibleTarget));

        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] is { Live: true }) continue;
            while (pending.Count > 0)
            {
                long key = pending.Dequeue();
                if (completedKeys.Contains(key)) continue;
                StartSlot(capi, i, key, pickupX, pickupZ, onsetChunks, targetCap);
                break;
            }
        }

        for (int i = 0; i < slots.Length; i++)
        {
            LodScoutEntity? scout = slots[i];
            if (scout is not { Live: true }) continue;
            long key = scout.Key;
            scout.Ticks++;
            HoldViewer(scout);

            int dim = capi.World.Player.Entity.Pos.Dimension;
            int target = Math.Min(LocalVisitRevealChunks, ClampReveal(scout, pickupX, pickupZ, onsetChunks, targetCap));
            GrowReveal(capi, scout, dim, target);

            if (scout.Current == LodScoutEntity.Phase.WaitChunks)
            {
                int cx = scout.Cx;
                int cz = scout.Cz;
                if (scout.Ticks % 2 == 0)
                    pipeline.SweepLoadedColumns(
                        cx, cz, SweepRadiusChunks, forceRecapture: false,
                        rowsPerCall: SweepRowsPerCall, lane: LodPipeline.SweepLaneScout);

                bool loaded = LodLoginSweep.AllMapChunksLoaded(capi.World.BlockAccessor, key);
                if (!loaded)
                {
                    if (scout.Ticks < MaxWaitTicks)
                        continue;
                    // Do not bake missing-tex white. Miss audit / retry can pick this L0 up.
                    ReleaseSlot(capi, i);
                    continue;
                }

                pipeline.SweepLoadedColumns(
                    cx, cz, SweepRadiusChunks, forceRecapture: false,
                    rowsPerCall: SweepRowsPerCall, lane: LodPipeline.SweepLaneScout);
                pipeline.QueueL0SectionForce(key);
                scout.Current = LodScoutEntity.Phase.Capture;
                scout.Ticks = 0;
                continue;
            }

            if (scout.Current == LodScoutEntity.Phase.Capture)
            {
                int cx = scout.Cx;
                int cz = scout.Cz;
                if (scout.Ticks % 2 == 0)
                    pipeline.SweepLoadedColumns(
                        cx, cz, SweepRadiusChunks, forceRecapture: false,
                        rowsPerCall: SweepRowsPerCall, lane: LodPipeline.SweepLaneScout);

                if (!pipeline.IsL0SectionCaptureIdle(key)
                    && scout.Ticks < MaxCaptureWaitTicks)
                    continue;

                if (!scout.PaintQueued)
                {
                    ready.Add(key);
                    LastFinishedKey = key;
                    FinishedThisTick++;
                    scout.PaintQueued = true;
                }

                scout.Current = LodScoutEntity.Phase.Mesh;
                scout.Ticks = 0;
                continue;
            }

            if (scout.Current == LodScoutEntity.Phase.Mesh)
            {
                bool meshed = renderer.HasDrawableMesh(key);
                if (!meshed && scout.Ticks < MaxMeshWaitTicks)
                    continue;
                ReleaseSlot(capi, i);
            }
        }

        liveCount = CountLive();
        return ready;
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
        try { viewer.UpdatePartitioning(); } catch { }
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
        return Math.Min(targetCap, room);
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
        var scout = new LodScoutEntity(key)
        {
            X = x,
            Y = y,
            Z = z,
            Cx = (int)Math.Floor(x / GlobalConstants.ChunkSize),
            Cz = (int)Math.Floor(z / GlobalConstants.ChunkSize),
        };

        try { scout.Viewer = LodScoutViewerEntity.SpawnAt(capi, key, x, y, z); }
        catch { scout.Viewer = null; }

        slots[index] = scout;
        liveCount = CountLive();
        int dim = capi.World.Player.Entity.Pos.Dimension;
        int radius = Math.Min(
            LocalVisitRevealChunks,
            ClampReveal(scout, pickupX, pickupZ, onsetChunks, targetCap));
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
