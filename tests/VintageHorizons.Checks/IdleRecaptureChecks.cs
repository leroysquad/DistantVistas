using System.Collections.Concurrent;
using DistantVistas.Net;

namespace DistantVistas.Checks;

public static class IdleRecaptureChecks
{
    public static void Run(Check c)
    {
        StreamingGate(c);
        ExtraWorkGate(c);
        LoadedVsExploredGates(c);
        LoadedRecapturePolicy(c);
        ExploredLoadOnly(c);
        InferredSnowQuadrant(c);
        ExploredBookLoadAndRefuse(c);
        ConcurrentTryRequestAndComplete(c);
    }

    static void StreamingGate(Check c)
    {
        var gate = new LodStreamingGate();
        gate.Tick();
        gate.NotePlayer("p", 0, 0);
        c.False(gate.IsStreaming, "standing still with no arrivals is idle");

        gate.NotePlayer("p", 15, 0);
        c.False(gate.IsStreaming, "a 15-block step is not a walk-away");

        var walk = new LodStreamingGate();
        walk.Tick();
        walk.NotePlayer("p", 0, 0);
        walk.NotePlayer("p", 16, 0);
        c.True(walk.IsStreaming, "16 blocks in one step yields so vanilla can stream");

        for (int i = 0; i < LodStreamingGate.MoveBusyTicks; i++) walk.Tick();
        c.False(walk.IsStreaming, "move hold expires after MoveBusyTicks");

        var arrivals = new LodStreamingGate();
        arrivals.Tick();
        arrivals.NoteChunkArrival();
        c.False(arrivals.IsStreaming, "one chunk arrival is not a stream burst");
        arrivals.NoteChunkArrival();
        c.True(arrivals.IsStreaming, "two arrivals in the window yield extra work");

        for (int i = 0; i <= LodStreamingGate.ArrivalWindowTicks; i++) arrivals.Tick();
        c.False(arrivals.IsStreaming, "old arrivals fall out of the window");

        arrivals.SetVanillaBusy(true);
        c.True(arrivals.IsStreaming, "worldgen in flight is streaming");
        arrivals.SetVanillaBusy(false);
        c.False(arrivals.IsStreaming, "quiet generator is idle again");
    }

    static void ExtraWorkGate(Check c)
    {
        c.True(LodIdleRecapturePolicy.AllowExtraWork(false, false, false),
            "quiet idle may do extra work");
        c.False(LodIdleRecapturePolicy.AllowExtraWork(true, false, false),
            "streaming blocks extra loads");
        c.False(LodIdleRecapturePolicy.AllowExtraWork(false, true, false),
            "mesh pressure yields");
        c.False(LodIdleRecapturePolicy.AllowExtraWork(false, false, true),
            "join epoch yields");
        c.False(LodIdleRecapturePolicy.AllowExtraWork(false, false, false, true),
            "player busy (look/walk/hitch) blocks extra work");
        c.True(LodIdleRecapturePolicy.AllowExtraWork(false, false, false, false),
            "sit still still allows extra work");
    }

    static void LoadedVsExploredGates(Check c)
    {
        c.True(LodIdleRecapturePolicy.AllowLoadedRecapture(false, false),
            "sit still with no mesh pressure recaptures RAM columns");
        c.False(LodIdleRecapturePolicy.AllowLoadedRecapture(true, false),
            "looking or walking yields loaded recapture");
        c.False(LodIdleRecapturePolicy.AllowLoadedRecapture(false, true),
            "mesh pressure yields loaded recapture");
        c.True(LodIdleRecapturePolicy.AllowExploredLoad(false, false, false),
            "quiet idle may ask the server for an explored column");
        c.False(LodIdleRecapturePolicy.AllowExploredLoad(false, false, true),
            "chunk arrivals yield extra disk loads, not RAM recapture");
    }

