using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DistantVistas.Net;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Colour and tint slot for a captured block. The only part of capture that is not
/// side-agnostic: block colour comes from <c>capi.BlockTextureAtlas</c>, which a
/// dedicated server does not have. See DESIGN.md §10.4.
/// </summary>
/// <param name="blockId">Live block id from the capture.</param>
/// <param name="cx">Chunk column X, for sampling position.</param>
/// <param name="cz">Chunk column Z, for sampling position.</param>
/// <param name="sampleY">Y of the run's top, for sampling position.</param>
public delegate (int Color, byte TintSlot, bool Baked) LodPaletteDescriber(int blockId, int blockX, int blockY, int blockZ);

/// <summary>Which live tint applies to a block. The server has none and answers 0.</summary>
public delegate byte LodTintSlotResolver(Block block);

/// <summary>
/// Everything between "a chunk column arrived" and "a section is on disk": capture
/// scheduling, palette registration, mip propagation and persistence. Owns all mutation
/// of the <see cref="LodWorld"/>; the worker thread only reads immutable snapshots.
///
/// Side-agnostic on purpose. The client drives it from `ChunkDirty` and also renders from
/// the same LodWorld; a server drives it from `ChunkColumnLoaded` and never renders. What
/// differs between them is which chunks arrive and what a palette entry's colour is, so
/// those are the two things injected rather than branched on - a copy of this per side
/// would drift, and the mip and persistence rules are exactly what must not.
///
/// Every method here must be called from the thread that owns the world (the game tick).
/// </summary>
public class LodPipeline
{
    // Scheduling only hands chunk references to the capture thread, so it is cheap and
    // runs ahead; the worker backlog is the real throttle on capture work in flight.
    const int CaptureSchedulesPerTick = 8;
    const int MaxWorkerCaptureBacklog = 32;

    /// <summary>
    /// Applies per tick: one when idle, more while results are stacked up, always
    /// under a time budget. Player looking/walking/hitch uses
    /// <see cref="LodFrameBudget.CaptureApplies"/> — queue, do not drop.
    /// </summary>
    const double CaptureApplyBudgetMs = 4.0;

    const int PropagationsPerTick = 4;
    const int CatchUpPropagationsPerTick = 48;
    const int SectionSavesPerTick = 2;
    /// <summary>
    /// After the join burst drains, keep circling resident plates around the player.
    /// Closest unfinished first; two a tick. Skip entirely under mesh pressure.
    /// Remesh only on palette change or a stale VBO for this bake token.
    /// </summary>
    const int SeasonIdleSectionsPerTick = 2;
    const int SeasonIdleSkipWalkCap = 64;
    const int DeciduousStripSectionsPerTick = 4;
    /// <summary>Log season catch-up every N pipeline ticks while the epoch is active.</summary>
    const int SeasonProgressLogEveryTicks = 150;
    const int ChunkSize = GlobalConstants.ChunkSize;

    /// <summary>
    /// Ceiling on columns waiting for capture. Memory only: an entry is one long. The
    /// old ceiling of 200 silently dropped every ChunkDirty past it, and nothing ever
    /// fired again for those columns while the chunk stayed loaded, so a fast flight
    /// over new land left a scatter of never-captured 32x32 quadrants that drew as sky
    /// squares once vanilla unloaded them. Anything that still hits this is counted.
    /// </summary>
    const int MaxPendingColumns = 16384;

    /// <summary>
    /// Queued snapshots hold copies of their section's run data, so an unbounded queue
    /// is an unbounded memory leak if the disk can't keep up. Past this depth the
    /// sections simply stay dirty (and therefore RAM-resident) and retry later.
    /// </summary>
    const int MaxStorageBacklog = 256;

    readonly ICoreAPI api;
    readonly ILogger logger;
    readonly LodPaletteDescriber describePalette;

    /// <summary>Tint slot for a block; the server has no tints and leaves it 0.</summary>
    readonly LodTintSlotResolver tintSlotFor;

    public LodWorld World { get; }
    public LodWorker Worker { get; }

    LodStore? store;
    LodStorageThread? storageThread;

    /// <summary>Block codes the registry never answered for; empty is the norm.</summary>
    public string[] UnresolvedBlockCodes() => store?.UnresolvedCodes() ?? Array.Empty<string>();

    /// <summary>
    /// Colours palette entries that were saved without one. Set by the client, which has
    /// the texture atlas; a server leaves it null, because storing 0 is what a server is
    /// supposed to do. See LodPaletteRepair for why an existing cache needs this.
    /// </summary>
    public System.Func<LodSection, int>? RepairUncoloredPalette;

    /// <summary>Palette entries given a colour on load because the cache had none.</summary>
    public int PaletteEntriesRepaired { get; private set; }

    /// <summary>Repaint baked palette colours for the current calendar month.</summary>
    public System.Func<LodSection, long, int>? RebakeSeasonPalette;

    /// <summary>Upgrade legacy live-tint palette rows to discover-baked on disk load.</summary>
    public System.Func<LodSection, long, int>? HealLegacyPalette;

    public int SeasonSectionsRepainted { get; private set; }

    /// <summary>Sections waiting for budgeted season rebake / legacy heal.</summary>
    public int SeasonDirtyCount => World.SeasonDirty.Count;
    int seasonDirtyResidentFrame = -1;
    bool seasonDirtyHasResident;
    readonly bool[] seasonDirtyLevelResident = new bool[3];

    /// <summary>
    /// LOD level currently being drained (0..MaxLevel). -1 when idle / only cold left.
    /// </summary>
    public int SeasonCatchUpLevel { get; private set; } = -1;

    /// <summary>
    /// Packed calendar bake token. Idle sweep remeshes a section once per token when
    /// the resident mesh is still last season.
    /// </summary>
    public int SeasonLookToken;

    /// <summary>Skip idle season work this tick (mesh pressure only).</summary>
    public bool YieldSeasonWork;

    /// <summary>Looking, walking, or hitch — not chunk arrivals.</summary>
    public bool PlayerBusy;

    /// <summary>Vanilla streaming, 16-block walk hold, or two chunk arrivals.</summary>
    public bool StreamingBusy;

    /// <summary>A real XZ step this tick (keep-circle must stay alive).</summary>
    public bool StepBusy;

    /// <summary>Previous frame over <see cref="LodFrameBudget.FrameBusyMs"/>.</summary>
    public bool LastFrameWasHitch;

    /// <summary>Yaw/pitch past the look deadzone. Distinct from 16-block streaming.</summary>
    public bool LookBusy;

    /// <summary>Idle-sweep sections whose palette actually melted or frosted.</summary>
    public int SeasonIdleMelted { get; private set; }

    /// <summary>Times the idle walk finished a player-centered lap and restarted.</summary>
    public int SeasonIdleLap { get; private set; }

    /// <summary>Resident sections not yet baked on the current lap.</summary>
    public int SeasonIdlePending { get; private set; }

    /// <summary>Blocks from the player to the next idle target; -1 if none.</summary>
    public int SeasonIdleNearestBlocks { get; private set; } = -1;

    /// <summary>Why idle did not bake this tick, or a short running status.</summary>
    public string SeasonIdleState { get; private set; } = "idle";

    /// <summary>Why idle accurate recapture did not run, or a short running status.</summary>
    public string IdleRecaptureState { get; private set; } = "off";

    public int IdleRecaptureQueued { get; private set; }
    public int IdleRecaptureLoads { get; private set; }

    readonly HashSet<long> idleRecaptureVisited = new();
    readonly List<(long Key, double DistSq)> idleRecaptureScratch = new();
    int idleRecaptureScratchIndex;
    double idleRecaptureAnchorX;
    double idleRecaptureAnchorZ;
    bool idleRecaptureAnchorSet;

    int seasonProgressTicks;
    int seasonIdleLogTicks;
    int seasonEpochKeepUntilTick;
    double seasonIdleAnchorX;
    double seasonIdleAnchorZ;
    bool seasonIdleAnchorSet;
    readonly HashSet<long> seasonIdleVisited = new();
    readonly List<(long Key, double DistSq)> seasonIdleScratch = new();
    int seasonIdleScratchIndex;

    /// <summary>True while a join or bake-epoch season sync is draining.</summary>
    public bool SeasonRepaintEpochActive => World.SeasonRepaintEpochActive;

    readonly ConcurrentDictionary<long, byte> queuedColumns = new();
    readonly ConcurrentQueue<long> pendingColumns = new();
    readonly BlockPos paletteSamplePos = new(0, 0, 0);

    /// <summary>L0 sections waiting for Deciduous Collapse strip + force recapture.</summary>
    readonly HashSet<long> deciduousRefresh = new();
    readonly List<long> deciduousRefreshDone = new();
    readonly List<long> seasonRepaintDone = new();
    readonly List<(long Key, double DistSq)> seasonRepaintScratch = new();

    /// <summary>
    /// False until the store exists. Nothing may touch sections before then: applying a
    /// capture to a freshly-created empty section would shadow (and later overwrite) the
    /// stored row. The column queue holds work until it flips.
    /// </summary>
    public bool Active { get; private set; }

