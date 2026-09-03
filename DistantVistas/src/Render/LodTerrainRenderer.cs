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
    /// Outside the keep-circle, retire the oldest GPU meshes a couple at a time, every
    /// frame. Disposing a MeshRef is a driver-side buffer delete, so the old once-a-second
    /// sweep of 24 put a whole second of deletes into a single frame: the average frame
    /// stayed fine while p95 spiked. Same meshes, same order, same work per second, spread
    /// across the second instead of piled into one frame.
    /// </summary>
    const int EvictOldestPerFrame = 2;
    const int EvictOldestPerFrameOverBudget = 4;

    /// <summary>
    /// Frames between rebuilds of the oldest-first candidate list. Choosing candidates
    /// walks every resident mesh, so that part keeps its old cadence and only the
    /// disposing is spread out. The list is capped at what the per-frame rate can retire
    /// before the next rebuild.
    /// </summary>
    const int EvictScanInterval = 60;

    public int EvictedTotal { get; private set; }
    readonly Matrixf modelMat = new();
    readonly List<long> drawList = new();
    IShaderProgram? prog;
    bool shaderOk;
    float appliedZFar;
    Vec3d camPos = new();
    float lookDown01;

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
    readonly HashSet<long> readyTerrainColumns = new();

    /// <summary>Vanilla view distance this frame, after the server's last-approved cap.</summary>
    public float LiveViewDistance => liveViewDistance;

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

    /// <summary>Sections selected by the walk but skipped this frame as off-screen.</summary>
    public int LastCulledCount { get; private set; }

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
        return LodCoveragePolicy.ShouldVisitChildForDraw(
            childLevel, parentWanted, parentDrawFullDetail, parentHasMesh,
            parentLandLike, childInCone, lookDown01, childHasData, childFarther);
    }

    bool IsFartherLoaded(long key) =>
        LodCoveragePolicy.IsFartherLoaded(
            Math.Sqrt(NearestDistanceSqTo(key)), LodWorld.KeyFootprintBlocks(key), FarthestKnownDistance)
        && CapturedBeyond(key);

    void Submit(long key)
    {
        drawList.Add(key);
        drawnThisFrame.Add(key);
        lastSelectedFrame[key] = frameCounter;
    }

    bool HasAnyMesh(long key) =>
        sectionMeshes.ContainsKey(key) || waterMeshes.ContainsKey(key) || emptyMeshKeys.Contains(key);

    bool IsProvisionalKey(long key)
    {
        if (world.ProvisionalL0Keys.Contains(key)) return true;
        return world.Sections.TryGetValue(key, out LodSection? section)
            && section.ProvisionalQuadrants != 0;
    }

    bool HasDrawableMesh(long key) =>
        sectionMeshes.ContainsKey(key) || waterMeshes.ContainsKey(key);

    bool InLeadCone(long key)
    {
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
        return frustum.BoxInLeadCone(relX, minY, relZ, relX + footprint, maxY, relZ + footprint);
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
        if (level < 1) return true;
        if (section == null) return false;
        if (!LodCoveragePolicy.IsLandLikeCoarseMesh(
                level, section.HasSurfaceBounds, section.SurfaceRelief, section.CapturedColumns))
            return false;
        if (!ChildSurfaceUnion(key, out int childYMin, out int childYMax))
            return false;
        return LodCoveragePolicy.ParentFollowsChildSurface(
            section.HasSurfaceBounds, section.SurfaceYMin, section.SurfaceYMax,
            true, childYMin, childYMax);
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
            RequestMesh(target);
            return;
        }

        if (world.HasDataSet.Contains(target) && !world.LoadFailed.Contains(target))
        {
            coarseParentRequestsThisFrame++;
            RequestMesh(target);
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
        loadedMapChunks.Clear();
        readyTerrainColumns.Clear();
        int cs = GlobalConstants.ChunkSize;
        if (cs <= 0) return;
        int cx0 = (int)Math.Floor(camPos.X / cs);
        int cz0 = (int)Math.Floor(camPos.Z / cs);
        int rad = Math.Max(4, (int)Math.Ceiling(liveViewDistance / cs) + 2);
        var ba = capi.World.BlockAccessor;
        int maxCy = Math.Max(0, (ba.MapSizeY / cs) - 1);
        for (int dz = -rad; dz <= rad; dz++)
        {
            for (int dx = -rad; dx <= rad; dx++)
            {
                int cx = cx0 + dx;
                int cz = cz0 + dz;
                var map = ba.GetMapChunk(cx, cz);
                if (map == null) continue;
                loadedMapChunks.Add(MapChunkKey(cx, cz));
                // Map-chunk is heightmap only. World-chunk at rain height is
                // the first frame vanilla can actually draw this column.
                int rain = 0;
                if (map.RainHeightMap is { Length: > 0 } heights)
                    rain = heights[heights.Length / 2];
                int cy = rain <= 0 ? 0 : Math.Clamp(rain / cs, 0, maxCy);
                var chunk = ba.GetChunk(cx, cy, cz);
                if (chunk != null && !chunk.Disposed)
                    readyTerrainColumns.Add(MapChunkKey(cx, cz));
            }
        }
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

    bool AllWorldChunksReadyForKey(long key)
    {
        int footprint = LodWorld.KeyFootprintBlocks(key);
        int minX = LodWorld.KeySx(key) * footprint;
        int minZ = LodWorld.KeySz(key) * footprint;
        return LodCoveragePolicy.AllMapChunksLoaded(
            minX, minX + footprint, minZ, minZ + footprint,
            GlobalConstants.ChunkSize,
            (cx, cz) => readyTerrainColumns.Contains(MapChunkKey(cx, cz)));
    }

    bool FarthestXzInsideViewDistance(long key, double radius)
    {
        int footprint = LodWorld.KeyFootprintBlocks(key);
        double minX = LodWorld.KeySx(key) * (double)footprint;
        double minZ = LodWorld.KeySz(key) * (double)footprint;
        double maxX = minX + footprint;
        double maxZ = minZ + footprint;
        double midX = (minX + maxX) * 0.5;
        double midZ = (minZ + maxZ) * 0.5;
        double farX = camPos.X < midX ? maxX : minX;
        double farZ = camPos.Z < midZ ? maxZ : minZ;
        double dx = farX - camPos.X;
        double dz = farZ - camPos.Z;
        return dx * dx + dz * dz < radius * radius;
    }

    bool VanillaOwnsKey(long key, LodSection? bounds, double hideRadius)
    {
        // Farthest corner still inside view distance: vanilla should be drawing
        // the whole tile. Nearest-point hide chopped a moving ring (0.7.38).
        bool inside3d = bounds != null && bounds.HasSurfaceBounds
            ? SectionFullyInsideVanilla(key, bounds, hideRadius)
            : FarthestXzInsideViewDistance(key, hideRadius);
        // Every map-chunk covering this tile must be present. One of four
        // left plates on the loaded trees; zero of four is grow-VD / walk-away.
        // World-chunks too: map-chunk-only is not-yet-drawn sky.
        bool allLoaded = AllMapChunksLoadedForKey(key);
        bool worldReady = AllWorldChunksReadyForKey(key);
        return LodCoveragePolicy.VanillaOwnsFootprint(inside3d, allLoaded, worldReady);
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
                    if (hasData && !hasMesh) RequestMesh(ck);
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

        // Optional extra ceiling. 0 means keep drawing the whole visited landscape
        // (cheap coarse rungs far out). GPU L0/L1 still pages outside the keep-circle.
        if (FarViewDistanceCap > 0 && nearDist > FarViewDistanceCap + LodSection.SectionBlocks)
            return false;

        // Near-cull must not abort quadtree descent; only skip *drawing* sections
        // whose nearest edge is inside the vanilla bubble. Top-level L6 sections are
        // 4096 blocks - the player is inside them (nearDist=0), so returning early
        // without descending would draw zero LOD meshes past the bubble.
        float overdraw = GameMath.Clamp(OverdrawStart, 0.15f, 0.95f);
        double vanillaCoverageRadius = liveViewDistance * overdraw;
        // Whole AABB inside view distance, and every map-chunk covering this
        // tile is loaded. A circle alone punches sky when you raise VD.
        world.Sections.TryGetValue(key, out LodSection? coverageSection);
        bool insideVanilla = VanillaOwnsKey(key, coverageSection, liveViewDistance);

        bool landLike = ComputeLandLike(level, coverageSection, key);
        bool inLeadCone = InLeadCone(key);

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

        // Intervening span: visited L0/L1 in front of the camera with land
        // already drawn past it. Coarsening is fine when a parent mesh takes
        // over the whole footprint; this flag is what stops a per-child skip
        // or a pruned mesh job from turning one tile of that span into sky.
        bool hasData = world.HasDataSet.Contains(key);
        bool fartherLoaded = LodCoveragePolicy.IsFartherLoaded(
                nearDist, LodWorld.KeyFootprintBlocks(key), FarthestKnownDistance)
            && (!hasData || !inLeadCone || CapturedBeyond(key));
        bool mustCover = !insideVanilla
            && LodCoveragePolicy.MustCoverIntervening(level, hasData, inLeadCone, fartherLoaded);
        // Gaps appended by this node's subtree start here; see gaps.
        int gapStart = gaps.Count;

        // Mesh the surface we will actually draw. L0/L1 at the 1.0x ring and
        // handoff; wanted-level further out, including mip parents of visited L0.
        // Do not request a parent just so we can paint a giant square over a hole.
        if (!hasMesh)
        {
            if (inLeadCone && !insideVanilla)
                RequestMesh(key);
            else if (handoff && level <= 1)
                RequestMesh(key);
            else if (LodCoveragePolicy.RequestVisitedKeepMesh(
                         level, hasMesh, hasData, insideVanilla,
                         nearDist, liveViewDistance, inLeadCone, fartherLoaded))
                RequestMesh(key);
            else if (!insideVanilla && (level == wanted || level == wanted + 1))
                RequestMesh(key);
            else if (!insideVanilla && level <= wanted + 1
                     && nearDist < liveViewDistance + LodSection.SectionBlocks * 4)
                RequestMesh(key);
            else if (insideVanilla && level == 0)
                RequestMesh(key);
            else if (hasData && LodCoveragePolicy.KeepVisitedSurface(level, true)
                     && LodCoveragePolicy.IsNearVisitedTrail(nearDist, liveViewDistance))
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

        if (!insideVanilla && !drawFullDetail && wanted >= 2)
            RequestCoarseFill(key, wanted);

        // In the lead cone never stop or draw L2+: only L0 and land-like L1
        // in front. A plate in the lead cone (including at the coarsen ring)
        // also does not stop: walk children so real L0/L1 land still covers
        // the hills. Behind the lead cone L2+ (even a plate) may stop as a
        // cheap stand-in.
        bool stopAtThisRung = LodCoveragePolicy.StopDescentAtAvailableRung(
            level, wanted, drawFullDetail, hasMesh, landLike, inLeadCone, lookDown01)
            && LodCoveragePolicy.MaySubmitCoarseWhole(level, nearDist, vanillaCoverageRadius);
        if (stopAtThisRung && level < wanted)
            RequestCoarseFill(key, wanted);

        bool drawableCoarse = hasMesh && LodCoveragePolicy.MayDrawCoarseParent(
            level, insideVanilla, landLike, inLeadCone, lookDown01);

        // L1 used to stop here and hide the L0 the player already walked. That
        // is the "I had data, it vanished when I backed up" hole.
        bool walkCapturedL0 = level == 1;

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
                    else
                    {
                        // Not walked at all this frame: nothing under it can draw.
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
            bool touchesVanilla = nearDist < vanillaCoverageRadius;
            if (!LodCoveragePolicy.MayPaintWholeAfterDescent(
                    anyChildDrew, drawableCoarse, forcedDetail, holdVisitedL0, touchesVanilla))
            {
                if (hasMesh) lastSelectedFrame[key] = frameCounter;
                // This parent is not drawing whole. Children that stepped aside
                // for it (SkipDrawTooFine) are now uncovered land: submit them.
                // Per-child wanted differs with relief, so a flat L0 beside a
                // hilly sibling used to vanish right here as a sky rectangle.
                anyChildDrew |= FlushDeferredTooFine(deferredStart);
                if (gaps.Count > gapStart)
                {
                    // Captured L0/L1 is already the real land. Remesh that.
                    // Do not paint a coarse parent over it (weird low-poly tile).
                    RequestVisitedKeepGaps(gapStart);
                    if (LodCoveragePolicy.MayFillGapWithParent(level, hasMesh, insideVanilla,
                            gapTouchesVanilla: false))
                    {
                        anyChildDrew |= FillGaps(key, gapStart, liveViewDistance);
                    }
                    else if (!hasMesh)
                    {
                        // Nothing here to fill with: hand the gaps up, as one
                        // footprint when the whole subtree is missing, and get a
                        // mesh of our own so next frames can fill closer in.
                        CoalesceGaps(key, gapStart);
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

            if (level == 0 && world.IncompleteL0Keys.Contains(key))
            {
                // Its captured quadrants are the finest picture of them we hold;
                // the parent mip has no more there. Draw them. The quadrants that
                // were never captured are not a gap either: no rung has them.
                if (LodCoveragePolicy.DrawIncompleteL0(hasMesh, insideVanilla))
                {
                    Submit(key);
                    return true;
                }
                lastSelectedFrame[key] = frameCounter;
                return false;
            }

            if (!LodCoveragePolicy.MayDrawCoarseParent(level, insideVanilla, landLike, inLeadCone, lookDown01))
            {
                // Refused as a whole plate (horizon rules) without having walked
                // its children. Far away, a meshed L1 is still land. Next to the
                // player it is the brown cubes over vanilla — do not Submit.
                lastSelectedFrame[key] = frameCounter;
                if (!insideVanilla && HasDrawableMesh(key)
                    && LodCoveragePolicy.MaySubmitCoarseWhole(level, nearDist, vanillaCoverageRadius))
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
                        mustCover))
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

            if (!LodCoveragePolicy.MaySubmitCoarseWhole(level, nearDist, vanillaCoverageRadius))
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

    /// <summary>
    /// Gaps that already have captured L0/L1 data remesh at that quality.
    /// Pull them out of the parent-fill list so a coarse mip cannot replace
    /// land the player already walked.
    /// </summary>
    void RequestVisitedKeepGaps(int start)
    {
        for (int i = gaps.Count - 1; i >= start; i--)
        {
            long gap = gaps[i];
            int level = LodWorld.KeyLevel(gap);
            if (!LodCoveragePolicy.KeepVisitedSurface(level, world.HasDataSet.Contains(gap)))
                continue;
            RequestMesh(gap);
            gaps.RemoveAt(i);
        }
    }

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
        bool mayWhole = wholeFootprint
            && LodCoveragePolicy.MaySubmitCoarseWhole(
                LodWorld.KeyLevel(key), Math.Sqrt(NearestDistanceSqTo(key)), coverageRadius);

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
                if (gapDraws.Count >= LodCoveragePolicy.MaxGapDrawsPerFrame) break;
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
    /// whole AABB inside live view distance AND every map-chunk present.
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
            if (VanillaOwnsKey(key, gapSec, liveViewDistance)) continue;
            float overdraw = GameMath.Clamp(OverdrawStart, 0.15f, 0.95f);
            if (!LodCoveragePolicy.MaySubmitCoarseWhole(
                    LodWorld.KeyLevel(key), Math.Sqrt(NearestDistanceSqTo(key)),
                    liveViewDistance * overdraw))
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
                RequestMesh(key);
                return;
            }
        }
        else if (world.HasDataSet.Contains(key) && !world.LoadFailed.Contains(key))
        {
            coarseParentRequestsThisFrame++;
            RequestMesh(key);
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
        int footprint = LodWorld.KeyFootprintBlocks(key);
        double cx = LodWorld.KeySx(key) * (double)footprint + footprint / 2.0;
        double cz = LodWorld.KeySz(key) * (double)footprint + footprint / 2.0;
        double dx = cx - camPos.X;
        double dz = cz - camPos.Z;
        double len = Math.Sqrt(dx * dx + dz * dz);
        if (len < 1) return false;
        dx /= len;
        dz /= len;

        const int probeLevel = 3;
        int probe = LodSection.SectionBlocks << probeLevel;
        double start = footprint / 2.0;
        for (int step = 1; step <= 2; step++)
        {
            double px = cx + dx * (start + probe * step);
            double pz = cz + dz * (start + probe * step);
            if (px < 0 || pz < 0) continue;
            long pk = LodWorld.SectionKey(probeLevel,
                (int)Math.Floor(px / probe), (int)Math.Floor(pz / probe));
            if (world.HasDataSet.Contains(pk)) return true;
        }
        return false;
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
            ClimateCondition? low = capi.World.BlockAccessor.GetClimateAt(climatePos);
            climatePos.Set(px, seaLevel + 150, pz);
            ClimateCondition? high = capi.World.BlockAccessor.GetClimateAt(climatePos);
            if (low == null || high == null) return LodSnow.Disabled;
            return LodSnow.OverlayY(seaLevel, low.Temperature, high.Temperature);
        }
        catch
        {
            return LodSnow.Disabled;
        }
    }


    /// <summary>Demand-driven (re)meshing: the selection walk is the load queue (Voxy's idea, CPU-side).</summary>
    void RequestMesh(long key)
    {
        if (meshJobInFlight.Contains(key)) return;

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

    void EvictStaleMeshes()
    {
        bool drained = evictCursor >= evictBatch.Count;
        if (frameCounter - lastEvictScanFrame >= EvictScanInterval || (drained && evictBatchFull))
            ScanEvictionCandidates();

        int budget = sectionMeshes.Count > LodMemoryBudget.MaxResidentMeshes
            ? EvictOldestPerFrameOverBudget
            : EvictOldestPerFrame;

        while (budget > 0 && evictCursor < evictBatch.Count)
        {
            long key = evictBatch[evictCursor++];

            // The list can be a whole scan old, so the keep-circle is checked again here:
            // a key the player has turned back toward is inside it now and stays.
            if (InJustLeftRing(key)) continue;

            // A mesh on screen this frame is not stale, whatever the keep-circle
            // says: dropping it while a sibling still draws is next frame's hole.
            if (drawnThisFrame.Contains(key)) continue;

            // No hole then pop: if this mesh was selected this frame and the coarser
            // parent is not drawable yet, leave it. Tiles that have left the keep-circle
            // and are no longer selected may go.
            if (lastSelectedFrame.TryGetValue(key, out long selected) && selected == frameCounter)
            {
                long parent = LodWorld.ParentKey(key);
                if (LodWorld.KeyLevel(key) < LodWorld.MaxLevel && !HasAnyMesh(parent))
                    continue;
            }

            bool freed = false;
            if (sectionMeshes.Remove(key, out MeshRef? mesh)) { mesh.Dispose(); freed = true; }
            if (waterMeshes.Remove(key, out MeshRef? water)) { water.Dispose(); freed = true; }

            // Nothing was holding GPU memory for this key any more, so it does not spend
            // the frame's budget. Move on to the next candidate instead.
            if (!freed) continue;

            lastSelectedFrame.Remove(key);
            meshBornFrame.Remove(key);
            EvictedTotal++;
            budget--;
        }
    }

    /// <summary>
    /// Rebuild the oldest-first eviction list. Selection is unchanged: L0/L1 only, never
    /// inside the RAM-scaled keep-circle or the just-left ring, oldest mesh first.
    /// </summary>
    void ScanEvictionCandidates()
    {
        lastEvictScanFrame = frameCounter;
        evictCursor = 0;

        LodCoveragePolicy.KeepCircleScale = LodMemoryBudget.LiveKeepScale(sectionMeshes.Count);

        int want = (sectionMeshes.Count > LodMemoryBudget.MaxResidentMeshes
            ? EvictOldestPerFrameOverBudget
            : EvictOldestPerFrame) * EvictScanInterval;

        evictBatch.Clear();
        evictBorn.Clear();
        foreach (long key in sectionMeshes.Keys) ConsiderForEviction(key, want);
        foreach (long key in waterMeshes.Keys)
        {
            if (sectionMeshes.ContainsKey(key)) continue;
            ConsiderForEviction(key, want);
        }

        // A list that filled up means there is more to retire than this window holds, so
        // draining it early earns a fresh scan rather than an idle wait.
        evictBatchFull = evictBatch.Count >= want;
    }

    /// <summary>
    /// Place one candidate in the oldest-first list, keeping at most <paramref name="want"/>
    /// of them. Coarse far meshes are the giant landscape, so only the expensive L0/L1
    /// outside the keep-circle is eligible, same idea as DH's clipmap.
    /// </summary>
    void ConsiderForEviction(long key, int want)
    {
        if (LodWorld.KeyLevel(key) > 1) return;
        if (InJustLeftRing(key)) return;

        long born = meshBornFrame.TryGetValue(key, out long b) ? b : 0;

        // Younger than everything already held, with no room left: it would fall off the
        // end of the list anyway, so skip the insert.
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
            return;
        }

        dirtyPrune.Clear();
        foreach (long key in world.RenderDirty)
        {
            int dirtyLevel = LodWorld.KeyLevel(key);
            double pruneDist = Math.Sqrt(NearestDistanceSqTo(key));
            float overdrawP = GameMath.Clamp(OverdrawStart, 0.15f, 0.95f);
            bool handoffJob = InHandoffRing(pruneDist, liveViewDistance * overdrawP);
            if (!HasAnyMesh(key) && dirtyLevel < LodWorld.WantedLevelForSq(NearestDistanceSqTo(key)))
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
        foreach (long key in dirtyPrune) world.RenderDirty.Remove(key);
        walkRequested.Clear();
    }

    readonly long[] scheduleCandidates = new long[MeshSchedulesPerFrame + IncompleteFillPerTick + MeshLoadRequestsPerFrame];
    readonly double[] scheduleCandidateDistSq = new double[MeshSchedulesPerFrame + IncompleteFillPerTick + MeshLoadRequestsPerFrame];
    int lastKeepOverlayCount;
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

        foreach (long key in world.RenderDirty)
        {
            // Skip anything already being meshed or reloaded, so the per-frame budget
            // goes to sections that can actually start work now.
            if (meshJobInFlight.Contains(key) || world.LoadsInFlight.Contains(key)) continue;
            // Idle: already-meshed land waits for a tile of travel, except peek
            // cubes that have to remesh when the real chunk lands at spawn.
            if (!LodCoveragePolicy.ShouldRemeshWhileIdle(
                    windowMovedThisFrame, HasAnyMesh(key), IsProvisionalKey(key)))
                continue;

            double distSq = NearestDistanceSqTo(key);
            int candLevel = LodWorld.KeyLevel(key);
            double candDist = Math.Sqrt(distSq);
            float overdrawS = GameMath.Clamp(OverdrawStart, 0.15f, 0.95f);
            if (InHandoffRing(candDist, liveViewDistance * overdrawS) && candLevel <= 1)
                distSq *= 0.05;
            else if (LodCoveragePolicy.KeepVisitedSurface(candLevel, world.HasDataSet.Contains(key))
                     && !HasAnyMesh(key))
                distSq *= 0.08;
            else if (candLevel >= 2 && !HasAnyMesh(key))
                distSq *= 0.25;
            // Visited L0/L1 trail: mesh the near captured ring first so fly-ahead holes
            // close before coarse parents swap in behind the player.
            if (LodCoveragePolicy.ShouldKeepVisitedDraw(
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

        foreach (long key in world.RenderDirty)
        {
            if (meshJobInFlight.Contains(key) || world.LoadsInFlight.Contains(key)) continue;
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
        if (world.RenderDirty.Count == 0 || worker.PendingMeshes >= maxWorkerMeshBacklog) return;

        // Two budgets. Starting a background reload costs this thread almost nothing
        // (enqueue a key), whereas building a mesh snapshot is real work, so charging a
        // reload against the mesh budget throttled join fill-in badly: every section
        // needed two passes to appear and only four could be touched per frame.
        int meshBudget = MeshSchedulesPerFrame + IncompleteFillPerTick;
        int loadBudget = MeshLoadRequestsPerFrame;

        // ONE pass over the dirty set, keeping the nearest few, rather than a fresh scan
        // of the whole set for every key scheduled.
        //
        // The iteration cap below has been here a while, and the reason it was added is
        // still true: the paths that drop a key without starting work charge neither
        // budget, so without a cap the loop runs until RenderDirty drains. But capping
        // the number of iterations left each iteration scanning everything, so the work
        // stayed proportional to cap times dirty, with a square root per element.
        // Measured at 2.6ms inside a single frame during fill-in, which is a visible
        // stutter exactly when the player is exploring.
        int candidates = SelectNearestDirty();
        int keepBudget = Math.Min(VisitedKeepSchedulesPerFrame, meshBudget);
        int nearBudget = meshBudget - keepBudget;
        int keepBegin = Math.Max(0, candidates - lastKeepOverlayCount);

        // Handoff / just-left first, then the reserved farthest visited slots
        // so a long walk cannot starve the start of the journey.
        for (int i = 0; i < keepBegin && (nearBudget > 0 || loadBudget > 0); i++)
            TryStartMeshJob(scheduleCandidates[i], ref nearBudget, ref loadBudget);

        int rest = keepBudget + nearBudget;
        for (int i = keepBegin; i < candidates && (rest > 0 || loadBudget > 0); i++)
            TryStartMeshJob(scheduleCandidates[i], ref rest, ref loadBudget);
    }

    bool TryStartMeshJob(long best, ref int meshBudget, ref int loadBudget)
    {
        // Standing still: keep GPU meshes. Do not clone a snapshot or enqueue a job
        // for land that is already on screen. Capture-dirty keys stay in RenderDirty
        // until the origin actually moves.
        if (!LodCoveragePolicy.ShouldRemeshWhileIdle(
                windowMovedThisFrame, HasAnyMesh(best), IsProvisionalKey(best)))
            return false;

        // It was dirty and not in flight a moment ago, and nothing below touches any
        // key but the one it is working on. Remove says so anyway for the price of
        // the probe the old code was making regardless.
        if (!world.RenderDirty.Remove(best)) return false;

        // Non-blocking: an evicted section starts a background reload and is
        // re-requested by the selection walk once it lands, rather than stalling
        // this frame on a decompress.
        if (!world.TryGetForRender(best, out LodSection section))
        {
            // Spill-to-disk / in-flight reload: KEEP the GPU mesh. Disposing it
            // here was the unload-into-sky after a fill quota.
            if (world.LoadsInFlight.Contains(best) && loadBudget > 0)
                loadBudget--;
            return false;
        }

        if (section.CapturedColumns == 0) return false;

        if (meshBudget <= 0)
        {
            world.RenderDirty.Add(best);
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
        int budget = MeshUploadsPerFrame;
        long uploadStart = LodPhaseCost.Start();

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
            if (System.Diagnostics.Stopwatch.GetTimestamp() - uploadStart >= MeshUploadBudgetTicks) break;
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
        lookDown01 = LodCoveragePolicy.LookDownAmount(look.Y);
        UpdateKeepOrigin();
        frameCounter++;

        // Timed apart, not together. Lumped into one counter they cannot be told apart,
        // and they are different shapes: pruning walks the whole dirty set once a frame,
        // while scheduling picks a bounded number of jobs out of it. A spike in the pair
        // was being read as a spike in scheduling.
        long phaseStart = LodPhaseCost.Start();
        PruneRenderDirty();
        PruneCost.Add(phaseStart);

        // View distance / far distance first so CollectDrawNodes can RequestMesh from
        // cache before ScheduleMeshJobs runs (same-frame schedule of those requests).
        // Empty sectionMeshes is normal on first frames after join while the dirty
        // queue fills â€” do not early-return solely because meshes are empty.
        var playerData = capi.World.Player.WorldData;
        float viewDistance = playerData.DesiredViewDistance;
        if (playerData.LastApprovedViewDistance > 0)
        {
            viewDistance = Math.Min(viewDistance, playerData.LastApprovedViewDistance);
        }
        // Keep LOD ladder glued to the live graphics setting (e.g. 256 â†’ 1000).
        liveViewDistance = Math.Max(64f, viewDistance);
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
        // Window does not move while idle, so nothing leaves the keep-circle and
        // nothing is disposed. Same meshes stay on the GPU until XZ actually shifts.
        if (windowMovedThisFrame)
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
        double originX = LodWorld.KeySx(key) * (double)footprint;
        double originZ = LodWorld.KeySz(key) * (double)footprint;

        double dx = originX + footprint / 2.0 - camPos.X;
        double dz = originZ + footprint / 2.0 - camPos.Z;
        if (dx * dx + dz * dz > cullDistSq) return false;

        double relX = originX - camPos.X;
        double relZ = originZ - camPos.Z;

        int level = LodWorld.KeyLevel(key);
        double drawDist = Math.Sqrt(
            (originX + footprint / 2.0 - camPos.X) * (originX + footprint / 2.0 - camPos.X)
            + (originZ + footprint / 2.0 - camPos.Z) * (originZ + footprint / 2.0 - camPos.Z));
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

            if (!frustum.BoxInView(relX + rectMinX, minY, relZ + rectMinZ,
                    relX + rectMaxX, maxY, relZ + rectMaxZ))
            {
                culledThisFrame++;
                return false;
            }
        }

        modelMat.Identity().Translate(relX, -camPos.Y, relZ);
        prog!.UniformMatrix("modelMatrix", modelMat.Values);
        prog.Uniform("columnBlocks", (float)LodWorld.ColumnStepBlocks(LodWorld.KeyLevel(key)));

        // Sides that border on never-captured area, so the shader can dissolve them
        // into the horizon instead of leaving a cliff at the edge of what we've seen.
        prog.Uniform("sectionSize", (float)footprint);
        prog.Uniform("openEdges",
            HasNeighbourData(key, -1, 0) ? 0f : 1f,
            HasNeighbourData(key, 1, 0) ? 0f : 1f,
            HasNeighbourData(key, 0, -1) ? 0f : 1f,
            HasNeighbourData(key, 0, 1) ? 0f : 1f);
        UploadSectionClimate((int)originX, (int)originZ, footprint);
        return true;
    }

    void CaptureKeepClimate(int x, int z)
    {
        if (!TrySampleClimateCell(x, z, out LodClimateField.Sample sample)) return;
        keepClimate = sample;
        keepClimateValid = true;
        lastSeasonTempX = sample.LowTemp;
        climateField.Put(x, z, sample);
    }

    void UploadSectionClimate(int originX, int originZ, int footprint)
    {
        int x1 = originX + footprint;
        int z1 = originZ + footprint;
        LodClimateField.Sample s00 = EnsureClimateCell(originX, originZ);
        LodClimateField.Sample s10 = EnsureClimateCell(x1, originZ);
        LodClimateField.Sample s01 = EnsureClimateCell(originX, z1);
        LodClimateField.Sample s11 = EnsureClimateCell(x1, z1);
        UploadClimateCorner("climateLow00", "climateHigh00", s00);
        UploadClimateCorner("climateLow10", "climateHigh10", s10);
        UploadClimateCorner("climateLow01", "climateHigh01", s01);
        UploadClimateCorner("climateLow11", "climateHigh11", s11);
    }

    void UploadClimateCorner(string lowName, string highName, in LodClimateField.Sample s)
    {
        prog!.Uniform(lowName, s.LowR, s.LowG, s.LowB, s.LowTemp / 255f);
        prog.Uniform(highName, s.HighR, s.HighG, s.HighB, s.HighTemp / 255f);
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
        ClimateCondition? cl = capi.World.BlockAccessor.GetClimateAt(climatePos);
        if (cl != null)
            temp = LodTintRegistry.UnscaledTempByteFromCelsius(cl.WorldGenTemperature);

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
    bool HasNeighbourData(long key, int dx, int dz) =>
        world.HasDataSet.Contains(LodWorld.NeighborKey(key, dx, dz));

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