    static void LoadedRecapturePolicy(Check c)
    {
        c.True(LodIdleRecapturePolicy.LoadedColumnNeedsRecapture(
            fullyCaptured: false, provisional: false, inferredSnow: false,
            month: 12, hasAnySnow: false, pendingVisit: false),
            "an incomplete quadrant always needs capture");
        c.True(LodIdleRecapturePolicy.LoadedColumnNeedsRecapture(
            fullyCaptured: true, provisional: true, inferredSnow: false,
            month: 12, hasAnySnow: false, pendingVisit: false),
            "provisional data is still work once the chunk is loaded");
        c.True(LodIdleRecapturePolicy.LoadedColumnNeedsRecapture(
            fullyCaptured: true, provisional: false, inferredSnow: true,
            month: 12, hasAnySnow: true, pendingVisit: false),
            "December inferred Cover snow recaptures when the chunk is loaded");
        c.False(LodIdleRecapturePolicy.LoadedColumnNeedsRecapture(
            fullyCaptured: true, provisional: false, inferredSnow: false,
            month: 12, hasAnySnow: true, pendingVisit: false),
            "December real FlagSnow-only is already accurate");
        c.True(LodIdleRecapturePolicy.LoadedColumnNeedsRecapture(
            fullyCaptured: true, provisional: false, inferredSnow: false,
            month: 6, hasAnySnow: true, pendingVisit: false),
            "June leftover snow still recaptures");
        c.False(LodIdleRecapturePolicy.LoadedColumnNeedsRecapture(
            fullyCaptured: true, provisional: false, inferredSnow: false,
            month: 6, hasAnySnow: false, pendingVisit: false),
            "June with no snow and no pending visit is done");
        c.True(LodIdleRecapturePolicy.LoadedColumnNeedsRecapture(
            fullyCaptured: true, provisional: false, inferredSnow: false,
            month: 6, hasAnySnow: false, pendingVisit: true),
            "alpine pending visit still recaptures in melt season");
    }

    static void ExploredLoadOnly(Check c)
    {
        c.True(LodIdleRecapturePolicy.MayLoadExplored(EnumColumnAction.Load),
            "an explored complete neighbourhood may load");
        c.False(LodIdleRecapturePolicy.MayLoadExplored(EnumColumnAction.Peek),
            "unexplored land is never generated");
        c.False(LodIdleRecapturePolicy.MayLoadExplored(EnumColumnAction.SkipFrontier),
            "a frontier neighbourhood is never loaded");

        var empty = new LodColumnMap();
        c.Eq(EnumColumnAction.Peek, empty.Classify(20, 20),
            "a missing column classifies as Peek and this path refuses it");

        var island = new LodColumnMap();
        island.Add(20, 20);
        c.Eq(EnumColumnAction.SkipFrontier, island.Classify(20, 20),
            "a lone savegame column is frontier, not a load");

        int r = LodColumnMap.SafeNeighbourhood;
        var full = new LodColumnMap();
        for (int dz = -r; dz <= r; dz++)
        for (int dx = -r; dx <= r; dx++)
            full.Add(20 + dx, 20 + dz);
        c.Eq(EnumColumnAction.Load, full.Classify(20, 20),
            "a complete 9x9 of explored columns is the only Load");
        c.True(LodIdleRecapturePolicy.MayLoadExplored(full.Classify(20, 20)),
            "that Load is the only action idle recapture will issue");
    }

    static void InferredSnowQuadrant(Check c)
    {
        var s = new LodSection();
        byte grassFlags = (byte)(LodPaletteEntry.FlagBaked | LodPaletteEntry.FlagFrostGround);
        int grass = s.FindOrAddPaletteEntry(10, 0x00305070, grassFlags, tintSlot: 2);
        s.SetColumn(0, new[] { LodSection.PackRun(grass, 40, 1) });
        c.False(s.QuadrantHasInferredSnow(0), "bare frost grass is not inferred snow");

        SeasonBakeChecks.SeedInferredSnow(s, grass, 0);
        c.True(s.HasInferredSnowSurface(), "seeded leftover Cover is FlagSnow+FlagBaked");
        c.True(s.QuadrantHasInferredSnow(0), "inferred snow is visible per quadrant");
        c.False(s.QuadrantHasInferredSnow(3), "a snow-free quadrant stays clean");

        var real = new LodSection();
        int snow = real.FindOrAddPaletteEntry(20, 0x00FFFFFF, LodPaletteEntry.FlagSnow, tintSlot: 0);
        real.SetColumn(0, new[] { LodSection.PackRun(snow, 40, 1) });
        c.True(real.QuadrantHasSnowSurface(0), "real snowlayer is FlagSnow");
        c.False(real.QuadrantHasInferredSnow(0), "real snowlayer is not inferred Cover");
        c.False(LodIdleRecapturePolicy.LoadedColumnNeedsRecapture(
            fullyCaptured: true, provisional: false,
            inferredSnow: real.QuadrantHasInferredSnow(0),
            month: 12, hasAnySnow: true, pendingVisit: false),
            "December real snow does not force a recapture");
        c.True(LodIdleRecapturePolicy.LoadedColumnNeedsRecapture(
            fullyCaptured: true, provisional: false,
            inferredSnow: s.QuadrantHasInferredSnow(0),
            month: 12, hasAnySnow: true, pendingVisit: false),
            "December inferred snow does force a recapture when loaded");
    }

