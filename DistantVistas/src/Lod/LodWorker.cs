using System.Buffers;
using System.Collections.Concurrent;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace DistantVistas;

/// <summary>Immutable view of a section for off-thread meshing. Arrays are never edited in place, only swapped.</summary>
public class SectionSnapshot
{
    public required ulong[] Runs;
    public required int[] ColumnStart;
    public required bool[] Captured;
    public required int[] PaletteColors;
    public required byte[] PaletteFlags;
    public required byte[] PaletteTintSlots;

    public Span<ulong> ColumnRuns(int col) =>
        Runs.AsSpan(ColumnStart[col], ColumnStart[col + 1] - ColumnStart[col]);

    public static SectionSnapshot Of(LodSection s)
    {
        s.FillPaletteSnapshot(out int[] colors, out byte[] flags, out byte[] slots);
        // Runs / ColumnStart are swapped as a whole when the section changes, never
        // edited through a snapshot. Captured is a flag array the mesher only reads.
        // Cloning it every job was 4 KB of gen0 per mesh for a copy nobody wrote.
        return new SectionSnapshot
        {
            Runs = s.Runs,
            ColumnStart = s.ColumnStart,
            Captured = s.Captured,
            PaletteColors = colors,
            PaletteFlags = flags,
            PaletteTintSlots = slots,
        };
    }
}

public class CaptureJob
{
    public int Cx, Cz;
    public required IWorldChunk?[] Chunks; // indexed by chunkY
    public required ushort[] RainMap;      // copied on the main thread
}

/// <summary>Runs carry raw BLOCK ids (not palette ids); the main thread remaps on apply.</summary>
public class CaptureResult
{
    public long SectionKey;
    public int Cx, Cz;
    public required ulong[]?[] RunsByColumn; // GridSize² entries, only this chunk column's 16×16 filled
}

public class MeshJob
{
    public long Key;
    public SectionSnapshot Self = null!;
    public SectionSnapshot?[] Neighbors = new SectionSnapshot?[4]; // W, E, N, S
}

public class MeshResult
{
    public long Key;
    public required float[] Xyz;
    public required byte[] Rgba;
    public required int[] Indices;
    public int VertexCount;
    public int IndexCount;

    // Water/translucent geometry, drawn in a second blended pass.
    public float[]? WaterXyz;
    public byte[]? WaterRgba;
    public int[]? WaterIndices;
    public int WaterVertexCount;
    public int WaterIndexCount;

    /// <summary>
    /// Vertex arrays came from ArrayPool. Hand them back after the GPU has copied
    /// them so the next mesh can reuse the same blocks instead of leaving them for GC.
    /// </summary>
    public void ReturnPooledBuffers()
    {
        ReturnNonNull(ref Xyz);
        ReturnNonNull(ref Rgba);
        ReturnNonNull(ref Indices);
        ReturnNullable(ref WaterXyz);
        ReturnNullable(ref WaterRgba);
        ReturnNullable(ref WaterIndices);
    }

    static void ReturnNonNull<T>(ref T[] a)
    {
        if (a.Length == 0) return;
        ArrayPool<T>.Shared.Return(a);
        a = Array.Empty<T>();
    }

    static void ReturnNullable<T>(ref T[]? a)
    {
        if (a == null || a.Length == 0) return;
        ArrayPool<T>.Shared.Return(a);
        a = null;
    }
}

/// <summary>
/// The background thread: converts chunk block data into RLE columns (capture) and
/// sections into vertex data (meshing). Capture jobs take priority - meshes are only
/// as good as the data beneath them. All game-state access is via refs the main
/// thread handed over; chunk reads are guarded against concurrent disposal.
/// </summary>
public class LodWorker : IDisposable
{
    const int ChunkSize = GlobalConstants.ChunkSize;

    readonly ConcurrentQueue<CaptureJob> captureJobs = new();
    readonly ConcurrentQueue<MeshJob> meshJobs = new();
    readonly ConcurrentBag<MeshJob> meshJobPool = new();
    public readonly ConcurrentQueue<CaptureResult> CaptureResults = new();
    public readonly ConcurrentQueue<MeshResult> MeshResults = new();

    /// <summary>Wakes the capture thread. One job, one thread, so auto-reset is right.</summary>
    readonly AutoResetEvent captureSignal = new(false);

    /// <summary>
    /// One permit per queued mesh job, so N waiting threads wake for N jobs. An
    /// AutoResetEvent would wake exactly one however many were queued.
    /// </summary>
    readonly SemaphoreSlim meshSignal = new(0);

    readonly Thread captureThread;
    readonly Thread[] meshThreads;
    volatile bool running = true;

    /// <summary>
    /// Mesh builders. Meshing reads only immutable SectionSnapshots - the reason the
    /// snapshot discipline exists - so it parallelises with no locking. Capture does not
    /// get the same treatment: it reads live IWorldChunk objects the engine owns, and
    /// multiplying that by a thread count multiplies the risk for no comparable gain.
    ///
    /// Leaves two cores for the game's own render and simulation threads.
    /// </summary>
    static int MeshThreadCount => Math.Clamp(Environment.ProcessorCount - 2, 1, 4);

    public int MeshThreads => meshThreads.Length;

    public int PendingCaptures => captureJobs.Count;
    public int PendingMeshes => meshJobs.Count;

    public int CaptureErrors;
    public int MeshErrors;

    /// <summary>First swallowed exception of each kind, for soak-log diagnosis.</summary>
    public string? FirstCaptureError;
    public string? FirstMeshError;

