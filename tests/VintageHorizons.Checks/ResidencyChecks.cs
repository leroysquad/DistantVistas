namespace DistantVistas.Checks;

/// <summary>
/// When a section may be touched, and when the caller has to wait for it.
///
/// This rule is why an evicted section can be reloaded without stalling the frame, and
/// also why doing it wrong is expensive rather than merely slow: creating an empty
/// section for a key that has a stored row shadows that row, and the empty one is then
/// written back over it. That is lost terrain, not a lost frame.
///
/// Three callers depend on the rule and none of them tested it. Mip propagation uses
/// EnsureResident, the selection walk uses TryGetForRender, and capture now uses
/// EnsureResident too.
/// </summary>
public static class ResidencyChecks
{
    public static void Run(Check c)
    {
        NothingToLoad(c);
        StoredButEvicted(c);
        AlreadyInFlight(c);
        FailedLoadIsRemembered(c);
        NoStorageThreadFallsBackToInline(c);
        LoadedSectionNeverClobbersALiveOne(c);
        IncompleteLeavesDoNotReplaceParentCoverage(c);
    }

    /// <summary>
    /// A key with no stored row is "proceed". The caller is about to create it, which is
    /// correct precisely because there is nothing on disk for it to shadow.
    /// </summary>
    static void NothingToLoad(Check c)
    {
        var world = NewWorld(out List<long> requested);

        c.False(world.TryGetForRender(Key, out _), "an unknown key has no section to render");
        c.True(world.EnsureResident(Key), "an unknown key is resident enough to proceed");
        c.Eq(0, requested.Count, "and nothing was asked of the storage thread");
    }

    /// <summary>
    /// The case the whole mechanism exists for: the row is on disk, the section is not in
    /// RAM. The caller must be told to wait, and exactly one background read must start.
    /// </summary>
    static void StoredButEvicted(Check c)
    {
        var world = NewWorld(out List<long> requested);
        world.InstallStoredKey(0, 3, 4, applyToParent: false);

        c.False(world.TryGetForRender(Key, out _), "a stored-but-evicted section is not renderable yet");
        c.False(world.EnsureResident(Key), "and the caller must wait rather than create an empty one");
        c.SeqEq(new[] { Key }, requested, "exactly one background read was started");

        // Asking again must not queue a second read for the same key.
        world.TryGetForRender(Key, out _);
        world.EnsureResident(Key);
        c.SeqEq(new[] { Key }, requested, "asking again does not queue the read twice");

        // Once it lands, both answers flip.
        world.InstallLoaded(Key, new LodSection());
        c.True(world.TryGetForRender(Key, out _), "the loaded section is renderable");
        c.True(world.EnsureResident(Key), "and the waiting caller may now proceed");
    }

    static void AlreadyInFlight(Check c)
    {
        var world = NewWorld(out _);
        world.InstallStoredKey(0, 3, 4, applyToParent: false);
        world.TryGetForRender(Key, out _);

        c.True(world.LoadsInFlight.Contains(Key), "the key is recorded as in flight");
        c.False(world.EnsureResident(Key), "a key with a read in flight is not proceedable");
    }

    /// <summary>
    /// A read that comes back empty must be remembered, or every caller re-requests it on
    /// every tick for the rest of the session and the section never becomes resident.
    /// </summary>
    static void FailedLoadIsRemembered(Check c)
    {
        var world = NewWorld(out List<long> requested);
        world.InstallStoredKey(0, 3, 4, applyToParent: false);
        world.TryGetForRender(Key, out _);
        world.InstallLoaded(Key, null);

        c.False(world.LoadsInFlight.Contains(Key), "a failed read stops being in flight");
        c.True(world.EnsureResident(Key), "and stops blocking its caller forever");

        requested.Clear();
        world.TryGetForRender(Key, out _);
        c.Eq(0, requested.Count, "a key that already failed is not requested again");
    }

