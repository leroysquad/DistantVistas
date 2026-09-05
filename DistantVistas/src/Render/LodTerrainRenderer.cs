using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace DistantVistas;

/// <summary>
/// Renders the LodWorld section pyramid beyond the vanilla view distance. Meshes are
/// built off-thread by the LodWorker from section snapshots; this class schedules
/// mesh jobs (nearest-first), uploads finished vertex data on the render thread, and
/// walks the quadtree each frame picking detail by distance - incomplete children stay empty until they mesh; we do not paint a parent box over the hole.
///
/// Rendering techniques (render order/stage, ZFar extension, camera-relative model
/// matrices, fog + transition handling in the shaders) adapted from Farseer
/// (https://github.com/ViciousBadger/VSMod-Farseer, MIT, (c) Badgerson).
/// </summary>
public class LodTerrainRenderer : IRenderer
{
    public double RenderOrder => DrawAfterCompanion ? 0.365 : 0.36; // Farseer is 0.36; later draw wins our tiles
    public int RenderRange => 9999;

    const int MeshSchedulesPerFrame = 12;
    /// <summary>
    /// Extra mesh starts while SeasonForcedRemesh is deep. post-fix-leaflook: forced
    /// backlog hit ~6800 while forcedStarted often stayed at 1 because PendingMeshes
    /// capped the whole ScheduleMeshJobs path before SeasonForced ran.
    /// </summary>
    /// <summary>Month/join catch-up: remesh many forced keys per frame or the map seams.</summary>
    const int SeasonForcedBurstSchedules = 48;
    /// <summary>Reserved starts each frame for SeasonForced keys before nearest-dirty.</summary>
    const int SeasonForcedPrioritySchedules = 32;
    /// <summary>
    /// Unmeshed L0/L1 in the handoff ring — only when SeasonForced is empty so a
    /// month-change remesh cannot stall behind upgrades (seam screenshot).
    /// </summary>
    const int HandoffUpgradeSchedules = 8;
    const int MeshUploadsPerFrame = 8;
    /// <summary>
    /// Wall clock a single frame may spend handing finished meshes to the driver. Each
    /// upload allocates a buffer and blocks until the driver takes it, so a full backlog
    /// of eight in one frame is long enough to be felt. What does not fit keeps its place
    /// in the queue and goes up next frame.
    /// </summary>
    const double MeshUploadBudgetMs = 2.0;
    static readonly long MeshUploadBudgetTicks =
        (long)(System.Diagnostics.Stopwatch.Frequency * MeshUploadBudgetMs / 1000.0);
    const int IncompleteFillPerTick = 16;
    /// <summary>
    /// Queue depth allowed at the mesh workers. Per thread, not absolute: a fixed 12 was
    /// sized for one builder and would leave a four-thread pool idling three quarters of
    /// the time. Deep enough that a thread finishing a job always has another waiting,
    /// shallow enough that the queue does not outlive the view that asked for it.
    /// </summary>
    const int MeshBacklogPerThread = 4;
    int maxWorkerMeshBacklog;

    /// <summary>Reload requests per frame; only enqueues a key, so it can far exceed the mesh budget.</summary>
    const int MeshLoadRequestsPerFrame = 32;
    /// <summary>
    /// Slots per frame for unmeshed visited L0/L1, farthest first. Nearest-only
    /// scheduling never caught up with the start of a long walk.
    /// </summary>
    const int VisitedKeepSchedulesPerFrame = 4;
    /// <summary>
    /// Far wanted-level parent mesh/mip requests per frame. Enough to fill L2/L3
    /// over a few seconds, small enough that the worker does not hitch.
    /// </summary>
    const int CoarseParentRequestsPerFrame = 4;
    int coarseParentRequestsThisFrame;

    readonly ICoreClientAPI capi;
    readonly LodWorld world;
    readonly LodWorker worker;
    /// <summary>
    /// What the renderer's own frame costs, by phase, since the last stats report. Each
    /// of these is tens of microseconds against a ten-millisecond frame, which is well
    /// under what any frame-rate comparison can resolve, so they are timed rather than
    /// inferred. Reported by .vhinfo and by the periodic stats line.
    /// </summary>
    public LodPhaseCost PruneCost, ScheduleCost, FarDistanceCost, WalkCost, DrawCost;

    public void ResetPhaseCosts()
    {
        PruneCost.Reset();
        ScheduleCost.Reset();
        FarDistanceCost.Reset();
        WalkCost.Reset();
        DrawCost.Reset();
    }

    readonly Dictionary<long, MeshRef> sectionMeshes = new();
    readonly Dictionary<long, MeshRef> waterMeshes = new();
    readonly HashSet<long> meshJobInFlight = new();

    /// <summary>
    /// Sections whose last mesh job produced no geometry at all. Without this
    /// they count as unmeshed, the walk asks again next frame, and the worker
    /// rebuilds the same empty result forever. They are "meshed" for the walk
    /// (nothing to draw is not a gap) and cleared when a new result arrives.
    /// </summary>
    readonly HashSet<long> emptyMeshKeys = new();
    readonly Dictionary<long, long> lastSelectedFrame = new();
    readonly Dictionary<long, long> meshBornFrame = new();
    readonly List<long> evictBatch = new();
    readonly List<long> evictBorn = new();
    int evictCursor;
    long lastEvictScanFrame;
    bool evictBatchFull;
    long frameCounter;

    /// <summary>
    /// Under real FPS/RAM pressure only: retire a few oldest L0/L1 GPU meshes outside
    /// 2× view distance per frame. Never dump for mesh-count alone. Spread deletes so
    /// p95 does not spike.
    /// </summary>
    const int EvictOldestPerFrame = 2;
    /// <summary>Hard cap under pressure — eviction itself must not spike walk/draw.</summary>
    const int EvictOldestPerFrameUnderPressure = 2;
    /// <summary>Frames to wait after a pressure eviction burst before scanning again.</summary>
    const int EvictPressureCooldownFrames = 20;

    /// <summary>
    /// Frames between rebuilds of the oldest-first candidate list. Choosing candidates
    /// walks every resident mesh, so that part keeps its old cadence and only the
    /// disposing is spread out. The list is capped at what the per-frame rate can retire
    /// before the next rebuild.
    /// </summary>
    const int EvictScanInterval = 60;

    public int EvictedTotal { get; private set; }

    /// <summary>Evictions that actually freed a mesh outside the 2× keep ring.</summary>
    public int EvictedOutside2xTotal { get; private set; }

    /// <summary>Candidates skipped because they sat inside 2× view distance.</summary>
    public int EvictBlockedInside2xTotal { get; private set; }

    /// <summary>True when UpdateMeshPressure saw sustained bad frame time and/or high managed memory.</summary>
    public bool MeshPressureActive { get; private set; }

    /// <summary>Times pressure latched on this session (hysteresis enter).</summary>
    public int PressureEnterCount { get; private set; }

    /// <summary>Times pressure cleared this session (hysteresis exit).</summary>
    public int PressureClearCount { get; private set; }

    /// <summary>Milliseconds spent with MeshPressureActive true this session.</summary>
    public double PressureActiveMsTotal { get; private set; }

    // Rolling frame-time samples for pressure (ms). Mesh count alone never opens pressure.
    const int FrameSampleCount = 64;
    readonly double[] frameMsSamples = new double[FrameSampleCount];
    int frameSampleAt;
    int frameSampleFilled;
    double pressureAvgFrameMs;
    double pressureP95FrameMs;
    long pressureManagedMb;
    double pressureEnterAccumMs;
    double pressureClearAccumMs;
    long lastPressureEvictFrame = -9999;
    int fineHorizonRequestsThisFrame;
    const int FineHorizonRequestsPerFrame = 2;
    /// <summary>Frames between full RenderDirty prunes while standing still (look-only).</summary>
    const int PruneIdleIntervalFrames = 4;
    /// <summary>Yaw slack before selection memos invalidate (matches FOV occ cache).</summary>
    const float MemoYawInvalidateRadians = 0.18f;
    float lookYaw;
    float memoYaw;
    float memoLiveViewDistance = float.NaN;
    bool selectionMemosValid;
    int pruneCursor;
    // Climate uniform cache: skip expensive array fill when SAME section is redrawn
    // (opaque+water+gaps). Key includes section key — lattice-only skip could leave a
    // neighbour drawing with another tile's climate arrays (brown/tan rectangles).
    int climateUploadGx0 = int.MinValue;
    int climateUploadGz0 = int.MinValue;
    int climateUploadStep = -1;
    long climateUploadSectionKey = long.MinValue;
    bool climateUploadValid;
    // Per-frame SetupSectionTransform reuse for full-footprint draws (opaque then water).
    struct SectionSetupCache
    {
        public bool Ok;
        public float Open0, Open1, Open2, Open3;
        public float OriginX, OriginZ, Footprint, ColumnBlocks;
    }
    readonly Dictionary<long, SectionSetupCache> setupCache = new();
    readonly Dictionary<long, bool> neighbourDataMemo = new();

    readonly Matrixf modelMat = new();
    readonly List<long> drawList = new();
    IShaderProgram? prog;
    bool shaderOk;
    float appliedZFar;
    Vec3d camPos = new();
    float lookDown01;
    float lookY;

    /// <summary>Dev/testing: keep the game unpaused even without window focus.</summary>
    public bool AutoUnpause;

    // Climate tints: sampled on a lattice one slot per frame so the field mean
    // is not one hashed row. Season is NOT in this table - that is a live
    // shader clock (seasonRel / seasonTints) uploaded every draw, same idea as
    // rgbaAmbientIn for night.
    const long SeasonalRefreshIntervalMs = 30_000;
    /// <summary>
    /// Climate tints are one table for the whole horizon. Resampling them at
    /// the camera every 30s painted every far forest with whatever biome you
    /// just walked into, so autumn snapped back to green. Only resample when
    /// the keep origin has moved this far.
    /// </summary>
    const int ClimateResampleBlocks = 384;
    float snowLineY = 99999;
    float pendingSnowLineY = 99999;
    long lastSeasonRefreshMs;
    int lastClimateSampleX;
    int lastClimateSampleZ;
    float lastSeasonTempX = 128f;
    bool seasonalStateInitialized;
    bool seasonalRefreshActive;
    int seasonalRefreshSlot;
    int seasonalRefreshX;
    int seasonalRefreshZ;
    readonly BlockPos climatePos = new(0, 0, 0);
    readonly LodClimateField climateField = new();
    LodClimateField.Sample keepClimate = LodClimateField.Identity;
    bool keepClimateValid;
    readonly float[] climateLowGrid = new float[LodClimateField.GridSize * LodClimateField.GridSize * 4];
    readonly float[] climateHighGrid = new float[LodClimateField.GridSize * LodClimateField.GridSize * 4];

    /// <summary>Optional hard cap in blocks; 0 = unlimited draw coverage.</summary>
    public int FarViewDistanceCap = 0;
    public bool DisableLodFog = true;
    public float FogDensityScale = 1.0f;
    public float SkyFadeStart = 0.88f;
    public float PastViewHaze = 0.22f;

    /// <summary>
    /// DH-style overdraw - fraction of live view distance where LOD may begin.
    /// Lower = more overlap under vanilla/fog; 1.0 = start at cut (seams).
    /// </summary>
    public float OverdrawStart = 0.55f;

    /// <summary>
    /// Farseer also registers at 0.36. Draw after it so our mesh wins any tile
    /// we actually submitted. Vanilla opaque still comes later and occludes both.
    /// </summary>
    public bool DrawAfterCompanion;

    /// <summary>Current far edge in blocks: the farthest loaded LOD data, independent of the vanilla view distance.</summary>
    public float EffectiveFarDistance { get; private set; } = 3000;
    float liveViewDistance = 512;
    readonly HashSet<long> loadedMapChunks = new();
    readonly HashSet<long> loadedWorldColumns = new();
    readonly Dictionary<long, bool> vanillaOwnsMemo = new();
    int mapChunkRefreshAge;
    float lastMapRefreshViewDistance = float.NaN;
    bool lookSampled;
    float lastLookYaw;
    float lastLookPitch;
    int lookHoldLeft;
    int hitchHoldLeft;
    int stepHoldLeft;
    int hitchWarmupFrames;
    bool camSampled;
    double lastCamX;
    double lastCamZ;

    /// <summary>Vanilla view distance this frame, after the server's last-approved cap.</summary>
    public float LiveViewDistance => liveViewDistance;

    /// <summary>Rebuild the loaded map-chunk set this frame (chunk arrival or first draw).</summary>
    public bool LoadedMapChunksDirty = true;

    public bool LookBusyThisFrame { get; private set; }
    public bool HitchThisFrame { get; private set; }
    public bool StepBusyThisFrame { get; private set; }
    public float LastFrameMs { get; private set; }
    public bool StarveCatchUpThisFrame { get; private set; }
    public bool StarveMeshRequestsThisFrame { get; private set; }
    public bool CatchUpBusyThisFrame => StarveCatchUpThisFrame;

    /// <summary>
    /// Centre distance of the farthest GPU mesh this frame. Unlike
    /// EffectiveFarDistance it is not padded to a ZFar floor, so a tile short
    /// of it really does have land drawn past it.
    /// </summary>
    double farthestMeshedDistance;

    /// <summary>
    /// Far edge of the captured extent (farthest L2 footprint in HasDataSet),
    /// rescanned every EvictScanInterval frames. Explored land that is not
    /// on the GPU right now still counts as "land past this tile": evicting
    /// interior meshes must pull neither the seal nor the shader far discard
    /// inward.
    /// </summary>
    double farthestCapturedDistance;
    long lastCapturedScanFrame = long.MinValue / 2;

    double FarthestKnownDistance => Math.Max(farthestMeshedDistance, farthestCapturedDistance);

    /// <summary>
    /// Keys the selection walk asked for last frame. PruneRenderDirty runs
    /// before the walk, so without this a lead-cone request finer than the
    /// wanted rung was dropped every frame it failed to win a schedule slot.
    /// </summary>
    readonly HashSet<long> walkRequested = new();

    /// <summary>Keys submitted to drawList this frame. Never evicted while drawn.</summary>
    readonly HashSet<long> drawnThisFrame = new();
    readonly Dictionary<long, bool> realSurfaceMemo = new();
    readonly Dictionary<long, bool> leadConeMemo = new();
    readonly Dictionary<long, bool> landLikeMemo = new();
    readonly Dictionary<long, bool> capturedBeyondMemo = new();
    readonly Dictionary<long, bool> boxInViewMemo = new();

    /// <summary>
    /// Captured footprints nothing has drawn yet this frame, as the walk
    /// unwinds. A node appends its uncovered children; the first ancestor up
    /// the recursion with a resident mesh paints them clipped to their own
    /// rectangle (gapDraws) and truncates back to where it started. Whatever
    /// is still here after the root returns is a real hole: no mesh at any
    /// rung. Scoped per node by start index, like tooFineDeferred.
    /// </summary>
    readonly List<long> gaps = new();

    /// <summary>A resident mesh drawn only inside one uncovered child footprint.</summary>
    readonly struct GapDraw
    {
        public readonly long Key;
        public readonly float MinX, MinZ, MaxX, MaxZ; // section-local blocks
        public GapDraw(long key, float minX, float minZ, float maxX, float maxZ)
        {
            Key = key; MinX = minX; MinZ = minZ; MaxX = maxX; MaxZ = maxZ;
        }
    }

    readonly List<GapDraw> gapDraws = new();
    static readonly float[] NoClip = { -1e9f, -1e9f, 1e9f, 1e9f };

    /// <summary>Clipped parent draws this frame (coverage where a finer mesh is missing).</summary>
    public int LastGapDrawCount { get; private set; }

    /// <summary>Captured footprints left as sky this frame: no mesh at any rung above them.</summary>
    public int LastUnfilledGaps { get; private set; }

    readonly long[] unfilledSample = new long[4];
    int unfilledSampleCount;
    long lastUnfilledLogFrame = long.MinValue / 2;
    const int UnfilledLogIntervalFrames = 600;
    Action<string>? holeLog;

    /// <summary>Where the throttled unfilled-gap report goes (the mod logger).</summary>
    public void SetHoleLogger(Action<string> log) => holeLog = log;

    /// <summary>
    /// Children a parent's descent skipped as too fine for the camera window.
    /// The skip assumed the parent would draw instead; when a sibling draws,
    /// the parent does not, and every entry here is a hole unless submitted.
    /// Scoped per parent by start index across the recursive walk.
    /// </summary>
    readonly List<long> tooFineDeferred = new();

    public int MeshCount => sectionMeshes.Count;
    public int LastDrawCount { get; private set; }

    /// <summary>Walk nodes handed to Farseer this frame (peek or past vanilla view distance).</summary>
    public int LastCompanionYieldCount { get; private set; }

    /// <summary>Walked tiles past view distance handed to Farseer this frame.</summary>
    public int LastPressureYieldCount { get; private set; }

    public bool PressureYieldActive =>
        DrawAfterCompanion && MeshPressureActive;

    /// <summary>Sections selected by the walk but skipped this frame as off-screen.</summary>
    public int LastCulledCount { get; private set; }

    /// <summary>L0/L1 meshes skipped this frame by heightfield FOV occlusion (draw skip only).</summary>
    public int LastOccludedCount { get; private set; }

    /// <summary>Potato FOV occlusion against resident section SurfaceYMax. Configured from DistantVistasConfig.</summary>
    public readonly LodHeightfieldOcclusion HeightOcclusion = new();

    readonly LodFrustum frustum = new();
    int worldHeight = 1024;


    /// <summary>
    /// Why each coarse node in the current draw list is not descending. Written for one
    /// specific failure: a node drawn far below its wanted level, with the pipeline idle, so
    /// no amount of waiting changes it. Reports each child's actual state rather than an
    /// inference - which is what three wrong diagnoses in a row cost.
    /// </summary>
    public string ExplainCoarseDraws(double px, double pz, int maxNodes = 6)
    {
        var sb = new System.Text.StringBuilder();
        int shown = 0;

        foreach (long key in drawList)
        {
            int level = LodWorld.KeyLevel(key);
            int wanted = WantedLevel(NearestDistanceTo(key));
            if (level <= wanted || shown >= maxNodes) continue;

            shown++;
            double dist = Math.Sqrt(LodWorld.NearestDistanceSqTo(key, px, pz));
            sb.Append($"\n  L{level} at {LodWorld.KeySx(key) * LodWorld.KeyFootprintBlocks(key)},")
              .Append($"{LodWorld.KeySz(key) * LodWorld.KeyFootprintBlocks(key)} dist {(int)dist} wants L{wanted}:");

            for (int qz = 0; qz < 2; qz++)
            {
                for (int qx = 0; qx < 2; qx++)
                {
                    long ck = LodWorld.ChildKey(key, qx, qz);
                    string state;
                    if (!world.HasDataSet.Contains(ck)) state = "no-data";
                    else if (!world.Sections.TryGetValue(ck, out LodSection? cs))
                    {
                        state = world.LoadsInFlight.Contains(ck) ? "loading"
                            : world.LoadFailed.Contains(ck) ? "load-failed"
                            : "not-resident";
                    }
                    else if (cs.CapturedColumns == 0) state = "empty";
                    else if (!HasAnyMesh(ck)) state = world.RenderDirty.Contains(ck) ? "meshing" : "no-mesh!";
                    else state = "ok";
                    sb.Append(' ').Append(state);
                }
            }
        }

        return shown == 0 ? "no coarse draws: every drawn node is at or below its wanted level" : sb.ToString();
    }

