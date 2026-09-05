using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DistantVistas;

/// <summary>
/// Decides whether the login visit sweep overlay should run or the player can enter play
/// immediately with persisted canvases.
/// </summary>
public static class LodLoginSweepGate
{
    public readonly record struct Result(
        bool RunSweep,
        string Reason,
        IReadOnlyList<LodLoginBakeAudit.Miss>? Misses = null);

    public static Result Decide(
        ICoreClientAPI capi,
        LodWorld world,
        LodPipeline pipeline,
        IList<Block> blocks,
        Block? plantTintFallback,
        System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf)
    {
        LodLoginSweepResume? resume = LodLoginSweepResume.TryLoad(capi);
        if (resume != null && resume.IsEligible(capi.World))
            return new Result(true, "resuming cancelled mid-sweep checkpoint");

        if (resume != null)
            LodLoginSweepResume.Delete(capi);

        int visited = LodLoginSweep.VisitedL0Keys(world).Count();
        if (visited == 0)
            return new Result(true, "empty canvas needs bootstrap sweep");

        List<LodLoginBakeAudit.Miss> misses = LodLoginBakeAudit.FindMisses(
            world, pipeline, blocks, plantTintFallback, untintedOf);
        if (misses.Count > 0)
            return new Result(true, $"{misses.Count} visited region(s) still incomplete", misses);

        LodLoginSweepComplete? complete = LodLoginSweepComplete.TryLoad(capi);
        if (complete != null)
        {
            if (complete.VisitedKeyCount < visited)
                return new Result(true, "visited canvas grew since last successful sweep");

            if (!LodLoginSweepWindow.IsWithin(capi.World, complete.Season, complete.SavedTotalDays))
                return new Result(true, "outside same-season / 30-day window since last sweep");
        }

        return new Result(false, "visited canvas complete within season window");
    }
}
