using System.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DistantVistas;

/// <summary>
/// Budgeted explore-time <strong>live visit bake</strong> — same
/// <see cref="LodSeasonBake.BakeSectionFromVisit"/> / <c>GetColor</c> path as login sweep,
/// not shader-repro or live-tint sheets.
///
/// Contract for chunked / incomplete work:
/// <list type="bullet">
/// <item>Capture already published the section mesh via <c>RequestGpuSwap</c> — incomplete
/// bake never drops drawing of that land.</item>
/// <item>Each drain paints more columns, remeshes what changed, and re-queues the same
/// L0 until every captured top is done. Time elongates; work is not abandoned.</item>
/// <item>In-progress sections are finished before a new one starts, so paint fills in
/// steadily instead of many half-baked cells competing.</item>
/// </list>
/// </summary>
public sealed class LodExploreBake
{
    /// <summary>L0 sections started per game tick during normal play (finish-first).</summary>
    public const int SectionsPerTick = 1;

    /// <summary>Extra start while capture results are stacked (still time-budgeted).</summary>
    public const int SectionsPerTickBusy = 2;

    /// <summary>
    /// Hard ceiling on explore GetColor work per tick. Spreads a full L0 across
    /// several ticks so discover walking does not freeze the frame.
    /// </summary>
    public const double DrainBudgetMs = 2.5;

    /// <summary>Captured tops painted per drain call (chunked walk bake).</summary>
    public const int ColumnsPerDrain = 96;

    /// <summary>
    /// Max not-ready / map-chunk probes per tick when nothing is in progress.
    /// </summary>
    public const int MaxReadinessSpinsPerTick = 8;

    readonly Queue<long> pending = new();
    readonly HashSet<long> queued = new();
    readonly HashSet<long> readyAttempted = new();
    readonly Dictionary<long, int> resumeCol = new();

    /// <summary>L0 currently mid-bake; finished before any other pending key starts.</summary>
    long inProgressKey;

    public int PendingCount => pending.Count + (inProgressKey != 0 ? 1 : 0);
    public int SectionsBaked { get; private set; }
    public int LastDrainSpins { get; private set; }

    public void Clear()
    {
        pending.Clear();
        queued.Clear();
        readyAttempted.Clear();
        resumeCol.Clear();
        inProgressKey = 0;
    }

    /// <summary>
    /// Soft-release handoff: overlay paint queue + partial resumes continue under
    /// <see cref="PlayModeBakeBudget"/> instead of being dropped on overlay end.
    /// </summary>
    public void HandoffFromLogin(
        IEnumerable<long> readyKeys,
        IReadOnlyDictionary<long, int> partialCols,
        LodPipeline pipeline)
    {
        foreach (KeyValuePair<long, int> kv in partialCols)
        {
            resumeCol[kv.Key] = kv.Value;
            readyAttempted.Remove(kv.Key);
            queued.Remove(kv.Key);
            if (inProgressKey == 0)
                inProgressKey = kv.Key;
        }

        foreach (long key in readyKeys)
        {
            if (resumeCol.ContainsKey(key)) continue;
            if (!pipeline.World.Sections.TryGetValue(key, out LodSection? section) || section == null)
                continue;
            Queue(key, section, defer: false);
        }
    }