    public LodWorker()
    {
        captureThread = new Thread(CaptureLoop)
        {
            Name = "distantvistas-capture",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        captureThread.Start();

        meshThreads = new Thread[MeshThreadCount];
        for (int i = 0; i < meshThreads.Length; i++)
        {
            meshThreads[i] = new Thread(MeshLoop)
            {
                Name = "distantvistas-mesh-" + i,
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
            };
            meshThreads[i].Start();
        }
    }

    public void EnqueueCapture(CaptureJob job)
    {
        captureJobs.Enqueue(job);
        captureSignal.Set();
    }

    public void EnqueueMesh(MeshJob job)
    {
        meshJobs.Enqueue(job);
        meshSignal.Release();
    }

    /// <summary>
    /// MeshJob plus its length-4 neighbor slot. Reused so enqueue does not allocate
    /// a fresh neighbor array for every section.
    /// </summary>
    public MeshJob RentMeshJob()
    {
        if (meshJobPool.TryTake(out MeshJob? job))
        {
            job.Key = 0;
            job.Self = null!;
            SectionSnapshot?[] n = job.Neighbors;
            n[0] = n[1] = n[2] = n[3] = null;
            return job;
        }
        return new MeshJob();
    }

    void ReturnMeshJob(MeshJob job)
    {
        job.Self = null!;
        SectionSnapshot?[] n = job.Neighbors;
        n[0] = n[1] = n[2] = n[3] = null;
        meshJobPool.Add(job);
    }

    // Separate loops, not one. The old shared loop drained EVERY queued capture before
    // taking a single mesh job, so exploring - which is exactly when new terrain most needs
    // drawing - starved meshing and left coarse parents on screen for minutes.

    void CaptureLoop()
    {
        while (running)
        {
            bool didWork = false;
            while (captureJobs.TryDequeue(out CaptureJob? job))
            {
                didWork = true;
                try
                {
                    CaptureResult? result = Capture(job);
                    if (result != null) CaptureResults.Enqueue(result);
                }
                catch (Exception e)
                {
                    // Chunk disposed mid-read or similar; the column re-enqueues on its next ChunkDirty.
                    Interlocked.Increment(ref CaptureErrors);
                    Interlocked.CompareExchange(ref FirstCaptureError, e.ToString(), null);
                }
            }

            if (!didWork) captureSignal.WaitOne(250);
        }
    }

    void MeshLoop()
    {
        while (running)
        {
            // Timed wait rather than indefinite, so shutdown never depends on a permit.
            if (!meshSignal.Wait(250)) continue;
            if (!meshJobs.TryDequeue(out MeshJob? job)) continue;

            try
            {
                MeshResults.Enqueue(LodMesher.BuildMesh(job));
            }
            catch (Exception e)
            {
                // Snapshot inconsistency; section will re-mesh on its next change.
                Interlocked.Increment(ref MeshErrors);
                Interlocked.CompareExchange(ref FirstMeshError, e.ToString(), null);
            }
            finally
            {
                ReturnMeshJob(job);
            }
        }
    }

    // ---- Capture: chunk column → RLE columns with raw block ids ----

    static CaptureResult? Capture(CaptureJob job)
    {
        const int step = LodSection.ColumnStepBlocks;
        const int colsPerChunk = ChunkSize / step;

        int baseX = job.Cx * ChunkSize;
        int baseZ = job.Cz * ChunkSize;
        int sectionX = baseX / LodSection.SectionBlocks;
        int sectionZ = baseZ / LodSection.SectionBlocks;
        int colOffsetX = (baseX % LodSection.SectionBlocks) / step;
        int colOffsetZ = (baseZ % LodSection.SectionBlocks) / step;

        var batch = new ulong[]?[LodSection.GridSize * LodSection.GridSize];
        var runs = new List<ulong>(24);
        bool anyColumn = false;

        // Rain map values can sit at/above map height on freshly streamed columns
        // (uninitialized sentinel) - clamp so the y walk stays inside the chunk stack.
        int maxY = job.Chunks.Length * ChunkSize - 1;

        for (int cz = 0; cz < colsPerChunk; cz++)
        {
            for (int cx = 0; cx < colsPerChunk; cx++)
            {
                int lx = cx * step;
                int lz = cz * step;
                int startY = Math.Min(job.RainMap[lz * ChunkSize + lx], maxY);
                if (startY <= 0) continue;

                runs.Clear();
                int currentBlock = 0;
                int runTop = 0;
                bool complete = true;

                for (int y = startY; y >= 1; y--)
                {
                    IWorldChunk? chunk = job.Chunks[y / ChunkSize];
                    if (chunk == null || chunk.Disposed)
                    {
                        complete = false;
                        break;
                    }

                    int blockId = chunk.UnpackAndReadBlock(
                        ((y % ChunkSize) * ChunkSize + lz) * ChunkSize + lx,
                        BlockLayersAccess.FluidOrSolid);

                    if (blockId != currentBlock)
                    {
                        if (currentBlock != 0) runs.Add(LodSection.PackRun(currentBlock, runTop, y + 1));
                        currentBlock = blockId;
                        runTop = y + 1;
                    }
                }

                if (!complete) continue;
                if (currentBlock != 0) runs.Add(LodSection.PackRun(currentBlock, runTop, 1));

                batch[LodSection.ColumnIndex(colOffsetX + cx, colOffsetZ + cz)] = runs.ToArray();
                anyColumn = true;
            }
        }

        if (!anyColumn) return null;

        return new CaptureResult
        {
            SectionKey = LodWorld.SectionKey(0, sectionX, sectionZ),
            Cx = job.Cx,
            Cz = job.Cz,
            RunsByColumn = batch,
        };
    }

    public void Dispose()
    {
        running = false;
        captureSignal.Set();
        meshSignal.Release(meshThreads.Length);

        captureThread.Join(2000);
        foreach (Thread t in meshThreads) t.Join(2000);

        captureSignal.Dispose();
        meshSignal.Dispose();
    }
}
