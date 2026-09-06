using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DistantVistas;

/// <summary>
/// Budgeted explore-time <strong>live visit bake</strong> — same
/// <see cref="LodSeasonBake.BakeSectionFromVisit"/> / <c>GetColor</c> path as login sweep,
/// not shader-repro or live-tint sheets. Queued when capture apply could not visit-bake
/// (chunks not yet resident); drained 1–2 L0 sections/tick while chunks load.
/// </summary>
public sealed class LodExploreBake
{
    /// <summary>L0 sections baked per game tick during normal play.</summary>
    public const int SectionsPerTick = 1;

    /// <summary>Extra drain while capture results are stacked (still time-budgeted).</summary>
    public const int SectionsPerTickBusy = 2;

    readonly Queue<long> pending = new();
    readonly HashSet<long> queued = new();

    public int PendingCount => pending.Count;
    public int SectionsBaked { get; private set; }

    public void Clear()
    {
        pending.Clear();
        queued.Clear();
    }

    public void Queue(long sectionKey, LodSection section, bool defer)
    {
        if (defer) return;
        if (LodWorld.KeyLevel(sectionKey) != 0) return;
        if (!SectionHasLiveTint(section)) return;
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

        while (budget > 0 && pending.Count > 0)
        {
            long key = pending.Dequeue();
            queued.Remove(key);

            if (!pipeline.World.Sections.TryGetValue(key, out LodSection? section)
                || section == null)
            {
                continue;
            }

            if (!SectionHasLiveTint(section))
                continue;

            int changed = LodSeasonBake.BakeSectionFromVisit(
                capi, section, key, plantTintFallback, untintedOf);

            if (changed > 0)
            {
                pipeline.World.MarkChanged(key);
                pipeline.World.RenderDirty.Add(key);
                pipeline.InvalidateGpuMesh?.Invoke(key);
                pipeline.DrainLoginPersistence(1);
                baked++;
                SectionsBaked++;
                budget--;
            }

            if (SectionHasLiveTint(section) && !CanBakeSectionNow(capi.World, key))
            {
                if (queued.Add(key))
                    pending.Enqueue(key);
            }
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
}