    public bool Persisting => store != null;
    public int CachedSectionsLoaded { get; private set; }
    public int ColumnsCaptured { get; private set; }
    public int PendingColumns => pendingColumns.Count;

    /// <summary>Columns refused by QueueColumn because MaxPendingColumns was reached. Zero is the norm.</summary>
    public int ColumnsDropped => columnsDropped;
    int columnsDropped;

    /// <summary>Columns the loaded-chunk sweep found uncaptured and re-queued.</summary>
    public int ColumnsSwept { get; private set; }

    /// <summary>Quadrants whose provisional (peeked / foreign) data a local capture replaced.</summary>
    public int ProvisionalQuadrantsConfirmed { get; private set; }
    public string? DbPath { get; private set; }

    // Main-thread storage cost, measured to decide whether moving SQLite work to a
    // background thread is worth its complexity.
    readonly System.Diagnostics.Stopwatch storageClock = new();
    public double SaveMsMax { get; private set; }
    public double SaveMsTotal { get; private set; }
    public int SaveCalls { get; private set; }
    public double LoadMsMax { get; private set; }
    public double LoadMsTotal { get; private set; }
    public int LoadCalls { get; private set; }
    public LodStorageThread? StorageThread => storageThread;

    int tickCounter;

    public LodPipeline(ICoreAPI api, ILogger logger, LodPaletteDescriber describePalette,
        LodTintSlotResolver? tintSlotFor = null)
    {
        this.api = api;
        this.logger = logger;
        this.describePalette = describePalette;
        this.tintSlotFor = tintSlotFor ?? (_ => 0);
        World = new LodWorld();
        Worker = new LodWorker();
        Remote = new LodRemoteKeySet(World);
    }

    public void ResetStorageStats()
    {
        SaveMsMax = SaveMsTotal = LoadMsMax = LoadMsTotal = 0;
        SaveCalls = LoadCalls = 0;
    }

    /// <summary>Note a chunk column as needing (re)capture. Safe from any thread.</summary>
    public void QueueColumn(int cx, int cz)
    {
        if (!NeedsCapture(cx, cz)) return;
        EnqueueColumn(cx, cz);
    }

    /// <summary>
    /// Re-capture even when the quadrant is already full. Used when Deciduous dormancy
    /// changes what a leaf cell means without ChunkDirty firing (blocks are not removed).
    /// Skip unloaded columns: a soil-only RLE from missing chunks would wipe snow.
    /// </summary>
    public void ForceQueueColumn(int cx, int cz)
    {
        if (!Active) return;
        if (api.World.BlockAccessor.GetMapChunk(cx, cz) == null) return;
        EnqueueColumn(cx, cz);
    }

    void EnqueueColumn(int cx, int cz)
    {
        if (pendingColumns.Count >= MaxPendingColumns)
        {
            Interlocked.Increment(ref columnsDropped);
            return;
        }

        long key = ((long)cz << 32) | (uint)cx;
        if (queuedColumns.TryAdd(key, 0)) pendingColumns.Enqueue(key);
    }

    /// <summary>
    /// Whether a loaded chunk column has anything to teach the cache. Safe from any thread.
    ///
    /// A level-0 section spans 2x2 vanilla chunks (64 blocks). Skipping whenever the
    /// SECTION key is in HasDataSet after the first chunk lands left the other three
    /// quadrants empty forever — median CapturedColumns=1024 on live caches, which
    /// draws as a regular 32-block checkerboard of cliffs into void. Skip only when
    /// THIS chunk's quadrant is already captured by this side. A quadrant that is
    /// captured but provisional (peek, sweep, another player) is still work: the loaded
    /// chunk is the real terrain and replaces it. Melt season (May–Oct) also recaptures
    /// a full non-provisional quadrant that still has inferred FlagSnow or a leftover
    /// snowlayer top — Cover must not invent on visited land, and a stuck snowlayer
    /// BlockId only goes away by recapture. Winter still recaptures inferred Cover
    /// snow (FlagSnow+FlagBaked) when the chunk is loaded; real FlagSnow-only stays skipped.
    /// Cold (non-resident) HasDataSet keys skip to
    /// avoid ChunkDirty GC thrash unless they are known sparse, incomplete or
    /// provisional; once the renderer residencies a partial section, the next dirty
    /// event or sweep fills missing quadrants.
    /// </summary>
    public bool NeedsCapture(int cx, int cz)
    {
        int sb = LodSection.SectionBlocks;
        int sx = (cx * ChunkSize) / sb;
        int sz = (cz * ChunkSize) / sb;
        long sectionKey = LodWorld.SectionKey(0, sx, sz);
        if (World.Sections.TryGetValue(sectionKey, out LodSection? sec))
        {
            int colOx = ((cx * ChunkSize) % sb) / LodSection.ColumnStepBlocks;
            int colOz = ((cz * ChunkSize) % sb) / LodSection.ColumnStepBlocks;
            int q = LodSection.QuadrantOf(colOx, colOz);
            if (sec.QuadrantFullyCaptured(q) && !sec.IsProvisionalQuadrant(q))
            {
                int month = 0;
                try { month = api.World.Calendar.Month; }
                catch { /* shutdown */ }
                int sea = 110;
                try { sea = api.World.SeaLevel; }
                catch { /* */ }
                bool hasSnow = sec.QuadrantHasSnowSurface(q);
                bool pendingVisit = sec.LoadedCaptureLookToken != SeasonLookToken
                    && sec.QuadrantMaxY(q) > sea + 12;
                if (!LodIdleRecapturePolicy.LoadedColumnNeedsRecapture(
                    fullyCaptured: true, provisional: false,
                    inferredSnow: sec.QuadrantHasInferredSnow(q),
                    month, hasSnow, pendingVisit))
                    return false;
            }
            // Track sparse pre-0.7.7 one-quadrant sections so cold skips stay open.
            World.ClassifySparseL0(sectionKey, sec);
            return true;
        }

        if (World.HasDataSet.Contains(sectionKey))
        {
            // Pre-0.7.7 caches often stored only 1/4 of an L0 section. Skipping every
            // cold HasDataSet key froze those holes forever. Allow re-queue when marked
            // sparse (or unknown — demand-load will classify on first resident hit).
            // Any incomplete L0 (2 or 3 of 4 quadrants) re-queues for the same
            // reason: the renderer never draws it alone, so a walk-away mid-capture
            // that spilled to disk stayed a sky square on every later visit.
            bool classified = World.SparseL0Classified.Contains(sectionKey);
            bool needsFill = World.SparseL0Keys.Contains(sectionKey)
                || World.IncompleteL0Keys.Contains(sectionKey)
                || World.ProvisionalL0Keys.Contains(sectionKey);
            return !classified || needsFill;
            // Enqueue falls through; GetOrCreateSection loads the row and fills gaps.
        }

        return true;
    }

    // ---- Loaded-chunk sweep ----

    int sweepRow = int.MinValue;
    int sweepRadius;

    /// <summary>
    /// Re-queue loaded chunk columns whose L0 quadrant is not captured (or only
    /// provisionally). ChunkDirty is the primary feed, but it fires once per chunk
    /// arrival: a column lost between that event and its capture - queue full, chunk
    /// stack not complete yet so Capture skipped it, chunk disposed mid-read - stayed
    /// uncaptured for as long as it stayed loaded, because nothing fired again. It only
    /// came back as sky after vanilla unloaded it, and the next visit repeated the race.
    ///
    /// One row of the square per call, so a 55x55 chunk square (view distance 832)
    /// costs 55 map-chunk lookups a tick and covers the whole disc in under 3 seconds.
    /// Main thread only: it reads the loaded chunk list.
    /// </summary>
    public void SweepLoadedColumns(int centreCx, int centreCz, int radiusChunks)
    {
        if (!Active || radiusChunks <= 0) return;

        if (sweepRow == int.MinValue || sweepRow > radiusChunks || sweepRadius != radiusChunks)
        {
            sweepRow = -radiusChunks;
            sweepRadius = radiusChunks;
        }

        int cz = centreCz + sweepRow;
        if (cz >= 0)
        {
            for (int dx = -radiusChunks; dx <= radiusChunks; dx++)
            {
                int cx = centreCx + dx;
                if (cx < 0) continue;
                // Cheap DV-side test first; the engine lookup only for columns we want.
                if (!NeedsCapture(cx, cz)) continue;
                long key = ((long)cz << 32) | (uint)cx;
                if (queuedColumns.ContainsKey(key)) continue;
                if (api.World.BlockAccessor.GetMapChunk(cx, cz) == null) continue;
                if (pendingColumns.Count >= MaxPendingColumns) return;
                if (queuedColumns.TryAdd(key, 0))
                {
                    pendingColumns.Enqueue(key);
                    ColumnsSwept++;
                }
            }
        }

        sweepRow++;
        if (sweepRow > radiusChunks) sweepRow = -radiusChunks;
    }