    public string DescribeDrawnLevels()
    {
        var counts = new int[LodWorld.MaxLevel + 1];
        foreach (long key in drawList) counts[LodWorld.KeyLevel(key)]++;
        return string.Join(" ", counts.Select((c, i) => $"L{i}:{c}"));
    }

    /// <summary>
    /// Parity of drawn L0 (sx,sz). A dominant (sx+sz)%2 means checkerboard selection.
    /// Also reports resident L0 capture fill so partial 1-of-4-chunk sections show up.
    /// </summary>
    public string DescribeL0ParityAndFill()
    {
        int sxEven = 0, sxOdd = 0, szEven = 0, szOdd = 0, parityEven = 0, parityOdd = 0, l0Drawn = 0;
        foreach (long key in drawList)
        {
            if (LodWorld.KeyLevel(key) != 0) continue;
            l0Drawn++;
            int sx = LodWorld.KeySx(key), sz = LodWorld.KeySz(key);
            if ((sx & 1) == 0) sxEven++; else sxOdd++;
            if ((sz & 1) == 0) szEven++; else szOdd++;
            if (((sx + sz) & 1) == 0) parityEven++; else parityOdd++;
        }

        int partial = 0, full = 0, thin = 0, residentL0 = 0;
        foreach (var kv in world.Sections)
        {
            if (LodWorld.KeyLevel(kv.Key) != 0) continue;
            residentL0++;
            int c = kv.Value.CapturedColumns;
            if (c >= LodSection.GridSize * LodSection.GridSize) full++;
            else if (c <= LodSection.GridSize * LodSection.GridSize / 4) thin++;
            else partial++;
        }

        return $"drawnL0={l0Drawn} sxEven/Odd={sxEven}/{sxOdd} szEven/Odd={szEven}/{szOdd} "
            + $"(sx+sz)%2 even/odd={parityEven}/{parityOdd}; residentL0={residentL0} "
            + $"capFull/partial/thin1quad={full}/{partial}/{thin}";
    }

    readonly LodTintRegistry tints;
    int uploadedTintVersion = -1;