    /// <summary>
    /// Queue resident L0 that still need visit GetColor, near player first.
    /// </summary>
    public int SeedUnfinishedSections(LodWorld world, double px, double pz, int maxScan = 512)
    {
        int added = 0;
        int scanned = 0;
        var candidates = new List<(double DistSq, long Key)>(64);
        foreach (long key in world.HasDataSet)
        {
            if (LodWorld.KeyLevel(key) != 0) continue;
            if (++scanned > maxScan) break;
            if (readyAttempted.Contains(key) || resumeCol.ContainsKey(key) || queued.Contains(key))
                continue;
            if (key == inProgressKey) continue;
            if (!world.Sections.TryGetValue(key, out LodSection? section) || section == null)
                continue;
            if (!SectionHasLiveTint(section)) continue;

            int sb = LodSection.SectionBlocks;
            double cx = (LodWorld.KeySx(key) + 0.5) * sb - px;
            double cz = (LodWorld.KeySz(key) + 0.5) * sb - pz;
            candidates.Add((cx * cx + cz * cz, key));
        }

        candidates.Sort((a, b) => a.DistSq.CompareTo(b.DistSq));
        for (int i = 0; i < candidates.Count; i++)
        {
            long key = candidates[i].Key;
            if (!world.Sections.TryGetValue(key, out LodSection? section) || section == null)
                continue;
            int before = pending.Count;
            Queue(key, section, defer: false);
            if (pending.Count > before) added++;
        }

        return added;
    }

    public void ReprioritizeNear(double px, double pz, int nearBlocks)
    {
        if (pending.Count < 2) return;
        double nearRsq = (double)nearBlocks * nearBlocks;
        int sb = LodSection.SectionBlocks;
        int n = pending.Count;
        var nearQ = new Queue<long>();
        var farQ = new Queue<long>();
        for (int i = 0; i < n; i++)
        {
            long key = pending.Dequeue();
            queued.Remove(key);
            double cx = (LodWorld.KeySx(key) + 0.5) * sb - px;
            double cz = (LodWorld.KeySz(key) + 0.5) * sb - pz;
            if (cx * cx + cz * cz <= nearRsq)
                nearQ.Enqueue(key);
            else
                farQ.Enqueue(key);
        }

        while (nearQ.Count > 0)
        {
            long key = nearQ.Dequeue();
            if (queued.Add(key))
                pending.Enqueue(key);
        }

        while (farQ.Count > 0)
        {
            long key = farQ.Dequeue();
            if (queued.Add(key))
                pending.Enqueue(key);
        }
    }

    public void ResetAttempt(long sectionKey)
    {
        readyAttempted.Remove(sectionKey);
        resumeCol.Remove(sectionKey);
        if (inProgressKey == sectionKey) inProgressKey = 0;
    }

    public void Queue(long sectionKey, LodSection section, bool defer)
    {
        if (defer) return;
        if (LodWorld.KeyLevel(sectionKey) != 0) return;
        if (readyAttempted.Contains(sectionKey)) return;
        _ = section;
        // Already mid-bake for this key — resume owns it; do not duplicate in pending.
        if (sectionKey == inProgressKey || resumeCol.ContainsKey(sectionKey)) return;
        if (!queued.Add(sectionKey)) return;
        pending.Enqueue(sectionKey);
    }

    /// <summary>
    /// Drain queued L0 sections under the wall budget. Incomplete sections stay
    /// resident and keep remeshing; only the GetColor work is elongated.
    /// </summary>
    public int Drain(
        ICoreClientAPI capi,
        LodPipeline pipeline,
        Block? plantTintFallback,
        System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf,
        int captureBacklog)
    {
        PlayModeBakeBudget.TickBudget budget = pipeline.DiscoverOnly
            ? PlayModeBakeBudget.Last
            : PlayModeBakeBudget.Compute(
                discoverOnly: false,
                paused: false,
                captureBacklog: captureBacklog,
                applyMs: 0);
        return Drain(capi, pipeline, plantTintFallback, untintedOf, captureBacklog, budget);
    }