    /// <summary>
    /// Player-priority idle: remesh from already-loaded columns first, then (when a
    /// server-side loader exists) pull explored savegame columns one at a time.
    /// Loaded recapture yields only while looking/walking/mesh pressure. Chunk
    /// arrivals and join epoch still yield extra disk loads, not RAM recapture.
    /// Does not raise viewDistance. Does not generate unexplored land.
    /// </summary>
    public void TickIdleAccurateRecapture(
        double px, double pz, IExploredColumnLoader? loader, bool streaming)
    {
        if (!Active)
        {
            IdleRecaptureState = "off";
            return;
        }
        if (!LodIdleRecapturePolicy.AllowLoadedRecapture(PlayerBusy, YieldSeasonWork))
        {
            IdleRecaptureState = LastFrameWasHitch ? "yield: hitch"
                : LookBusy ? "yield: looking"
                : PlayerBusy ? "yield: walking"
                : "yield: mesh pressure";
            return;
        }

        if (!idleRecaptureAnchorSet
            || LodSeasonIdleOrder.PlayerMovedEnough(idleRecaptureAnchorX, idleRecaptureAnchorZ, px, pz))
        {
            idleRecaptureVisited.Clear();
            idleRecaptureAnchorX = px;
            idleRecaptureAnchorZ = pz;
            idleRecaptureAnchorSet = true;
            FillIdleRecaptureScratch(px, pz);
        }
        else if (idleRecaptureScratchIndex >= idleRecaptureScratch.Count)
        {
            idleRecaptureVisited.Clear();
            FillIdleRecaptureScratch(px, pz);
        }

        if (idleRecaptureScratch.Count == 0 || idleRecaptureScratchIndex >= idleRecaptureScratch.Count)
        {
            IdleRecaptureState = "no resident L0";
            return;
        }

        int queued = 0;
        int loads = 0;
        int probes = 0;
        int skipped = 0;
        bool stillIdle() => LodIdleRecapturePolicy.AllowExploredLoad(
            PlayerBusy, YieldSeasonWork, streaming)
            && loader is not { IsVanillaBusy: true };

        while (idleRecaptureScratchIndex < idleRecaptureScratch.Count
            && (queued < LodIdleRecapturePolicy.ForceQueuePerTick
                || (loader != null && loads < LodIdleRecapturePolicy.LoadsPerTick)))
        {
            long key = idleRecaptureScratch[idleRecaptureScratchIndex++].Key;
            idleRecaptureVisited.Add(key);
            int didQueue = 0;
            int didLoad = 0;
            int didProbe = 0;
            WalkSectionColumns(key, (cx, cz) =>
            {
                if (!NeedsCapture(cx, cz)) return;
                if (api.World.BlockAccessor.GetMapChunk(cx, cz) != null)
                {
                    if (queued + didQueue >= LodIdleRecapturePolicy.ForceQueuePerTick) return;
                    int before = pendingColumns.Count;
                    ForceQueueColumn(cx, cz);
                    if (pendingColumns.Count > before) didQueue++;
                    return;
                }
                if (loader == null) return;
                if (loads + didLoad >= LodIdleRecapturePolicy.LoadsPerTick) return;
                ExploredLoadAttempt attempt = loader.TryRequest(cx, cz, stillIdle);
                if (attempt == ExploredLoadAttempt.Loading) didLoad++;
                else if (attempt == ExploredLoadAttempt.Probing) didProbe++;
            });
            queued += didQueue;
            loads += didLoad;
            probes += didProbe;
            if (didQueue == 0 && didLoad == 0 && didProbe == 0)
            {
                skipped++;
                if (skipped >= LodIdleRecapturePolicy.SkipWalkCap) break;
            }
        }

        IdleRecaptureQueued += queued;
        IdleRecaptureLoads += loads;
        IdleRecaptureState = loads > 0 || queued > 0 || probes > 0
            ? $"queued {queued}, load {loads}, probe {probes}"
            : "idle";
    }

    void FillIdleRecaptureScratch(double px, double pz)
    {
        LodSeasonIdleOrder.FillNearestCapped(
            idleRecaptureScratch, World.Sections.Keys, idleRecaptureVisited, px, pz,
            LodFrameBudget.ScratchCap, maxDistBlocks: 0,
            static key => LodWorld.KeyLevel(key) == 0);
        idleRecaptureScratchIndex = 0;
    }

    void WalkSectionColumns(long sectionKey, Action<int, int> visit)
    {
        int sb = LodSection.SectionBlocks;
        int chunksPerEdge = sb / ChunkSize;
        if (chunksPerEdge < 1) chunksPerEdge = 1;
        int sx = LodWorld.KeySx(sectionKey);
        int sz = LodWorld.KeySz(sectionKey);
        for (int dz = 0; dz < chunksPerEdge; dz++)
        {
            for (int dx = 0; dx < chunksPerEdge; dx++)
                visit(sx * chunksPerEdge + dx, sz * chunksPerEdge + dz);
        }
    }

    /// <summary>
    /// Capture a chunk column the caller already holds, rather than one the world can
    /// be asked for. <see cref="QueueColumn"/> cannot serve a peeked column: a peek
    /// puts nothing in the loaded chunk list, so the BlockAccessor lookup in
    /// ScheduleCaptures finds nothing - or, for a coordinate that also exists on disk,
    /// finds the savegame's version instead.
    ///
    /// Safe from any thread. The chunk array is copied and the rain map is cloned, so
    /// the caller can drop its references as soon as this returns. Deliberately not
    /// routed through the queued-column dedup dictionaries: that keeps this lock-free,
    /// and a duplicate capture of identical data is idempotent at apply time.
    /// </summary>
    /// <returns>False when the pipeline is closed or the inputs cannot describe a column.</returns>
    public bool CaptureColumn(int cx, int cz, IWorldChunk?[] chunks, ushort[] rainMap)
    {
        if (!Active) return false;
        if (chunks.Length == 0 || rainMap.Length < ChunkSize * ChunkSize) return false;

        var refs = new IWorldChunk?[chunks.Length];
        Array.Copy(chunks, refs, chunks.Length);

        Worker.EnqueueCapture(new CaptureJob
        {
            Cx = cx,
            Cz = cz,
            Chunks = refs,
            RainMap = (ushort[])rainMap.Clone(),
            Provisional = true, // PeekChunkColumn stops at Terrain; trees are not in this yet.
        });
        return true;
    }

    /// <summary>
    /// True when the capture thread is at its backlog. A producer that can throttle
    /// itself (chunk generation) must stop at the source: every queued job holds a
    /// whole unpacked chunk column in memory until the capture thread drains it.
    /// </summary>
    public bool CaptureBacklogFull => Worker.PendingCaptures >= MaxWorkerCaptureBacklog;

    /// <summary>One quadrant of an L0 64x64 grid (pre-0.7.7 QueueColumn bug fingerprint).</summary>
    public static bool IsSparseL0(LodSection sec) =>
        sec.CapturedColumns > 0 && sec.CapturedColumns <= LodSection.GridSize * LodSection.GridSize / 4;

