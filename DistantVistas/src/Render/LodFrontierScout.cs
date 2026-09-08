using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;

namespace DistantVistas;

/// <summary>
/// Quiet midground filler: streams + season-bakes L0 between the capture rim and
/// late Farseer (<see cref="LodCoveragePolicy.HorizonDrawScale"/>). Hard per-tick
/// caps, yield when queues are hot, steady drip. Force-loads with
/// SetChunkColumnVisible only - never moves the player.
/// </summary>
public sealed class LodFrontierScout
{
    public const int CooldownTicks = 40;
    public const int MaxChunkWaitTicks = 60;
    public const int ChunkVisibleRadius = 2;
    public const int SweepRadiusChunks = 2;
    public const int MaxPendingYield = 48;
    public const int MaxExplorePendingYield = 24;
    public const int MaxCaptureResultsYield = 8;
    public const int MaxPlanCandidates = 32;
    public const float LeadConeCos = 0.9659258f; // cos(15 deg)
    public const float MinRingScale = 1.3f;

    enum Phase : byte
    {
        Idle,
        StreamWait,
        Cooldown
    }

    Phase phase = Phase.Idle;
    long targetKey;
    int waitTicks;
    int cooldownLeft;
    int scanIndex;

    public int TargetsCompleted { get; private set; }
    public int LastTargetSx { get; private set; } = int.MinValue;
    public int LastTargetSz { get; private set; } = int.MinValue;
    public string PhaseName => phase.ToString();

    public void Reset()
    {
        phase = Phase.Idle;
        targetKey = 0;
        waitTicks = 0;
        cooldownLeft = 0;
    }

    public void Tick(
        ICoreClientAPI capi,
        LodPipeline pipeline,
        LodTerrainRenderer renderer)
    {
        if (!pipeline.Active || !renderer.LoginBakeComplete) return;
        if (renderer.LoginBakeOverlayActive || renderer.LoginBakeBlocked) return;
        if (capi.IsGamePaused) return;
        if (LodLoginBakeCharacterWait.IsPending(capi)) return;

        if (phase == Phase.Cooldown)
        {
            if (--cooldownLeft > 0) return;
            phase = Phase.Idle;
            return;
        }

        if (QueuesHot(pipeline)) return;

        switch (phase)
        {
            case Phase.Idle:
                TryStart(capi, pipeline, renderer);
                break;
            case Phase.StreamWait:
                TickStreamWait(capi, pipeline);
                break;
        }
    }

    static bool QueuesHot(LodPipeline pipeline) =>
        pipeline.PendingColumns > MaxPendingYield
        || pipeline.ExploreBake.PendingCount > MaxExplorePendingYield
        || pipeline.Worker.CaptureResults.Count > MaxCaptureResultsYield
        || pipeline.Worker.PendingCaptures > MaxPendingYield
        || pipeline.Worker.PendingMeshes > 8;

    void TryStart(
        ICoreClientAPI capi,
        LodPipeline pipeline,
        LodTerrainRenderer renderer)
    {
        if (!TryPickTarget(capi, pipeline, renderer, out long key, out bool needsStream))
            return;

        targetKey = key;
        LastTargetSx = LodWorld.KeySx(key);
        LastTargetSz = LodWorld.KeySz(key);

        if (!needsStream)
        {
            if (pipeline.World.Sections.TryGetValue(key, out LodSection? section))
                pipeline.ExploreBake.Queue(key, section, pipeline.DeferLegacyHeal);
            TargetsCompleted++;
            EnterCooldown();
            return;
        }

        var (x, _, z) = LodLoginSweep.VisitPosition(capi.World, key);
        int dim = capi.World.Player.Entity.Pos.Dimension;
        LodLoginBakePlayerMove.RequestChunkColumnsVisible(
            capi, x, z, dim, ChunkVisibleRadius, "frontier-visible",
            LodChunkRequestPriority.Background);

        if (LodLoginSweep.AllMapChunksLoaded(capi.World.BlockAccessor, key))
        {
            FinishCapture(capi, pipeline);
            return;
        }

        phase = Phase.StreamWait;
        waitTicks = 0;
    }

    void TickStreamWait(ICoreClientAPI capi, LodPipeline pipeline)
    {
        waitTicks++;
        if (LodLoginSweep.AllMapChunksLoaded(capi.World.BlockAccessor, targetKey))
        {
            FinishCapture(capi, pipeline);
            return;
        }

        if (waitTicks < MaxChunkWaitTicks) return;
        EnterCooldown();
    }

