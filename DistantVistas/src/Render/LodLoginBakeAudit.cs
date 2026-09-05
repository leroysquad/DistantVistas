using Vintagestory.API.Common;

namespace DistantVistas;

/// <summary>
/// After the main login visit sweep, find L0 canvases that still need capture, bake, or
/// persistence before the overlay can release.
/// </summary>
public static class LodLoginBakeAudit
{
    public const int MaxResweepRounds = 1;

    public enum MissReason
    {
        None,
        LoadFailed,
        MissingSection,
        EmptyCapture,
        CapturePending,
        ProvisionalCapture,
        BakeIncomplete,
    }

    public readonly record struct Miss(long Key, MissReason Reason);

    public static List<Miss> FindMisses(
        LodWorld world,
        LodPipeline pipeline,
        IList<Block> blocks,
        Block? plantTintFallback,
        System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf)
    {
        var misses = new List<Miss>();
        foreach (long key in LodLoginSweep.VisitedL0Keys(world))
        {
            MissReason reason = Classify(world, pipeline, key, blocks, plantTintFallback, untintedOf);
            if (reason != MissReason.None)
                misses.Add(new Miss(key, reason));
        }
        return misses;
    }

    public static MissReason Classify(
        LodWorld world,
        LodPipeline pipeline,
        long l0Key,
        IList<Block> blocks,
        Block? plantTintFallback,
        System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf)
    {
        if (world.LoadFailed.Contains(l0Key))
            return MissReason.LoadFailed;

        if (!TryGetSection(world, l0Key, out LodSection? section))
            return MissReason.MissingSection;

        if (section!.CapturedColumns == 0)
            return MissReason.EmptyCapture;

        if (!pipeline.IsL0SectionCaptureIdle(l0Key))
            return MissReason.CapturePending;

        if (section.ProvisionalQuadrants != 0)
            return MissReason.ProvisionalCapture;

        if (NeedsBake(section, blocks, plantTintFallback, untintedOf))
            return MissReason.BakeIncomplete;

        return MissReason.None;
    }

    static bool TryGetSection(LodWorld world, long key, out LodSection? section)
    {
        if (world.Sections.TryGetValue(key, out section))
            return true;

        section = world.LoadFromStore?.Invoke(key);
        if (section == null) return false;

        world.InstallLoaded(key, section);
        return true;
    }

    static bool NeedsBake(
        LodSection section,
        IList<Block> blocks,
        Block? plantTintFallback,
        System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf)
    {
        for (int i = 0; i < section.Palette.Count; i++)
        {
            LodPaletteEntry entry = section.Palette[i];
            if (entry.BlockId <= 0 || entry.BlockId >= blocks.Count) continue;
            Block block = blocks[entry.BlockId];
            (int untinted, _) = untintedOf(block);
            if (!LodSeasonBake.CanBake(block, untinted, plantTintFallback)) continue;
            if ((entry.Flags & LodPaletteEntry.FlagBaked) == 0)
                return true;
        }
        return false;
    }

    public static string Describe(MissReason reason) => reason switch
    {
        MissReason.LoadFailed => "load failed",
        MissReason.MissingSection => "no section",
        MissReason.EmptyCapture => "no capture",
        MissReason.CapturePending => "capture pending",
        MissReason.ProvisionalCapture => "provisional capture",
        MissReason.BakeIncomplete => "bake incomplete",
        _ => "unknown",
    };
}