    /// <summary>
    /// Open (or create) the LOD cache for the current world and adopt its key set.
    /// Failing to open is not fatal: capture and rendering work without persistence.
    /// </summary>
    /// <param name="subdir">ModData-relative directory for the cache file.</param>
    /// <param name="suffix">
    /// Appended to the world key. Belt and braces after a real bug: client and server
    /// resolve the same ModData path from the same savegame identifier, so in one process
    /// they opened one file through two connections. Naming them apart means that class of
    /// mistake cannot silently corrupt a cache even if the two ever coexist again.
    /// </param>
    public void Open(string subdir, string suffix = "")
    {
        string worldKey = api.World.SavegameIdentifier;
        if (string.IsNullOrEmpty(worldKey)) worldKey = "seed-" + api.World.Seed;
        worldKey = Regex.Replace(worldKey, "[^A-Za-z0-9_-]", "_");

        string dir = api.GetOrCreateDataPath(subdir);
        string dbPath = Path.Combine(dir, worldKey + suffix + ".db");

        var newStore = new LodStore(logger);
        if (!newStore.Open(dbPath))
        {
            newStore.Dispose();
            Active = true; // no persistence this session; everything else still works
            return;
        }

        store = newStore;
        DbPath = dbPath;
        newStore.ClassifyBlock = blockId =>
        {
            Block? block = blockId > 0 ? api.World.GetBlock(blockId) : null;
            return block == null ? ((byte)0, (byte)0) : (LodBlockPolicy.FlagsFor(block), tintSlotFor(block));
        };
        storageThread = new LodStorageThread(newStore);

        // Background reloads for the render path. The loader runs on the storage
        // thread; results are installed on the world thread in Tick.
        storageThread.SetLoader(key => newStore.LoadSection(
            LodWorld.KeyLevel(key), LodWorld.KeySx(key), LodWorld.KeySz(key), api.World, resolveBlockIds: false));
        // Routing, not just loading: a key the server offered and local disk has never
        // held would come back empty from the store and land in LoadFailed, which is
        // permanent. Those go to the network instead, and the quadtree's own
        // LoadsInFlight bookkeeping covers both paths unchanged.
        World.RequestAsyncLoad = key =>
        {
            if (!Remote.WantFromRemote(key)) storageThread?.RequestLoad(key);
        };

        World.LoadFromStore = key =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            LodSection? loaded = store?.LoadSection(
                LodWorld.KeyLevel(key), LodWorld.KeySx(key), LodWorld.KeySz(key), api.World);
            double ms = clock.Elapsed.TotalMilliseconds;
            LoadCalls++;
            LoadMsTotal += ms;
            if (ms > LoadMsMax) LoadMsMax = ms;
            if (loaded != null) AfterSectionLoaded(key, loaded);
            return loaded;
        };
        CachedSectionsLoaded = store.LoadAllKeys((level, sx, sz, applyToParent, provisional) =>
        {
            Remote.AddLocalKey(LodWorld.SectionKey(level, sx, sz));
            World.InstallStoredKey(level, sx, sz, applyToParent, provisional != 0,
                LodFrameBudget.QueueMipOnStoreIndex);
        });
        Active = true;
        logger.Notification("LOD cache: {0}", dbPath);
    }

    /// <summary>
    /// The stored blob for a key, unparsed, for serving over the network. Null when the
    /// key is not on disk - including when it is resident in RAM but not yet flushed,
    /// which is why the caller treats a miss as "ask again later" rather than "gone".
    /// </summary>
    public byte[]? LoadBlob(long key) => store?.LoadBlob(
        LodWorld.KeyLevel(key), LodWorld.KeySx(key), LodWorld.KeySz(key));

    /// <summary>
    /// Adopt a section that arrived from somewhere other than local disk. Returns false if
    /// the key already has local data, which wins: the client's own capture is what it
    /// actually observed, including edits it witnessed (DESIGN.md §10.5).
    /// </summary>
    public bool InstallForeignBlob(long key, byte[] blob, Action<LodSection>? recolor)
    {
        if (store == null || blob.Length == 0) return false;
        if (World.Sections.ContainsKey(key)) return false;

        LodSection? section = store.DeserializeForeign(blob, api.World);
        if (section == null) return false;

        // The sender had no texture atlas, so every palette colour is 0. Fill them in
        // before anything can draw the section.
        recolor?.Invoke(section);
        section.RemoveRunsWithFlag(LodPaletteEntry.FlagSkip);

        // Not observed by this side. A peek stops at the Terrain pass (no trees, no
        // ponds) and a sweep or another player's capture can predate edits; the chunk
        // the player actually loads is the truth and re-captures over this.
        if (LodWorld.KeyLevel(key) == 0) section.MarkCapturedQuadrantsProvisional();

        World.InstallLoaded(key, section);
        // Persist it: re-fetching a mean 45.9 KB a section every session is not an option,
        // so a section from the network becomes part of the local cache like any other.
        World.MarkChanged(key);
        ForeignSectionsInstalled++;
        return true;
    }

    public int ForeignSectionsInstalled { get; private set; }

    /// <summary>
    /// Which keys a remote source can supply and which the view is waiting on. Its own
    /// class so the set logic can be tested without a game API - see LodRemoteKeySet.
    /// Private, and reached only through the delegating members below: the pipeline is
    /// the facade the mod system talks to, and two doors to the same state is how they
    /// drift apart.
    /// </summary>
    readonly LodRemoteKeySet Remote;

    /// <inheritdoc cref="LodRemoteKeySet.RemoteOnly"/>
    public HashSet<long> RemoteOnly => Remote.RemoteOnly;

    /// <inheritdoc cref="LodRemoteKeySet.MarkUnavailable"/>
    public void MarkRemoteUnavailable(long key) => Remote.MarkUnavailable(key);

    /// <inheritdoc cref="LodRemoteKeySet.AddRemoteKeys"/>
    public int AddRemoteKeys(IEnumerable<long> keys) => Remote.AddRemoteKeys(keys);

    /// <inheritdoc cref="LodRemoteKeySet.Wanted"/>
    public long[] RemoteWanted() => Remote.Wanted();

    /// <inheritdoc cref="LodRemoteKeySet.MarkRequested"/>
    public void MarkRemoteRequested(IEnumerable<long> sent) => Remote.MarkRequested(sent);

    /// <summary>One step of the whole pipeline. Call once per game tick.</summary>
    public void Tick()
    {
        if (!Active) return;

        InstallLoadedSections();
        ScheduleCaptures();
        ApplyCaptureResults();
        World.ProcessPropagation(LodFrameBudget.PropagationsThisTick(
            PlayerBusy, World.MipDirty.Count, CatchUpPropagationsPerTick, PropagationsPerTick));
        ProcessSeasonRepaint();
        ProcessSeasonIdleSweep();
        ProcessDeciduousLeafRefresh();
        SaveSomeDirtySections(SectionSavesPerTick);
        tickCounter++;
    }

    /// <summary>
    /// Drop cold sections from RAM around an anchor. Only meaningful once reload-from-disk
    /// exists, and only every ~5s: the sweep walks every resident section.
    /// </summary>
    public bool MaybeEvictAround(double x, double z)
    {
        if (tickCounter % 100 != 0 || World.LoadFromStore == null) return false;
        // Soft-cap spill: under pressure only (caller gates MeshPressureActive).
        // Never dumps the SQLite index into RAM; only drops cold residents already loaded.
        // 0.8.46: when the heap is already multi-GB / over soft, spill harder (still
        // never inside the keep ring — EvictColdSections respects distance).
        int budget = 50;
        int soft = LodMemoryBudget.MaxResidentSections;
        int n = World.Sections.Count;
        if (soft > 0 && n > soft)
            budget = 220;
        else if (soft > 0 && n > (soft * 85) / 100)
            budget = 120;
        World.EvictColdSections(x, z, budget);
        return tickCounter % 1200 == 0;
    }

    /// <summary>
    /// Adopt sections the storage thread finished reading. Cheap: the decompress
    /// already happened off-thread, this only publishes the reference.
    /// </summary>
    void InstallLoadedSections()
    {
        if (storageThread == null) return;

        while (storageThread.LoadResults.TryDequeue(out (long Key, LodSection? Section) result))
        {
            int repaired = 0;
            // Palette ids are resolved here, on the world thread, before anything can
            // read them: the storage thread must not touch the block registry.
            if (result.Section != null && store != null)
            {
                store.ResolvePendingPalette(result.Section, api.World);
                result.Section.RemoveRunsWithFlag(LodPaletteEntry.FlagSkip);
                AfterSectionLoaded(result.Key, result.Section, ref repaired);
            }
            World.InstallLoaded(result.Key, result.Section);

            // Written back, so the repair is done once rather than on every load for the
            // rest of the world's life. This is the only reason a read marks a section
            // dirty, and it stops as soon as the cache is clean.
            if (repaired > 0)
            {
                PaletteEntriesRepaired += repaired;
                World.MarkChanged(result.Key);
            }
        }
    }

    /// <summary>
    /// Palette repair and legacy discover-bake after a section is read from disk. Sync and
    /// async load paths both land here so FlagBaked survives Reclassify and old live-tint
    /// caches upgrade on revisit without a manual cache wipe.
    /// </summary>
    void AfterSectionLoaded(long key, LodSection section, ref int repaired)
    {
        seasonIdleVisited.Remove(key);
        repaired += RepairUncoloredPalette?.Invoke(section) ?? 0;
        // Live tint is shader uniforms. Do not remesh the resident set on load,
        // ForceAncestor, or SeasonForced. Leftover FlagBaked vegetation and
        // inferred Cover snow wait for the sit-still idle sweep.
        section.SeasonLookToken = SeasonLookToken;
        // Deciduous hides canopy without ChunkDirty: strip Collapse from cached leaf
        // sections as they load so winter join does not keep a summer canopy blob.
        // Month change ForceQueues for spring canopy return; load only strips.
        if (DeciduousLeafCompat.Present
            && LodWorld.KeyLevel(key) == 0
            && DeciduousLeafCompat.SectionHasLeafPalette(section, api.World)
            && StripCollapsedDeciduousLeaves(key, section))
        {
            World.MarkChanged(key);
            World.RenderDirty.Add(key);
        }
    }

    void AfterSectionLoaded(long key, LodSection section)
    {
        int repaired = 0;
        AfterSectionLoaded(key, section, ref repaired);
        if (repaired > 0)
        {
            PaletteEntriesRepaired += repaired;
            World.MarkChanged(key);
        }
    }

    // ---- Capture scheduling (world thread gathers refs, worker reads blocks) ----

    void ScheduleCaptures()
    {
        int chunkYCount = api.World.BlockAccessor.MapSizeY / ChunkSize;
        captureRainRetry.Clear();

        for (int n = 0; n < CaptureSchedulesPerTick
             && Worker.PendingCaptures < MaxWorkerCaptureBacklog
             && pendingColumns.TryDequeue(out long key); n++)
        {
            queuedColumns.TryRemove(key, out _);
            int cx = (int)(key & 0xFFFFFFFF);
            int cz = (int)(key >> 32);

            IMapChunk? mapChunk = api.World.BlockAccessor.GetMapChunk(cx, cz);
            ushort[]? rainMap = mapChunk?.RainHeightMap;
            if (rainMap == null)
            {
                // Map chunk present but rain map not ready yet — retry next tick.
                // Missing map chunk stays dropped: a soil-only RLE would wipe snow.
                if (mapChunk != null) captureRainRetry.Add(key);
                continue;
            }

            var chunks = new IWorldChunk?[chunkYCount];
            for (int cy = 0; cy < chunkYCount; cy++)
            {
                chunks[cy] = api.World.BlockAccessor.GetChunk(cx, cy, cz);
            }

            Worker.EnqueueCapture(new CaptureJob
            {
                Cx = cx,
                Cz = cz,
                Chunks = chunks,
                RainMap = (ushort[])rainMap.Clone(),
            });
        }

        for (int i = 0; i < captureRainRetry.Count; i++)
        {
            if (pendingColumns.Count >= MaxPendingColumns) break;
            long retry = captureRainRetry[i];
            if (queuedColumns.TryAdd(retry, 0)) pendingColumns.Enqueue(retry);
        }
    }

    // ---- Applying capture results: block ids → section palette ids ----

    /// <summary>
    /// Capture results whose section was evicted and is being reloaded. Bounded, and the
    /// bound is not decoration: past it the tick takes the blocking load rather than let
    /// this grow without limit, because throwing a result away loses captured terrain.
    /// </summary>
    readonly List<CaptureResult> deferredCaptures = new();

    const int MaxDeferredCaptures = 64;

    readonly List<long> captureRainRetry = new();

    readonly System.Diagnostics.Stopwatch applyClock = new();

    void ApplyCaptureResults()
    {
        // Idle: one a tick. Backed up while sitting still: several, until the time
        // budget is spent. Looking applies one. Sit-hitch applies none. Walking
        // still applies one so discovered land lands on the canvas.
        int backlog = Worker.CaptureResults.Count + deferredCaptures.Count;
        int budget = LodFrameBudget.CaptureApplies(
            PlayerBusy, LastFrameWasHitch, backlog, StepBusy);
        int applied = 0;
        applyClock.Restart();

        // Results waiting on a reload get first refusal, so a section that has come back
        // is merged before anything newer touches it.
        for (int i = 0; i < deferredCaptures.Count && budget > 0;)
        {
            if (!World.EnsureResident(deferredCaptures[i].SectionKey)) { i++; continue; }
            ApplyOneCaptureResult(deferredCaptures[i]);
            deferredCaptures.RemoveAt(i);
            budget--;
            applied++;
            if (applyClock.Elapsed.TotalMilliseconds > CaptureApplyBudgetMs) return;
        }

        while (budget-- > 0)
        {
            // Checked before the dequeue, so an over-budget result stays in the worker's
            // queue in order rather than being pulled out and parked ahead of older ones.
            if (applied > 0 && applyClock.Elapsed.TotalMilliseconds > CaptureApplyBudgetMs) return;
            if (!Worker.CaptureResults.TryDequeue(out CaptureResult? result)) return;
            applied++;

            // An evicted section has to come back from disk before capture may merge into
            // it, or the merge writes into an empty section that then overwrites the
            // stored row. That was solved for mip propagation and not here, so this path
            // still paid a synchronous SQLite read and a Deflate on the game tick:
            // measured at 10.60ms average and 112.98ms worst, in a 50ms tick.
            //
            // EnsureResident starts the background reload and says "not yet". The result
            // waits a tick or two, which is invisible, instead of the whole world waiting
            // on a decompress.
            if (!World.EnsureResident(result.SectionKey))
            {
                // Room is made by forcing the OLDEST result through, blocking load and
                // all, never by letting this one past. A newer result that overtook an
                // older one for the same section would leave the older one landing last,
                // writing columns that have already been superseded. The list is a queue
                // for that reason, and the bound is enforced from its head.
                if (deferredCaptures.Count >= MaxDeferredCaptures)
                {
                    ApplyOneCaptureResult(deferredCaptures[0]);
                    deferredCaptures.RemoveAt(0);
                }
                deferredCaptures.Add(result);
                continue;
            }

            ApplyOneCaptureResult(result);
        }
    }

    /// <summary>
    /// Palette identity: snow, frost-mottle bin, and baked wood/soil never collapse
    /// onto one row. Packed as blockId | flags | tintSlot.
    /// </summary>
    static long PaletteStyleKey(int blockId, byte flags, byte tintSlot) =>
        ((long)(uint)blockId << 16) | ((long)flags << 8) | tintSlot;

    /// <summary>
    /// Snow and frost-bin identity is a function of world XZ + block, not of the
    /// climate sample. Reuse the first bake in this batch for that style.
    /// </summary>
    static bool TryCachedPaletteStyle(
        Block block,
        int blockId,
        int x,
        int z,
        Dictionary<long, int> pidByStyle,
        out long style,
        out int pid)
    {
        byte flags = LodBlockPolicy.FlagsFor(block);
        byte tintSlot = (byte)LodTintRegistry.SlotNone;
        if ((flags & LodPaletteEntry.FlagSnow) != 0 || LodBlockPolicy.IsSnowLayer(block))
        {
            flags = (byte)(LodBlockPolicy.FlagsFor(block) | LodPaletteEntry.FlagSnow);
            flags &= unchecked((byte)~LodPaletteEntry.FlagFrostGround);
        }
        else if (LodSeasonBake.IsGroundFrost(block))
        {
            // Grass/soil live-tint; do not cache FlagBaked frost-mottle identity.
            style = 0;
            pid = 0;
            return false;
        }
        else
        {
            style = 0;
            pid = 0;
            return false;
        }

        style = PaletteStyleKey(blockId, flags, tintSlot);
        return pidByStyle.TryGetValue(style, out pid);
    }

    void ApplyOneCaptureResult(CaptureResult result)
    {
        LodSection section = World.GetOrCreateSection(result.SectionKey);

        // Per-(block, snow|frostBin|baked) — not one colour for the whole 64×64.
        var pidByStyle = new Dictionary<long, int>();
        ulong[]?[] batch = result.RunsByColumn;

        for (int col = 0; col < batch.Length; col++)
        {
            ulong[]? runs = batch[col];
            if (runs == null) continue;

            int kept = 0;
            for (int i = 0; i < runs.Length; i++)
            {
                int blockId = LodSection.RunPaletteId(runs[i]); // raw block id from capture
                Block? live = null;
                int bx = 0, by = 0, bz = 0;
                if ((uint)blockId < (uint)api.World.Blocks.Count)
                {
                    live = api.World.Blocks[blockId];
                    (bx, by, bz) = CaptureBlockPos(result.SectionKey, col, runs[i]);
                    paletteSamplePos.Set(bx, by, bz);
                    // Deciduous Collapse = fully hidden leaf cell. Omit; keep Fan + wood.
                    if (DeciduousLeafCompat.ShouldOmitLeafRun(live, paletteSamplePos)) continue;
                }

                int pid;
                if (live != null
                    && TryCachedPaletteStyle(live, blockId, bx, bz, pidByStyle, out long style, out pid)
                    && (uint)pid < (uint)section.Palette.Count)
                {
                    // Same snow / frost-bin row already baked from this batch.
                }
                else
                {
                    ResolveCapturePalette(result.SectionKey, blockId, col, runs[i],
                        out int color, out byte flags, out byte tintSlot);
                    style = PaletteStyleKey(blockId, flags, tintSlot);
                    if (!pidByStyle.TryGetValue(style, out pid))
                    {
                        pid = UpsertPaletteEntry(section, blockId, color, flags, tintSlot);
                        pidByStyle[style] = pid;
                    }
                }

                // Decorative ground cover never becomes terrain: a flower would
                // otherwise be a solid, pale-grey 1-block cube.
                if ((section.Palette[pid].Flags & LodPaletteEntry.FlagSkip) != 0) continue;

                runs[kept++] = LodSection.PackRun(pid, LodSection.RunYTop(runs[i]), LodSection.RunYBottom(runs[i]));
            }

            if (kept != runs.Length) batch[col] = runs[..kept];
        }

        ColumnsCaptured++;

        int sb = LodSection.SectionBlocks;
        int colOx = ((result.Cx * ChunkSize) % sb) / LodSection.ColumnStepBlocks;
        int colOz = ((result.Cz * ChunkSize) % sb) / LodSection.ColumnStepBlocks;
        int quadrant = LodSection.QuadrantOf(colOx, colOz);

        if (result.Provisional)
        {
            // A peek must not replace a column this side already observed for real.
            int probe = LodSection.ColumnIndex(colOx, colOz);
            if (section.Captured[probe] && !section.IsProvisionalQuadrant(quadrant))
                return;
        }

        bool changed = section.ReplaceColumns(batch);

        if (result.Provisional)
        {
            byte mask = (byte)(1 << quadrant);
            if ((section.ProvisionalQuadrants & mask) == 0)
            {
                section.ProvisionalQuadrants |= mask;
                changed = true;
            }
        }
        else if (section.ClearProvisional(quadrant))
        {
            // This side saw the real chunk: a peek or a foreign cache is superseded,
            // even when the columns came out identical.
            ProvisionalQuadrantsConfirmed++;
            changed = true;
        }

        if (!result.Provisional)
        {
            section.LoadedCaptureLookToken = SeasonLookToken;
            // Unbake leftover FlagBaked vegetation and melt leftover inferred
            // Cover snow. Do not invent FlagSnow — far snow is the shader snowline.
            if (RebakeSeasonPalette != null)
            {
                int painted = RebakeSeasonPalette(section, result.SectionKey);
                if (painted > 0) changed = true;
            }
        }

        if (changed)
        {
            World.ClassifySparseL0(result.SectionKey, section);
            World.MarkChanged(result.SectionKey);
            World.ForceAncestorGpuRemesh(result.SectionKey);
        }
    }

    /// <summary>
    /// World position of the top of a run (for climate/season bake and frost).
    /// Level L columns span (ColumnStepBlocks &lt;&lt; L) blocks; section keys are in
    /// level space. Using L0-only math (sx * 64) on L1+ sampled half/quarter world
    /// coords — debug e8d91d post-fix-heat: every L1 probe airTempC ~35–50°C and
    /// frostAmt=0 while L0 at the same hills was −8°C / frost 0.38 (far green band).
    /// Sample column centre so fat L≥1 columns still hit the right climate cell.
    /// yTop is exclusive (a run spans [yBottom, yTop)).
    /// </summary>
    public static (int X, int Y, int Z) CaptureBlockPos(long sectionKey, int col, ulong run)
    {
        int level = LodWorld.KeyLevel(sectionKey);
        int step = LodSection.ColumnStepBlocks << level;
        int sectionBlocks = LodSection.SectionBlocks << level;
        int localX = col % LodSection.GridSize;
        int localZ = col / LodSection.GridSize;
        int half = step >> 1;
        return (
            LodWorld.KeySx(sectionKey) * sectionBlocks + localX * step + half,
            LodSection.RunYTop(run) - 1,
            LodWorld.KeySz(sectionKey) * sectionBlocks + localZ * step + half);
    }

    void ResolveCapturePalette(long sectionKey, int blockId, int col, ulong run,
        out int color, out byte flags, out byte tintSlot)
    {
        (int x, int y, int z) = CaptureBlockPos(sectionKey, col, run);
        if ((uint)blockId >= (uint)api.World.Blocks.Count)
        {
            color = 0;
            flags = 0;
            tintSlot = 0;
            return;
        }

        Block block = api.World.Blocks[blockId];
        bool baked;
        (color, tintSlot, baked) = describePalette(blockId, x, y, z);
        flags = LodBlockPolicy.FlagsFor(block);

        // Real snow / opaque ice: FlagSnow bright white only. Never FlagFrostGround.
        if ((flags & LodPaletteEntry.FlagSnow) != 0
            || LodBlockPolicy.IsSnowLayer(block))
        {
            flags = (byte)(LodBlockPolicy.FlagsFor(block) | LodPaletteEntry.FlagSnow);
            flags &= unchecked((byte)~LodPaletteEntry.FlagFrostGround);
            tintSlot = (byte)LodTintRegistry.SlotNone;
            return;
        }

        if (!baked) return;

        flags |= LodPaletteEntry.FlagBaked;
        // Ground frost only: mottle bin in TintSlot. Foliage never uses FrostMottleBin.
        if (LodSeasonBake.IsGroundFrost(block))
        {
            flags |= LodPaletteEntry.FlagFrostGround;
            tintSlot = (byte)LodSeasonBake.FrostMottleBin(x, z);
        }
        else
        {
            flags &= unchecked((byte)~LodPaletteEntry.FlagFrostGround);
            tintSlot = (byte)LodTintRegistry.SlotNone;
        }
    }

    int UpsertPaletteEntry(LodSection section, int blockId, int color, byte flags, byte tintSlot)
    {
        bool frostGround = (flags & LodPaletteEntry.FlagFrostGround) != 0;
        bool snow = (flags & LodPaletteEntry.FlagSnow) != 0;
        bool bakedLook = (flags & LodPaletteEntry.FlagBaked) != 0 && !frostGround && !snow;
        for (int i = 0; i < section.Palette.Count; i++)
        {
            if (section.Palette[i].BlockId != blockId) continue;
            if (snow)
            {
                if ((section.Palette[i].Flags & LodPaletteEntry.FlagSnow) == 0) continue;
            }
            else if ((section.Palette[i].Flags & LodPaletteEntry.FlagSnow) != 0)
            {
                continue;
            }
            else if (frostGround)
            {
                if ((section.Palette[i].Flags & LodPaletteEntry.FlagFrostGround) == 0) continue;
                if (section.Palette[i].TintSlot != tintSlot) continue;
            }
            else if ((section.Palette[i].Flags & LodPaletteEntry.FlagFrostGround) != 0)
            {
                continue;
            }
            else if (bakedLook)
            {
                if ((section.Palette[i].Flags & LodPaletteEntry.FlagBaked) == 0) continue;
                if (!LodSeasonBake.SameFoliageLook(section.Palette[i].Color, color)) continue;
                return i;
            }

            LodPaletteEntry e = section.Palette[i];
            e.Color = color;
            e.Flags = flags;
            e.TintSlot = tintSlot;
            section.Palette[i] = e;
            section.InvalidatePaletteSnapshot();
            return i;
        }

        return section.FindOrAddPaletteEntry(blockId, color, flags, tintSlot);
    }

    /// <summary>
    /// On bake-epoch change: resident RAM may need a seasonal repaint. Disk rows
    /// rebake when the renderer demand-loads them — HasDataSet is not a work queue.
    /// </summary>
    public void QueueSeasonRepaintAll()
    {
        World.SeasonRepaintEpochActive = true;
        seasonProgressTicks = 0;
        SeasonCatchUpLevel = 0;
        seasonIdleVisited.Clear();
        seasonIdleAnchorSet = false;
        seasonEpochKeepUntilTick = tickCounter + LodSeasonCatchUp.JoinEpochKeepTicks;
        LodSeasonCatchUp.PruneNonResident(World.SeasonDirty, World.Sections.ContainsKey);
        if (LodSeasonCatchUp.EnqueueResidentOnEpoch)
            LodSeasonCatchUp.EnqueueResident(World.SeasonDirty, World.Sections.Keys);
    }

    /// <summary>
    /// Map-region heatmaps only exist while the region is client-loaded. Discovering
    /// new chunks loads the region; far LOD baked earlier used the engine placeholder
    /// climate (11842740) and stayed summer-green. Re-queue overlapping sections so
    /// frost bake sees the real ClimateMap.
    /// </summary>
    public int QueueSeasonRepaintForMapRegion(int regionX, int regionZ)
    {
        _ = regionX;
        _ = regionZ;
        // Live climate corners are shader uniforms / LodTintRegistry, not a remesh.
        return 0;
    }

    /// <summary>Resident SeasonDirty counts at L0/L1/L2 for .dvcolor.</summary>
    public void CountResidentSeasonDirtyByLevel(out int l0, out int l1, out int l2)
    {
        l0 = l1 = l2 = 0;
        foreach (long key in World.SeasonDirty)
        {
            if (!World.Sections.ContainsKey(key)) continue;
            switch (LodWorld.KeyLevel(key))
            {
                case 0: l0++; break;
                case 1: l1++; break;
                default: l2++; break;
            }
        }
    }

    /// <summary>
    /// Deciduous dormancy can flip without ChunkDirty. Queue resident L0 leaf sections
    /// for an in-place Collapse strip plus forced re-capture (spring canopy return).
    /// Cold rows join via <see cref="AfterSectionLoaded"/> when demand-loaded.
    /// </summary>
    public void QueueDeciduousLeafRefreshAll()
    {
        if (!DeciduousLeafCompat.Present) return;
        foreach (KeyValuePair<long, LodSection> kv in World.Sections)
        {
            if (LodWorld.KeyLevel(kv.Key) != 0) continue;
            if (DeciduousLeafCompat.SectionHasLeafPalette(kv.Value, api.World))
                deciduousRefresh.Add(kv.Key);
        }
    }

    void ProcessSeasonRepaint()
    {
        if (RebakeSeasonPalette == null) return;

        // Orphan sync: SeasonForced keys dropped from RenderDirty after a failed
        // TryStart would stall forever while SeasonForced stayed high (probe-h).
        if (World.SeasonForcedRemesh.Count > 0)
        {
            foreach (long fk in World.SeasonForcedRemesh)
                World.RenderDirty.Add(fk);
        }

        int budget = LodFrameBudget.ResidentPaletteThisTick(
            PlayerBusy || StreamingBusy, LastFrameWasHitch);
        if (World.SeasonDirty.Count == 0)
        {
            // Remesh may still be draining after palette work finished.
            // Do not wait on cold HasDataSet — epoch ends with cache still on disk.
            if (World.SeasonForcedRemesh.Count == 0)
                TryEndSeasonEpoch();
            return;
        }
        if (budget <= 0)
            return;

        ResolveSeasonAnchor(out double px, out double pz);

        // L0 palette first so nearby fine tiles catch up before coarse parents.
        // Every level rebakes: inferred FlagSnow lives on dirt that may not be
        // FlagBaked, and far L2/L3 never inherit a child's snow cover via remesh-only.
        int focusLevel = -1;
        if (SeasonDirtyHasResidentAtLevel(0))
            focusLevel = 0;
        else
        {
            for (int L = 1; L <= LodWorld.MaxLevel; L++)
            {
                if (!SeasonDirtyHasResidentAtLevel(L)) continue;
                focusLevel = L;
                break;
            }
        }
        SeasonCatchUpLevel = focusLevel;

        seasonRepaintScratch.Clear();
        if (SeasonCatchUpLevel >= 0)
        {
            foreach (long key in World.SeasonDirty)
            {
                if (!World.Sections.ContainsKey(key)) continue;
                if (LodWorld.KeyLevel(key) != SeasonCatchUpLevel) continue;
                double distSq = LodWorld.NearestDistanceSqTo(key, px, pz);
                seasonRepaintScratch.Add((key, distSq));
            }
            seasonRepaintScratch.Sort((a, b) => a.DistSq.CompareTo(b.DistSq));
        }

        seasonRepaintDone.Clear();
        int changedThisTick = 0;
        for (int i = 0; i < seasonRepaintScratch.Count && budget > 0; i++)
        {
            long key = seasonRepaintScratch[i].Key;
            if (!World.Sections.TryGetValue(key, out LodSection? section)) continue;

            int changed = RebakeSeasonPalette(section, key);
            if (changed > 0)
            {
                World.MarkChanged(key);
                SeasonSectionsRepainted++;
                changedThisTick++;
                World.RenderDirty.Add(key);
            }
            section.SeasonLookToken = SeasonLookToken;
            seasonRepaintDone.Add(key);
            budget--;
        }

        // Cold HasDataSet is not catch-up work. Sorting that set every tick is why
        // a well-explored world sat for minutes before the first mesh. The renderer
        // demand-loads what it draws; AfterSectionLoaded Cover-rebakes those.

        foreach (long key in seasonRepaintDone) World.SeasonDirty.Remove(key);
        // Palette drain alone is not catch-up done — GPU meshes still hold last season
        // RGB until SeasonForcedRemesh empties. Clearing epoch here left a hard seam
        // (white sections remeshed near the player, green VBO next door) while dirty=0
        // and forced remesh stalled (debug e8d91d post-fix-bandfollow).
        if (World.SeasonDirty.Count == 0 && World.SeasonForcedRemesh.Count == 0)
            TryEndSeasonEpoch();

        seasonProgressTicks++;
        if (World.SeasonRepaintEpochActive
            && (seasonProgressTicks % SeasonProgressLogEveryTicks == 0 || changedThisTick > 0 && seasonProgressTicks <= 2))
        {
            CountResidentSeasonDirtyByLevel(out int d0, out int d1, out int d2);
            logger.Notification(
                "[DistantVistas] Season catch-up: level {0}, dirtyAtLevel {1}, dirtyTotal {2} "
                + "(L0 {3} L1 {4} L2 {5}), forcedRemesh {6}, repainted this session {7}, "
                + "changed this tick {8}, anchor {9:0},{10:0}",
                SeasonCatchUpLevel,
                SeasonCatchUpLevel == 0 ? d0 : SeasonCatchUpLevel == 1 ? d1 : d2,
                World.SeasonDirty.Count, d0, d1, d2,
                World.SeasonForcedRemesh.Count,
                SeasonSectionsRepainted, changedThisTick, px, pz);
        }
    }

    /// <summary>
    /// Cheap player-centered lap after the join burst. Closest unfinished pad first,
    /// then the next ring out. Remesh queue does not stall the walk — that used to
    /// freeze idle forever on a stuck ancestor key. Hash-order round-robin is gone.
    /// </summary>
    void ProcessSeasonIdleSweep()
    {
        if (RebakeSeasonPalette == null)
        {
            SeasonIdleState = "no rebake";
            return;
        }
        if (PlayerBusy)
        {
            SeasonIdleState = "blocked: looking or walking";
            return;
        }
        if (YieldSeasonWork)
        {
            SeasonIdleState = "blocked: mesh pressure";
            return;
        }
        if (World.SeasonRepaintEpochActive)
        {
            SeasonIdleState = "blocked: join epoch";
            return;
        }
        if (HasResidentSeasonDirty())
        {
            SeasonIdleState = "blocked: catch-up";
            return;
        }

        ResolveSeasonAnchor(out double px, out double pz);
        if (!seasonIdleAnchorSet
            || LodSeasonIdleOrder.PlayerMovedEnough(seasonIdleAnchorX, seasonIdleAnchorZ, px, pz))
        {
            seasonIdleVisited.Clear();
            seasonIdleAnchorX = px;
            seasonIdleAnchorZ = pz;
            seasonIdleAnchorSet = true;
            LodSeasonIdleOrder.FillNearestCapped(
                seasonIdleScratch, World.Sections.Keys, seasonIdleVisited, px, pz,
                LodFrameBudget.ScratchCap, maxDistBlocks: 0);
            seasonIdleScratchIndex = 0;
        }
        else if (seasonIdleScratchIndex >= seasonIdleScratch.Count)
        {
            if (World.Sections.Count > 0)
            {
                seasonIdleVisited.Clear();
                SeasonIdleLap++;
                LodSeasonIdleOrder.FillNearestCapped(
                    seasonIdleScratch, World.Sections.Keys, seasonIdleVisited, px, pz,
                    LodFrameBudget.ScratchCap, maxDistBlocks: 0);
                seasonIdleScratchIndex = 0;
            }
        }

        SeasonIdlePending = Math.Max(0, seasonIdleScratch.Count - seasonIdleScratchIndex);
        if (seasonIdleScratch.Count == 0 || seasonIdleScratchIndex >= seasonIdleScratch.Count)
        {
            SeasonIdleNearestBlocks = -1;
            SeasonIdleState = "no resident plates";
            return;
        }

        SeasonIdleNearestBlocks = (int)Math.Sqrt(seasonIdleScratch[seasonIdleScratchIndex].DistSq);
        int month = 0;
        try { month = api.World.Calendar.Month; }
        catch { /* shutdown */ }
        int baked = 0;
        int melted = 0;
        int skipped = 0;
        int budget = SeasonIdleSectionsPerTick;
        while (budget > 0 && seasonIdleScratchIndex < seasonIdleScratch.Count)
        {
            long key = seasonIdleScratch[seasonIdleScratchIndex++].Key;
            if (!World.Sections.TryGetValue(key, out LodSection? section)) continue;

            seasonIdleVisited.Add(key);
            if (!LodSeasonBake.SectionNeedsIdleSeasonPass(
                section, SeasonLookToken, month, api.World.Blocks))
            {
                skipped++;
                if (skipped >= SeasonIdleSkipWalkCap) break;
                continue;
            }

            int changed = RebakeSeasonPalette(section, key);
            baked++;
            section.SeasonLookToken = SeasonLookToken;
            if (changed > 0)
            {
                World.MarkChanged(key);
                SeasonSectionsRepainted++;
                SeasonIdleMelted++;
                melted++;
                World.RenderDirty.Add(key);
            }
            budget--;
        }

        SeasonIdleState = melted > 0
            ? $"lap {SeasonIdleLap}, nearest {SeasonIdleNearestBlocks}, pending {SeasonIdlePending}, melted {melted}"
            : $"lap {SeasonIdleLap}, nearest {SeasonIdleNearestBlocks}, pending {SeasonIdlePending}";

        seasonIdleLogTicks++;
        if (seasonIdleLogTicks % SeasonProgressLogEveryTicks == 0 || melted > 0 && seasonIdleLogTicks <= 2)
        {
            logger.Notification(
                "[DistantVistas] Season idle: {0}, baked {1}, remesh {2}",
                SeasonIdleState, baked, World.SeasonForcedRemesh.Count);
        }
    }

    void TryEndSeasonEpoch()
    {
        if (LodSeasonCatchUp.KeepJoinEpoch(
            tickCounter, seasonEpochKeepUntilTick, residentDirty: 0, forcedRemesh: 0))
            return;
        World.SeasonRepaintEpochActive = false;
        SeasonCatchUpLevel = -1;
    }

    bool HasResidentSeasonDirty()
    {
        EnsureSeasonDirtyResidentCache();
        return seasonDirtyHasResident;
    }

    bool SeasonDirtyHasResidentAtLevel(int level)
    {
        EnsureSeasonDirtyResidentCache();
        if ((uint)level < 3) return seasonDirtyLevelResident[level];
        foreach (long key in World.SeasonDirty)
        {
            if (LodWorld.KeyLevel(key) != level) continue;
            if (World.Sections.ContainsKey(key)) return true;
        }
        return false;
    }

    void EnsureSeasonDirtyResidentCache()
    {
        int stamp = World.SeasonDirty.Count
            ^ (World.Sections.Count * 397)
            ^ (Environment.TickCount & ~0x3F);
        if (stamp == seasonDirtyResidentFrame) return;
        seasonDirtyResidentFrame = stamp;
        seasonDirtyHasResident = false;
        seasonDirtyLevelResident[0] = false;
        seasonDirtyLevelResident[1] = false;
        seasonDirtyLevelResident[2] = false;
        foreach (long key in World.SeasonDirty)
        {
            if (!World.Sections.ContainsKey(key)) continue;
            seasonDirtyHasResident = true;
            int lvl = LodWorld.KeyLevel(key);
            if ((uint)lvl < 3) seasonDirtyLevelResident[lvl] = true;
            if (seasonDirtyLevelResident[0] && seasonDirtyLevelResident[1] && seasonDirtyLevelResident[2])
                break;
        }
    }

    /// <summary>
    /// Prefer the live player once they are actually in the world. Before that
    /// (or when Pos is still the 0,0 startup trap on mid-map worlds), use spawn.
    /// </summary>
    void ResolveSeasonAnchor(out double px, out double pz)
    {
        var spawn = api.World.DefaultSpawnPosition;
        px = spawn.X;
        pz = spawn.Z;

        if (api.World is not Vintagestory.API.Client.IClientWorldAccessor cworld) return;
        var entity = cworld.Player?.Entity;
        if (entity == null) return;

        double ex = entity.Pos.X;
        double ez = entity.Pos.Z;
        bool nearOrigin = Math.Abs(ex) < 32 && Math.Abs(ez) < 32;
        bool spawnFar = Math.Abs(spawn.X) > 256 || Math.Abs(spawn.Z) > 256;
        if (nearOrigin && spawnFar) return; // still on the 0,0 trap — keep spawn

        px = ex;
        pz = ez;
    }

    void ProcessDeciduousLeafRefresh()
    {
        if (!DeciduousLeafCompat.Present || deciduousRefresh.Count == 0) return;
        if (LastFrameWasHitch) return;

        int budget = PlayerBusy ? 1 : DeciduousStripSectionsPerTick;
        deciduousRefreshDone.Clear();
        foreach (long key in deciduousRefresh)
        {
            if (budget <= 0) break;
            if (LodWorld.KeyLevel(key) != 0)
            {
                deciduousRefreshDone.Add(key);
                continue;
            }
            if (!World.EnsureResident(key)) continue;
            if (!World.Sections.TryGetValue(key, out LodSection? section))
            {
                deciduousRefreshDone.Add(key);
                continue;
            }

            if (!DeciduousLeafCompat.SectionHasLeafPalette(section, api.World))
            {
                deciduousRefreshDone.Add(key);
                continue;
            }

            bool stripped = StripCollapsedDeciduousLeaves(key, section);
            ForceQueueSectionChunks(key);
            if (stripped)
            {
                World.MarkChanged(key);
                World.RenderDirty.Add(key);
            }
            deciduousRefreshDone.Add(key);
            budget--;
        }

        foreach (long key in deciduousRefreshDone) deciduousRefresh.Remove(key);
    }

    /// <summary>
    /// Drop Collapse leaf runs from a resident section. Wood and Fan branchy leaves stay.
    /// </summary>
    bool StripCollapsedDeciduousLeaves(long sectionKey, LodSection section)
    {
        int total = LodSection.GridSize * LodSection.GridSize;
        var nextRuns = new ulong[section.Runs.Length];
        var nextStart = new int[total + 1];
        int offset = 0;
        bool changed = false;

        for (int col = 0; col < total; col++)
        {
            nextStart[col] = offset;
            int from = section.ColumnStart[col], to = section.ColumnStart[col + 1];
            for (int r = from; r < to; r++)
            {
                ulong run = section.Runs[r];
                int pid = LodSection.RunPaletteId(run);
                if ((uint)pid >= (uint)section.Palette.Count)
                {
                    nextRuns[offset++] = run;
                    continue;
                }

                int blockId = section.Palette[pid].BlockId;
                if ((uint)blockId >= (uint)api.World.Blocks.Count)
                {
                    nextRuns[offset++] = run;
                    continue;
                }

                Block block = api.World.Blocks[blockId];
                (int x, int y, int z) = CaptureBlockPos(sectionKey, col, run);
                paletteSamplePos.Set(x, y, z);
                if (DeciduousLeafCompat.ShouldOmitLeafRun(block, paletteSamplePos))
                {
                    changed = true;
                    continue;
                }

                nextRuns[offset++] = run;
            }
        }
        nextStart[total] = offset;
        if (!changed) return false;

        Array.Resize(ref nextRuns, offset);
        section.Runs = nextRuns;
        section.ColumnStart = nextStart;
        return true;
    }

    /// <summary>
    /// Re-capture nearest resident L0 (no distance ring). A 256-block band left far
    /// snow on summer cache after login and /time.
    /// </summary>
    public void ForceRecaptureResidentL0(string reason)
    {
        ResolveSeasonAnchor(out double px, out double pz);
        ForceRecaptureResidentL0(reason, px, pz);
    }

    public void ForceRecaptureResidentL0(string reason, double px, double pz)
    {
        if (!Active) return;
        var visited = new HashSet<long>();
        var scratch = new List<(long Key, double DistSq)>();
        LodSeasonIdleOrder.FillNearestCapped(
            scratch, World.Sections.Keys, visited, px, pz,
            LodFrameBudget.ScratchCap, maxDistBlocks: 0,
            static key => LodWorld.KeyLevel(key) == 0);
        int queued = 0;
        for (int i = 0; i < scratch.Count; i++)
        {
            int before = pendingColumns.Count;
            ForceQueueSectionChunks(scratch[i].Key);
            queued += Math.Max(0, pendingColumns.Count - before);
        }
        if (queued > 0)
            api.Logger.Notification(
                "[DistantVistas] Force-recapture resident L0 ({0}): {1} column(s) queued",
                reason, queued);
    }

    void ForceQueueSectionChunks(long sectionKey)
    {
        int sb = LodSection.SectionBlocks;
        int chunksPerEdge = sb / ChunkSize;
        if (chunksPerEdge < 1) chunksPerEdge = 1;
        int sx = LodWorld.KeySx(sectionKey);
        int sz = LodWorld.KeySz(sectionKey);
        for (int dz = 0; dz < chunksPerEdge; dz++)
        {
            for (int dx = 0; dx < chunksPerEdge; dx++)
                ForceQueueColumn(sx * chunksPerEdge + dx, sz * chunksPerEdge + dz);
        }
    }

    // ---- Persistence ----

    void SaveSomeDirtySections(int budget)
    {
        if (store == null || World.SaveDirty.Count == 0) return;
        if (storageThread != null && storageThread.Backlog >= MaxStorageBacklog) return;

        storageClock.Restart();
        List<long>? saved = null;
        foreach (long key in World.SaveDirty)
        {
            if (World.Sections.TryGetValue(key, out LodSection? section))
            {
                // Freeze on this thread (the section keeps mutating), compress and
                // write on the storage thread.
                var snap = LodSaveSnapshot.Of(LodWorld.KeyLevel(key), LodWorld.KeySx(key), LodWorld.KeySz(key),
                    section, api.World, World.MipDirty.Contains(key));
                storageThread?.Enqueue(snap);
            }
            (saved ??= new List<long>()).Add(key);
            if (--budget <= 0) break;
        }
        if (saved != null) foreach (long key in saved) World.SaveDirty.Remove(key);

        double ms = storageClock.Elapsed.TotalMilliseconds;
        SaveCalls++;
        SaveMsTotal += ms;
        if (ms > SaveMsMax) SaveMsMax = ms;
    }

    /// <summary>
    /// Flush and shut the cache down. Order matters: queue everything, let the writer
    /// finish, stop the thread, and only then close the connection it was writing through.
    /// </summary>
    public void Close()
    {
        Active = false;
        World.LoadFromStore = null;
        World.RequestAsyncLoad = null;

        if (store != null)
        {
            SaveSomeDirtySections(int.MaxValue);
            if (storageThread != null)
            {
                storageThread.Drain();
                if (storageThread.Backlog > 0)
                {
                    logger.Warning("Storage drain timed out with {0} sections unwritten", storageThread.Backlog);
                }
                storageThread.Dispose();
                storageThread = null;
            }
            store.Close();
            store.Dispose();
            store = null;
        }

        queuedColumns.Clear();
        pendingColumns.Clear();
        Remote.Clear();
        // Results for the world we are leaving must not be applied to the next one.
        // Both queues, or a result held back for a reload would cross worlds.
        while (Worker.CaptureResults.TryDequeue(out _)) { }
        deferredCaptures.Clear();
        World.Clear();
        CachedSectionsLoaded = 0;
        ColumnsCaptured = 0;
        ColumnsSwept = 0;
        ProvisionalQuadrantsConfirmed = 0;
        columnsDropped = 0;
        sweepRow = int.MinValue;
        DbPath = null;
    }

    public void Dispose()
    {
        storageThread?.Drain();
        storageThread?.Dispose();
        storageThread = null;
        store?.Dispose();
        store = null;
        Worker.Dispose();
    }
}