    void FinishCapture(ICoreClientAPI capi, LodPipeline pipeline)
    {
        if (targetKey == 0)
        {
            EnterCooldown();
            return;
        }

        int footprint = LodSection.SectionBlocks;
        int sx = LodWorld.KeySx(targetKey);
        int sz = LodWorld.KeySz(targetKey);
        int cx = (sx * footprint) / GlobalConstants.ChunkSize;
        int cz = (sz * footprint) / GlobalConstants.ChunkSize;
        pipeline.SweepLoadedColumns(cx, cz, SweepRadiusChunks, forceRecapture: false, rowsPerCall: 1);

        if (pipeline.World.Sections.TryGetValue(targetKey, out LodSection? section))
            pipeline.ExploreBake.Queue(targetKey, section, pipeline.DeferLegacyHeal);

        TargetsCompleted++;
        EnterCooldown();
    }

    void EnterCooldown()
    {
        phase = Phase.Cooldown;
        cooldownLeft = CooldownTicks;
        targetKey = 0;
        waitTicks = 0;
    }

    bool TryPickTarget(
        ICoreClientAPI capi,
        LodPipeline pipeline,
        LodTerrainRenderer renderer,
        out long bestKey,
        out bool needsStream)
    {
        bestKey = 0;
        needsStream = true;

        EntityPos pos = capi.World.Player.Entity.Pos;
        double px = pos.X;
        double pz = pos.Z;
        double vd = renderer.LiveViewDistance;
        if (vd <= 0) vd = 256;
        double maxR = LodCoveragePolicy.HorizonDrawDistance(vd);
        // Do not clamp to EffectiveFarDistance: that is the *meshed* rim and would
        // trap post-login fill inside a thin ring instead of Farseer onset + 700.
        double minR = vd * MinRingScale;
        if (minR >= maxR) return false;

        float yaw = pos.Yaw;
        double lookX = -Math.Sin(yaw);
        double lookZ = -Math.Cos(yaw);
        double lookLen = Math.Sqrt(lookX * lookX + lookZ * lookZ);
        if (lookLen > 1e-6)
        {
            lookX /= lookLen;
            lookZ /= lookLen;
        }

        int sectionBlocks = LodSection.SectionBlocks;
        LodWorld world = pipeline.World;

        // Pass A: incomplete FlagBaked already resident (no stream).
        long bakeKey = 0;
        double bakeScore = double.MinValue;
        int bakeChecked = 0;
        foreach (long key in world.HasDataSet)
        {
            if (LodWorld.KeyLevel(key) != 0) continue;
            if (++bakeChecked > 256) break;
            if (!world.Sections.TryGetValue(key, out LodSection? section)) continue;
            if (!LodExploreBake.SectionHasLiveTint(section)) continue;

            double cx = (LodWorld.KeySx(key) + 0.5) * sectionBlocks;
            double cz = (LodWorld.KeySz(key) + 0.5) * sectionBlocks;
            double dx = cx - px;
            double dz = cz - pz;
            double dist = Math.Sqrt(dx * dx + dz * dz);
            if (dist < minR || dist > maxR) continue;

            double score = ScoreCandidate(dx, dz, dist, lookX, lookZ, forBake: true);
            if (score > bakeScore)
            {
                bakeScore = score;
                bakeKey = key;
            }
        }
        if (bakeKey != 0)
        {
            bestKey = bakeKey;
            needsStream = false;
            return true;
        }

        // Pass B: missing L0 in ring ù lead cone first, then far.
        long missKey = 0;
        double missScore = double.MinValue;
        for (int i = 0; i < MaxPlanCandidates; i++)
        {
            scanIndex++;
            double t = (scanIndex % 4096) / 4096.0;
            double radius = minR + (maxR - minR) * t;
            double angle = scanIndex * 2.399963229728653;
            double wx = px + Math.Cos(angle) * radius;
            double wz = pz + Math.Sin(angle) * radius;
            if (wx < 0 || wz < 0) continue;

            int sx = (int)Math.Floor(wx / sectionBlocks);
            int sz = (int)Math.Floor(wz / sectionBlocks);
            if (sx < 0 || sz < 0) continue;
            long key = LodWorld.SectionKey(0, sx, sz);
            if (world.HasDataSet.Contains(key)) continue;

            double cx = (sx + 0.5) * sectionBlocks;
            double cz = (sz + 0.5) * sectionBlocks;
            double dx = cx - px;
            double dz = cz - pz;
            double dist = Math.Sqrt(dx * dx + dz * dz);
            if (dist < minR || dist > maxR) continue;

            double score = ScoreCandidate(dx, dz, dist, lookX, lookZ, forBake: false);
            if (score > missScore)
            {
                missScore = score;
                missKey = key;
            }
        }

        if (missKey == 0) return false;
        bestKey = missKey;
        needsStream = true;
        return true;
    }

    static double ScoreCandidate(
        double dx,
        double dz,
        double dist,
        double lookX,
        double lookZ,
        bool forBake)
    {
        double inv = dist > 1e-3 ? 1.0 / dist : 0;
        double dot = (dx * lookX + dz * lookZ) * inv;
        bool inCone = dot >= LeadConeCos;
        double score = (inCone ? 1_000_000.0 : 0.0) + dist;
        if (forBake) score += 500_000.0;
        return score;
    }
}
