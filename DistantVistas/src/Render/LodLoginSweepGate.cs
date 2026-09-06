using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DistantVistas;

/// <summary>
/// Decides whether the login visit sweep overlay should run or the player can enter play
/// immediately with persisted canvases.
/// </summary>
public static class LodLoginSweepGate
{
    public readonly record struct Result(bool RunSweep, string Reason);

    public static Result Decide(
        ICoreClientAPI capi,
        LodWorld world,
        LodPipeline pipeline,
        IList<Block> blocks,
        Block? plantTintFallback,
        System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf)
    {
        string worldId = LodWorldKey.For(capi.World);

        LodLoginSweepResume? resume = LodLoginSweepResume.TryLoad(capi);
        if (resume != null && resume.IsEligible(capi.World))
            return new Result(true, "resuming cancelled mid-sweep checkpoint");

        if (resume != null)
            LodLoginSweepResume.Delete(capi);

        int visited = LodLoginSweep.VisitedL0Keys(world).Count();
        if (visited == 0)
            return new Result(true, "empty canvas needs bootstrap sweep");

        LodLoginSweepComplete? complete = LodLoginSweepComplete.TryLoad(capi);

        // First successful sweep for this world must expand land (bootstrap), not season-
        // refresh a tiny walked set — decide run before miss-repair / revisit paths.
        if (complete == null
            || string.IsNullOrEmpty(complete.WorldId)
            || !string.Equals(complete.WorldId, worldId, StringComparison.Ordinal))
            return new Result(true, "no successful sweep recorded yet for this world");

        // Prefer skip when a successful sweep is still in-window and the canvas did not grow —
        // even if FindMisses reports leftovers. User intent: do not re-canvas the same world
        // within the 30 in-game-day window. Never skip across worlds (0.8.24).
        if (complete.VisitedKeyCount >= visited
            && LodLoginSweepWindow.IsWithin(capi.World, complete.Season, complete.SavedTotalDays))
        {
            return new Result(false,
                "visited canvas complete within 30-day window (skip re-canvas)");
        }

        List<LodLoginBakeAudit.Miss> misses = LodLoginBakeAudit.FindMisses(
            world, pipeline, blocks, plantTintFallback, untintedOf);
        if (misses.Count > 0)
            return new Result(true, $"{misses.Count} visited region(s) still incomplete");

        if (complete.VisitedKeyCount < visited)
            return new Result(true, "visited canvas grew since last successful sweep");

        if (!LodLoginSweepWindow.IsWithin(capi.World, complete.Season, complete.SavedTotalDays))
            return new Result(true, "outside 30-day window since last sweep");

        return new Result(false, "visited canvas complete within 30-day window");
    }
}