    public LodTerrainRenderer(ICoreClientAPI capi, LodWorld world, LodWorker worker, LodTintRegistry tints)
    {
        this.capi = capi;
        this.world = world;
        this.worker = worker;
        this.tints = tints;
        maxWorkerMeshBacklog = worker.MeshThreads * MeshBacklogPerThread;

        capi.Event.ReloadShader += LoadShader;
        LoadShader();

        capi.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "distantvistas-lod");
    }

    public bool LoadShader()
    {
        prog = capi.Shader.NewShaderProgram();
        prog.AssetDomain = "distantvistas";

        prog.VertexShader = capi.Shader.NewShader(EnumShaderType.VertexShader);
        prog.FragmentShader = capi.Shader.NewShader(EnumShaderType.FragmentShader);

        capi.Shader.RegisterFileShaderProgram("lodterrain", prog);

        // The shaders carry their own `const int TINT_SLOTS`, because this game version
        // exposes no way to inject a #define, and a mismatch decodes water as opaque and
        // thin plants as water with no compile error. That used to be guarded here by
        // comparing MaxSlots against a hand-maintained C# mirror of the shader's value -
        // which is two constants in one file, and so could never notice a shader being
        // edited. The compiler agreed: the branch raised CS0162, unreachable code.
        // The check that works reads the shader files, in the fast tier of check.sh.

        uploadedTintVersion = -1; // fresh program object: uniform state is gone
        shaderOk = prog.Compile();
        if (!shaderOk) capi.Logger.Error("[DistantVistas] lodterrain shader failed to compile; LOD rendering disabled");
        return shaderOk;
    }

    public void ApplyZFar()
    {
        float needed = GameMath.Max(28000, EffectiveFarDistance + 2048);
        var clientMain = (ClientMain)capi.World;

        if (clientMain.MainCamera.ZFar >= needed && appliedZFar == needed) return;

        clientMain.MainCamera.ZFar = needed;
        capi.Render.Reset3DProjection();
        appliedZFar = needed;
    }

    void UpdateEffectiveFarDistance(float vanillaViewDistance)
    {
        double maxDistSq = 0;
        foreach (long key in sectionMeshes.Keys)
        {
            int footprint = LodWorld.KeyFootprintBlocks(key);
            double dx = LodWorld.KeySx(key) * (double)footprint + footprint / 2.0 - camPos.X;
            double dz = LodWorld.KeySz(key) * (double)footprint + footprint / 2.0 - camPos.Z;
            double distSq = dx * dx + dz * dz;
            if (distSq > maxDistSq) maxDistSq = distSq;
        }

        float far = (float)Math.Sqrt(maxDistSq) + LodSection.SectionBlocks * 1.5f;
        farthestMeshedDistance = maxDistSq > 0 ? far : 0;

        if (frameCounter - lastCapturedScanFrame >= EvictScanInterval)
        {
            lastCapturedScanFrame = frameCounter;
            farthestCapturedDistance = ScanFarthestCapturedDistance();
        }
        // The discard / sky fade at dist == 1 sits on the captured rim, never
        // on visited interior whose far meshes happen to be evicted.
        far = Math.Max(far, (float)farthestCapturedDistance);

        if (FarViewDistanceCap > 0)
            EffectiveFarDistance = Math.Min(far, FarViewDistanceCap);
        else
            EffectiveFarDistance = Math.Max(far, vanillaViewDistance + 16384);
    }

    /// <summary>
    /// Farthest captured L2 (256-block) footprint from the camera, padded so
    /// its far corner is inside. L2 keeps the scan at a few thousand keys.
    /// </summary>
    double ScanFarthestCapturedDistance()
    {
        const int scanLevel = 2;
        int footprint = LodSection.SectionBlocks << scanLevel;
        double maxDistSq = 0;
        foreach (long key in world.HasDataSet)
        {
            if (LodWorld.KeyLevel(key) != scanLevel) continue;
            double dx = LodWorld.KeySx(key) * (double)footprint + footprint / 2.0 - camPos.X;
            double dz = LodWorld.KeySz(key) * (double)footprint + footprint / 2.0 - camPos.Z;
            double distSq = dx * dx + dz * dz;
            if (distSq > maxDistSq) maxDistSq = distSq;
        }
        return maxDistSq > 0 ? Math.Sqrt(maxDistSq) + footprint * 0.75 : 0;
    }

    // ---- Detail selection (quadtree walk) ----

    double NearestDistanceTo(long key)
    {
        int footprint = LodWorld.KeyFootprintBlocks(key);
        double minX = LodWorld.KeySx(key) * (double)footprint;
        double minZ = LodWorld.KeySz(key) * (double)footprint;
        double dx = Math.Max(0, Math.Max(minX - camPos.X, camPos.X - (minX + footprint)));
        double dz = Math.Max(0, Math.Max(minZ - camPos.Z, camPos.Z - (minZ + footprint)));
        return Math.Sqrt(dx * dx + dz * dz);
    }

    static int WantedLevel(double distance) => LodWorld.WantedLevelFor(distance);

    /// <summary>
    /// Squared distance to the section's nearest edge. The hot paths compare and pick a
    /// level, and both work on the square, so neither needs the root.
    /// </summary>
    double NearestDistanceSqTo(long key) => LodWorld.NearestDistanceSqTo(key, camPos.X, camPos.Z);

    /// <summary>
    /// Far land stops descending below the wanted rung. The 1.0x draw ring
    /// still reaches L0/L1. The keep-circle is not part of this.
    /// </summary>
    bool ShouldVisitChildForDraw(
        long childKey, int parentWanted, bool parentDrawFullDetail, bool parentHasMesh,
        bool parentLandLike, bool inLeadCone)
    {
        int childLevel = LodWorld.KeyLevel(childKey);
        bool childHasData = world.HasDataSet.Contains(childKey);
        // The child's own cone test: a parent that straddles the cone edge has
        // children on both sides, and only the ones in front are intervening.
        bool childFarther = childHasData && childLevel <= LodCoveragePolicy.VisitedKeepMaxLevel
            && IsFartherLoaded(childKey);
        bool childInCone = inLeadCone || (childFarther && InLeadCone(childKey));
        double childNear = Math.Sqrt(NearestDistanceSqTo(childKey));
        return LodCoveragePolicy.ShouldVisitChildForDraw(
            childLevel, parentWanted, parentDrawFullDetail, parentHasMesh,
            parentLandLike, childInCone, lookDown01, childHasData, childFarther,
            childNear, liveViewDistance, DrawAfterCompanion);
    }

    bool IsFartherLoaded(long key) =>
        LodCoveragePolicy.IsFartherLoaded(
            Math.Sqrt(NearestDistanceSqTo(key)), LodWorld.KeyFootprintBlocks(key), FarthestKnownDistance)
        && CapturedBeyond(key);

    void Submit(long key)
    {
        // Keep the mesh pinned even when occlusion skips the draw — cache stays.
        lastSelectedFrame[key] = frameCounter;
        if (HeightOcclusion.Enabled
            && LodWorld.KeyLevel(key) <= HeightOcclusion.MaxLevel
            && HeightOcclusion.IsOccluded(
                world, key, camPos.X, camPos.Y, camPos.Z, lookY, out _))
        {
            LastOccludedCount++;
            return;
        }
        drawList.Add(key);
        drawnThisFrame.Add(key);
    }

    bool HasAnyMesh(long key) =>
        sectionMeshes.ContainsKey(key) || waterMeshes.ContainsKey(key) || emptyMeshKeys.Contains(key);

    bool IsProvisionalKey(long key)
    {
        if (world.ProvisionalL0Keys.Contains(key)) return true;
        return world.Sections.TryGetValue(key, out LodSection? section)
            && section.ProvisionalQuadrants != 0;
    }

    /// <summary>
    /// Land this client actually walked (or a parent of that land).
    /// </summary>
    bool HasRealSurface(long key)
    {
        if (realSurfaceMemo.TryGetValue(key, out bool cached)) return cached;
        int level = LodWorld.KeyLevel(key);
        bool result;
        if (level == 0)
        {
            result = IsRealL0(key);
        }
        else
        {
            result = false;
            for (int qz = 0; qz < 2 && !result; qz++)
            {
                for (int qx = 0; qx < 2; qx++)
                {
                    long ck = LodWorld.ChildKey(key, qx, qz);
                    if (world.HasDataSet.Contains(ck) && HasRealSurface(ck))
                    {
                        result = true;
                        break;
                    }
                }
            }
        }
        realSurfaceMemo[key] = result;
        return result;
    }

    bool IsRealL0(long key)
    {
        if (!world.HasDataSet.Contains(key)) return false;
        if (!world.ProvisionalL0Keys.Contains(key)) return true;
        if (world.Sections.TryGetValue(key, out LodSection? section))
            return !section.IsPeekOnly();
        return false;
    }

    bool YieldToCompanion(long key, bool count = false)
    {
        double near = Math.Sqrt(NearestDistanceSqTo(key));
        bool yield = LodCoveragePolicy.YieldFootprintToCompanion(
            DrawAfterCompanion, HasRealSurface(key),
            sectionMeshes.Count, LodMemoryBudget.MaxResidentMeshes,
            near, liveViewDistance);
        if (yield && count)
        {
            LastCompanionYieldCount++;
            if (HasRealSurface(key)) LastPressureYieldCount++;
        }
        return yield;
    }

    bool HasDrawableMesh(long key) =>
        sectionMeshes.ContainsKey(key) || waterMeshes.ContainsKey(key);

    bool InLeadCone(long key)
    {
        if (leadConeMemo.TryGetValue(key, out bool cached)) return cached;
        int footprint = LodWorld.KeyFootprintBlocks(key);
        double originX = LodWorld.KeySx(key) * (double)footprint;
        double originZ = LodWorld.KeySz(key) * (double)footprint;
        double relX = originX - camPos.X;
        double relZ = originZ - camPos.Z;
        double minY = -camPos.Y;
        double maxY = worldHeight - camPos.Y;
        if (world.Sections.TryGetValue(key, out LodSection? bounds) && bounds.HasSurfaceBounds)
        {
            const int pad = 48;
            minY = bounds.SurfaceYMin - pad - camPos.Y;
            maxY = bounds.SurfaceYMax + pad - camPos.Y;
        }
        bool result = frustum.BoxInLeadCone(relX, minY, relZ, relX + footprint, maxY, relZ + footprint);
        leadConeMemo[key] = result;
        return result;
    }

    /// <summary>
    /// Tight frustum test for the section AABB (camera-relative). Visited-keep
    /// tiles inside the 2× ring skip cull so fast flight does not punch holes.
    /// </summary>
    bool SectionBoxInView(long key)
    {
        if (boxInViewMemo.TryGetValue(key, out bool cached)) return cached;
        int footprint = LodWorld.KeyFootprintBlocks(key);
        double originX = LodWorld.KeySx(key) * (double)footprint;
        double originZ = LodWorld.KeySz(key) * (double)footprint;
        int level = LodWorld.KeyLevel(key);
        double drawDist = Math.Sqrt(
            (originX + footprint / 2.0 - camPos.X) * (originX + footprint / 2.0 - camPos.X)
            + (originZ + footprint / 2.0 - camPos.Z) * (originZ + footprint / 2.0 - camPos.Z));
        bool keepVisited = LodCoveragePolicy.ShouldKeepVisitedDraw(
            level, world.HasDataSet.Contains(key), drawDist, liveViewDistance);
        bool result;
        if (keepVisited)
        {
            result = true;
        }
        else
        {
            double relX = originX - camPos.X;
            double relZ = originZ - camPos.Z;
            double minY = -camPos.Y;
            double maxY = worldHeight - camPos.Y;
            if (world.Sections.TryGetValue(key, out LodSection? bounds) && bounds.HasSurfaceBounds)
            {
                const int pad = 48;
                minY = bounds.SurfaceYMin - pad - camPos.Y;
                maxY = bounds.SurfaceYMax + pad - camPos.Y;
            }
            result = frustum.BoxInView(relX, minY, relZ, relX + footprint, maxY, relZ + footprint);
        }
        boxInViewMemo[key] = result;
        return result;
    }

    /// <summary>
    /// Standing still: do not walk or mesh-request tiles entirely behind the camera
    /// and outside the lead cone. GPU meshes stay pinned via lastSelectedFrame.
    /// </summary>
    bool TrySkipTurnOnlyOffscreen(long key, bool insideVanilla, bool drawFullDetail, bool inLeadCone, bool hasMesh)
    {
        if (windowMovedThisFrame || insideVanilla || drawFullDetail || inLeadCone)
            return false;
        if (SectionBoxInView(key)) return false;
        if (hasMesh) lastSelectedFrame[key] = frameCounter;
        return true;
    }

    /// <summary>
    /// Defer mesh jobs for off-screen tiles while look-only so ScheduleMeshJobs
    /// does not scan thousands of dirty keys every yaw tick.
    /// Unmeshed L0/L1 in the handoff ("second") band are never deferred — that
    /// ring must follow the player 360°, and stuck coarse L2 after backing out
    /// of far white was children never entering RenderDirty (BAND / e8d91d).
    /// </summary>
    bool ShouldDeferMeshRequest(long key)
    {
        if (windowMovedThisFrame) return false;
        if (InLeadCone(key)) return false;
        if (SectionBoxInView(key)) return false;
        int level = LodWorld.KeyLevel(key);
        double dist = Math.Sqrt(NearestDistanceSqTo(key));
        float overdraw = GameMath.Clamp(OverdrawStart, 0.15f, 0.95f);
        double vanillaR = liveViewDistance * overdraw;
        if (level <= 1 && !HasAnyMesh(key) && InHandoffRing(dist, vanillaR))
            return false;
        if (LodCoveragePolicy.ShouldKeepVisitedDraw(
                level, world.HasDataSet.Contains(key), dist, liveViewDistance)
            && LodCoveragePolicy.IsDrawFullDetail(dist, liveViewDistance))
            return false;
        return true;
    }

    bool ChildSurfaceUnion(long key, out int yMin, out int yMax)
    {
        yMin = int.MaxValue;
        yMax = int.MinValue;
        if (LodWorld.KeyLevel(key) <= 0) return false;
        bool any = false;
        for (int qz = 0; qz < 2; qz++)
        {
            for (int qx = 0; qx < 2; qx++)
            {
                long ck = LodWorld.ChildKey(key, qx, qz);
                if (!world.Sections.TryGetValue(ck, out LodSection? cs) || !cs.HasSurfaceBounds)
                    continue;
                any = true;
                if (cs.SurfaceYMin < yMin) yMin = cs.SurfaceYMin;
                if (cs.SurfaceYMax > yMax) yMax = cs.SurfaceYMax;
            }
        }
        return any;
    }

    bool ComputeLandLike(int level, LodSection? section, long key)
    {
        if (landLikeMemo.TryGetValue(key, out bool cached)) return cached;
        bool result;
        if (level < 1) result = true;
        else if (section == null) result = false;
        else if (!LodCoveragePolicy.IsLandLikeCoarseMesh(
                level, section.HasSurfaceBounds, section.SurfaceRelief, section.CapturedColumns))
            result = false;
        else if (!ChildSurfaceUnion(key, out int childYMin, out int childYMax))
            result = false;
        else
            result = LodCoveragePolicy.ParentFollowsChildSurface(
                section.HasSurfaceBounds, section.SurfaceYMin, section.SurfaceYMax,
                true, childYMin, childYMax);
        landLikeMemo[key] = result;
        return result;
    }

    /// <summary>
    /// Ask for the wanted-level (or coarser) parent of a far tile. If the parent
    /// section is not in RAM and not on disk, queue child-to-parent mip so the
    /// existing LodMip path can build it. Capped per frame.
    /// </summary>
    void RequestCoarseFill(long key, int wanted)
    {
        if (wanted < 2) return;
        if (coarseParentRequestsThisFrame >= CoarseParentRequestsPerFrame) return;

        long target = key;
        while (LodWorld.KeyLevel(target) < wanted && LodWorld.KeyLevel(target) < LodWorld.MaxLevel)
            target = LodWorld.ParentKey(target);

        if (LodWorld.KeyLevel(target) < 2) return;
        if (HasAnyMesh(target) || meshJobInFlight.Contains(target)) return;
        // Already queued: keep it alive past the prune, but do not charge the
        // budget again. Re-charging for the same pending target every frame
        // starved every other coarse target in the walk order behind it.
        if (world.RenderDirty.Contains(target))
        {
            walkRequested.Add(target);
            return;
        }

        if (world.Sections.TryGetValue(target, out LodSection? section))
        {
            if (section.CapturedColumns == 0) return;
            coarseParentRequestsThisFrame++;
            RequestMesh(target, allowWhileStarving: true);
            return;
        }

        if (world.HasDataSet.Contains(target) && !world.LoadFailed.Contains(target))
        {
            coarseParentRequestsThisFrame++;
            RequestMesh(target, allowWhileStarving: true);
            return;
        }

        // Parent section missing: mip it from children. ProcessPropagation creates
        // the parent via LodMip.DownsampleIntoParent; no invented cake plates.
        if (QueueMipFromChildren(target)) coarseParentRequestsThisFrame++;
    }

    /// <summary>
    /// Ask the mip pipeline to build <paramref name="parent"/> from whichever of
    /// its children hold data. Idempotent: MipDirty is a set, and propagation
    /// is budgeted per tick on the game thread. Returns whether anything was queued.
    /// </summary>
    bool QueueMipFromChildren(long parent)
    {
        if (LodWorld.KeyLevel(parent) < 1) return false;
        bool queued = false;
        for (int qz = 0; qz < 2; qz++)
        {
            for (int qx = 0; qx < 2; qx++)
            {
                long ck = LodWorld.ChildKey(parent, qx, qz);
                if (world.HasDataSet.Contains(ck))
                {
                    world.MipDirty.Add(ck);
                    queued = true;
                }
            }
        }
        return queued;
    }

    bool InHandoffRing(double nearDist, double vanillaCoverageRadius)
    {
        double outer = liveViewDistance + LodSection.SectionBlocks * 6;
        return nearDist >= vanillaCoverageRadius * 0.45 && nearDist <= outer;
    }

    bool SectionFullyInsideVanilla(long key, LodSection section, double radius)
    {
        int footprint = LodWorld.KeyFootprintBlocks(key);
        double minX = LodWorld.KeySx(key) * (double)footprint;
        double minZ = LodWorld.KeySz(key) * (double)footprint;
        return LodCoveragePolicy.EntireAabbInsideVanilla(
            minX, minX + footprint, minZ, minZ + footprint,
            camPos.X, camPos.Z, camPos.Y,
            section.SurfaceYMin, section.SurfaceYMax, radius, lookDown01);
    }

    static long MapChunkKey(int cx, int cz) => ((long)cz << 32) | (uint)cx;

    void RefreshLoadedMapChunks()
    {
        bool viewChanged = lastMapRefreshViewDistance != liveViewDistance;
        mapChunkRefreshAge++;
        if (!LoadedMapChunksDirty
            && !viewChanged
            && mapChunkRefreshAge < LodFrameBudget.MapChunkRefreshIntervalFrames)
            return;

        LoadedMapChunksDirty = false;
        mapChunkRefreshAge = 0;
        lastMapRefreshViewDistance = liveViewDistance;
        vanillaOwnsMemo.Clear();

        loadedMapChunks.Clear();
        loadedWorldColumns.Clear();
        int cs = GlobalConstants.ChunkSize;
        if (cs <= 0) return;
        int cx0 = (int)Math.Floor(camPos.X / cs);
        int cz0 = (int)Math.Floor(camPos.Z / cs);
        int rad = Math.Max(4, (int)Math.Ceiling(liveViewDistance / cs) + 2);
        var ba = capi.World.BlockAccessor;
        for (int dz = -rad; dz <= rad; dz++)
        {
            for (int dx = -rad; dx <= rad; dx++)
            {
                int cx = cx0 + dx;
                int cz = cz0 + dz;
                if (ba.GetMapChunk(cx, cz) != null)
                    loadedMapChunks.Add(MapChunkKey(cx, cz));
            }
        }
        // Do not prefill loadedWorldColumns from camera Y. Air around a high
        // camera is not the ocean surface; that HashSet short-circuit punched
        // wide-open L1/L2 holes you could see neighbouring seafloor through.
    }

    double VanillaCoverageRadius()
    {
        float overdraw = GameMath.Clamp(OverdrawStart, 0.15f, 0.95f);
        return liveViewDistance * overdraw;
    }

    bool AllMapChunksLoadedForKey(long key)
    {
        int footprint = LodWorld.KeyFootprintBlocks(key);
        int minX = LodWorld.KeySx(key) * footprint;
        int minZ = LodWorld.KeySz(key) * footprint;
        return LodCoveragePolicy.AllMapChunksLoaded(
            minX, minX + footprint, minZ, minZ + footprint,
            GlobalConstants.ChunkSize,
            (cx, cz) => loadedMapChunks.Contains(MapChunkKey(cx, cz)));
    }

    bool AllWorldColumnsLoadedForKey(long key, LodSection? bounds)
    {
        // No surface Y → not owned. Camera Y is air when you fly; using it
        // hid LOD and left empty pads that filled only while you stood in them.
        if (bounds == null || !bounds.HasSurfaceBounds) return false;
        int footprint = LodWorld.KeyFootprintBlocks(key);
        int minX = LodWorld.KeySx(key) * footprint;
        int minZ = LodWorld.KeySz(key) * footprint;
        int cs = GlobalConstants.ChunkSize;
        if (cs <= 0) return false;
        int mapY = capi.World.BlockAccessor.MapSizeY;
        int maxCy = Math.Max(0, mapY / cs - 1);
        int surfaceY = (bounds.SurfaceYMin + bounds.SurfaceYMax) / 2;
        int cySurf = GameMath.Clamp(surfaceY / cs, 0, maxCy);
        var ba = capi.World.BlockAccessor;
        return LodCoveragePolicy.AllMapChunksLoaded(
            minX, minX + footprint, minZ, minZ + footprint, cs,
            (cx, cz) =>
            {
                long col = MapChunkKey(cx, cz);
                if (loadedWorldColumns.Contains(col)) return true;
                bool surfaceLoaded = ba.GetChunk(cx, cySurf, cz) != null;
                if (!LodCoveragePolicy.WorldColumnIsTessellated(false, surfaceLoaded))
                    return false;
                loadedWorldColumns.Add(col);
                return true;
            });
    }

    bool VanillaOwnsKey(long key, LodSection? bounds, double hideRadius)
    {
        if (vanillaOwnsMemo.TryGetValue(key, out bool cached)) return cached;

        // Farthest corner still inside the skip disc: vanilla should be drawing
        // the whole tile. Nearest-point hide chopped a moving ring (0.7.38).
        // Missing surface bounds is not owned: a 2D XZ fallback hid newly
        // captured ocean/land at altitude while vanilla was not drawing those
        // columns — 128/256 sky squares after you flew up.
        bool inside3d = bounds != null && bounds.HasSurfaceBounds
            && SectionFullyInsideVanilla(key, bounds, hideRadius);
        // Map chunks mean the column has arrived (0.7.48 grow-VD). World
        // columns mean vanilla is tessellating. Explored minimap land without
        // a loaded chunk is LOD, not a sky square.
        bool owned;
        if (!inside3d) owned = LodCoveragePolicy.VanillaOwnsFootprint(false, false, false);
        else
        {
            bool allMap = AllMapChunksLoadedForKey(key);
            if (!allMap) owned = LodCoveragePolicy.VanillaOwnsFootprint(true, false, false);
            else
            {
                bool allWorld = AllWorldColumnsLoadedForKey(key, bounds);
                owned = LodCoveragePolicy.VanillaOwnsFootprint(true, true, allWorld);
            }
        }
        vanillaOwnsMemo[key] = owned;
        return owned;
    }

    bool AnyChildVanillaOwned(long key, double hideRadius)
    {
        if (LodWorld.KeyLevel(key) < 1) return false;
        for (int qz = 0; qz < 2; qz++)
        {
            for (int qx = 0; qx < 2; qx++)
            {
                long ck = LodWorld.ChildKey(key, qx, qz);
                world.Sections.TryGetValue(ck, out LodSection? child);
                if (VanillaOwnsKey(ck, child, hideRadius)) return true;
            }
        }
        return false;
    }

    bool ChildHasVisitedSurface(long key)
    {
        if (LodWorld.KeyLevel(key) <= 0) return false;
        for (int qz = 0; qz < 2; qz++)
        {
            for (int qx = 0; qx < 2; qx++)
            {
                if (world.HasDataSet.Contains(LodWorld.ChildKey(key, qx, qz)))
                    return true;
            }
        }
        return false;
    }

    bool AllVisitedL0Children(long key)
    {
        if (LodWorld.KeyLevel(key) != 1) return false;
        for (int qz = 0; qz < 2; qz++)
        {
            for (int qx = 0; qx < 2; qx++)
            {
                long ck = LodWorld.ChildKey(key, qx, qz);
                if (!world.HasDataSet.Contains(ck)) return false;
                if (world.IncompleteL0Keys.Contains(ck) || world.SparseL0Keys.Contains(ck)) return false;
            }
        }
        return true;
    }

    bool AllChildrenCovered(long key)
    {
        bool covered = true;
        for (int qz = 0; qz < 2; qz++)
        {
            for (int qx = 0; qx < 2; qx++)
            {
                long ck = LodWorld.ChildKey(key, qx, qz);
                bool hasData = world.HasDataSet.Contains(ck);
                bool hasMesh = HasAnyMesh(ck);
                int capturedColumns = LodSection.GridSize * LodSection.GridSize;

                if (world.Sections.TryGetValue(ck, out LodSection? child))
                    capturedColumns = child.CapturedColumns;
                else if (LodWorld.KeyLevel(ck) == 0 && world.SparseL0Keys.Contains(ck))
                    capturedColumns /= 4;

                if (LodCoveragePolicy.ChildCanReplaceParent(
                        LodWorld.KeyLevel(ck), hasData, capturedColumns, hasMesh))
                {
                    // Empty children intentionally need no mesh. Real gate meshes are
                    // load-bearing even when never drawn, so spare them from eviction.
                    if (hasMesh) lastSelectedFrame[ck] = frameCounter;
                }
                else
                {
                    // A missing child or incomplete L0 fragment cannot replace broad
                    // parent coverage. Re-request only when data exists to mesh.
                    if (hasData && !hasMesh) RequestMesh(ck, allowWhileStarving: true);
                    covered = false;
                }
            }
        }
        return covered;
    }

    bool CollectDrawNodes(long key)
    {
        bool hasMesh = HasAnyMesh(key);
        int level = LodWorld.KeyLevel(key);
        double nearDistSq = NearestDistanceSqTo(key);
        double nearDist = Math.Sqrt(nearDistSq);

        // Optional extra ceiling. Returning false used to make the L6 parent
        // paint a giant square over the cap. Treat it as covered fog instead.
        if (FarViewDistanceCap > 0 && nearDist > FarViewDistanceCap + LodSection.SectionBlocks)
            return true;

        if (YieldToCompanion(key, count: true))
        {
            if (level == 0) return false;
            bool yieldedChild = false;
            for (int qz = 0; qz < 2; qz++)
            {
                for (int qx = 0; qx < 2; qx++)
                {
                    long ck = LodWorld.ChildKey(key, qx, qz);
                    if (world.HasDataSet.Contains(ck))
                        yieldedChild |= CollectDrawNodes(ck);
                }
            }
            return yieldedChild;
        }

        // Near-cull must not abort quadtree descent; only skip *drawing* sections
        // whose nearest edge is inside the vanilla bubble. Top-level L6 sections are
        // 4096 blocks - the player is inside them (nearDist=0), so returning early
        // without descending would draw zero LOD meshes past the bubble.
        double vanillaCoverageRadius = VanillaCoverageRadius();
        // Whole AABB inside the 0.55 skip disc, every map-chunk arrived, and
        // every world column tessellated. Full-VD hide left mid-ground sky
        // squares on explored land vanilla was not drawing.
        world.Sections.TryGetValue(key, out LodSection? coverageSection);
        bool insideVanilla = VanillaOwnsKey(key, coverageSection, vanillaCoverageRadius);

        bool landLike = ComputeLandLike(level, coverageSection, key);
        bool inLeadCone = InLeadCone(key);
        bool horizonCone = LodCoveragePolicy.HorizonLeadCone(inLeadCone, lookDown01);
        bool fineHorizon = LodCoveragePolicy.HorizonLeadConeFine(
            inLeadCone, lookDown01, nearDist, liveViewDistance);

        // Past 3x view distance in front: stop only when a companion is
        // actually drawing that band. Farseer off means empty sky if we stop,
        // so keep DV land-like cover instead.
        if (horizonCone && LodCoveragePolicy.PastHorizonEmptyStop(
                nearDist, liveViewDistance, DrawAfterCompanion))
        {
            if (hasMesh) lastSelectedFrame[key] = frameCounter;
            return true;
        }

        int wanted = LodWorld.WantedLevelForSq(nearDistSq);

        // Relief-aware coarsen: far flats stay cheap; mountains keep sharper LODs.
        // FidelityStep 1: raise flatten thresholds and soften the +2 bump so ridges
        // hold continuous silhouette (no floating peaks / washed plateaus).
        if (wanted >= 1 && world.Sections.TryGetValue(key, out LodSection? reliefSec) && reliefSec.HasSurfaceBounds)
        {
            int relief = reliefSec.SurfaceRelief;
            int flatMax = LodWorld.FidelityStep >= 0.5 ? 8 : 10;
            int midMax = LodWorld.FidelityStep >= 0.5 ? 20 : 24;
            if (relief < flatMax) wanted = Math.Min(LodWorld.MaxVisualLevel, wanted + (LodWorld.FidelityStep >= 0.5 ? 1 : 2));
            else if (relief < midMax) wanted = Math.Min(LodWorld.MaxVisualLevel, wanted + 1);
            // High relief (mountains): optionally delay coarsen by one rung.
            else if (LodWorld.FidelityStep >= 0.5 && wanted >= 1)
                wanted = Math.Max(0, wanted - 1);
        }

        bool handoff = InHandoffRing(nearDist, vanillaCoverageRadius);
        // Draw full L0/L1 only inside live view distance and the vanilla seam.
        // The keep-circle is larger and only holds GPU meshes.
        bool drawFullDetail = LodCoveragePolicy.IsDrawFullDetail(nearDist, liveViewDistance) || handoff;
        if (TrySkipTurnOnlyOffscreen(key, insideVanilla, drawFullDetail, inLeadCone, hasMesh))
            return false;

        // Intervening span: visited L0/L1 in front of the camera with land
        // already drawn past it. Coarsening is fine when a parent mesh takes
        // over the whole footprint; this flag is what stops a per-child skip
        // or a pruned mesh job from turning one tile of that span into sky.
        bool hasData = world.HasDataSet.Contains(key);
        bool fartherLoaded = LodCoveragePolicy.IsFartherLoaded(
                nearDist, LodWorld.KeyFootprintBlocks(key), FarthestKnownDistance)
            && (!hasData || !inLeadCone || CapturedBeyond(key));
        bool mustCover = !insideVanilla
            && LodCoveragePolicy.MustCoverIntervening(
                level, hasData, inLeadCone, fartherLoaded, nearDist, liveViewDistance);
        // Gaps appended by this node's subtree start here; see gaps.
        int gapStart = gaps.Count;

        // Mesh the surface we will actually draw. L0/L1 at the 1.0x ring and
        // handoff; wanted-level further out, including mip parents of visited L0.
        // Do not request a parent just so we can paint a giant square over a hole.
        if (!hasMesh && !(horizonCone && level > LodCoveragePolicy.LeadConeMaxDrawLevel))
        {
            // Cap lead-cone L0 promote per frame so a small yaw does not remesh storm.
            // 0.8.46: look-only starve — only walking may bypass StarveMeshRequests.
            bool allowStarve = StepBusyThisFrame;
            if (fineHorizon && !insideVanilla
                && fineHorizonRequestsThisFrame < FineHorizonRequestsPerFrame)
            {
                fineHorizonRequestsThisFrame++;
                RequestMesh(key, allowWhileStarving: allowStarve);
            }
            else if (handoff && level <= 1)
                RequestMesh(key, allowWhileStarving: allowStarve);
            else if (LodCoveragePolicy.RequestVisitedKeepMesh(
                         level, hasMesh, hasData, insideVanilla,
                         nearDist, liveViewDistance, inLeadCone, fartherLoaded))
                RequestMesh(key, allowWhileStarving: allowStarve);
            else if (LodCoveragePolicy.RequestKeepCircleParent(
                         level, hasMesh, hasData, insideVanilla,
                         nearDist, liveViewDistance, wanted))
                RequestMesh(key, allowWhileStarving: allowStarve);
            else if (!insideVanilla && (level == wanted || level == wanted + 1))
                RequestMesh(key);
            else if (!insideVanilla && level <= wanted + 1
                     && nearDist < liveViewDistance + LodSection.SectionBlocks * 4)
                RequestMesh(key);
            else if (!insideVanilla && horizonCone && !fineHorizon
                     && level == LodCoveragePolicy.LeadConeMaxCoverLevel)
                RequestMesh(key);
            else if (insideVanilla && level == 0 && nearDist > vanillaCoverageRadius * 0.65)
                RequestMesh(key);
        }

        bool holdVisitedL0 = drawFullDetail && level == 1 && AllVisitedL0Children(key);
        if (holdVisitedL0)
        {
            if (handoff) wanted = 0;
            else if (wanted >= 1) wanted = Math.Max(0, wanted - 1);
        }

        // Inside vanilla: always descend. Never draw this parent here - that is
        // how 0.7.21 put Horizons plates under a look-down / high camera.
        if (insideVanilla && level > 0)
        {
            AllChildrenCovered(key);
            bool anyChildDrew = false;
            for (int qz = 0; qz < 2; qz++)
            {
                for (int qx = 0; qx < 2; qx++)
                {
                    long ck = LodWorld.ChildKey(key, qx, qz);
                    if (!ShouldVisitChildForDraw(ck, wanted, true, hasMesh, landLike, inLeadCone)) continue;
                    if (world.HasDataSet.Contains(ck)) anyChildDrew |= CollectDrawNodes(ck);
                }
            }
            // Whatever the children left uncovered is loaded chunks, not sky.
            DiscardGaps(gapStart);
            return anyChildDrew;
        }

        bool forcedDetail = LodCoveragePolicy.MustDescendForVisualCap(level, LodWorld.MaxVisualLevel);
        bool keepVisited = LodCoveragePolicy.DescendForVisitedKeep(
            level, ChildHasVisitedSurface(key), nearDist, liveViewDistance, inLeadCone, fartherLoaded);

        if (!insideVanilla && !drawFullDetail && wanted >= 2 && !horizonCone)
            RequestCoarseFill(key, wanted);

        // PreferParentCoverage: until every child can replace this parent, a
        // land-like mesh here is cover, not a license to punch sky by refusing
        // L2+ in the lead cone with nothing underneath.
        bool preferParent = level >= 1
            && LodCoveragePolicy.PreferParentCoverage(hasMesh, AllChildrenCovered(key));

        // In the lead cone prefer L0/L1, but a land-like parent may stop when
        // PreferParentCoverage says children are not ready, or land-like L2
        // past the fine ring. Plates still never stop in front.
        bool stopAtThisRung = LodCoveragePolicy.StopDescentAtAvailableRung(
            level, wanted, drawFullDetail, hasMesh, landLike, inLeadCone, lookDown01,
            nearDist, liveViewDistance, preferParent)
            && LodCoveragePolicy.MaySubmitCoarseWhole(
                level, nearDist, vanillaCoverageRadius, insideVanilla);
        if (stopAtThisRung && level < wanted && !horizonCone)
            RequestCoarseFill(key, wanted);

        bool drawableCoarse = hasMesh && LodCoveragePolicy.MayDrawCoarseParent(
            level, insideVanilla, landLike, inLeadCone, lookDown01, nearDist, liveViewDistance,
            preferParent);

        // L1 used to stop here and hide the L0 the player already walked. That
        // is the "I had data, it vanished when I backed up" hole.
        bool walkCapturedL0 = level == 1 && (drawFullDetail || fineHorizon);

        if ((walkCapturedL0 || !stopAtThisRung) && level > 0 && (walkCapturedL0 || forcedDetail || (level > wanted && AllChildrenCovered(key)) || !drawableCoarse
            || (holdVisitedL0 && wanted == 0) || keepVisited || drawFullDetail))
        {
            int deferredStart = tooFineDeferred.Count;
            bool anyChildDrew = false;
            for (int qz = 0; qz < 2; qz++)
            {
                for (int qx = 0; qx < 2; qx++)
                {
                    long ck = LodWorld.ChildKey(key, qx, qz);
                    if (!world.HasDataSet.Contains(ck)) continue;
                    if (walkCapturedL0 || ShouldVisitChildForDraw(ck, wanted, drawFullDetail, hasMesh, landLike, inLeadCone))
                    {
                        // A child that returns false has already listed its own
                        // uncovered footprints (or parked itself in tooFineDeferred).
                        if (CollectDrawNodes(ck)) anyChildDrew = true;
                    }
                    else if (!LodCoveragePolicy.PastHorizonEmptyStop(
                                 Math.Sqrt(NearestDistanceSqTo(ck)), liveViewDistance,
                                 DrawAfterCompanion))
                    {
                        // Not walked this frame: nothing under it can draw.
                        // The cone used to skip this AddGap (`!horizonCone`), so
                        // L3 dropped four L2 pads on the skyline with no fill.
                        AddGap(ck);
                    }
                }
            }

            // 0.7.20 rule: if any child drew, or MaxVisualLevel wants L0, do not
            // substitute this parent as a box over the whole footprint. But the
            // footprints its subtree left uncovered are not "empty until they
            // mesh" any more: this mip fills exactly those, clipped, and the
            // siblings that drew stay as they are. A parent without a mesh
            // leaves them for its own ancestor and asks for a mesh of its own.
            // A footprint that reaches into the vanilla bubble never paints
            // whole either: its children that returned false there did so for
            // loaded chunks, and painting over them put L1 mip pillars beside
            // the player in freshly captured land.
            bool touchesVanilla = AnyChildVanillaOwned(key, vanillaCoverageRadius);
            // PreferParent whole paint caps at L2 in the cone (LeadConeMaxCoverLevel).
            // L3+ never paints the whole footprint in front  -  that is cake plates.
            bool paintWhole = LodCoveragePolicy.MayPaintWholeAfterDescent(
                    anyChildDrew, drawableCoarse, forcedDetail, holdVisitedL0, touchesVanilla)
                && (!(horizonCone && level > LodCoveragePolicy.LeadConeMaxDrawLevel)
                    || (landLike && preferParent
                        && level <= LodCoveragePolicy.LeadConeMaxCoverLevel));
            if (!paintWhole)
            {
                if (hasMesh) lastSelectedFrame[key] = frameCounter;
                // This parent is not drawing whole. Children that stepped aside
                // for it (SkipDrawTooFine) are now uncovered land: submit them.
                // Per-child wanted differs with relief, so a flat L0 beside a
                // hilly sibling used to vanish right here as a sky rectangle.
                anyChildDrew |= FlushDeferredTooFine(deferredStart);
                if (gaps.Count > gapStart)
                {
                    if (LodCoveragePolicy.MayFillGapWithParent(level, hasMesh, insideVanilla,
                            gapTouchesVanilla: false))
                    {
                        anyChildDrew |= FillGaps(key, gapStart, liveViewDistance);
                    }
                    else if (!hasMesh)
                    {
                        // Nothing here to fill with: hand the gaps up, as one
                        // footprint when the whole subtree is missing. L2 in the
                        // cone must still mesh — flatten bumps wanted to L3 and
                        // the old cone skip never asked for this pad. L3+ cake
                        // plates stay unrequested; the ancestor clip-fills.
                        CoalesceGaps(key, gapStart);
                        if (!(horizonCone && level > LodCoveragePolicy.LeadConeMaxCoverLevel))
                            EnsureCoverMesh(key);
                    }
                }
                return anyChildDrew;
            }
            // Parent draws itself below; the deferred children stay hidden and
            // the gaps are inside the footprint it is about to paint whole.
            DiscardDeferredTooFine(deferredStart);
            DiscardGaps(gapStart);
        }

        if (hasMesh)
        {
            // Keep the GPU mesh while vanilla owns this column so walking away
            // does not remesh from scratch. Do not draw it on top of chunks.
            if (insideVanilla && level == 0)
            {
                lastSelectedFrame[key] = frameCounter;
                return false;
            }

            if (level == 0 && (world.IncompleteL0Keys.Contains(key) || world.SparseL0Keys.Contains(key)))
            {
                // Thin / incomplete L0 is a hole next to real hills when a
                // parent mesh can cover the footprint, including a flat ocean
                // plate. PreferParentCoverage keeps that parent until remesh.
                if (LodCoveragePolicy.DrawIncompleteL0(hasMesh, insideVanilla))
                {
                    long parentKey = LodWorld.ParentKey(key);
                    bool parentHasMesh = HasAnyMesh(parentKey);
                    if (LodCoveragePolicy.PreferParentCoverage(
                            parentHasMesh, AllChildrenCovered(parentKey)))
                    {
                        lastSelectedFrame[key] = frameCounter;
                        if (!insideVanilla) AddGap(key);
                        RequestMesh(key);
                        return false;
                    }
                    Submit(key);
                    return true;
                }
                lastSelectedFrame[key] = frameCounter;
                return false;
            }

            bool preferHere = level >= 1
                && LodCoveragePolicy.PreferParentCoverage(hasMesh, AllChildrenCovered(key));
            if (!LodCoveragePolicy.MayDrawCoarseParent(
                    level, insideVanilla, landLike, inLeadCone, lookDown01, nearDist, liveViewDistance,
                    preferHere))
            {
                // Refused as a whole plate (horizon / L2+ / non-land). Land-like
                // L1/L2 may still cover via MayLeadConeCoarseCover when children
                // are not ready. L3+ never whole-submits here  -  AddGap so an
                // ancestor can clip-fill. Silent refuse with no AddGap was the
                // giant sky vanish on yaw.
                lastSelectedFrame[key] = frameCounter;
                if (!insideVanilla
                    && landLike
                    && preferHere
                    && LodCoveragePolicy.MayLeadConeCoarseCover(
                        level, landLike, inLeadCone, lookDown01, nearDist, liveViewDistance,
                        preferHere)
                    && LodCoveragePolicy.MaySubmitCoarseWhole(
                        level, nearDist, vanillaCoverageRadius, insideVanilla))
                {
                    Submit(key);
                    return true;
                }
                if (!insideVanilla) AddGap(key);
                return false;
            }

            // Coarsen by wanted level even inside the keep-circle. Stamp the
            // finer mesh so eviction still treats it as live. If the parent
            // has no real mesh yet, keep drawing this one and request the parent.
            // A plate or L2+ parent in the lead cone must not hide children.
            if (level < wanted && !drawFullDetail)
            {
                lastSelectedFrame[key] = frameCounter;
                long parentKey = LodWorld.ParentKey(key);
                bool parentHasMesh = level < LodWorld.MaxLevel && HasAnyMesh(parentKey);
                world.Sections.TryGetValue(parentKey, out LodSection? parentSec);
                bool parentLandLike = ComputeLandLike(LodWorld.KeyLevel(parentKey), parentSec, parentKey);
                if (LodCoveragePolicy.SkipDrawTooFine(
                        level, wanted, drawFullDetail, parentHasMesh, parentLandLike, inLeadCone, lookDown01,
                        mustCover, nearDist, liveViewDistance))
                {
                    // Only valid if the parent really draws. The parent decides
                    // after all its children, so park this one until then.
                    tooFineDeferred.Add(key);
                    return false;
                }
                if (level < LodWorld.MaxLevel)
                    RequestCoarseFill(key, wanted);
            }

            // Open sides still draw. Hiding them behind a parent mesh is what
            // turned the frontier into giant flat rectangles.
            if (level == 0)
            {
                int open = CountOpenSides(key);
                if (open >= 1)
                {
                    RequestMissingNeighbourMeshes(key);
                    long parent = LodWorld.ParentKey(key);
                    if (world.HasDataSet.Contains(parent) && !HasAnyMesh(parent))
                        RequestMesh(parent);
                }
            }

            if (!LodCoveragePolicy.MaySubmitCoarseWhole(
                    level, nearDist, vanillaCoverageRadius, insideVanilla))
            {
                lastSelectedFrame[key] = frameCounter;
                if (!insideVanilla) AddGap(key);
                return false;
            }
            Submit(key);
            return true;
        }

        // No mesh here (L0 that is unmeshed, evicted, still loading or being
        // fetched). Not sky: the nearest ancestor with a mesh paints this footprint.
        if (!insideVanilla && hasData) AddGap(key);
        return false;
    }

    /// <summary>
    /// Record a captured footprint that nothing has drawn this frame. The first
    /// ancestor up the recursion that holds a mesh paints it (FillGaps).
    /// </summary>
    void AddGap(long key) => gaps.Add(key);

    void DiscardGaps(int start)
    {
        if (gaps.Count > start) gaps.RemoveRange(start, gaps.Count - start);
    }

    /// <summary>
    /// When every child of <paramref name="key"/> is a gap, replace the four
    /// with the parent footprint so the ancestor that fills them spends one
    /// clipped draw instead of four (or sixteen, a level further up).
    /// </summary>
    void CoalesceGaps(long key, int start)
    {
        if (gaps.Count - start != 4) return;
        for (int i = start; i < gaps.Count; i++)
        {
            if (LodWorld.ParentKey(gaps[i]) != key) return;
        }
        gaps.RemoveRange(start, 4);
        gaps.Add(key);
    }

    /// <summary>
    /// Paint <paramref name="key"/>'s mesh over every gap its subtree left since
    /// <paramref name="start"/>, each clipped to that gap's own rectangle in this
    /// section's local blocks. Gaps that reach into the vanilla bubble are
    /// loaded chunks and are dropped; when all four children are gaps the whole
    /// footprint is one draw. Returns whether anything was submitted.
    /// </summary>
    bool FillGaps(long key, int start, double vanillaCoverageRadius)
    {
        int footprint = LodWorld.KeyFootprintBlocks(key);
        double originX = LodWorld.KeySx(key) * (double)footprint;
        double originZ = LodWorld.KeySz(key) * (double)footprint;
        bool drew = false;

        int count = gaps.Count - start;
        bool wholeFootprint = false;
        if (count == 4)
        {
            wholeFootprint = true;
            for (int i = start; i < gaps.Count; i++)
            {
                if (LodWorld.ParentKey(gaps[i]) != key) { wholeFootprint = false; break; }
            }
        }

        world.Sections.TryGetValue(key, out LodSection? fillerBounds);

        float overdraw = GameMath.Clamp(OverdrawStart, 0.15f, 0.95f);
        double coverageRadius = liveViewDistance * overdraw;
        int fillerLevel = LodWorld.KeyLevel(key);
        bool fillerLandLike = ComputeLandLike(fillerLevel, fillerBounds, key);
        bool coneCoarse = LodCoveragePolicy.HorizonLeadCone(InLeadCone(key), lookDown01)
            && fillerLevel > LodCoveragePolicy.LeadConeMaxDrawLevel;
        // In the cone: land-like L2 may whole-fill; L3+ never (cake plates).
        // Flat / non-land plates stay clipped, never discarded to sky.
        // L3+ wholeFootprint falls through to clipped gap draws below.
        bool mayWhole = wholeFootprint
            && LodCoveragePolicy.MaySubmitCoarseWhole(
                fillerLevel, Math.Sqrt(NearestDistanceSqTo(key)), coverageRadius,
                vanillaOwns: false)
            && (!coneCoarse
                || (fillerLandLike && fillerLevel <= LodCoveragePolicy.LeadConeMaxCoverLevel));

        // Ocean/beach L2 plates in the cone used to DiscardGaps here, which
        // threw four L1 holes into the sky. Clip-fill instead: a flat water
        // square is land, empty sky is not.

        if (mayWhole)
        {
            if (gapDraws.Count < LodCoveragePolicy.MaxGapDrawsPerFrame
                && !GapInsideVanilla(key, fillerBounds, vanillaCoverageRadius))
            {
                gapDraws.Add(new GapDraw(key, 0, 0, footprint, footprint));
                drew = true;
            }
        }
        else
        {
            for (int i = start; i < gaps.Count; i++)
            {
                long gap = gaps[i];
                if (GapInsideVanilla(gap, fillerBounds, vanillaCoverageRadius)) continue;
                if (gapDraws.Count >= LodCoveragePolicy.MaxGapDrawsPerFrame)
                {
                    if (holeLog != null && frameCounter - lastUnfilledLogFrame >= UnfilledLogIntervalFrames)
                        holeLog($"gap budget capped at {LodCoveragePolicy.MaxGapDrawsPerFrame}; nearest gaps kept, rest wait a frame");
                    break;
                }
                int gapFootprint = LodWorld.KeyFootprintBlocks(gap);
                double gx = LodWorld.KeySx(gap) * (double)gapFootprint - originX;
                double gz = LodWorld.KeySz(gap) * (double)gapFootprint - originZ;
                gapDraws.Add(new GapDraw(key,
                    (float)gx, (float)gz, (float)(gx + gapFootprint), (float)(gz + gapFootprint)));
                drew = true;
            }
        }

        if (drew)
        {
            // Coverage in use: never evict this mesh while it is the only land here.
            drawnThisFrame.Add(key);
            lastSelectedFrame[key] = frameCounter;
        }
        gaps.RemoveRange(start, count);
        return drew;
    }

    /// <summary>
    /// Is this gap loaded chunks rather than sky? Same test as insideVanilla:
    /// whole AABB inside the skip disc AND every world column tessellated.
    /// A geometric circle alone punches sky when you raise VD before the
    /// columns arrive. Missing bounds: not vanilla, fill with LOD.
    /// </summary>
    bool GapInsideVanilla(long gap, LodSection? filler, double vanillaCoverageRadius)
    {
        LodSection? bounds = filler;
        if (world.Sections.TryGetValue(gap, out LodSection? own) && own.HasSurfaceBounds) bounds = own;
        if (bounds == null || !bounds.HasSurfaceBounds)
            return false;
        return VanillaOwnsKey(gap, bounds, vanillaCoverageRadius);
    }

    /// <summary>
    /// A leftover gap that already has a GPU mesh is land, not sky. The walk
    /// used to AddGap a meshed L1 and then fail to fill because the L2 above
    /// it was load-failed after a remip wipe.
    /// </summary>
    void SubmitMeshedGaps()
    {
        for (int i = gaps.Count - 1; i >= 0; i--)
        {
            long key = gaps[i];
            if (!HasDrawableMesh(key)) continue;
            world.Sections.TryGetValue(key, out LodSection? gapSec);
            if (VanillaOwnsKey(key, gapSec, VanillaCoverageRadius())) continue;
            int gapLevel = LodWorld.KeyLevel(key);
            if (gapLevel > LodCoveragePolicy.LeadConeMaxDrawLevel
                && LodCoveragePolicy.HorizonLeadCone(InLeadCone(key), lookDown01))
            {
                // Land-like L2 meshed gaps may submit; flat plates and L3+
                // whole plates stay skipped (clip-fill handles L3+ holes).
                if (gapLevel > LodCoveragePolicy.LeadConeMaxCoverLevel) continue;
                if (!ComputeLandLike(gapLevel, gapSec, key)) continue;
            }
            float overdraw = GameMath.Clamp(OverdrawStart, 0.15f, 0.95f);
            if (!LodCoveragePolicy.MaySubmitCoarseWhole(
                    LodWorld.KeyLevel(key), Math.Sqrt(NearestDistanceSqTo(key)),
                    liveViewDistance * overdraw, vanillaOwns: false))
                continue;
            Submit(key);
            gaps.RemoveAt(i);
        }
    }

    /// <summary>
    /// Gaps still listed after the roots return have no mesh at any rung: real
    /// holes. Count them, keep a few for the report, and say so every few
    /// hundred frames with each key's actual state so the cause is read off
    /// the log rather than guessed.
    /// </summary>
    void ReportUnfilledGaps()
    {
        LastUnfilledGaps = gaps.Count;
        if (gaps.Count == 0) return;

        unfilledSampleCount = Math.Min(unfilledSample.Length, gaps.Count);
        // Nearest first: the ones the player is most likely looking at.
        for (int i = 0; i < unfilledSampleCount; i++) unfilledSample[i] = gaps[i];
        for (int i = unfilledSampleCount; i < gaps.Count; i++)
        {
            long k = gaps[i];
            double d = NearestDistanceSqTo(k);
            int worst = -1;
            double worstD = -1;
            for (int j = 0; j < unfilledSampleCount; j++)
            {
                double dj = NearestDistanceSqTo(unfilledSample[j]);
                if (dj > worstD) { worstD = dj; worst = j; }
            }
            if (worst >= 0 && d < worstD) unfilledSample[worst] = k;
        }

        if (holeLog == null || frameCounter - lastUnfilledLogFrame < UnfilledLogIntervalFrames) return;
        lastUnfilledLogFrame = frameCounter;

        var sb = new System.Text.StringBuilder();
        sb.Append("Unfilled gaps this frame: ").Append(gaps.Count)
          .Append(" (no mesh at any rung above them). cam ")
          .Append((int)camPos.X).Append(',').Append((int)camPos.Y).Append(',').Append((int)camPos.Z)
          .Append(" lookDown ").Append(lookDown01.ToString("0.00"))
          .Append(" renderDirty ").Append(world.RenderDirty.Count)
          .Append(" meshJobs ").Append(meshJobInFlight.Count)
          .Append(" loadsInFlight ").Append(world.LoadsInFlight.Count)
          .Append(" loadFailed ").Append(world.LoadFailed.Count)
          .Append(" mipDirty ").Append(world.MipDirty.Count)
          .Append(" gapDraws ").Append(gapDraws.Count);
        for (int i = 0; i < unfilledSampleCount; i++)
        {
            long k = unfilledSample[i];
            sb.Append("\n  ").Append(DescribeKeyState(k));
            for (long a = k; LodWorld.KeyLevel(a) < LodWorld.MaxLevel;)
            {
                a = LodWorld.ParentKey(a);
                sb.Append(" <- ").Append(DescribeKeyState(a));
                if (LodWorld.KeyLevel(a) >= 3) break;
            }
        }
        holeLog(sb.ToString());
    }

    string DescribeKeyState(long key)
    {
        int level = LodWorld.KeyLevel(key);
        int footprint = LodWorld.KeyFootprintBlocks(key);
        string state;
        if (!world.HasDataSet.Contains(key)) state = "no-data";
        else if (world.Sections.TryGetValue(key, out LodSection? s))
        {
            state = s.CapturedColumns == 0 ? "resident-empty"
                : HasAnyMesh(key) ? "meshed"
                : meshJobInFlight.Contains(key) ? "meshing"
                : world.RenderDirty.Contains(key) ? "queued"
                : "resident-unrequested";
            if (level == 0 && world.IncompleteL0Keys.Contains(key)) state += "-incomplete";
        }
        else
        {
            state = world.LoadsInFlight.Contains(key) ? "loading"
                : world.LoadFailed.Contains(key) ? "load-failed"
                : world.RenderDirty.Contains(key) ? "queued-not-resident"
                : "not-resident";
        }
        return $"L{level}@{LodWorld.KeySx(key) * footprint},{LodWorld.KeySz(key) * footprint}"
             + $" d{(int)Math.Sqrt(NearestDistanceSqTo(key))} {state}";
    }

    /// <summary>
    /// Submit the children parked since <paramref name="start"/>: their parent
    /// yielded to a sibling and will not draw, so each of them is otherwise a
    /// hole. Entries were already screened for vanilla ownership and
    /// completeness before they reached SkipDrawTooFine.
    /// </summary>
    bool FlushDeferredTooFine(int start)
    {
        bool drew = false;
        for (int i = start; i < tooFineDeferred.Count; i++)
        {
            long key = tooFineDeferred[i];
            if (!HasAnyMesh(key)) continue;
            Submit(key);
            drew = true;
        }
        tooFineDeferred.RemoveRange(start, tooFineDeferred.Count - start);
        return drew;
    }

    void DiscardDeferredTooFine(int start)
    {
        if (tooFineDeferred.Count > start)
            tooFineDeferred.RemoveRange(start, tooFineDeferred.Count - start);
    }

    /// <summary>
    /// A parent whose children left gaps has no mesh of its own: get one so
    /// the next frames can fill. Request it when its section has columns (or
    /// is on disk); otherwise mip it from its captured children. Shares the
    /// coarse-request budget with RequestCoarseFill; only a request that is
    /// new this frame is charged, so a target that is already queued cannot
    /// eat the budget every frame while the rest of the map waits.
    /// </summary>
    void EnsureCoverMesh(long key)
    {
        int level = LodWorld.KeyLevel(key);
        if (level < 1) return;
        if (HasAnyMesh(key) || meshJobInFlight.Contains(key)) return;
        if (world.RenderDirty.Contains(key))
        {
            walkRequested.Add(key);
            return;
        }
        if (coarseParentRequestsThisFrame >= CoarseParentRequestsPerFrame) return;

        if (world.Sections.TryGetValue(key, out LodSection? section))
        {
            if (section.CapturedColumns > 0)
            {
                coarseParentRequestsThisFrame++;
                RequestMesh(key, allowWhileStarving: true);
                return;
            }
        }
        else if (world.HasDataSet.Contains(key) && !world.LoadFailed.Contains(key))
        {
            coarseParentRequestsThisFrame++;
            RequestMesh(key, allowWhileStarving: true);
            return;
        }

        if (QueueMipFromChildren(key)) coarseParentRequestsThisFrame++;
    }

    /// <summary>
    /// Captured land past this tile along the camera ray, probed at L3
    /// (512-block) granularity one and two steps beyond its far edge. The
    /// radial farthest-mesh test alone made a frontier tile on the west count
    /// as interior because land was loaded far to the east - which is how a
    /// plate would end up as a shelf on the unexplored edge.
    /// </summary>
    bool CapturedBeyond(long key)
    {
        if (capturedBeyondMemo.TryGetValue(key, out bool cached)) return cached;
        int footprint = LodWorld.KeyFootprintBlocks(key);
        double cx = LodWorld.KeySx(key) * (double)footprint + footprint / 2.0;
        double cz = LodWorld.KeySz(key) * (double)footprint + footprint / 2.0;
        double dx = cx - camPos.X;
        double dz = cz - camPos.Z;
        double len = Math.Sqrt(dx * dx + dz * dz);
        bool result = false;
        if (len >= 1)
        {
            dx /= len;
            dz /= len;

            const int probeLevel = 3;
            int probe = LodSection.SectionBlocks << probeLevel;
            double start = footprint / 2.0;
            for (int step = 1; step <= 2 && !result; step++)
            {
                double px = cx + dx * (start + probe * step);
                double pz = cz + dz * (start + probe * step);
                if (px < 0 || pz < 0) continue;
                long pk = LodWorld.SectionKey(probeLevel,
                    (int)Math.Floor(px / probe), (int)Math.Floor(pz / probe));
                if (world.HasDataSet.Contains(pk)) result = true;
            }
        }
        capturedBeyondMemo[key] = result;
        return result;
    }

    int CountOpenSides(long key)
    {
        int n = 0;
        if (!HasNeighbourData(key, -1, 0)) n++;
        if (!HasNeighbourData(key, 1, 0)) n++;
        if (!HasNeighbourData(key, 0, -1)) n++;
        if (!HasNeighbourData(key, 0, 1)) n++;
        return n;
    }

    void RequestMissingNeighbourMeshes(long key)
    {
        TryRequestNeighbourMesh(key, -1, 0);
        TryRequestNeighbourMesh(key, 1, 0);
        TryRequestNeighbourMesh(key, 0, -1);
        TryRequestNeighbourMesh(key, 0, 1);
    }

    void TryRequestNeighbourMesh(long key, int dx, int dz)
    {
        long nk = LodWorld.NeighborKey(key, dx, dz);
        if (world.HasDataSet.Contains(nk) && !HasAnyMesh(nk))
            RequestMesh(nk);
    }

    /// <summary>
    /// Refresh live tints and snow line. Seasonal maps are 2D; the engine picks the ROW
    /// from a hash of each block position, so one sample painted the whole far landscape
    /// with a single winter/summer texel. Average a lattice and publish the table only
    /// when every slot is done.
    /// </summary>
    void RefreshSeasonalState()
    {
        long now = Environment.TickCount64;
        if (!seasonalRefreshActive)
        {
            int ox = (int)lastKeepOriginX;
            int oz = (int)lastKeepOriginZ;
            int dx = ox - lastClimateSampleX;
            int dz = oz - lastClimateSampleZ;
            bool originMoved = !seasonalStateInitialized
                || dx * dx + dz * dz >= ClimateResampleBlocks * ClimateResampleBlocks;
            if (seasonalStateInitialized
                && !originMoved
                && now - lastSeasonRefreshMs < SeasonalRefreshIntervalMs) return;

            seasonalRefreshActive = true;
            seasonalRefreshSlot = 1;
            seasonalRefreshX = ox;
            seasonalRefreshZ = oz;
            tints.BeginRefresh(capi.World);
            pendingSnowLineY = CalculateSnowLine(seasonalRefreshX, seasonalRefreshZ);
        }

        if (seasonalRefreshSlot < tints.SlotCount)
        {
            tints.RefreshSlot(capi.World, seasonalRefreshX, seasonalRefreshZ,
                seasonalRefreshSlot++);
            return;
        }

        tints.CompleteRefresh();
        snowLineY = pendingSnowLineY;
        seasonalRefreshActive = false;
        seasonalStateInitialized = true;
        lastSeasonRefreshMs = now;
        lastClimateSampleX = seasonalRefreshX;
        lastClimateSampleZ = seasonalRefreshZ;
        CaptureKeepClimate(seasonalRefreshX, seasonalRefreshZ);
    }

    float CalculateSnowLine(int px, int pz)
    {
        try
        {
            int seaLevel = capi.World.SeaLevel;
            climatePos.Set(px, seaLevel, pz);
            ClimateCondition? low = capi.World.BlockAccessor.GetClimateAt(
                climatePos, EnumGetClimateMode.ForSuppliedDate_TemperatureOnly, capi.World.Calendar.TotalDays);
            climatePos.Set(px, seaLevel + 150, pz);
            ClimateCondition? high = capi.World.BlockAccessor.GetClimateAt(
                climatePos, EnumGetClimateMode.ForSuppliedDate_TemperatureOnly, capi.World.Calendar.TotalDays);
            if (low == null || high == null) return LodSnow.Disabled;
            return LodSnow.OverlayY(seaLevel, low.Temperature, high.Temperature);
        }
        catch
        {
            return LodSnow.Disabled;
        }
    }


    /// <summary>Demand-driven (re)meshing: the selection walk is the load queue (Voxy's idea, CPU-side).</summary>
    void RequestMesh(long key, bool allowWhileStarving = false)
    {
        if (StarveMeshRequestsThisFrame && !allowWhileStarving)
            return;
        if (YieldToCompanion(key))
            return;
        if (ShouldDeferMeshRequest(key))
            return;
        if (meshJobInFlight.Contains(key)) return;
        if (StepBusyThisFrame
            && HasAnyMesh(key)
            && world.RenderDirty.Count >= LodFrameBudget.WalkRenderDirtyCap)
            return;

        // RAM-evicted sections still count: HasDataSet says whether the subtree has
        // data at all; the scheduler reloads the row from disk when it picks the job.
        if (world.Sections.TryGetValue(key, out LodSection? section))
        {
            if (section.CapturedColumns == 0) return;
        }
        else if (!world.HasDataSet.Contains(key))
        {
            return;
        }
        else if (world.LoadFailed.Contains(key))
        {
            // HasDataSet holds every ancestor of a captured tile, row or no row.
            // A parent whose mip has not been built yet loads as "missing" and
            // used to sit in RenderDirty anyway, where it won a nearest-first
            // schedule slot every frame and failed it - dozens of those pushed
            // the real work out of the candidate buffer. Ask the mip pipeline
            // for the row instead; the walk asks again once it exists.
            if (LodWorld.KeyLevel(key) >= 1) QueueMipFromChildren(key);
            return;
        }

        world.RenderDirty.Add(key);
        walkRequested.Add(key);

        // Start reload / server-assist wanted-by-view now, not only when a mesh slot
        // opens. Otherwise remote-only keys sit pending while the worker idles.
        if (!world.Sections.ContainsKey(key))
            world.TryGetForRender(key, out _);
    }

    void UpdateFrameBusySignals(float deltaTime)
    {
        LastFrameMs = deltaTime * 1000f;
        float pitch = MathF.Asin(GameMath.Clamp(lookY, -1f, 1f));

        if (lookSampled && LodFrameBudget.LookMoved(lastLookYaw, lastLookPitch, lookYaw, pitch))
            lookHoldLeft = LodFrameBudget.LookHoldFrames;
        else if (lookHoldLeft > 0)
            lookHoldLeft--;
        lastLookYaw = lookYaw;
        lastLookPitch = pitch;
        lookSampled = true;
        LookBusyThisFrame = lookHoldLeft > 0;

        if (hitchWarmupFrames < 3)
        {
            hitchWarmupFrames++;
            HitchThisFrame = false;
            hitchHoldLeft = 0;
        }
        else if (LodFrameBudget.FrameIsHitch(LastFrameMs))
        {
            hitchHoldLeft = LodFrameBudget.HitchHoldFrames;
            HitchThisFrame = true;
        }
        else if (hitchHoldLeft > 0)
        {
            hitchHoldLeft--;
            HitchThisFrame = hitchHoldLeft > 0;
        }
        else
        {
            HitchThisFrame = false;
        }

        if (camSampled)
        {
            double dx = camPos.X - lastCamX;
            double dz = camPos.Z - lastCamZ;
            if (dx * dx + dz * dz >= LodFrameBudget.StepBlocksSq)
                stepHoldLeft = LodFrameBudget.StepHoldFrames;
            else if (stepHoldLeft > 0)
                stepHoldLeft--;
        }
        lastCamX = camPos.X;
        lastCamZ = camPos.Z;
        camSampled = true;
        StepBusyThisFrame = stepHoldLeft > 0;

        StarveCatchUpThisFrame = LodFrameBudget.StarveCatchUp(
            LookBusyThisFrame, HitchThisFrame, StepBusyThisFrame);
        StarveMeshRequestsThisFrame = LodFrameBudget.StarveMeshRequests(
            LookBusyThisFrame, HitchThisFrame, StepBusyThisFrame);
    }

    void UpdateKeepOrigin()
    {
        if (!keepOriginValid)
        {
            lastKeepOriginX = camPos.X;
            lastKeepOriginZ = camPos.Z;
            keepOriginValid = true;
            windowMovedThisFrame = false;
            return;
        }

        if (LodCoveragePolicy.OriginShifted(lastKeepOriginX, lastKeepOriginZ, camPos.X, camPos.Z))
        {
            lastKeepOriginX = camPos.X;
            lastKeepOriginZ = camPos.Z;
            windowMovedThisFrame = true;
            climateUploadValid = false;
            ClearSelectionMemos();
            selectionMemosValid = false;
            int maxDist = (int)(liveViewDistance * LodCoveragePolicy.KeepCircleScale * 3.0);
            if (maxDist < 2048) maxDist = 2048;
            if (climateField.Count > LodClimateField.MaxCells)
                climateField.EvictFar((int)camPos.X, (int)camPos.Z, maxDist);
        }
        else
        {
            windowMovedThisFrame = false;
        }
    }

    // No mesh-count cap. 0.7.21's 6000/8000 residency dump dropped visited L0
    // onto parent tiles the moment the map had filled enough, which is the
    // "it stops and land disappears" bug. RAM still spills to disk; GPU meshes
    // of land you can see, or just left, stay.
    bool InJustLeftRing(long key)
    {
        double dist = Math.Sqrt(NearestDistanceSqTo(key));
        return LodCoveragePolicy.IsNearVisitedTrail(dist, liveViewDistance);
    }

    bool OutsidePressureKeepRing(long key)
    {
        double dist = Math.Sqrt(NearestDistanceSqTo(key));
        return dist >= liveViewDistance * LodMemoryBudget.PressureKeepScale;
    }

    void UpdateMeshPressure(float deltaTime)
    {
        double ms = deltaTime > 0 && deltaTime < 2.0 ? deltaTime * 1000.0 : pressureAvgFrameMs;
        if (ms < 1.0) ms = 1.0;
        frameMsSamples[frameSampleAt] = ms;
        frameSampleAt = (frameSampleAt + 1) % FrameSampleCount;
        if (frameSampleFilled < FrameSampleCount) frameSampleFilled++;

        double sum = 0;
        int n = frameSampleFilled;
        for (int i = 0; i < n; i++) sum += frameMsSamples[i];
        pressureAvgFrameMs = n == 0 ? ms : sum / n;

        // Cheap p95: copy + partial sort of the small ring.
        double p95 = pressureAvgFrameMs;
        if (n >= 8)
        {
            Span<double> tmp = stackalloc double[FrameSampleCount];
            for (int i = 0; i < n; i++) tmp[i] = frameMsSamples[i];
            tmp.Slice(0, n).Sort();
            int idx = (int)((n - 1) * 0.95);
            if (idx < 0) idx = 0;
            if (idx >= n) idx = n - 1;
            p95 = tmp[idx];
        }
        pressureP95FrameMs = p95;

        try { pressureManagedMb = GC.GetTotalMemory(false) / (1024 * 1024); }
        catch { /* keep prior */ }

        double dtMs = ms;
        if (MeshPressureActive)
            PressureActiveMsTotal += dtMs;

        // Raw enter: sustained bad frames OR memory/hitch. Soft mesh hint may reinforce
        // enter only when frame enter is already signalling — never mesh count alone.
        bool enterSignal = LodMemoryBudget.IsUnderPressure(
            pressureAvgFrameMs, pressureP95FrameMs, pressureManagedMb, sectionMeshes.Count);
        if (!enterSignal
            && LodMemoryBudget.IsFrameEnterSignal(pressureAvgFrameMs, pressureP95FrameMs)
            && sectionMeshes.Count > LodMemoryBudget.MaxResidentMeshes)
            enterSignal = true;

        bool clearSignal = LodMemoryBudget.IsFrameClearSignal(pressureAvgFrameMs, pressureP95FrameMs)
            && !LodMemoryBudget.IsMemoryPressure(pressureP95FrameMs, pressureManagedMb);

        if (!MeshPressureActive)
        {
            pressureClearAccumMs = 0;
            if (enterSignal)
            {
                pressureEnterAccumMs += dtMs;
                if (pressureEnterAccumMs >= LodMemoryBudget.PressureEnterSustainMs)
                {
                    MeshPressureActive = true;
                    PressureEnterCount++;
                    pressureEnterAccumMs = 0;
                    pressureClearAccumMs = 0;
                }
            }
            else
            {
                pressureEnterAccumMs = 0;
            }
        }
        else
        {
            pressureEnterAccumMs = 0;
            if (clearSignal)
            {
                pressureClearAccumMs += dtMs;
                if (pressureClearAccumMs >= LodMemoryBudget.PressureClearSustainMs)
                {
                    MeshPressureActive = false;
                    PressureClearCount++;
                    pressureClearAccumMs = 0;
                }
            }
            else
            {
                pressureClearAccumMs = 0;
            }
        }
    }

    void EvictStaleMeshes()
    {
        // Never drop meshes for count alone. Only when the player is actually hurting.
        if (!MeshPressureActive) return;

        // Cooldown so an eviction burst cannot keep walk/draw spiked every frame.
        if (frameCounter - lastPressureEvictFrame < EvictPressureCooldownFrames
            && evictCursor >= evictBatch.Count)
            return;

        bool drained = evictCursor >= evictBatch.Count;
        if (frameCounter - lastEvictScanFrame >= EvictScanInterval || (drained && evictBatchFull))
            ScanEvictionCandidates();

        int budget = EvictOldestPerFrameUnderPressure;

        while (budget > 0 && evictCursor < evictBatch.Count)
        {
            long key = evictBatch[evictCursor++];

            bool farseerFill = YieldToCompanion(key);

            // Re-check 2× ring: turning back toward land must keep it.
            if (!farseerFill && !OutsidePressureKeepRing(key))
            {
                EvictBlockedInside2xTotal++;
                continue;
            }
            if (!farseerFill && InJustLeftRing(key))
            {
                EvictBlockedInside2xTotal++;
                continue;
            }

            if (drawnThisFrame.Contains(key)) continue;

            if (lastSelectedFrame.TryGetValue(key, out long selected) && selected == frameCounter)
            {
                int lvl = LodWorld.KeyLevel(key);
                long parent = LodWorld.ParentKey(key);
                if (lvl < LodWorld.MaxLevel && !HasAnyMesh(parent))
                    continue;
                if (lvl <= 1 && lvl < LodWorld.MaxLevel)
                {
                    world.Sections.TryGetValue(parent, out LodSection? psec);
                    bool parentLandLike = ComputeLandLike(LodWorld.KeyLevel(parent), psec, parent);
                    bool parentPrefer = LodCoveragePolicy.PreferParentCoverage(
                        HasAnyMesh(parent), AllChildrenCovered(parent));
                    if (!LodCoveragePolicy.MayDrawCoarseParent(
                            LodWorld.KeyLevel(parent), false, parentLandLike,
                            InLeadCone(parent), lookDown01,
                            Math.Sqrt(NearestDistanceSqTo(parent)), liveViewDistance,
                            parentPrefer))
                        continue;
                }
            }
            if (LodWorld.KeyLevel(key) <= 1)
            {
                bool pinData = world.HasDataSet.Contains(key);
                double pinDist = Math.Sqrt(NearestDistanceSqTo(key));
                bool pinFarther = LodCoveragePolicy.IsFartherLoaded(
                    pinDist, LodWorld.KeyFootprintBlocks(key), FarthestKnownDistance);
                if (LodCoveragePolicy.MustCoverIntervening(
                        LodWorld.KeyLevel(key), pinData, InLeadCone(key), pinFarther,
                        pinDist, liveViewDistance))
                    continue;
            }

            bool freed = false;
            if (sectionMeshes.Remove(key, out MeshRef? mesh)) { mesh.Dispose(); freed = true; }
            if (waterMeshes.Remove(key, out MeshRef? water)) { water.Dispose(); freed = true; }

            if (!freed) continue;

            lastSelectedFrame.Remove(key);
            meshBornFrame.Remove(key);
            EvictedTotal++;
            lastPressureEvictFrame = frameCounter;
            EvictedOutside2xTotal++;
            budget--;
        }
    }

    /// <summary>
    /// Oldest-first L0/L1 outside 2× view distance. Disk cache stays; only GPU meshes go.
    /// </summary>
    void ScanEvictionCandidates()
    {
        lastEvictScanFrame = frameCounter;
        evictCursor = 0;

        LodCoveragePolicy.KeepCircleScale = LodMemoryBudget.LiveKeepScale(sectionMeshes.Count);

        int want = EvictOldestPerFrameUnderPressure * EvictScanInterval;

        evictBatch.Clear();
        evictBorn.Clear();
        foreach (long key in sectionMeshes.Keys) ConsiderForEviction(key, want);
        foreach (long key in waterMeshes.Keys)
        {
            if (sectionMeshes.ContainsKey(key)) continue;
            ConsiderForEviction(key, want);
        }

        evictBatchFull = evictBatch.Count >= want;
    }

    /// <summary>
    /// Pressure candidates: expensive L0/L1 outside the 2× keep ring, oldest first.
    /// Fail toward keeping meshes when unsure.
    /// </summary>
    void ConsiderForEviction(long key, int want)
    {
        bool farseerFill = YieldToCompanion(key);
        if (!farseerFill)
        {
            if (LodWorld.KeyLevel(key) > 1) return;
            if (!OutsidePressureKeepRing(key)) return;
            if (InJustLeftRing(key)) return;
            bool pinData = world.HasDataSet.Contains(key);
            double pinDist = Math.Sqrt(NearestDistanceSqTo(key));
            bool pinFarther = LodCoveragePolicy.IsFartherLoaded(
                pinDist, LodWorld.KeyFootprintBlocks(key), FarthestKnownDistance);
            if (LodCoveragePolicy.MustCoverIntervening(
                    LodWorld.KeyLevel(key), pinData, InLeadCone(key), pinFarther,
                    pinDist, liveViewDistance))
                return;
            long parent = LodWorld.ParentKey(key);
            if (LodWorld.KeyLevel(key) < LodWorld.MaxLevel && !HasAnyMesh(parent))
                return;
        }

        long born = meshBornFrame.TryGetValue(key, out long b) ? b : 0;

        if (evictBatch.Count >= want && born >= evictBorn[evictBorn.Count - 1]) return;

        int at = evictBorn.Count;
        evictBatch.Add(key);
        evictBorn.Add(born);
        while (at > 0 && evictBorn[at - 1] > born)
        {
            evictBorn[at] = evictBorn[at - 1];
            evictBatch[at] = evictBatch[at - 1];
            at--;
        }
        evictBorn[at] = born;
        evictBatch[at] = key;

        if (evictBatch.Count > want)
        {
            evictBatch.RemoveAt(evictBatch.Count - 1);
            evictBorn.RemoveAt(evictBorn.Count - 1);
        }
    }

    // ---- Mesh job scheduling + result upload ----

    readonly List<long> dirtyPrune = new();

    /// <summary>
    /// Drop meaningless render-dirty entries: no live mesh AND finer than the level
    /// the walk wants there - meshing those wastes work. Entries at wanted level or
    /// COARSER must survive: they are draw targets or gate meshes the walk descends
    /// through (pruning gates stalls descent and freezes approached terrain at the
    /// coarse level it was first meshed at). Runs every frame regardless of worker
    /// backlog - pruning must never starve.
    /// </summary>
    void PruneRenderDirty()
    {
        if (world.RenderDirty.Count == 0)
        {
            walkRequested.Clear();
            pruneCursor = 0;
            return;
        }

        // Look-only: pruning walks every dirty key with a sqrt; spread it out so
        // a small yaw does not stack prune + walk + schedule in one frame.
        // Walking must prune or remesh keys flood past WalkRenderDirtyCap.
        if (!windowMovedThisFrame
            && !StepBusyThisFrame
            && world.RenderDirty.Count < 8000
            && frameCounter % PruneIdleIntervalFrames != 0)
        {
            walkRequested.Clear();
            return;
        }

        bool budgeted = windowMovedThisFrame || StepBusyThisFrame;
        int keyBudget = budgeted ? LodFrameBudget.PruneWalkKeyBudget : int.MaxValue;

        dirtyPrune.Clear();
        int n = world.RenderDirty.Count;
        if (pruneCursor >= n) pruneCursor = 0;
        int examined = 0;
        int idx = 0;
        foreach (long key in world.RenderDirty)
        {
            if (budgeted)
            {
                if (idx++ < pruneCursor) continue;
                if (examined >= keyBudget) break;
            }
            examined++;

            if (YieldToCompanion(key))
            {
                dirtyPrune.Add(key);
                continue;
            }
            int dirtyLevel = LodWorld.KeyLevel(key);
            double distSq = NearestDistanceSqTo(key);
            double pruneDist = Math.Sqrt(distSq);
            float overdrawP = GameMath.Clamp(OverdrawStart, 0.15f, 0.95f);
            bool handoffJob = InHandoffRing(pruneDist, liveViewDistance * overdrawP);
            if (!HasAnyMesh(key) && dirtyLevel < LodWorld.WantedLevelForSq(distSq))
            {
                // The walk asked for this last frame (lead cone, intervening
                // span, handoff). It is a draw target regardless of the wanted
                // rung; pruning it here was the frame-to-frame request churn
                // that kept an in-view L0 from ever reaching the mesher.
                if (walkRequested.Contains(key)) continue;
                // Visited near tiles may sit behind the camera or past the wanted rung
                // while the player flies; dropping their mesh jobs leaves a trail of sky.
                if (LodCoveragePolicy.ShouldKeepVisitedDraw(
                        dirtyLevel, world.HasDataSet.Contains(key), pruneDist, liveViewDistance))
                    continue;
                if (dirtyLevel == 0 && world.IncompleteL0Keys.Contains(key)) continue;
                if (handoffJob && dirtyLevel <= 1) continue;
                if (LodCoveragePolicy.IsNearVisitedTrail(pruneDist, liveViewDistance)
                    && dirtyLevel <= 1 && world.HasDataSet.Contains(key)) continue;
                dirtyPrune.Add(key);
            }
        }
        if (budgeted)
        {
            pruneCursor += examined;
            if (pruneCursor >= n) pruneCursor = 0;
        }
        else
        {
            pruneCursor = 0;
        }
        foreach (long key in dirtyPrune) world.RenderDirty.Remove(key);
        walkRequested.Clear();
    }

    readonly long[] scheduleCandidates = new long[MeshSchedulesPerFrame + IncompleteFillPerTick + MeshLoadRequestsPerFrame];
    readonly double[] scheduleCandidateDistSq = new double[MeshSchedulesPerFrame + IncompleteFillPerTick + MeshLoadRequestsPerFrame];
    int lastKeepOverlayCount;
    /// <summary>Rotating cursor so huge RenderDirty sets do not full-scan every frame.</summary>
    int scheduleDirtyCursor;
    readonly long[] farthestKeepFound = new long[VisitedKeepSchedulesPerFrame];
    readonly double[] farthestKeepDist = new double[VisitedKeepSchedulesPerFrame];

    // QuadTreeMover-style window: origin only moves when XZ travels one L0 tile.
    // Looking around is not a move. Standing still keeps every GPU mesh.
    double lastKeepOriginX;
    double lastKeepOriginZ;
    bool keepOriginValid;
    bool windowMovedThisFrame;

    MeshData? uploadScratch;

    /// <summary>
    /// The nearest dirty keys that can start work now, nearest first, at most as many as
    /// one frame could possibly use. Returns how many were found.
    ///
    /// A fixed insertion buffer rather than a sort: the buffer holds 36 and the dirty set
    /// can hold thousands, so sorting the set to take its head would be the same mistake
    /// in a different shape. Squared distances, because the order is all that is wanted
    /// from them and the square root does not change it.
    /// </summary>
    int SelectNearestDirty()
    {
        int count = 0;
        int capacity = scheduleCandidates.Length;
        int dirtyN = world.RenderDirty.Count;
        bool budgeted = dirtyN > LodFrameBudget.SelectDirtyFullScanCap;
        int examineBudget = budgeted ? LodFrameBudget.SelectDirtyExamineBudget : dirtyN;
        int examined = 0;
        int skip = budgeted && dirtyN > 0 ? scheduleDirtyCursor % dirtyN : 0;
        int idx = 0;
        double nearKeepSq = liveViewDistance * liveViewDistance * 6.25;

        foreach (long key in world.RenderDirty)
        {
            if (budgeted)
            {
                int ord = idx++;
                bool inWindow = ord >= skip && examined < examineBudget;
                if (!inWindow && ord < skip && examined < examineBudget
                    && ord + (dirtyN - skip) < examineBudget)
                    inWindow = true;
                if (!inWindow)
                {
                    double dQuick = NearestDistanceSqTo(key);
                    if (dQuick > nearKeepSq) continue;
                }
                examined++;
            }
            // Skip anything already being meshed or reloaded, so the per-frame budget
            // goes to sections that can actually start work now.
            if (meshJobInFlight.Contains(key) || world.LoadsInFlight.Contains(key)) continue;
            if (YieldToCompanion(key))
                continue;
            // Idle: already-meshed land waits for a tile of travel, except peek
            // cubes that have to remesh when the real chunk lands at spawn, and
            // season-forced remeshes (palette catch-up while standing still).
            if (!LodCoveragePolicy.ShouldRemeshWhileIdle(
                    windowMovedThisFrame, HasAnyMesh(key), IsProvisionalKey(key))
                && !world.SeasonForcedRemesh.Contains(key))
                continue;

            double distSq = NearestDistanceSqTo(key);
            int candLevel = LodWorld.KeyLevel(key);
            double candDist = Math.Sqrt(distSq);
            float overdrawS = GameMath.Clamp(OverdrawStart, 0.15f, 0.95f);
            bool seasonForceMesh = world.SeasonForcedRemesh.Contains(key) && HasAnyMesh(key);
            // SeasonForced remesh must beat visited-trail distSq=0 — otherwise fly-ahead
            // L0 churn keeps far LODs on summer VBOs until the player walks into them
            // (user repro: green far until approach, then whitish).
            if (seasonForceMesh)
                distSq = candLevel >= 1 ? 0 : 0.0001;
            else if (InHandoffRing(candDist, liveViewDistance * overdrawS)
                     && candLevel <= 1 && !HasAnyMesh(key))
                // Unmeshed second-band L0/L1 beat visited-trail remesh (distSq=0).
                distSq = 0.00005;
            else if (InHandoffRing(candDist, liveViewDistance * overdrawS) && candLevel <= 1)
                distSq *= 0.05;
            else if (LodCoveragePolicy.KeepVisitedSurface(candLevel, world.HasDataSet.Contains(key))
                     && !HasAnyMesh(key))
                distSq *= 0.08;
            else if (candLevel >= 2 && !HasAnyMesh(key))
                distSq *= 0.25;
            // Visited L0/L1 trail: mesh the near captured ring first so fly-ahead holes
            // close before coarse parents swap in behind the player.
            // Do not override SeasonForced or handoff upgrades.
            if (!seasonForceMesh
                && !(InHandoffRing(candDist, liveViewDistance * overdrawS)
                     && candLevel <= 1 && !HasAnyMesh(key))
                && LodCoveragePolicy.ShouldKeepVisitedDraw(
                    candLevel, world.HasDataSet.Contains(key), candDist, liveViewDistance))
                distSq = 0;
            if (count == capacity && distSq >= scheduleCandidateDistSq[count - 1]) continue;

            int at = count < capacity ? count++ : capacity - 1;
            while (at > 0 && scheduleCandidateDistSq[at - 1] > distSq)
            {
                scheduleCandidateDistSq[at] = scheduleCandidateDistSq[at - 1];
                scheduleCandidates[at] = scheduleCandidates[at - 1];
                at--;
            }
            scheduleCandidateDistSq[at] = distSq;
            scheduleCandidates[at] = key;
        }

        lastKeepOverlayCount = OverlayFarthestVisitedKeep(ref count);
        if (budgeted && dirtyN > 0)
            scheduleDirtyCursor = (skip + examineBudget) % dirtyN;
        return count;
    }

    /// <summary>
    /// Nearest-first never reached the start of a long walk. Put the farthest
    /// unmeshed visited L0/L1 at the end of the candidate list so the reserved
    /// keep budget can actually start them. Same verts, from disk if RAM spilled.
    /// </summary>
    int OverlayFarthestVisitedKeep(ref int count)
    {
        int take = VisitedKeepSchedulesPerFrame;
        long[] found = farthestKeepFound;
        double[] foundDist = farthestKeepDist;
        int n = 0;
        int dirtyN = world.RenderDirty.Count;
        bool budgeted = dirtyN > LodFrameBudget.SelectDirtyFullScanCap;
        int examined = 0;
        int examineBudget = budgeted ? LodFrameBudget.SelectDirtyExamineBudget : dirtyN;

        foreach (long key in world.RenderDirty)
        {
            if (budgeted && ++examined > examineBudget) break;
            if (meshJobInFlight.Contains(key) || world.LoadsInFlight.Contains(key)) continue;
            if (YieldToCompanion(key))
                continue;
            int level = LodWorld.KeyLevel(key);
            if (!LodCoveragePolicy.KeepVisitedSurface(level, world.HasDataSet.Contains(key))) continue;
            if (HasAnyMesh(key)) continue;

            double distSq = NearestDistanceSqTo(key);
            double dist = Math.Sqrt(distSq);
            if (!LodCoveragePolicy.IsNearVisitedTrail(dist, liveViewDistance)) continue;
            if (n == take && distSq <= foundDist[n - 1]) continue;
            int at = n < take ? n++ : take - 1;
            while (at > 0 && foundDist[at - 1] < distSq)
            {
                foundDist[at] = foundDist[at - 1];
                found[at] = found[at - 1];
                at--;
            }
            foundDist[at] = distSq;
            found[at] = key;
        }

        int placed = 0;
        for (int i = 0; i < n; i++)
        {
            bool already = false;
            for (int j = 0; j < count; j++)
            {
                if (scheduleCandidates[j] == found[i]) { already = true; break; }
            }
            if (already) continue;

            if (count < scheduleCandidates.Length)
            {
                scheduleCandidates[count] = found[i];
                scheduleCandidateDistSq[count] = foundDist[i];
                count++;
                placed++;
            }
            else
            {
                int slot = count - 1 - placed;
                if (slot < 0) break;
                scheduleCandidates[slot] = found[i];
                scheduleCandidateDistSq[slot] = foundDist[i];
                placed++;
            }
        }
        return placed;
    }

    void ScheduleMeshJobs()
    {
        if (world.RenderDirty.Count == 0) return;

        bool look = LookBusyThisFrame;
        bool hitch = HitchThisFrame;
        bool step = StepBusyThisFrame;
        int keepBudget = LodFrameBudget.KeepMeshStarts(look, hitch, step);
        int seasonBudget = LodFrameBudget.SeasonForcedStarts(
            look, hitch, step, SeasonForcedBurstSchedules);
        int fineBudget = LodFrameBudget.FineMeshStarts(
            look, hitch, step, MeshSchedulesPerFrame + IncompleteFillPerTick);

        // Hitch from our own uploads must not starve keep. Never 3× oversubscribe
        // for a SeasonForced dump while walking.
        int pendingCap = maxWorkerMeshBacklog;
        if (worker.PendingMeshes >= pendingCap)
            return;

        int room = Math.Max(0, pendingCap - worker.PendingMeshes);
        int loadBudget = MeshLoadRequestsPerFrame;

        keepBudget = Math.Min(keepBudget, room);
        int keepStarted = ScheduleKeepMeshes(ref keepBudget, ref loadBudget);
        room = Math.Max(0, room - keepStarted);

        seasonBudget = Math.Min(seasonBudget, room);
        int forcedStarted = ScheduleSeasonForcedRemeshes(ref seasonBudget, ref loadBudget);
        room = Math.Max(0, room - forcedStarted);

        if (fineBudget <= 0) return;
        fineBudget = Math.Min(fineBudget, room);
        int upgradeReserve = Math.Min(HandoffUpgradeSchedules, fineBudget);
        int upgradeStarted = ScheduleHandoffUpgrades(ref upgradeReserve, ref loadBudget);
        int meshBudget = Math.Max(0, fineBudget - upgradeStarted);

        int candidates = SelectNearestDirty();
        int keepOverlay = Math.Min(VisitedKeepSchedulesPerFrame, meshBudget);
        int nearBudget = meshBudget - keepOverlay;
        int keepBegin = Math.Max(0, candidates - lastKeepOverlayCount);

        for (int i = 0; i < keepBegin && (nearBudget > 0 || loadBudget > 0); i++)
            TryStartMeshJob(scheduleCandidates[i], ref nearBudget, ref loadBudget);

        int rest = keepOverlay + nearBudget;
        for (int i = keepBegin; i < candidates && (rest > 0 || loadBudget > 0); i++)
            TryStartMeshJob(scheduleCandidates[i], ref rest, ref loadBudget);
    }

    readonly List<long> keepMeshScratch = new();

    /// <summary>
    /// First mesh of unmeshed keep-circle coverage. L0/L1 inside 1.0× view;
    /// L2+ wanted-level parents inside the 2× keep-circle. Not L0 at 2×.
    /// </summary>
    int ScheduleKeepMeshes(ref int budget, ref int loadBudget)
    {
        if (budget <= 0) return 0;
        keepMeshScratch.Clear();
        foreach (long key in world.RenderDirty)
        {
            if (HasAnyMesh(key)) continue;
            if (meshJobInFlight.Contains(key) || world.LoadsInFlight.Contains(key)) continue;
            if (YieldToCompanion(key)) continue;
            int level = LodWorld.KeyLevel(key);
            double d = Math.Sqrt(NearestDistanceSqTo(key));
            bool inside1x = LodCoveragePolicy.IsDrawFullDetail(d, liveViewDistance);
            bool keepCircle = LodCoveragePolicy.IsNearVisitedTrail(d, liveViewDistance);
            if (level <= 1 && inside1x)
                keepMeshScratch.Add(key);
            else if (level >= 2 && keepCircle)
                keepMeshScratch.Add(key);
        }
        keepMeshScratch.Sort((a, b) =>
            NearestDistanceSqTo(a).CompareTo(NearestDistanceSqTo(b)));
        int started = 0;
        for (int i = 0; i < keepMeshScratch.Count && budget > 0; i++)
        {
            int before = budget;
            TryStartMeshJob(keepMeshScratch[i], ref budget, ref loadBudget);
            if (budget < before) started++;
        }
        return started;
    }

    /// <summary>
    /// Start unmeshed L0/L1 in the handoff ring so the second band follows the
    /// player and coarse L2 upgrades when you walk back from far white.
    /// </summary>
    int ScheduleHandoffUpgrades(ref int budget, ref int loadBudget)
    {
        if (budget <= 0) return 0;
        float overdraw = GameMath.Clamp(OverdrawStart, 0.15f, 0.95f);
        double vanillaR = liveViewDistance * overdraw;
        int started = 0;
        // Snapshot keys — TryStart may mutate RenderDirty.
        handoffUpgradeScratch.Clear();
        foreach (long key in world.RenderDirty)
        {
            if (LodWorld.KeyLevel(key) > 1) continue;
            if (HasAnyMesh(key)) continue;
            if (meshJobInFlight.Contains(key) || world.LoadsInFlight.Contains(key)) continue;
            double d = Math.Sqrt(NearestDistanceSqTo(key));
            if (!InHandoffRing(d, vanillaR)) continue;
            handoffUpgradeScratch.Add(key);
        }
        handoffUpgradeScratch.Sort((a, b) =>
            NearestDistanceSqTo(a).CompareTo(NearestDistanceSqTo(b)));
        for (int i = 0; i < handoffUpgradeScratch.Count && budget > 0; i++)
        {
            int before = budget;
            TryStartMeshJob(handoffUpgradeScratch[i], ref budget, ref loadBudget);
            if (budget < before) started++;
        }
        return started;
    }

    readonly List<long> handoffUpgradeScratch = new();

    /// <summary>
    /// SeasonForced keys (stale summer VBOs, winter palette ready). Nearest-first.
    /// Dedicated candidate buffer so SelectNearestDirty cannot shrink the pool.
    /// Unmeshed forced keys are started too — skipping them left them stuck forever.
    /// </summary>
    int ScheduleSeasonForcedRemeshes(ref int budget, ref int loadBudget)
    {
        if (budget <= 0 || world.SeasonForcedRemesh.Count == 0) return 0;

        int n = 0;
        int cap = forcedRemeshCandidates.Length;
        foreach (long key in world.SeasonForcedRemesh)
        {
            if (meshJobInFlight.Contains(key) || world.LoadsInFlight.Contains(key))
                continue;
            if (YieldToCompanion(key))
                continue;
            // Still schedule unmeshed — first mesh after palette catch-up.

            double distSq = NearestDistanceSqTo(key);
            int level = LodWorld.KeyLevel(key);
            if (level >= 1) distSq *= 0.01;

            if (n == cap && distSq >= forcedRemeshDistSq[n - 1]) continue;
            int at = n < cap ? n++ : cap - 1;
            while (at > 0 && forcedRemeshDistSq[at - 1] > distSq)
            {
                forcedRemeshDistSq[at] = forcedRemeshDistSq[at - 1];
                forcedRemeshCandidates[at] = forcedRemeshCandidates[at - 1];
                at--;
            }
            forcedRemeshDistSq[at] = distSq;
            forcedRemeshCandidates[at] = key;
        }

        int started = 0;
        for (int i = 0; i < n && budget > 0; i++)
        {
            int before = budget;
            if (TryStartMeshJob(forcedRemeshCandidates[i], ref budget, ref loadBudget) && budget < before)
                started++;
        }
        return started;
    }

    readonly long[] forcedRemeshCandidates = new long[SeasonForcedBurstSchedules + SeasonForcedPrioritySchedules];
    readonly double[] forcedRemeshDistSq = new double[SeasonForcedBurstSchedules + SeasonForcedPrioritySchedules];

    bool TryStartMeshJob(long best, ref int meshBudget, ref int loadBudget)
    {
        // Standing still: keep GPU meshes. Do not clone a snapshot or enqueue a job
        // for land that is already on screen. Capture-dirty keys stay in RenderDirty
        // until the origin actually moves. SeasonForcedRemesh is the catch-up bypass.
        bool seasonForce = world.SeasonForcedRemesh.Contains(best);
        if (!LodCoveragePolicy.ShouldRemeshWhileIdle(
                windowMovedThisFrame, HasAnyMesh(best), IsProvisionalKey(best))
            && !seasonForce)
            return false;

        // It was dirty and not in flight a moment ago, and nothing below touches any
        // key but the one it is working on. Remove says so anyway for the price of
        // the probe the old code was making regardless.
        if (!world.RenderDirty.Remove(best)) return false;
        if (seasonForce) world.SeasonForcedRemesh.Remove(best);

        // Non-blocking: an evicted section starts a background reload and is
        // re-requested by the selection walk once it lands, rather than stalling
        // this frame on a decompress.
        if (!world.TryGetForRender(best, out LodSection section))
        {
            // Spill-to-disk / in-flight reload: KEEP the GPU mesh. Disposing it
            // here was the unload-into-sky after a fill quota.
            if (world.LoadsInFlight.Contains(best) && loadBudget > 0)
                loadBudget--;
            if (seasonForce)
            {
                world.RenderDirty.Add(best);
                world.SeasonForcedRemesh.Add(best);
            }
            return false;
        }

        if (section.CapturedColumns == 0)
        {
            if (seasonForce)
            {
                world.RenderDirty.Add(best);
                world.SeasonForcedRemesh.Add(best);
            }
            return false;
        }

        if (meshBudget <= 0)
        {
            world.RenderDirty.Add(best);
            if (seasonForce) world.SeasonForcedRemesh.Add(best);
            return false;
        }

        MeshJob job = worker.RentMeshJob();
        SectionSnapshot?[] neighbors = job.Neighbors;
        for (int d = 0; d < 4; d++)
        {
            long nk = LodWorld.NeighborKey(best, d == 0 ? -1 : d == 1 ? 1 : 0, d == 2 ? -1 : d == 3 ? 1 : 0);
            if (world.Sections.TryGetValue(nk, out LodSection? nb)) neighbors[d] = SectionSnapshot.Of(nb);
        }

        meshBudget--;
        meshJobInFlight.Add(best);
        job.Key = best;
        job.Self = SectionSnapshot.Of(section);
        worker.EnqueueMesh(job);
        return true;
    }

    void UploadFinishedMeshes()
    {
        int budget = HitchThisFrame ? 1 : MeshUploadsPerFrame;
        long uploadStart = LodPhaseCost.Start();
        long budgetTicks = HitchThisFrame
            ? Math.Max(1, MeshUploadBudgetTicks / 2)
            : MeshUploadBudgetTicks;

        while (budget-- > 0 && worker.MeshResults.TryDequeue(out MeshResult? result))
        {
            meshJobInFlight.Remove(result.Key);

            if (sectionMeshes.Remove(result.Key, out MeshRef? old)) old.Dispose();
            if (waterMeshes.Remove(result.Key, out MeshRef? oldWater)) oldWater.Dispose();
            emptyMeshKeys.Remove(result.Key);
            if (result.IndexCount == 0 && result.WaterIndexCount == 0)
                emptyMeshKeys.Add(result.Key);

            if (result.IndexCount > 0)
            {
                sectionMeshes[result.Key] = Upload(result.Xyz, result.Rgba, result.Indices,
                    result.VertexCount, result.IndexCount);
                if (!meshBornFrame.ContainsKey(result.Key))
                    meshBornFrame[result.Key] = frameCounter;
            }

            if (result.WaterIndexCount > 0 && result.WaterXyz != null)
            {
                waterMeshes[result.Key] = Upload(result.WaterXyz, result.WaterRgba!, result.WaterIndices!,
                    result.WaterVertexCount, result.WaterIndexCount);
            }

            result.ReturnPooledBuffers();

            // Fresh uploads get a grace stamp so they aren't evicted before first selection.
            lastSelectedFrame[result.Key] = frameCounter;

            // Tested after an upload, never before, so every frame lands at least one and
            // the queue can never stall. Past the slice the remaining results stay queued
            // in order for the next frame; none are dropped.
            if (System.Diagnostics.Stopwatch.GetTimestamp() - uploadStart >= budgetTicks) break;
        }
    }

    MeshRef Upload(float[] xyz, byte[] rgba, int[] indices, int vertCount, int indexCount)
    {
        MeshData mesh = uploadScratch ??= new MeshData(false);
        mesh.SetVerticesCount(vertCount);
        mesh.SetIndicesCount(indexCount);
        mesh.xyz = xyz;
        mesh.Rgba = rgba;
        mesh.Indices = indices;
        MeshRef uploaded = capi.Render.UploadMesh(mesh);
        mesh.xyz = null!;
        mesh.Rgba = null!;
        mesh.Indices = null!;
        return uploaded;
    }

    // ---- Frame ----

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (frameCounter == 0)
        {
            LodMemoryBudget.Probe();
            LodCoveragePolicy.KeepCircleScale = LodMemoryBudget.KeepScale;
        }

        if (AutoUnpause && capi.IsGamePaused) capi.PauseGame(false);

        if (prog == null || !shaderOk || prog.LoadError) return;

        var rapi = capi.Render;
        if (rapi.FrameWidth == 0) return;

        camPos = capi.World.Player.Entity.CameraPos;
        Vec3f look = capi.World.Player.Entity.Pos.GetViewVector();
        lookY = look.Y;
        lookYaw = MathF.Atan2(look.X, look.Z);
        lookDown01 = LodCoveragePolicy.LookDownAmount(look.Y);
        UpdateFrameBusySignals(deltaTime);
        UpdateKeepOrigin();
        UpdateMeshPressure(deltaTime);
        HeightOcclusion.BeginFrame(camPos.X, camPos.Z, lookYaw);
        fineHorizonRequestsThisFrame = 0;
        frameCounter++;

        // Timed apart, not together. Lumped into one counter they cannot be told apart,
        // and they are different shapes: pruning walks the whole dirty set once a frame,
        // while scheduling picks a bounded number of jobs out of it. A spike in the pair
        // was being read as a spike in scheduling.
        long phaseStart = LodPhaseCost.Start();
        InvalidateSelectionMemosIfNeeded();
        neighbourDataMemo.Clear();
        setupCache.Clear();
        PruneRenderDirty();
        PruneCost.Add(phaseStart);

        // View distance / far distance first so CollectDrawNodes can RequestMesh from
        // cache before ScheduleMeshJobs runs (same-frame schedule of those requests).
        // Empty sectionMeshes is normal on first frames after join while the dirty
        // queue fills Ã¢â‚¬â€ do not early-return solely because meshes are empty.
        var playerData = capi.World.Player.WorldData;
        float viewDistance = playerData.DesiredViewDistance;
        if (playerData.LastApprovedViewDistance > 0)
        {
            viewDistance = Math.Min(viewDistance, playerData.LastApprovedViewDistance);
        }
        // Keep LOD ladder glued to the live graphics setting (e.g. 256 Ã¢â€ â€™ 1000).
        liveViewDistance = Math.Max(64f, viewDistance);
        if (selectionMemosValid
            && !float.IsNaN(memoLiveViewDistance)
            && Math.Abs(liveViewDistance - memoLiveViewDistance) > 0.5f)
        {
            ClearSelectionMemos();
            selectionMemosValid = false;
        }
        LodWorld.ViewDistanceAnchor = liveViewDistance;
        LodWorld.OverdrawStart = GameMath.Clamp(OverdrawStart, 0.15f, 0.95f);
        RefreshLoadedMapChunks();

        phaseStart = LodPhaseCost.Start();
        UpdateEffectiveFarDistance(viewDistance);
        FarDistanceCost.Add(phaseStart);
        ApplyZFar();

        worldHeight = capi.World.BlockAccessor.MapSizeY;
        frustum.Update(rapi.CurrentProjectionMatrix, rapi.CameraMatrixOriginf);

        phaseStart = LodPhaseCost.Start();
        drawList.Clear();
        LastCompanionYieldCount = 0;
        LastPressureYieldCount = 0;
        LastOccludedCount = 0;
        drawnThisFrame.Clear();
        tooFineDeferred.Clear();
        gaps.Clear();
        gapDraws.Clear();
        coarseParentRequestsThisFrame = 0;
        foreach (long top in world.TopLevelKeys) CollectDrawNodes(top);
        SubmitMeshedGaps();
        ReportUnfilledGaps();
        WalkCost.Add(phaseStart);
        LastDrawCount = drawList.Count;
        LastGapDrawCount = gapDraws.Count;

        phaseStart = LodPhaseCost.Start();
        ScheduleMeshJobs();
        ScheduleCost.Add(phaseStart);

        UploadFinishedMeshes();
        // Pressure-only: idle turning with a fat cache must not punch mid-land holes.
        if (MeshPressureActive)
            EvictStaleMeshes();
        RefreshSeasonalState();

        if (drawList.Count == 0) return;

        prog.Use();
        rapi.GlDisableCullFace();

        prog.UniformMatrix("viewMatrix", rapi.CameraMatrixOriginf);
        prog.UniformMatrix("projectionMatrix", rapi.CurrentProjectionMatrix);

        // Same matrices the shader gets, so the cull can never disagree with the draw.
        worldHeight = capi.World.BlockAccessor.MapSizeY;
        frustum.Update(rapi.CurrentProjectionMatrix, rapi.CameraMatrixOriginf);
        culledThisFrame = 0;

        prog.Uniform("sunPosition", capi.World.Calendar.SunPositionNormalized);
        prog.Uniform("sunColor", capi.World.Calendar.SunColor);
        prog.Uniform("dayLight", Math.Max(0, capi.World.Calendar.DayLightStrength));
        // Same ambient the chunk shaders use. SunColor is the disc color and stays
        // orange at dusk/night; this is the clock that actually darkens the ground.
        prog.Uniform("rgbaAmbientIn", capi.Ambient.BlendedAmbientColor);

        // Live season, same class of clock: the calendar, every frame, not a recapture.
        // Climate slots stay the keep-origin table. Vegetation shifts by the coarse
        // climate field at that vertex XZ so mountain leaves match mountain grass.
        climatePos.Set((int)lastKeepOriginX, capi.World.SeaLevel, (int)lastKeepOriginZ);
        float seasonRel = 0.5f;
        try
        {
            seasonRel = capi.World.Calendar.GetSeasonRel(climatePos);
            tints.RefreshSeason(capi.World, climatePos.X, climatePos.Z);
        }
        catch
        {
        }
        if (!keepClimateValid)
            CaptureKeepClimate(climatePos.X, climatePos.Z);
        prog.Uniform("seasonRel", seasonRel);
        prog.Uniform("keepClimateLow",
            keepClimate.LowR, keepClimate.LowG, keepClimate.LowB, keepClimate.LowTemp / 255f);
        prog.Uniform("keepClimateHigh",
            keepClimate.HighR, keepClimate.HighG, keepClimate.HighB, keepClimate.HighTemp / 255f);
        prog.Uniforms4("seasonTints", LodTintRegistry.MaxSlots, tints.SeasonTints);
        UploadFallbackSeasonTint();

        // Live ambient fog so the overdraw ring matches vanilla chunks in front.
        // DisableLodFog only skips extra pastViewHaze, not BlendedFog*.
        prog.Uniform("rgbaFogIn", capi.Ambient.BlendedFogColor);
        prog.Uniform("fogDensityIn", capi.Ambient.BlendedFogDensity * FogDensityScale);
        prog.Uniform("fogMinIn", capi.Ambient.BlendedFogMin);
        prog.Uniform("horizonFog", capi.Ambient.BlendedCloudDensity);

        prog.Uniform("viewDistance", viewDistance);
        prog.Uniform("overdrawStart", GameMath.Clamp(OverdrawStart, 0.15f, 0.95f));
        prog.Uniform("lookDown", LodCoveragePolicy.LookDownSteepAmount(lookDown01));
        prog.Uniform("farViewDistance", EffectiveFarDistance);
        prog.Uniform("skyFadeStart", SkyFadeStart);
        prog.Uniform("pastViewHaze", DisableLodFog ? 0f : PastViewHaze);
        prog.Uniform("disableLodFog", DisableLodFog ? 1f : 0f);

        // Uniforms persist in the program between Use() calls, so re-upload only when
        // the table actually changed (every ~240 frames) rather than every frame.
        if (uploadedTintVersion != tints.Version)
        {
            uploadedTintVersion = tints.Version;
            prog.Uniforms4("tintsLow", LodTintRegistry.MaxSlots, tints.TintsLow);
            prog.Uniforms4("tintsHigh", LodTintRegistry.MaxSlots, tints.TintsHigh);
            prog.Uniform("tintYLow", tints.SampleYLow);
            prog.Uniform("tintYHigh", tints.SampleYHigh);
        }
        prog.Uniform("snowLineY", snowLineY);

        float cullDistSq = float.MaxValue;
        if (FarViewDistanceCap > 0)
        {
            float cull = FarViewDistanceCap + LodSection.SectionBlocks;
            cullDistSq = cull * cull;
        }

        phaseStart = LodPhaseCost.Start();

        // Pass 1: opaque terrain.
        prog.Uniform("clipRect", NoClip[0], NoClip[1], NoClip[2], NoClip[3]);
        foreach (long key in drawList)
        {
            if (!sectionMeshes.TryGetValue(key, out MeshRef? mesh)) continue;
            if (!SetupSectionTransform(key, cullDistSq)) continue;
            capi.Render.RenderMesh(mesh);
        }

        LastCulledCount = culledThisFrame; // opaque pass only: water covers a subset

        // Pass 1b: gap fill. Each is a resident coarser mesh drawn only inside
        // one footprint its subtree left uncovered (clipRect in section-local
        // blocks); the fragment shader discards everything outside.
        DrawGaps(sectionMeshes, cullDistSq);

        // Pass 2: water, alpha-blended over the terrain.
        rapi.GlToggleBlend(true);
        prog.Uniform("clipRect", NoClip[0], NoClip[1], NoClip[2], NoClip[3]);
        foreach (long key in drawList)
        {
            if (!waterMeshes.TryGetValue(key, out MeshRef? mesh)) continue;
            if (!SetupSectionTransform(key, cullDistSq)) continue;
            capi.Render.RenderMesh(mesh);
        }
        DrawGaps(waterMeshes, cullDistSq);
        rapi.GlToggleBlend(false);

        // Submission only. RenderMesh queues work for the GPU and returns, so this
        // measures the CPU cost of the draw loop -- the uniform uploads, the culling and
        // the dictionary probes -- and not what the GPU then does with it.
        DrawCost.Add(phaseStart);

        rapi.GlEnableCullFace();
        prog.Stop();
    }

    /// <summary>
    /// The gap-fill pass for one mesh table (opaque or water): each entry is the
    /// parent's mesh, frustum-tested and clipped to the uncovered footprint.
    /// The clip is reset afterwards so the next pass draws whole sections.
    /// </summary>
    void DrawGaps(Dictionary<long, MeshRef> meshes, float cullDistSq)
    {
        if (gapDraws.Count == 0) return;
        foreach (GapDraw gap in gapDraws)
        {
            if (!meshes.TryGetValue(gap.Key, out MeshRef? mesh)) continue;
            if (!SetupSectionTransform(gap.Key, cullDistSq, gap.MinX, gap.MinZ, gap.MaxX, gap.MaxZ)) continue;
            prog!.Uniform("clipRect", gap.MinX, gap.MinZ, gap.MaxX, gap.MaxZ);
            capi.Render.RenderMesh(mesh);
        }
        prog!.Uniform("clipRect", NoClip[0], NoClip[1], NoClip[2], NoClip[3]);
    }

    bool SetupSectionTransform(long key, float cullDistSq)
    {
        int footprint = LodWorld.KeyFootprintBlocks(key);
        return SetupSectionTransform(key, cullDistSq, 0, 0, footprint, footprint);
    }

    /// <summary>
    /// Model matrix, per-section uniforms and frustum test for one draw. The
    /// rectangle (section-local blocks) is the part that will actually be
    /// visible: the whole footprint for a normal draw, one child footprint for
    /// a gap fill, so a gap behind the camera does not cost a draw call.
    /// </summary>
    bool SetupSectionTransform(long key, float cullDistSq,
        float rectMinX, float rectMinZ, float rectMaxX, float rectMaxZ)
    {
        int footprint = LodWorld.KeyFootprintBlocks(key);
        bool fullFootprint = rectMinX <= 0f && rectMinZ <= 0f
            && rectMaxX >= footprint && rectMaxZ >= footprint;

        // Opaque then water: reuse hoist + neighbour work for this key this frame.
        if (fullFootprint && setupCache.TryGetValue(key, out SectionSetupCache cached))
        {
            if (!cached.Ok) return false;
            double relX = cached.OriginX - camPos.X;
            double relZ = cached.OriginZ - camPos.Z;
            modelMat.Identity().Translate(relX, -camPos.Y, relZ);
            prog!.UniformMatrix("modelMatrix", modelMat.Values);
            prog.Uniform("columnBlocks", cached.ColumnBlocks);
            prog.Uniform("sectionSize", cached.Footprint);
            prog.Uniform("sectionOriginX", cached.OriginX);
            prog.Uniform("sectionOriginZ", cached.OriginZ);
            prog.Uniform("openEdges",
                cached.Open0, cached.Open1, cached.Open2, cached.Open3);
            UploadSectionClimate(key, (int)cached.OriginX, (int)cached.OriginZ, footprint);
            return true;
        }

        double originX = LodWorld.KeySx(key) * (double)footprint;
        double originZ = LodWorld.KeySz(key) * (double)footprint;
        double midX = originX + footprint * 0.5;
        double midZ = originZ + footprint * 0.5;
        double dx = midX - camPos.X;
        double dz = midZ - camPos.Z;
        double distSq = dx * dx + dz * dz;
        if (distSq > cullDistSq)
        {
            if (fullFootprint)
                setupCache[key] = new SectionSetupCache { Ok = false };
            return false;
        }

        double relX2 = originX - camPos.X;
        double relZ2 = originZ - camPos.Z;

        int level = LodWorld.KeyLevel(key);
        double drawDist = Math.Sqrt(distSq);
        bool keepVisited = LodCoveragePolicy.ShouldKeepVisitedDraw(
            level, world.HasDataSet.Contains(key), drawDist, liveViewDistance);
        if (!keepVisited)
        {
            // Tight Y when we know the surface band; a world-tall box false-rejects on
            // the top/bottom planes while the player is flying high beside the tile.
            double minY = -camPos.Y;
            double maxY = worldHeight - camPos.Y;
            if (world.Sections.TryGetValue(key, out LodSection? bounds) && bounds.HasSurfaceBounds)
            {
                const int pad = 48;
                minY = bounds.SurfaceYMin - pad - camPos.Y;
                maxY = bounds.SurfaceYMax + pad - camPos.Y;
            }

            if (!frustum.BoxInView(relX2 + rectMinX, minY, relZ2 + rectMinZ,
                    relX2 + rectMaxX, maxY, relZ2 + rectMaxZ))
            {
                culledThisFrame++;
                if (fullFootprint)
                    setupCache[key] = new SectionSetupCache { Ok = false };
                return false;
            }
        }

        float open0 = HasNeighbourData(key, -1, 0) ? 0f : 1f;
        float open1 = HasNeighbourData(key, 1, 0) ? 0f : 1f;
        float open2 = HasNeighbourData(key, 0, -1) ? 0f : 1f;
        float open3 = HasNeighbourData(key, 0, 1) ? 0f : 1f;
        float columnBlocks = (float)LodWorld.ColumnStepBlocks(level);

        modelMat.Identity().Translate(relX2, -camPos.Y, relZ2);
        prog!.UniformMatrix("modelMatrix", modelMat.Values);
        prog.Uniform("columnBlocks", columnBlocks);

        // Sides that border on never-captured area, so the shader can dissolve them
        // into the horizon instead of leaving a cliff at the edge of what we've seen.
        prog.Uniform("sectionSize", (float)footprint);
        prog.Uniform("sectionOriginX", (float)originX);
        prog.Uniform("sectionOriginZ", (float)originZ);
        prog.Uniform("openEdges", open0, open1, open2, open3);
        UploadSectionClimate(key, (int)originX, (int)originZ, footprint);

        if (fullFootprint)
        {
            setupCache[key] = new SectionSetupCache
            {
                Ok = true,
                Open0 = open0,
                Open1 = open1,
                Open2 = open2,
                Open3 = open3,
                OriginX = (float)originX,
                OriginZ = (float)originZ,
                Footprint = footprint,
                ColumnBlocks = columnBlocks
            };
        }
        return true;
    }

    void CaptureKeepClimate(int x, int z)
    {
        if (!TrySampleClimateCell(x, z, out LodClimateField.Sample sample)) return;
        keepClimate = sample;
        keepClimateValid = true;
        // Keep RGB changed → local/keep ratios change; force climate array refresh.
        climateUploadValid = false;
        lastSeasonTempX = sample.LowTemp;
        climateField.Put(x, z, sample);
    }

    void UploadSectionClimate(long sectionKey, int originX, int originZ, int footprint)
    {
        int step = LodClimateField.GridStep(footprint);
        int gx0 = LodClimateField.GridOrigin(originX, step);
        int gz0 = LodClimateField.GridOrigin(originZ, step);
        int n = LodClimateField.GridSize;
        // Opaque+water+gaps for one section: skip array refill. Always set origin/step
        // floats so a neighbour cannot inherit the wrong lattice after a cache hit.
        bool sameSection = climateUploadValid
            && climateUploadSectionKey == sectionKey
            && climateUploadGx0 == gx0
            && climateUploadGz0 == gz0
            && climateUploadStep == step;

        if (!sameSection)
        {
            int i = 0;
            for (int iz = 0; iz < n; iz++)
            {
                for (int ix = 0; ix < n; ix++)
                {
                    LodClimateField.Sample s = EnsureClimateCell(
                        gx0 + ix * step, gz0 + iz * step);
                    climateLowGrid[i] = s.LowR;
                    climateLowGrid[i + 1] = s.LowG;
                    climateLowGrid[i + 2] = s.LowB;
                    climateLowGrid[i + 3] = s.LowTemp / 255f;
                    climateHighGrid[i] = s.HighR;
                    climateHighGrid[i + 1] = s.HighG;
                    climateHighGrid[i + 2] = s.HighB;
                    climateHighGrid[i + 3] = s.HighTemp / 255f;
                    i += 4;
                }
            }
            prog!.Uniforms4("climateLow", n * n, climateLowGrid);
            prog.Uniforms4("climateHigh", n * n, climateHighGrid);
            climateUploadGx0 = gx0;
            climateUploadGz0 = gz0;
            climateUploadStep = step;
            climateUploadSectionKey = sectionKey;
            climateUploadValid = true;
        }

        prog!.Uniform("climateGridOriginX", (float)gx0);
        prog.Uniform("climateGridOriginZ", (float)gz0);
        prog.Uniform("climateGridStep", (float)step);
    }

    void UploadFallbackSeasonTint()
    {
        float r = 1f, g = 1f, b = 1f, a = 0f;
        Block? plant = tints.PlantTintFallback;
        if (plant != null)
        {
            int slot = tints.SlotFor(plant, LodUntintedShare.None);
            if ((uint)slot < LodTintRegistry.MaxSlots)
            {
                int i = slot * 4;
                float[] s = tints.SeasonTints;
                r = s[i];
                g = s[i + 1];
                b = s[i + 2];
                a = s[i + 3];
            }
        }
        prog!.Uniform("fallbackSeasonTint", r, g, b, a);
    }

    LodClimateField.Sample EnsureClimateCell(int x, int z)
    {
        if (climateField.TryGet(x, z, out LodClimateField.Sample existing))
            return existing;
        if (TrySampleClimateCell(x, z, out LodClimateField.Sample sample))
        {
            climateField.Put(x, z, sample);
            return sample;
        }
        return keepClimate;
    }

    bool TrySampleClimateCell(int x, int z, out LodClimateField.Sample sample)
    {
        sample = keepClimate;
        try
        {
            int sea = capi.World.SeaLevel;
            int yHigh = sea + LodTintRegistry.HighSampleOffsetBlocks;
            if (!TrySamplePlantTint(x, sea, z, out float lr, out float lg, out float lb, out float lt))
                return false;
            if (!TrySamplePlantTint(x, yHigh, z, out float hr, out float hg, out float hb, out float ht))
                return false;
            if (LodTintRegistry.IsSnowLikeTint(hr, hg, hb)
                && !LodTintRegistry.IsSnowLikeTint(lr, lg, lb))
            {
                hr = lr;
                hg = lg;
                hb = lb;
            }
            sample = new LodClimateField.Sample
            {
                LowR = lr, LowG = lg, LowB = lb, LowTemp = lt,
                HighR = hr, HighG = hg, HighB = hb, HighTemp = ht,
                Filled = true
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    bool TrySamplePlantTint(int x, int y, int z, out float r, out float g, out float b, out float temp)
    {
        r = g = b = 1f;
        temp = 128f;
        climatePos.Set(x, y, z);
        // Dated temp so climate-field heatmap tracks calendar + XZ (same authority as frost bake).
        ClimateCondition? cl = capi.World.BlockAccessor.GetClimateAt(
            climatePos,
            EnumGetClimateMode.ForSuppliedDate_TemperatureOnly,
            capi.World.Calendar.TotalDays);
        if (cl != null)
            temp = LodTintRegistry.UnscaledTempByteFromCelsius(cl.Temperature);

        int rgba;
        Block? plant = tints.PlantTintFallback;
        if (plant != null && plant.ClimateColorMapResolved != null)
        {
            rgba = capi.World.ApplyColorMapOnRgba(
                plant.ClimateColorMap, (string?)null,
                unchecked((int)0xFFFFFFFF), x, y, z);
        }
        else
        {
            rgba = capi.World.ApplyColorMapOnRgba(
                "climatePlantTint", (string?)null,
                unchecked((int)0xFFFFFFFF), x, y, z);
        }
        r = ((rgba >> 16) & 0xFF) / 255f;
        g = ((rgba >> 8) & 0xFF) / 255f;
        b = (rgba & 0xFF) / 255f;
        LodTintRegistry.ClampTintAwayFromWhite(ref r, ref g, ref b);
        return true;
    }

    int culledThisFrame;

    /// <summary>
    /// Whether the neighbouring section holds (or covers) data. Checked at the drawn
    /// section's own level: a coarse section's neighbour is coarse too, and its
    /// presence in HasDataSet means something in that subtree was captured.
    /// </summary>
    bool HasNeighbourData(long key, int dx, int dz)
    {
        long nkey = LodWorld.NeighborKey(key, dx, dz);
        if (neighbourDataMemo.TryGetValue(nkey, out bool cached)) return cached;
        bool has = world.HasDataSet.Contains(nkey);
        neighbourDataMemo[nkey] = has;
        return has;
    }

    void ClearSelectionMemos()
    {
        realSurfaceMemo.Clear();
        leadConeMemo.Clear();
        landLikeMemo.Clear();
        capturedBeyondMemo.Clear();
        boxInViewMemo.Clear();
    }

    /// <summary>
    /// Selection memos used to clear every frame (alloc + refill hitch). Keep them
    /// across frames; invalidate on keep-origin shift, view-distance change, or yaw
    /// past slack — same idea as FOV occlusion temporal cache.
    /// </summary>
    void InvalidateSelectionMemosIfNeeded()
    {
        if (!selectionMemosValid)
        {
            ClearSelectionMemos();
            memoYaw = lookYaw;
            memoLiveViewDistance = liveViewDistance;
            selectionMemosValid = true;
            return;
        }

        if (windowMovedThisFrame)
        {
            ClearSelectionMemos();
            memoYaw = lookYaw;
            memoLiveViewDistance = liveViewDistance;
            return;
        }

        float dyaw = LodFrameBudget.WrapAngle(lookYaw - memoYaw);
        if (MathF.Abs(dyaw) >= MemoYawInvalidateRadians)
        {
            ClearSelectionMemos();
            memoYaw = lookYaw;
            memoLiveViewDistance = liveViewDistance;
        }
    }

    public void ClearMeshes()
    {
        foreach (MeshRef meshRef in sectionMeshes.Values) meshRef.Dispose();
        foreach (MeshRef meshRef in waterMeshes.Values) meshRef.Dispose();
        sectionMeshes.Clear();
        waterMeshes.Clear();
        emptyMeshKeys.Clear();
        meshJobInFlight.Clear();
        lastSelectedFrame.Clear();
        keepOriginValid = false;
        windowMovedThisFrame = false;
        snowLineY = pendingSnowLineY = 99999;
        seasonalRefreshActive = false;
        seasonalStateInitialized = false;
        climateField.Clear();
        keepClimate = LodClimateField.Identity;
        keepClimateValid = false;
        climateUploadValid = false;
        ClearSelectionMemos();
        selectionMemosValid = false;
        neighbourDataMemo.Clear();
    }

    public void Dispose()
    {
        // Our own resources first. UnregisterRenderer refuses to run off the main thread,
        // and the game's shutdown crash path disposes mods from another one, so putting
        // the engine call first meant a crashing client freed none of its GPU meshes.
        ClearMeshes();
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
    }
}







