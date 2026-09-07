using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace DistantVistas;

/// <summary>
/// Login-overlay coverage without hopping the player: stagger
/// <see cref="LodScoutEntity"/> workers that
/// <see cref="LodLoginBakePlayerMove.RequestChunkColumnsVisible"/> at planned
/// L0 cells, capture+GetColor bake when map chunks land, then despawn.
/// Concurrency is capped so the client does not flood. Overlay stays up;
/// Esc cancel still owned by <see cref="LodLoginBake"/>.
/// </summary>
public sealed class LodLoginScoutFill
{
    public const int MaxConcurrent = 6;
    public const int MaxWaitTicks = 100;
    public const int MaxCaptureWaitTicks = 80;
    public const int ChunkVisibleRadius = 2;
    public const int SweepRadiusChunks = 3;
    public const int SweepRowsPerCall = 2;
    public const int RevealGrowPerTick = 2;

    readonly LodScoutEntity?[] slots = new LodScoutEntity[MaxConcurrent];
    int liveCount;

    public int LiveCount => liveCount;
    public int FinishedThisTick { get; private set; }
    public long? LastFinishedKey { get; private set; }

    public void Reset()
    {
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
    /// Returns keys that finished capture this tick (caller bakes + marks done).
    /// </summary>
    public List<long> Tick(
        ICoreClientAPI capi,
        LodPipeline pipeline,
        Queue<long> pending,
        HashSet<long> completedKeys,
        int chunkVisibleTarget)
    {
        FinishedThisTick = 0;
        LastFinishedKey = null;
        var ready = new List<long>(MaxConcurrent);
        int target = Math.Max(ChunkVisibleRadius, chunkVisibleTarget);

        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] is { Live: true }) continue;
            while (pending.Count > 0)
            {
                long key = pending.Dequeue();
                if (completedKeys.Contains(key)) continue;
                StartSlot(capi, i, key);
                break;
            }
        }

        for (int i = 0; i < slots.Length; i++)
        {
            LodScoutEntity? scout = slots[i];
            if (scout is not { Live: true }) continue;
            long key = scout.Key;
            scout.Ticks++;

            var (x, _, z) = LodLoginSweep.VisitPosition(capi.World, key);
            int dim = capi.World.Player.Entity.Pos.Dimension;
            GrowReveal(capi, scout, x, z, dim, target);

            if (scout.Current == LodScoutEntity.Phase.WaitChunks)
            {
                int cx = (int)Math.Floor(x / GlobalConstants.ChunkSize);
                int cz = (int)Math.Floor(z / GlobalConstants.ChunkSize);
                if (scout.Ticks % 2 == 0)
                    pipeline.SweepLoadedColumns(cx, cz, SweepRadiusChunks, forceRecapture: true, rowsPerCall: SweepRowsPerCall);

                bool loaded = LodLoginSweep.AllMapChunksLoaded(capi.World.BlockAccessor, key);
                if (!loaded && scout.Ticks < MaxWaitTicks)
                    continue;

                pipeline.SweepLoadedColumns(cx, cz, SweepRadiusChunks, forceRecapture: true, rowsPerCall: SweepRowsPerCall);
                pipeline.QueueL0SectionForce(key);
                scout.Current = LodScoutEntity.Phase.Capture;
                scout.Ticks = 0;
                continue;
            }

            if (scout.Current == LodScoutEntity.Phase.Capture)
            {
                int cx = (int)Math.Floor(x / GlobalConstants.ChunkSize);
                int cz = (int)Math.Floor(z / GlobalConstants.ChunkSize);
                if (scout.Ticks % 2 == 0)
                    pipeline.SweepLoadedColumns(cx, cz, SweepRadiusChunks, forceRecapture: true, rowsPerCall: SweepRowsPerCall);

                if (!pipeline.IsL0SectionCaptureIdle(key)
                    && scout.Ticks < MaxCaptureWaitTicks)
                    continue;

                scout.Current = LodScoutEntity.Phase.Bake;
                scout.Ticks = 0;
            }

            if (scout.Current == LodScoutEntity.Phase.Bake)
            {
                ready.Add(key);
                LastFinishedKey = key;
                FinishedThisTick++;
                scout.Live = false;
                slots[i] = null;
            }
        }

        liveCount = CountLive();
        return ready;
    }

    static void GrowReveal(
        ICoreClientAPI capi,
        LodScoutEntity scout,
        double x,
        double z,
        int dim,
        int target)
    {
        if (scout.RevealRadius >= target)
            return;

        int before = scout.RevealRadius;
        scout.RevealRadius = Math.Min(target, scout.RevealRadius + RevealGrowPerTick);
        LodLoginBakePlayerMove.RequestChunkColumnRing(
            capi, x, z, dim, before, scout.RevealRadius);
    }

    void StartSlot(ICoreClientAPI capi, int index, long key)
    {
        slots[index] = new LodScoutEntity(key);
        liveCount = CountLive();
        var (x, _, z) = LodLoginSweep.VisitPosition(capi.World, key);
        int dim = capi.World.Player.Entity.Pos.Dimension;
        LodLoginBakePlayerMove.RequestChunkColumnsVisible(
            capi, x, z, dim, ChunkVisibleRadius);
    }

    int CountLive()
    {
        int n = 0;
        for (int i = 0; i < slots.Length; i++)
            if (slots[i] is { Live: true }) n++;
        return n;
    }
}