    /// <summary>
    /// With no storage thread there is nothing to wait for, so the inline load is the
    /// only option and must still happen. A session without persistence would otherwise
    /// never see a stored section at all.
    /// </summary>
    static void NoStorageThreadFallsBackToInline(Check c)
    {
        var world = new LodWorld();
        int inlineLoads = 0;
        world.LoadFromStore = _ => { inlineLoads++; return new LodSection(); };
        world.RequestAsyncLoad = null;
        world.InstallStoredKey(0, 3, 4, applyToParent: false);

        c.True(world.TryGetForRender(Key, out _), "without a storage thread the load happens inline");
        c.Eq(1, inlineLoads, "and it really did read the store");
    }

    /// <summary>
    /// A section that became resident while a read was in flight is newer than the copy
    /// arriving from disk. Overwriting it would discard whatever was captured meanwhile.
    /// </summary>
    static void LoadedSectionNeverClobbersALiveOne(Check c)
    {
        var world = NewWorld(out _);
        world.InstallStoredKey(0, 3, 4, applyToParent: false);
        world.TryGetForRender(Key, out _);

        LodSection live = world.GetOrCreateSection(Key);
        world.InstallLoaded(Key, new LodSection());

        c.True(ReferenceEquals(live, world.Sections[Key]),
            "the section that was already live survives the arriving copy");
    }

    static void IncompleteLeavesDoNotReplaceParentCoverage(Check c)
    {
        int full = LodSection.GridSize * LodSection.GridSize;

        c.False(LodCoveragePolicy.ChildCanReplaceParent(
                level: 0, hasData: false, capturedColumns: 0, hasMesh: false),
            "a missing child does not unlock descent from its parent");
        c.True(LodCoveragePolicy.ChildCanReplaceParent(
                level: 0, hasData: true, capturedColumns: 0, hasMesh: false),
            "a known empty child does not pin its parent forever");
        c.False(LodCoveragePolicy.ChildCanReplaceParent(
                level: 0, hasData: true, capturedColumns: full / 4, hasMesh: true),
            "a one-chunk L0 fragment cannot replace broad parent coverage");
        c.True(LodCoveragePolicy.ChildCanReplaceParent(
                level: 0, hasData: true, capturedColumns: full, hasMesh: true),
            "a complete meshed L0 child can replace its parent");
        c.True(LodCoveragePolicy.ChildCanReplaceParent(
                level: 1, hasData: true, capturedColumns: full / 4, hasMesh: true),
            "the incomplete guard stays limited to L0 instead of hiding all far LOD levels");
        c.True(LodCoveragePolicy.MustDescendForVisualCap(level: 6, maxVisualLevel: 2),
            "a huge L6 section must descend when visible compression is capped at L2");
        c.False(LodCoveragePolicy.MustDescendForVisualCap(level: 2, maxVisualLevel: 2),
            "the cap permits its four-block target level");
        c.True(LodCoveragePolicy.ShouldKeepVisitedDraw(level: 1, hasDataSet: true),
            "captured L1 stays on the visited-keep draw path");
        c.False(LodCoveragePolicy.ShouldKeepVisitedDraw(level: 3, hasDataSet: true),
            "coarse parents still honour frustum cull");

        var classified = new LodWorld();
        var leaf = new LodSection();
        leaf.SetColumn(0, Array.Empty<ulong>());
        long leafKey = LodWorld.SectionKey(0, 8, 9);
        classified.ClassifySparseL0(leafKey, leaf);
        c.True(classified.IncompleteL0Keys.Contains(leafKey),
            "a partially captured L0 is marked unsafe to draw by itself");

        for (int col = 1; col < full; col++) leaf.SetColumn(col, Array.Empty<ulong>());
        classified.ClassifySparseL0(leafKey, leaf);
        c.False(classified.IncompleteL0Keys.Contains(leafKey),
            "a fully captured L0 becomes safe without changing its distance");
    }

    // ---- helpers ----

    static readonly long Key = LodWorld.SectionKey(0, 3, 4);

    /// <summary>
    /// A world with a storage thread that records requests instead of serving them, so a
    /// read stays in flight until the test decides it landed.
    /// </summary>
    static LodWorld NewWorld(out List<long> requested)
    {
        var world = new LodWorld();
        var log = new List<long>();
        requested = log;
        world.RequestAsyncLoad = key => log.Add(key);
        world.LoadFromStore = _ => null;
        return world;
    }
}
