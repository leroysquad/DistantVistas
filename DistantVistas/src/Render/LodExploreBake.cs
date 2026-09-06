using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DistantVistas;

/// <summary>
/// Budgeted explore-time bake: after capture while chunks are still loaded, lock each
/// column top to vanilla <c>GetColor</c> (<see cref="LodPaletteEntry.FlagBaked"/>)
/// so far LOD matches near green when the player leaves. Queued from capture apply;
/// drained on the game tick like <see cref="LodPipeline.ApplyCaptureResults"/>.
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

            if (!CanBakeSectionNow(capi.World, key))
            {
                // Chunks unloaded before drain — retry when player returns.
                if (queued.Add(key))
                    pending.Enqueue(key);
                continue;
            }

            int changed = LodSeasonBake.BakeSectionFromVisit(
                capi, section, key, plantTintFallback, untintedOf);
            if (changed <= 0)
                continue;

            pipeline.World.MarkChanged(key);
            pipeline.World.RenderDirty.Add(key);
            pipeline.InvalidateGpuMesh?.Invoke(key);
            pipeline.DrainLoginPersistence(1);
            baked++;
            SectionsBaked++;
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
}
