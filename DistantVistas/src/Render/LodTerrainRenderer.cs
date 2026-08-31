using Vintagestory.API.Client;
using Vintagestory.API.Common;
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
    public double RenderOrder => 0.36; // just before opaque terrain Ã¢â€ â€™ occluded by real chunks
    public int RenderRange => 9999;

    const int MeshSchedulesPerFrame = 12;
    const int MeshUploadsPerFrame = 8;
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
    readonly Dictionary<long, long> lastSelectedFrame = new();
    readonly List<long> evictBatch = new();
    long frameCounter;

    /// <summary>Meshes unselected for this many frames (~1 min) get evicted; the quadtree re-requests on demand.</summary>
    const int EvictAfterFrames = 3600;
    const int EvictSweepInterval = 300;

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
    float snowLineY = 99999;
    float pendingSnowLineY = 99999;
    long lastSeasonRefreshMs;
    bool seasonalStateInitialized;
    bool seasonalRefreshActive;
    int seasonalRefreshSlot;
    int seasonalRefreshX;
    int seasonalRefreshZ;
    readonly BlockPos climatePos = new(0, 0, 0);

    /// <summary>Optional hard cap in blocks; 0 = unlimited (render every cached section).</summary>
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

    /// <summary>Current far edge in blocks: the farthest loaded LOD data, independent of the vanilla view distance.</summary>
    public float EffectiveFarDistance { get; private set; } = 3000;
    float liveViewDistance = 512;

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
        if (FarViewDistanceCap > 0)
            EffectiveFarDistance = Math.Min(far, FarViewDistanceCap);
        else
            EffectiveFarDistance = Math.Max(far, vanillaViewDistance + 16384);
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
    /// Far visited land stops descending below the wanted rung; the near trail still reaches L0/L1.
    /// </summary>
    bool ShouldVisitChildForDraw(long childKey, int parentWanted, bool parentNearTrail)
    {
        if (parentNearTrail) return true;
        return LodWorld.KeyLevel(childKey) >= parentWanted;
    }

    bool HasAnyMesh(long key) => sectionMeshes.ContainsKey(key) || waterMeshes.ContainsKey(key);

    bool InHandoffRing(double nearDist, double vanillaCoverageRadius)
    {
        double outer = liveViewDistance + LodSection.SectionBlocks * 6;
        return nearDist >= vanillaCoverageRadius * 0.45 && nearDist <= outer;
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

        // Hard cap trims draw selection CPU and GPU; unlimited cap still coarsens far L0.
        if (FarViewDistanceCap > 0 && nearDist > FarViewDistanceCap + LodSection.SectionBlocks)
            return false;

        bool nearVisitedTrail = LodCoveragePolicy.IsNearVisitedTrail(nearDist, liveViewDistance);

        // Near-cull must not abort quadtree descent; only skip *drawing* sections
        // whose nearest edge is inside the vanilla bubble. Top-level L6 sections are
        // 4096 blocks - the player is inside them (nearDist=0), so returning early
        // without descending would draw zero LOD meshes past the bubble.
        float overdraw = GameMath.Clamp(OverdrawStart, 0.15f, 0.95f);
        double vanillaCoverageRadius = liveViewDistance * overdraw;
        // Never 2D-own. Missing bounds or a look-down / high camera means vanilla
        // is not drawing this column, so LOD must. That is the 0.7.21 look-down
        // fix. It does not need a parent box as a stand-in.
        bool insideVanilla = world.Sections.TryGetValue(key, out LodSection? coverageSection)
            && coverageSection.HasSurfaceBounds
            && LodCoveragePolicy.InsideVanillaCoverage(
                nearDistSq, camPos.Y, coverageSection.SurfaceYMin,
                coverageSection.SurfaceYMax, vanillaCoverageRadius, lookDown01);

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

        // Keep filling the visible / just-left ring. Mesh the surface we will
        // actually draw (L0/L1 at handoff, wanted-level further out). Do not
        // request a parent just so we can paint a giant square over a hole.
        // Visited L0/L1 is not a camera window: if we already captured it,
        // request the same mesh even when WantedLevel has moved on.
        if (!hasMesh)
        {
            if (handoff && level <= 1)
                RequestMesh(key);
            else if (LodCoveragePolicy.RequestVisitedKeepMesh(
                         level, hasMesh, world.HasDataSet.Contains(key), insideVanilla,
                         nearDist, liveViewDistance))
                RequestMesh(key);
            else if (!insideVanilla && (level == wanted || level == wanted + 1))
                RequestMesh(key);
            else if (!insideVanilla && level <= wanted + 1
                     && nearDist < liveViewDistance + LodSection.SectionBlocks * 4)
                RequestMesh(key);
            else if (insideVanilla && level == 0 && nearDist > vanillaCoverageRadius * 0.65)
                RequestMesh(key);
        }

        bool holdVisitedL0 = nearVisitedTrail && level == 1 && AllVisitedL0Children(key);
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
                    if (!ShouldVisitChildForDraw(ck, wanted, nearVisitedTrail)) continue;
                    if (world.HasDataSet.Contains(ck)) anyChildDrew |= CollectDrawNodes(ck);
                }
            }
            return anyChildDrew;
        }

        bool forcedDetail = LodCoveragePolicy.MustDescendForVisualCap(level, LodWorld.MaxVisualLevel);
        bool keepVisited = LodCoveragePolicy.DescendForVisitedKeep(
            level, ChildHasVisitedSurface(key), nearDist, liveViewDistance);
        if (level > 0 && (forcedDetail || (level > wanted && AllChildrenCovered(key)) || !hasMesh
            || (holdVisitedL0 && wanted == 0) || keepVisited))
        {
            bool anyChildDrew = false;
            for (int qz = 0; qz < 2; qz++)
            {
                for (int qx = 0; qx < 2; qx++)
                {
                    long ck = LodWorld.ChildKey(key, qx, qz);
                    if (!ShouldVisitChildForDraw(ck, wanted, nearVisitedTrail)) continue;
                    if (world.HasDataSet.Contains(ck)) anyChildDrew |= CollectDrawNodes(ck);
                }
            }
            // 0.7.20 rule: if any child drew, or MaxVisualLevel wants L0, do not
            // substitute this parent as a box. Missing children stay empty until
            // they mesh. Drawing the parent was the square-plate regression.
            if (anyChildDrew || !hasMesh || forcedDetail || holdVisitedL0) return anyChildDrew;
        }

        if (hasMesh)
        {
            if (level == 0 && world.IncompleteL0Keys.Contains(key))
                return false;

            // Keep the GPU mesh while vanilla owns this column so walking away
            // does not remesh from scratch. Do not draw it on top of chunks.
            if (insideVanilla && level == 0)
            {
                lastSelectedFrame[key] = frameCounter;
                return false;
            }

            // Far visited land coarsens to the wanted rung; only the near trail keeps L0/L1.
            if (level < wanted && !nearVisitedTrail)
            {
                lastSelectedFrame[key] = frameCounter;
                return false;
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

            drawList.Add(key);
            lastSelectedFrame[key] = frameCounter;
            return true;
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
            if (seasonalStateInitialized
                && now - lastSeasonRefreshMs < SeasonalRefreshIntervalMs) return;

            seasonalRefreshActive = true;
            seasonalRefreshSlot = 1;
            seasonalRefreshX = (int)camPos.X;
            seasonalRefreshZ = (int)camPos.Z;
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

        world.RenderDirty.Add(key);

        // Start reload / server-assist wanted-by-view now, not only when a mesh slot
        // opens. Otherwise remote-only keys sit pending while the worker idles.
        if (!world.Sections.ContainsKey(key))
            world.TryGetForRender(key, out _);
    }


    // No mesh-count cap. 0.7.21's 6000/8000 residency dump dropped visited L0
    // onto parent tiles the moment the map had filled enough, which is the
    // "it stops and land disappears" bug. RAM still spills to disk; GPU meshes
    // of land you can see, or just left, stay.
    bool InJustLeftRing(long key)
    {
        double dist = Math.Sqrt(NearestDistanceSqTo(key));
        // 0.7.22 was view distance plus eight L0 tiles (~1k). Tenfold so the
        // trail behind the player is not the thing that gets paged first.
        return dist < liveViewDistance + LodSection.SectionBlocks * 80;
    }

    void EvictStaleMeshes()
    {
        if (frameCounter % EvictSweepInterval != 0) return;

        evictBatch.Clear();
        foreach ((long key, MeshRef _) in sectionMeshes)
        {
            if (!lastSelectedFrame.TryGetValue(key, out long last) || frameCounter - last > EvictAfterFrames)
            {
                if (InJustLeftRing(key)) continue;
                // Visited land stays on the GPU. The old unselected-timeout was a
                // moving window: WantedLevel stopped picking far tiles, this sweep
                // dumped them, and the trail behind the player turned into sky.
                if (LodWorld.KeyLevel(key) <= 1) continue;
                if (world.HasDataSet.Contains(key)) continue;
                evictBatch.Add(key);
            }
        }
        foreach ((long key, MeshRef _) in waterMeshes)
        {
            if (!sectionMeshes.ContainsKey(key)
                && (!lastSelectedFrame.TryGetValue(key, out long last) || frameCounter - last > EvictAfterFrames))
            {
                if (InJustLeftRing(key)) continue;
                if (LodWorld.KeyLevel(key) <= 1) continue;
                if (world.HasDataSet.Contains(key)) continue;
                evictBatch.Add(key);
            }
        }

        foreach (long key in evictBatch)
        {
            if (sectionMeshes.Remove(key, out MeshRef? mesh)) mesh.Dispose();
            if (waterMeshes.Remove(key, out MeshRef? water)) water.Dispose();
            lastSelectedFrame.Remove(key);
            EvictedTotal++;
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
        if (world.RenderDirty.Count == 0) return;

        dirtyPrune.Clear();
        foreach (long key in world.RenderDirty)
        {
            int dirtyLevel = LodWorld.KeyLevel(key);
            double pruneDist = Math.Sqrt(NearestDistanceSqTo(key));
            float overdrawP = GameMath.Clamp(OverdrawStart, 0.15f, 0.95f);
            bool handoffJob = InHandoffRing(pruneDist, liveViewDistance * overdrawP);
            if (!HasAnyMesh(key) && dirtyLevel < LodWorld.WantedLevelForSq(NearestDistanceSqTo(key)))
            {
                // Visited near tiles may sit behind the camera or past the wanted rung
                // while the player flies; dropping their mesh jobs leaves a trail of sky.
                if (LodCoveragePolicy.ShouldKeepVisitedDraw(
                        dirtyLevel, world.HasDataSet.Contains(key), pruneDist, liveViewDistance))
                    continue;
                if (dirtyLevel == 0 && !world.IncompleteL0Keys.Contains(key)) continue;
                if (handoffJob && dirtyLevel <= 1) continue;
                if (dirtyLevel <= 1 && world.HasDataSet.Contains(key)) continue;
                dirtyPrune.Add(key);
            }
        }
        foreach (long key in dirtyPrune) world.RenderDirty.Remove(key);
    }

    readonly long[] scheduleCandidates = new long[MeshSchedulesPerFrame + IncompleteFillPerTick + MeshLoadRequestsPerFrame];
    readonly double[] scheduleCandidateDistSq = new double[MeshSchedulesPerFrame + IncompleteFillPerTick + MeshLoadRequestsPerFrame];
    int lastKeepOverlayCount;

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

            double distSq = NearestDistanceSqTo(key);
            int candLevel = LodWorld.KeyLevel(key);
            double candDist = Math.Sqrt(distSq);
            float overdrawS = GameMath.Clamp(OverdrawStart, 0.15f, 0.95f);
            if (InHandoffRing(candDist, liveViewDistance * overdrawS) && candLevel <= 1)
                distSq *= 0.05;
            else if (LodCoveragePolicy.KeepVisitedSurface(candLevel, world.HasDataSet.Contains(key))
                     && !HasAnyMesh(key))
                distSq *= 0.08;
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
        long[] found = new long[take];
        double[] foundDist = new double[take];
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

        var neighbors = new SectionSnapshot?[4];
        for (int d = 0; d < 4; d++)
        {
            long nk = LodWorld.NeighborKey(best, d == 0 ? -1 : d == 1 ? 1 : 0, d == 2 ? -1 : d == 3 ? 1 : 0);
            if (world.Sections.TryGetValue(nk, out LodSection? nb)) neighbors[d] = SectionSnapshot.Of(nb);
        }

        meshBudget--;
        meshJobInFlight.Add(best);
        worker.EnqueueMesh(new MeshJob
        {
            Key = best,
            Self = SectionSnapshot.Of(section),
            Neighbors = neighbors,
        });
        return true;
    }

    void UploadFinishedMeshes()
    {
        int budget = MeshUploadsPerFrame;
        while (budget-- > 0 && worker.MeshResults.TryDequeue(out MeshResult? result))
        {
            meshJobInFlight.Remove(result.Key);

            if (sectionMeshes.Remove(result.Key, out MeshRef? old)) old.Dispose();
            if (waterMeshes.Remove(result.Key, out MeshRef? oldWater)) oldWater.Dispose();

            if (result.IndexCount > 0)
            {
                sectionMeshes[result.Key] = Upload(result.Xyz, result.Rgba, result.Indices,
                    result.VertexCount, result.IndexCount);
            }

            if (result.WaterIndexCount > 0 && result.WaterXyz != null)
            {
                waterMeshes[result.Key] = Upload(result.WaterXyz, result.WaterRgba!, result.WaterIndices!,
                    result.WaterVertexCount, result.WaterIndexCount);
            }

            // Fresh uploads get a grace stamp so they aren't evicted before first selection.
            lastSelectedFrame[result.Key] = frameCounter;
        }
    }

    MeshRef Upload(float[] xyz, byte[] rgba, int[] indices, int vertCount, int indexCount)
    {
        var mesh = new MeshData(false);
        mesh.SetVerticesCount(vertCount);
        mesh.SetIndicesCount(indexCount);
        mesh.xyz = xyz;
        mesh.Rgba = rgba;
        mesh.Indices = indices;
        return capi.Render.UploadMesh(mesh);
    }

    // ---- Frame ----

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (AutoUnpause && capi.IsGamePaused) capi.PauseGame(false);

        if (prog == null || !shaderOk || prog.LoadError) return;

        var rapi = capi.Render;
        if (rapi.FrameWidth == 0) return;

        camPos = capi.World.Player.Entity.CameraPos;
        Vec3f look = capi.World.Player.Entity.Pos.GetViewVector();
        lookDown01 = LodCoveragePolicy.LookDownAmount(look.Y);
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

        phaseStart = LodPhaseCost.Start();
        UpdateEffectiveFarDistance(viewDistance);
        FarDistanceCost.Add(phaseStart);
        ApplyZFar();

        phaseStart = LodPhaseCost.Start();
        drawList.Clear();
        foreach (long top in world.TopLevelKeys) CollectDrawNodes(top);
        WalkCost.Add(phaseStart);
        LastDrawCount = drawList.Count;

        phaseStart = LodPhaseCost.Start();
        ScheduleMeshJobs();
        ScheduleCost.Add(phaseStart);

        UploadFinishedMeshes();
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
        // Climate stays in tintsLow/High. Vegetation slots mix seasonTints in the shader.
        climatePos.Set((int)camPos.X, capi.World.SeaLevel, (int)camPos.Z);
        float seasonRel = 0.5f;
        float seasonTempX = 128f;
        try
        {
            seasonRel = capi.World.Calendar.GetSeasonRel(climatePos);
            ClimateCondition? cl = capi.World.BlockAccessor.GetClimateAt(climatePos);
            if (cl != null)
                seasonTempX = LodTintRegistry.UnscaledTempByteFromCelsius(cl.WorldGenTemperature);
            tints.RefreshSeason(capi.World, climatePos.X, climatePos.Z);
        }
        catch
        {
            // Keep temperate defaults; identity season is worse than a guess.
        }
        prog.Uniform("seasonRel", seasonRel);
        prog.Uniform("seasonTempX", seasonTempX);
        prog.Uniforms4("seasonTints", LodTintRegistry.MaxSlots, tints.SeasonTints);

        // Live ambient fog so the overdraw ring matches vanilla chunks in front.
        // DisableLodFog only skips extra pastViewHaze, not BlendedFog*.
        prog.Uniform("rgbaFogIn", capi.Ambient.BlendedFogColor);
        prog.Uniform("fogDensityIn", capi.Ambient.BlendedFogDensity * FogDensityScale);
        prog.Uniform("fogMinIn", capi.Ambient.BlendedFogMin);
        prog.Uniform("horizonFog", capi.Ambient.BlendedCloudDensity);

        prog.Uniform("viewDistance", viewDistance);
        prog.Uniform("overdrawStart", GameMath.Clamp(OverdrawStart, 0.15f, 0.95f));
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
        foreach (long key in drawList)
        {
            if (!sectionMeshes.TryGetValue(key, out MeshRef? mesh)) continue;
            if (!SetupSectionTransform(key, cullDistSq)) continue;
            capi.Render.RenderMesh(mesh);
        }

        LastCulledCount = culledThisFrame; // opaque pass only: water covers a subset

        // Pass 2: water, alpha-blended over the terrain.
        rapi.GlToggleBlend(true);
        foreach (long key in drawList)
        {
            if (!waterMeshes.TryGetValue(key, out MeshRef? mesh)) continue;
            if (!SetupSectionTransform(key, cullDistSq)) continue;
            capi.Render.RenderMesh(mesh);
        }
        rapi.GlToggleBlend(false);

        // Submission only. RenderMesh queues work for the GPU and returns, so this
        // measures the CPU cost of the draw loop -- the uniform uploads, the culling and
        // the dictionary probes -- and not what the GPU then does with it.
        DrawCost.Add(phaseStart);

        rapi.GlEnableCullFace();
        prog.Stop();
    }

    bool SetupSectionTransform(long key, float cullDistSq)
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

            if (!frustum.BoxInView(relX, minY, relZ, relX + footprint, maxY, relZ + footprint))
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
        meshJobInFlight.Clear();
        lastSelectedFrame.Clear();
        snowLineY = pendingSnowLineY = 99999;
        seasonalRefreshActive = false;
        seasonalStateInitialized = false;
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




