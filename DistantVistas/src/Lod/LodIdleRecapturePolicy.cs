using DistantVistas.Net;

namespace DistantVistas;

/// <summary>
/// When an idle tick may force-queue a loaded column or ask the server to load an
/// already-explored one. Never generates. Peek (unexplored) and frontier neighbourhoods
/// stay untouched. Pure so the winter inferred-snow rule cannot drift from NeedsCapture.
/// </summary>
public static class LodIdleRecapturePolicy
{
    public const int ForceQueuePerTick = 2;
    public const int LoadsPerTick = 1;
    public const int MaxInFlight = 8;
    public const int SkipWalkCap = 64;

    /// <summary>
    /// Recapture columns already in RAM. Join epoch and chunk arrivals must not
    /// block this: that left winter snow and the walked trail dead until /time.
    /// </summary>
    public static bool AllowLoadedRecapture(bool cameraBusy, bool meshPressure) =>
        !cameraBusy && !meshPressure;

    /// <summary>Ask the server for an explored column. Yields while vanilla is streaming.</summary>
    public static bool AllowExploredLoad(bool cameraBusy, bool meshPressure, bool streaming) =>
        AllowLoadedRecapture(cameraBusy, meshPressure) && !streaming;

    public static bool AllowExtraWork(
        bool streaming, bool meshPressure, bool joinEpoch, bool playerBusy = false) =>
        AllowExploredLoad(playerBusy, meshPressure, streaming) && !joinEpoch;

    /// <summary>
    /// A loaded column still has something to teach the cache. Inferred Cover snow
    /// (FlagSnow+FlagBaked) recaptures in every month, including December: sitting
    /// still with vanilla chunks in RAM must replace the invented hats with real
    /// snowlayer / bare ground. Real FlagSnow-only in winter is already accurate.
    /// </summary>
    public static bool LoadedColumnNeedsRecapture(
        bool fullyCaptured, bool provisional, bool inferredSnow,
        int month, bool hasAnySnow, bool pendingVisit)
    {
        if (!fullyCaptured || provisional) return true;
        if (inferredSnow) return true;
        return LodSeasonBake.RecaptureLoadedSnowForMelt(month, hasAnySnow, pendingVisit);
    }

    /// <summary>
    /// Only Load is safe. Peek would generate unexplored land. SkipFrontier would
    /// generate neighbours. Both are forbidden for this path.
    /// </summary>
    public static bool MayLoadExplored(EnumColumnAction action) =>
        action == EnumColumnAction.Load;
}
