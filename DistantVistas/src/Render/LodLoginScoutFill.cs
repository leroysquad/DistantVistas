using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace DistantVistas;

/// <summary>
/// Login-overlay coverage without hopping the player: stagger
/// <see cref="LodLoginBakePlayerMove.RequestChunkColumnsVisible"/> across planned
/// L0 cells (same stream path as <see cref="LodFrontierScout"/>), capture+GetColor
/// bake when map chunks land, capped concurrency so the client does not flood.
/// Overlay stays up; Esc cancel still owned by <see cref="LodLoginBake"/>.
/// </summary>
public sealed class LodLoginScoutFill
{
    public const int MaxConcurrent = 6;
    public const int MaxWaitTicks = 100;
    public const int MaxCaptureWaitTicks = 80;
    public const int ChunkVisibleRadius = 2;
    public const int SweepRadiusChunks = 3;
    public const int SweepRowsPerCall = 2;

    enum SlotPhase : byte { WaitChunks, Capture, Bake }

    struct Slot
    {
        public long Key;
        public SlotPhase Phase;
        public int Ticks;
        public bool Live;
    }

    readonly Slot[] slots = new Slot[MaxConcurrent];
    int liveCount;

    public int LiveCount => liveCount;
    public int FinishedThisTick { get; private set; }
    public long? LastFinishedKey { get; private set; }

    public void Reset()
    {
        for (int i = 0; i < slots.Length; i++) slots[i] = default;
        liveCount = 0;
        FinishedThisTick = 0;
        LastFinishedKey = null;
    }

    public bool HasWork => liveCount > 0;

    /// <summary>
    /// Advance active streams and pull new keys from <paramref name="pending"/>.
    /// Returns keys that finished capture this tick (caller bakes + marks done).
    /// </summary>
    public List<long> Tick(
        ICoreClientAPI capi,
        LodPipeline pipeline,
        Queue<long> pending,
        HashSet<long> completedKeys)
    {
        FinishedThisTick = 0;
        LastFinishedKey = null;
        var ready = new List<long>(MaxConcurrent);

        // Prefetch / refill empty slots.
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].Live) continue;
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
            if (!slots[i].Live) continue;
            long key = slots[i].Key;
            slots[i].Ticks++;

            var (x, _, z) = LodLoginSweep.VisitPosition(capi.World, key);
            int dim = capi.World.Player.Entity.Pos.Dimension;
            LodLoginBakePlayerMove.RequestChunkColumnsVisible(
                capi, x, z, dim, ChunkVisibleRadius);

            if (slots[i].Phase == SlotPhase.WaitChunks)
            {
                int cx = (int)Math.Floor(x / GlobalConstants.ChunkSize);
                int cz = (int)Math.Floor(z / GlobalConstants.ChunkSize);
                if (slots[i].Ticks % 2 == 0)
                    pipeline.SweepLoadedColumns(cx, cz, SweepRadiusChunks, forceRecapture: true, rowsPerCall: SweepRowsPerCall);

                bool loaded = LodLoginSweep.AllMapChunksLoaded(capi.World.BlockAccessor, key);
                if (!loaded && slots[i].Ticks < MaxWaitTicks)
                    continue;

                pipeline.SweepLoadedColumns(cx, cz, SweepRadiusChunks, forceRecapture: true, rowsPerCall: SweepRowsPerCall);
                pipeline.QueueL0SectionForce(key);
                slots[i].Phase = SlotPhase.Capture;
                slots[i].Ticks = 0;
                continue;
            }

            if (slots[i].Phase == SlotPhase.Capture)
            {
                int cx = (int)Math.Floor(x / GlobalConstants.ChunkSize);
                int cz = (int)Math.Floor(z / GlobalConstants.ChunkSize);
                if (slots[i].Ticks % 2 == 0)
                    pipeline.SweepLoadedColumns(cx, cz, SweepRadiusChunks, forceRecapture: true, rowsPerCall: SweepRowsPerCall);

                if (!pipeline.IsL0SectionCaptureIdle(key)
                    && slots[i].Ticks < MaxCaptureWaitTicks)
                    continue;

                slots[i].Phase = SlotPhase.Bake;
                slots[i].Ticks = 0;
            }

            if (slots[i].Phase == SlotPhase.Bake)
            {
                ready.Add(key);
                LastFinishedKey = key;
                FinishedThisTick++;
                slots[i] = default;
                liveCount = CountLive();
            }
        }

        liveCount = CountLive();
        return ready;
    }

    void StartSlot(ICoreClientAPI capi, int index, long key)
    {
        slots[index] = new Slot
        {
            Key = key,
            Phase = SlotPhase.WaitChunks,
            Ticks = 0,
            Live = true,
        };
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
            if (slots[i].Live) n++;
        return n;
    }
}