    public int Drain(
        ICoreClientAPI capi,
        LodPipeline pipeline,
        Block? plantTintFallback,
        System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf,
        int captureBacklog,
        PlayModeBakeBudget.TickBudget budget)
    {
        if (pipeline.DeferLegacyHeal) return 0;
        if (!budget.AllowExploreDrain) return 0;
        if (pending.Count == 0 && inProgressKey == 0) return 0;

        int startsLeft = budget.ExploreStarts;
        int baked = 0;
        int readinessSpins = 0;
        LastDrainSpins = 0;
        long drainStart = Stopwatch.GetTimestamp();
        double wallMs = budget.ExploreWallMs;
        int columnsPerDrain = budget.ExploreColumns;
        long drainBudgetTicks = (long)(Stopwatch.Frequency * wallMs / 1000.0);

        while (Stopwatch.GetTimestamp() - drainStart < drainBudgetTicks)
        {
            LastDrainSpins++;

            if (!TryTakeNext(capi, pipeline, ref readinessSpins, ref startsLeft, out long key, out LodSection section))
                break;

            resumeCol.TryGetValue(key, out int startCol);
            double remainMs = wallMs
                - 1000.0 * (Stopwatch.GetTimestamp() - drainStart) / Stopwatch.Frequency;
            if (remainMs < 0.4) remainMs = 0.4;

            int changed = LodSeasonBake.BakeSectionFromVisitChunked(
                capi, section, key, plantTintFallback, untintedOf,
                startCol, columnsPerDrain, remainMs,
                out int nextCol, out bool complete);

            if (!complete)
            {
                resumeCol[key] = nextCol;
                inProgressKey = key;
            }
            else
            {
                resumeCol.Remove(key);
                readyAttempted.Add(key);
                if (inProgressKey == key) inProgressKey = 0;
            }

            if (changed > 0)
            {
                pipeline.World.MarkChanged(key);
                pipeline.World.RequestGpuSwap(key);
                if (complete)
                    pipeline.DrainLoginPersistence(Math.Min(budget.SaveRows, 1));
                baked++;
                if (complete) SectionsBaked++;
            }

            if (!complete || Stopwatch.GetTimestamp() - drainStart >= drainBudgetTicks)
                break;
        }

        return baked;
    }

    bool TryTakeNext(
        ICoreClientAPI capi,
        LodPipeline pipeline,
        ref int readinessSpins,
        ref int startsLeft,
        out long key,
        out LodSection section)
    {
        key = 0;
        section = null!;

        // Finish-first: never abandon a half-baked L0 for a newer arrival.
        if (inProgressKey != 0)
        {
            key = inProgressKey;
            if (!pipeline.World.Sections.TryGetValue(key, out LodSection? prog) || prog == null)
            {
                resumeCol.Remove(key);
                inProgressKey = 0;
                return TryTakeNext(capi, pipeline, ref readinessSpins, ref startsLeft, out key, out section);
            }

            if (!CanBakeSectionNow(capi.World, key))
            {
                // Chunks unloaded mid-bake — keep resume; try again when maps return.
                // Do not drop the section or clear resumeCol.
                return false;
            }

            section = prog;
            return true;
        }

        if (startsLeft <= 0 || pending.Count == 0) return false;

        int guard = pending.Count;
        while (guard-- > 0 && pending.Count > 0)
        {
            key = pending.Dequeue();
            queued.Remove(key);

            if (!pipeline.World.Sections.TryGetValue(key, out LodSection? next) || next == null)
            {
                resumeCol.Remove(key);
                continue;
            }

            if (!CanBakeSectionNow(capi.World, key))
            {
                if (queued.Add(key))
                    pending.Enqueue(key);
                if (++readinessSpins >= MaxReadinessSpinsPerTick)
                    return false;
                continue;
            }

            startsLeft--;
            section = next;
            inProgressKey = key;
            return true;
        }

        return false;
    }

    public static bool SectionHasLiveTint(LodSection section)
    {
        for (int i = 0; i < section.Palette.Count; i++)
        {
            LodPaletteEntry entry = section.Palette[i];
            if ((entry.Flags & LodPaletteEntry.FlagBaked) != 0) continue;
            if (entry.TintSlot != LodTintRegistry.SlotNone) return true;
        }
        return false;
    }

    public static bool CanBakeSectionNow(IClientWorldAccessor world, long l0Key) =>
        LodLoginSweep.AllMapChunksLoaded(world.BlockAccessor, l0Key);
}
