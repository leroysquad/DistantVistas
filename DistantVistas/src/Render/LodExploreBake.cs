using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DistantVistas;

/// <summary>
/// Budgeted explore-time <strong>live visit bake</strong> — same
/// <see cref="LodSeasonBake.BakeSectionFromVisit"/> / <c>GetColor</c> path as login sweep,
/// not shader-repro or live-tint sheets. Queues L0 including FlagBaked so a walk
/// into loaded winter chunks can overwrite stored May/fall tops. Drained 1–2
/// L0 sections/tick while chunks load.
/// </summary>
public sealed class LodExploreBake
{
    /// <summary>L0 sections baked per game tick during normal play.</summary>
    public const int SectionsPerTick = 1;

    /// <summary>Extra drain while capture results are stacked (still time-budgeted).</summary>
    public const int SectionsPerTickBusy = 2;

    readonly Queue<long> pending = new();
    readonly HashSet<long> queued = new();
    readonly HashSet<long> readyAttempted = new();

    public int PendingCount => pending.Count;
    public int SectionsBaked { get; private set; }
    public int LastDrainSpins { get; private set; }

    public void Clear()
    {
        pending.Clear();
        queued.Clear();
        readyAttempted.Clear();
    }

    public void ResetAttempt(long sectionKey) => readyAttempted.Remove(sectionKey);

    public void Queue(long sectionKey, LodSection section, bool defer)
    {
        if (defer) return;
        if (LodWorld.KeyLevel(sectionKey) != 0) return;
        if (readyAttempted.Contains(sectionKey)) return;
        _ = section;
        if (!queued.Add(sectionKey)) return;
        pending.Enqueue(sectionKey);
    }

    /// <summary>Drain queued L0 sections. Returns how many sections were baked this tick.</summary>
    public int Drain(
        ICoreClientAPI capi,
        LodPipeline pipeline,
        Block? plantTintFallback,
        System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf,
        int captureBacklog)
    {
        if (pipeline.DeferLegacyHeal || pending.Count == 0) return 0;

        int budget = captureBacklog >= LodPipeline.CaptureBusyThreshold
            ? SectionsPerTickBusy
            : SectionsPerTick;
        int baked = 0;
        // One pass over the current queue. Re-queued not-ready keys must not be
        // retried this tick — budget only dropped on a successful bake, so a
        // live-tint L0 whose sweep chunks are already unloaded livelocked Tick.
        int remaining = pending.Count;
        LastDrainSpins = 0;

        while (budget > 0 && remaining-- > 0 && pending.Count > 0)
        {
            LastDrainSpins++;
            long key = pending.Dequeue();
            queued.Remove(key);

            if (!pipeline.World.Sections.TryGetValue(key, out LodSection? section)
                || section == null)
            {
                continue;
            }

            bool ready = CanBakeSectionNow(capi.World, key);
            if (!ready)
            {
                if (queued.Add(key))
                    pending.Enqueue(key);
                continue;
            }

            // #region agent log
            string prevVisit = LodSeasonBake.DebugVisitKind;
            LodSeasonBake.DebugVisitKind = "walk";
            LodSeasonBake.FlagBakedLuma(section, out int lumaBeforeN, out int lumaBefore, out int paleBefore);
            // #endregion
            int changed = LodSeasonBake.BakeSectionFromVisit(
                capi, section, key, plantTintFallback, untintedOf);
            // #region agent log
            LodSeasonBake.DebugVisitKind = prevVisit;
            LodSeasonBake.FlagBakedLuma(section, out int lumaAfterN, out int lumaAfter, out int paleAfter);
            // #endregion

            if (changed > 0)
            {
                pipeline.World.MarkChanged(key);
                pipeline.World.RequestGpuSwap(key);
                pipeline.DrainLoginPersistence(1);
                baked++;
                SectionsBaked++;
                // #region agent log
                if (baked == 1 && SectionsBaked <= 24)
                {
                    try
                    {
                        System.IO.File.AppendAllText(
                            @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                            "{\"sessionId\":\"40cccb\",\"runId\":\"post-fix-walk\",\"hypothesisId\":\"H-WHITE-1\",\"location\":\"LodExploreBake.Drain\",\"message\":\"explore-invalidate\",\"data\":{\"swap\":true,\"keyL0\":true,\"sx\":"
                            + LodWorld.KeySx(key) + ",\"sz\":" + LodWorld.KeySz(key)
                            + ",\"changed\":" + changed
                            + ",\"pending\":" + pending.Count
                            + ",\"dirty\":" + pipeline.World.RenderDirty.Count
                            + ",\"mipDirty\":" + pipeline.World.MipDirty.Count
                            + ",\"baked\":" + SectionsBaked
                            + ",\"lumaBefore\":" + lumaBefore
                            + ",\"lumaAfter\":" + lumaAfter
                            + ",\"nBefore\":" + lumaBeforeN
                            + ",\"nAfter\":" + lumaAfterN
                            + ",\"paleBefore\":" + paleBefore
                            + ",\"paleAfter\":" + paleAfter
                            + ",\"towardWhite\":" + (lumaAfter > lumaBefore + 12 ? "true" : "false")
                            + "},\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n");
                    }
                    catch { }
                }
                if (SectionsBaked <= 6)
                    TryLogWalkVsHorizon(pipeline.World, key, lumaAfter, paleAfter);
                // #endregion
            }
            readyAttempted.Add(key);
            budget--;
        }

        return baked;
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

    // #region agent log
    static void TryLogWalkVsHorizon(LodWorld world, long walkedKey, int walkLuma, int walkPale)
    {
        int sx = LodWorld.KeySx(walkedKey);
        int sz = LodWorld.KeySz(walkedKey);
        long farKey = 0;
        int farLuma = -1;
        int farPale = 0;
        int farN = 0;
        foreach (var kv in world.Sections)
        {
            if (LodWorld.KeyLevel(kv.Key) != 0 || kv.Value == null) continue;
            int dsx = LodWorld.KeySx(kv.Key) - sx;
            int dsz = LodWorld.KeySz(kv.Key) - sz;
            if (dsx * dsx + dsz * dsz < 36) continue;
            LodSeasonBake.FlagBakedLuma(kv.Value, out farN, out farLuma, out farPale);
            if (farN == 0) continue;
            farKey = kv.Key;
            break;
        }
        if (farN == 0) return;
        try
        {
            System.IO.File.AppendAllText(
                @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                "{\"sessionId\":\"40cccb\",\"runId\":\"post-fix-walk\",\"hypothesisId\":\"H-WHITE-4\",\"location\":\"LodExploreBake.Drain\",\"message\":\"walk-vs-horizon\",\"data\":{\"walkSx\":"
                + sx + ",\"walkSz\":" + sz
                + ",\"walkLuma\":" + walkLuma
                + ",\"walkPale\":" + walkPale
                + ",\"farSx\":" + LodWorld.KeySx(farKey)
                + ",\"farSz\":" + LodWorld.KeySz(farKey)
                + ",\"farLuma\":" + farLuma
                + ",\"farPale\":" + farPale
                + ",\"farN\":" + farN
                + "},\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n");
        }
        catch { }
    }
    // #endregion
}