    static void ExploredBookLoadAndRefuse(Check c)
    {
        int r = LodColumnMap.SafeNeighbourhood;
        var load = new LodExploredColumnBook();
        int loads = 0, probes = 0;
        for (int dz = -r; dz <= r; dz++)
        for (int dx = -r; dx <= r; dx++)
            load.CompleteProbe(20 + dx, 20 + dz, hit: true);

        c.Eq(ExploredLoadAttempt.Loading, load.TryRequest(20, 20, () => true, false,
            (_, _) => probes++, (_, _) => loads++),
            "a complete explored neighbourhood issues Load, never Peek");
        c.Eq(1, loads, "LoadChunkColumn runs once for that neighbourhood");
        c.Eq(0, probes, "known columns are not probed again");

        var miss = new LodExploredColumnBook();
        int missLoads = 0;
        c.Eq(ExploredLoadAttempt.Probing, miss.TryRequest(20, 20, () => true, false,
            (_, _) => { }, (_, _) => missLoads++),
            "unknown neighbours probe first");
        for (int dz = -r; dz <= r; dz++)
        for (int dx = -r; dx <= r; dx++)
            miss.CompleteProbe(20 + dx, 20 + dz, hit: false);
        c.Eq(ExploredLoadAttempt.Refused, miss.TryRequest(20, 20, () => true, false,
            (_, _) => { }, (_, _) => missLoads++),
            "unexplored land is refused, never generated");
        c.Eq(0, missLoads, "Peek never becomes LoadChunkColumn");

        var yield = new LodExploredColumnBook();
        for (int dz = -r; dz <= r; dz++)
        for (int dx = -r; dx <= r; dx++)
            yield.CompleteProbe(20 + dx, 20 + dz, hit: true);
        int yieldLoads = 0, idleCalls = 0;
        c.Eq(ExploredLoadAttempt.None, yield.TryRequest(20, 20, () => ++idleCalls == 1, false,
            (_, _) => { }, (_, _) => yieldLoads++),
            "yield after classify skips a new load");
        c.Eq(0, yieldLoads, "a yielded tick does not force-load");
    }

    static void ConcurrentTryRequestAndComplete(Check c)
    {
        var book = new LodExploredColumnBook();
        var errors = new ConcurrentBag<Exception>();
        const int requestors = 4;
        using var start = new Barrier(requestors + 1);
        using var done = new CountdownEvent(requestors + 1);

        void Run(Action body)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    start.SignalAndWait();
                    body();
                }
                catch (Exception e) { errors.Add(e); }
                finally { done.Signal(); }
            });
        }

        for (int t = 0; t < requestors; t++)
        {
            Run(() =>
            {
                for (int i = 0; i < 2500; i++)
                {
                    book.Tick();
                    book.TryRequest(20 + (i & 3), 20 + ((i >> 2) & 3),
                        () => (i & 7) != 0,
                        vanillaBusy: (i & 15) == 0,
                        startProbe: (_, _) => { },
                        loadColumn: (_, _) => { });
                }
            });
        }

        Run(() =>
        {
            int r = LodColumnMap.SafeNeighbourhood;
            for (int i = 0; i < 6000; i++)
            {
                for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                    book.CompleteProbe(20 + dx, 20 + dz, hit: (i & 1) == 0);
            }
        });

        c.True(done.Wait(30_000), "concurrent TryRequest/complete finished");
        c.True(errors.IsEmpty,
            errors.IsEmpty
                ? "TryRequest concurrent with complete does not throw"
                : "TryRequest concurrent with complete does not throw: " + errors.First().GetType().Name
                  + ": " + errors.First().Message);
    }
}
