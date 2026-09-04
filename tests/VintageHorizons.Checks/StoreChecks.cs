namespace DistantVistas.Checks;

/// <summary>
/// The on-disk blob format, round-tripped with no database.
///
/// LodStore extends the game's SQLite base class, but Serialize is static and the base
/// constructor only stores its logger - so the format can be exercised without opening a
/// file, and DeserializeForeign explicitly accepts a null world to defer block-id lookup
/// to the main thread. That same door is what the network path uses for foreign sections.
/// </summary>
public static class StoreChecks
{
    public static void Run(Check c)
    {
        RoundTrip(c);
        DeferredPalette(c);
        AFailedLookupIsNotRemembered(c);
        AColourlessCacheIsRepaired(c);
        MissingTextureWhiteTakesNeighbour(c);
        AStaleStableColourIsRefreshed(c);
        DerivedMipUpgradeKeepsDetailedLeaves(c);
        DisposingTheOfferReaderReleasesItsFileHandle(c);
        Rejection(c);
        PurgeKeepsMatchingData(c);
        ProvisionalBitsSurviveReopen(c);
        CapturePolicyStamp(c);
        FlagBakedSurvivesReclassify(c);
    }

    /// <summary>
    /// Reopening a cache at the same format version must keep every row.
    ///
    /// This is the one place the mod deletes data a player accumulated over weeks, so it
    /// gets a check that opens a real file rather than reasoning about the SQL. The
    /// nearby bug was the reverse of this: a brand-new cache announced that it was
    /// discarding data it never had, because a missing FormatVersion row compares
    /// unequal to the current version.
    /// </summary>
    static void PurgeKeepsMatchingData(Check c)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vh-purge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cache.db");
        try
        {
            var logger = new CaptureLogger();
            var store = new LodStore(logger);
            c.True(store.Open(path), "a new cache file opens");

            // A first-ever cache holds nothing, so it must not claim to discard anything.
            c.False(logger.Contains("discarding"),
                "a brand new cache does not announce that it is discarding data");

            store.SaveBlob(0, 1, 1, new byte[] { 1, 2, 3, 4 }, applyToParent: false);
            store.SaveBlob(0, 2, 2, new byte[] { 5, 6, 7, 8 }, applyToParent: true);
            store.Dispose();

            // Reopen at the SAME version. Every row survives.
            var reopenLogger = new CaptureLogger();
            var reopened = new LodStore(reopenLogger);
            c.True(reopened.Open(path), "an existing cache reopens");
            int kept = reopened.LoadAllKeys((_, _, _, _) => { });
            c.Eq(2, kept, "reopening at the same format version keeps every section");
            c.False(reopenLogger.Contains("discarding"),
                "reopening at the same version does not discard anything");
            reopened.Dispose();

            // Now make the stored version disagree. The purge must fire, and say how
            // much it took - silent destruction of a player's cache is the thing to
            // avoid, not the destruction itself.
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                "Data Source=" + path + ";Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE Meta SET Value='1' WHERE Key='FormatVersion'";
                cmd.ExecuteNonQuery();
            }

            var staleLogger = new CaptureLogger();
            var stale = new LodStore(staleLogger);
            c.True(stale.Open(path), "a cache in an older format still opens");
            c.Eq(0, stale.LoadAllKeys((_, _, _, _) => { }),
                "an older format is discarded, not read");
            c.True(staleLogger.Contains("discarding"), "the purge says that it discarded data");
            c.True(staleLogger.Contains("2"), "the purge reports how many sections it took");
            stale.Dispose();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp dir */ }
        }
    }

    /// <summary>
    /// 0.7.43 bookkeeping: peek/foreign quadrants live in a column beside the blob,
    /// not in a format bump that would purge the table. A reopen must still see them
    /// so QueueColumn recaptures a cold peeked row without loading it.
    /// </summary>
    static void ProvisionalBitsSurviveReopen(Check c)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vh-prov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cache.db");
        try
        {
            var store = new LodStore(new CaptureLogger());
            c.True(store.Open(path), "a new cache file opens for the provisional column");
            store.SaveBlob(0, 7, 9, new byte[] { 1, 2, 3, 4 }, applyToParent: false, provisional: 0b0101);
            store.Dispose();

            var reopened = new LodStore(new CaptureLogger());
            c.True(reopened.Open(path), "the cache reopens with the new column");
            int seen = -1;
            int kept = reopened.LoadAllKeys((_, _, _, _, prov) => seen = prov);
            c.Eq(1, kept, "the row survived");
            c.Eq(0b0101, seen, "provisional bits survive a reopen without loading the blob");
            reopened.Dispose();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp dir */ }
        }
    }

    static void RoundTrip(Check c)
    {
        var section = new LodSection();
        int stone = section.FindOrAddPaletteEntry(blockId: 11, color: 0x00445566, flags: 0);
        int water = section.FindOrAddPaletteEntry(blockId: 22, color: 0x00112233,
            flags: LodPaletteEntry.FlagWater, tintSlot: 9);

        section.SetColumn(0, new[] { LodSection.PackRun(stone, 30, 0) });
        section.SetColumn(1, new[] { LodSection.PackRun(water, 40, 30), LodSection.PackRun(stone, 30, 0) });
        section.SetColumn(Fixtures.Total - 1, new[] { LodSection.PackRun(stone, 12, 0) });

        LodSection back = Restore(c, section);
        if (back == null!) return;

        c.SeqEq(section.Runs, back.Runs, "runs survive the round trip");
        c.SeqEq(section.ColumnStart, back.ColumnStart, "column offsets survive the round trip");
        c.SeqEq(section.Captured, back.Captured, "the captured bitmask survives the round trip");
        c.Eq(section.CapturedColumns, back.CapturedColumns, "the captured count is rebuilt");
        c.Eq(section.Palette.Count, back.Palette.Count, "the palette keeps its length");

        for (int i = 0; i < section.Palette.Count; i++)
        {
            c.Eq(section.Palette[i].Color, back.Palette[i].Color, $"palette[{i}] colour survives");
            c.Eq(section.Palette[i].Flags, back.Palette[i].Flags, $"palette[{i}] flags survive");
        }

        // The last column is the one an off-by-one in the 4096 run counts or the 512-byte
        // captured bitmask would lose, and losing it is invisible until terrain has a seam.
        c.True(back.Captured[Fixtures.Total - 1], "the final column survives the bitmask");
        c.SeqEq(section.ColumnRuns(Fixtures.Total - 1).ToArray(),
            back.ColumnRuns(Fixtures.Total - 1).ToArray(), "the final column's runs survive");
    }

    /// <summary>
    /// Two fields deliberately do NOT round-trip, and asserting full equality would lock in
    /// the wrong thing:
    ///   - TintSlot is never written, so an existing cache picks up corrected per-species
    ///     tints without re-capturing, and stays right when a game update remaps them.
    ///   - BlockId cannot be resolved off the main thread, so a null world defers the codes
    ///     into PendingPaletteCodes for the main thread to resolve on install.
    /// </summary>
    static void DeferredPalette(Check c)
    {
        var section = new LodSection();
        section.FindOrAddPaletteEntry(blockId: 11, color: 0x00445566, flags: 0, tintSlot: 5);
        section.SetColumn(0, new[] { LodSection.PackRun(0, 10, 0) });

        string[] codes = { "game:rock-granite" };
        LodSection back = Restore(c, section, codes);
        if (back == null!) return;

        c.True(back.PendingPaletteCodes != null, "a null world defers palette codes for the main thread");
        c.SeqEq(codes, back.PendingPaletteCodes!, "the deferred codes are the ones written");
        c.Eq(0, back.Palette[0].BlockId, "block ids stay unresolved until the main thread runs");
        c.Eq((byte)0, back.Palette[0].TintSlot, "tint slots are re-derived, never persisted");
        c.Eq(0x00445566, back.Palette[0].Color, "colour is persisted and comes back");
    }

    /// <summary>
    /// A block code that does not resolve must not become a permanent answer.
    ///
    /// The lookup runs on the storage thread against whatever the registry holds at that
    /// moment, and sections start loading from cache before a world has finished coming
    /// up. Caching the failure alongside the successes meant a code that lost that race
    /// answered 0 for the rest of the session, and an entry with no block id keeps the
    /// flags the capturing side worked out - so it was still drawn, as terrain, with the
    /// colour a server leaves at zero. Black ground, correctly shaped, and nothing logged.
    /// </summary>
    static void AFailedLookupIsNotRemembered(Check c)
    {
        var section = new LodSection();
        section.FindOrAddPaletteEntry(blockId: 0, color: 0, flags: 0, tintSlot: 0);
        section.SetColumn(0, new[] { LodSection.PackRun(0, 10, 0) });
        section.PendingPaletteCodes = new[] { "game:rock-granite" };

        var store = new LodStore(null!);

        // The registry is not ready yet, which is the race.
        int calls = 0;
        store.ResolvePendingPalette(section, _ => { calls++; return 0; });
        c.Eq(1, calls, "the first resolve asks the registry");
        c.Eq(0, section.Palette[0].BlockId, "and gets nothing, because nothing is registered yet");
        c.SeqEq(new[] { "game:rock-granite" }, store.UnresolvedCodes(),
            "the code that did not resolve is recorded, so it can be named rather than guessed at");

        // Same code, same store, registry now up. Asking again is the whole point.
        section.PendingPaletteCodes = new[] { "game:rock-granite" };
        store.ResolvePendingPalette(section, _ => { calls++; return 42; });
        c.Eq(2, calls, "a code that failed is asked again rather than answered from cache");
        c.Eq(42, section.Palette[0].BlockId, "and resolves once the registry has it");
        c.Eq(0, store.UnresolvedCodes().Length, "and stops being reported as unresolved");

        // A code that DID resolve is cached, because that is the hot path.
        section.PendingPaletteCodes = new[] { "game:rock-granite" };
        store.ResolvePendingPalette(section, _ => { calls++; return 99; });
        c.Eq(2, calls, "a code that resolved is answered from cache and not looked up again");
        c.Eq(42, section.Palette[0].BlockId, "keeping the id it first resolved to");
    }

    /// <summary>
    /// A cache holding sections with no palette colour must repair itself as it loads.
    ///
    /// A capturing server stores 0 for every colour, because it has no texture atlas, and
    /// the receiving client fills them in. A client also PERSISTS what it received, so
    /// anything that stopped the fill-in was written to disk and stayed there. Measured on
    /// a real world afterwards: 7 sections entirely uncoloured and 59 partly, on ground as
    /// ordinary as soil-low-normal and rock-slate. Colour 0 draws as pure black, and no
    /// amount of fixing the cause reaches a cache that already has it.
    /// </summary>
    static void AColourlessCacheIsRepaired(Check c)
    {
        c.True(LodPaletteRepair.NeedsColor(0), "colour 0 is what a writer leaves when it cannot answer");
        c.False(LodPaletteRepair.NeedsColor(unchecked((int)0xFF000000)),
            "opaque black is a real colour and is left alone");
        c.False(LodPaletteRepair.NeedsColor(1), "so is anything else");

        var section = new LodSection();
        section.FindOrAddPaletteEntry(blockId: 11, color: 0, flags: 0, tintSlot: 0);
        section.FindOrAddPaletteEntry(blockId: 12, color: unchecked((int)0xFF336699), flags: 0, tintSlot: 0);
        section.FindOrAddPaletteEntry(blockId: 0, color: 0, flags: 0, tintSlot: 0);

        int asked = 0;
        int repaired = LodPaletteRepair.Fill(section, id =>
        {
            asked++;
            return id == 11 ? unchecked((int)0xFF112233) : 0;
        });

        c.Eq(2, repaired, "both uncoloured entries are repaired");
        c.Eq(2, asked, "and the entry that already had a colour is not asked about");
        c.Eq(unchecked((int)0xFF112233), section.Palette[0].Color, "a known block takes its real colour");
        c.Eq(unchecked((int)0xFF336699), section.Palette[1].Color, "an already-coloured entry is untouched");

        // The provider answered 0 for the unknown block. Take a neighbour rock/dirt
        // sample from this section instead of white or black. Storing 0 would leave the
        // entry needing repair for ever, and repairing marks the section dirty, so the
        // cache would be rewritten on every single load.
        c.Eq(unchecked((int)0xFF336699), section.Palette[2].Color,
            "a block nothing can colour takes a neighbour earth tone, never white");
        c.False(LodPaletteRepair.NeedsColor(section.Palette[2].Color),
            "so the repair finishes instead of running again every load");

        c.Eq(0, LodPaletteRepair.Fill(section, _ => 0), "a repaired section needs no second pass");
    }


    static void MissingTextureWhiteTakesNeighbour(Check c)
    {
        c.True(LodPaletteRepair.NeedsColor(unchecked((int)0x00FCFCFC)),
            "unknown.png near-white is treated as missing colour");
        // Isolated without TrueScale: unknown.png can sample as Farseer slate
        // (0.26, 0.29, 0.45) or packed 0x001D3954, which is not near-white.
        int farseerSlate = 66 | (74 << 8) | (115 << 16);
        c.True(LodPaletteRepair.IsMissingTextureSky(farseerSlate),
            "Farseer slate-blue is a missing-tex stand-in, not rock");
        c.True(LodPaletteRepair.NeedsColor(farseerSlate),
            "slate-blue missing tex is repaired like unknown.png white");
        c.False(LodPaletteRepair.IsMissingTextureSky(unchecked((int)0xFF336699)),
            "a real mid-chroma earth/water sample is not treated as sky");
        int glacier = 170 | (200 << 8) | (220 << 16);
        c.True(LodPaletteRepair.IsIceLikeAlbedo(glacier),
            "pale cyan glacier ice is ice, not missing tex");
        c.False(LodPaletteRepair.IsMissingTextureSky(glacier),
            "glacier ice is not Farseer slate");
        c.False(LodPaletteRepair.NeedsColor(glacier),
            "glacier ice is not repaired into grass");
        c.True(LodPaletteRepair.IsSnowOrIceAlbedo(unchecked((int)0x00E8E8E8)),
            "luma-232 snow is a snow/ice albedo");
        c.Eq(unchecked((int)0x00FCFCFC),
            LodPaletteRepair.KeepCapturedColor(unchecked((int)0x00FCFCFC), LodPaletteRepair.TerrainFallbackColor, snowOrIceBlock: true),
            "a known snow block keeps near-white instead of becoming grass");
        c.Eq(LodPaletteRepair.TerrainFallbackColor,
            LodPaletteRepair.KeepCapturedColor(farseerSlate, LodPaletteRepair.TerrainFallbackColor, snowOrIceBlock: true),
            "Farseer slate on a snow-named block is still missing tex");
        c.True(LodPaletteRepair.IsBrightCap(unchecked((int)0xFFFCFCFC)),
            "opaque near-white is a bright cap");

        var section = new LodSection();
        int dirt = unchecked((int)0xFF406080); // R=0x80 G=0x60 B=0x40, mid-luma earth
        section.FindOrAddPaletteEntry(blockId: 11, color: dirt, flags: 0);
        section.FindOrAddPaletteEntry(blockId: 12, color: unchecked((int)0x00FCFCFC), flags: 0);

        int repaired = LodPaletteRepair.Fill(section, id =>
            id == 12 ? unchecked((int)0x00FCFCFC) : dirt);

        c.Eq(1, repaired, "the missing-tex entry is repaired");
        c.Eq(dirt, section.Palette[1].Color,
            "missing-tex white takes the neighbour dirt, never stays white");
        c.Eq(dirt, LodPaletteRepair.NeighborTerrainColor(section, skipIndex: 1),
            "neighbour lookup prefers the earth-tone entry");
    }

    static void AStaleStableColourIsRefreshed(Check c)
    {
        var section = new LodSection();
        section.FindOrAddPaletteEntry(blockId: 11, color: unchecked((int)0xFF806040), flags: 0);
        section.FindOrAddPaletteEntry(blockId: 12, color: unchecked((int)0xFF112233), flags: 0);

        int refreshed = LodPaletteRepair.RefreshStable(section, id =>
            id == 11 ? unchecked((int)0xFF336699) : null);

        c.Eq(1, refreshed, "one stale stable palette colour is refreshed");
        c.Eq(unchecked((int)0xFF336699), section.Palette[0].Color,
            "old cached grass takes the current stable composite");
        c.Eq(unchecked((int)0xFF112233), section.Palette[1].Color,
            "position-dependent palette colour is preserved");
        c.Eq(0, LodPaletteRepair.RefreshStable(section, id =>
            id == 11 ? unchecked((int)0xFF336699) : null),
            "a refreshed palette does not rewrite every time it loads");
    }

    static void DerivedMipUpgradeKeepsDetailedLeaves(Check c)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vh-mip-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cache.db");
        try
        {
            var store = new LodStore(new CaptureLogger());
            c.True(store.Open(path), "derived-mip fixture cache opens");
            store.SaveBlob(0, 4, 4, new byte[] { 1 }, applyToParent: false);
            store.SaveBlob(1, 2, 2, new byte[] { 1 }, applyToParent: false);
            store.Dispose();

            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                "Data Source=" + path + ";Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT OR REPLACE INTO Meta (Key, Value) VALUES ('DerivedMipVersion', 'old')";
                cmd.ExecuteNonQuery();
            }

            var logger = new CaptureLogger();
            var reopened = new LodStore(logger);
            c.True(reopened.Open(path), "cache with old derived mips reopens");
            var keys = new List<(int Level, bool Apply)>();
            int kept = reopened.LoadAllKeys((level, _, _, apply) => keys.Add((level, apply)));

            c.Eq(1, kept, "the rebuild discards compressed parents but preserves detailed L0");
            c.Eq(0, keys[0].Level, "the preserved row is detailed L0 terrain");
            c.True(keys[0].Apply, "the preserved L0 row is queued to rebuild its parents");
            c.True(logger.Contains("rebuild"), "the cache explains why coarse levels were removed");
            reopened.Dispose();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp dir */ }
        }
    }

    /// <summary>
    /// Disposing the local offer reader must release its file handle, not park it.
    ///
    /// Microsoft.Data.Sqlite pools connections by default: Dispose returns the native
    /// handle to a process-wide pool keyed by connection string, and the file stays open
    /// until the process exits. In singleplayer that handle points at the server side's
    /// cache, and it survived leaving the world - so the next load of the same world had
    /// the integrated server's LodStore.Open refused with "it seems to be not writable",
    /// every time, on the platform whose file sharing blocks it. Reported from the field
    /// against 0.2.0, the release that introduced this reader; 0.1.0 had nothing to leak.
    ///
    /// Proven through /proc/self/fd, which is why the assertion is Linux-only: the leak is
    /// a handle held by this process, and that is where a process's handles are listed.
    /// </summary>
    static void DisposingTheOfferReaderReleasesItsFileHandle(Check c)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vh-offer-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string clientDb = Path.Combine(dir, "world.db");
            string serverDb = Path.Combine(dir, "world-server.db");

            // A throwaway unpooled writer builds the fixture file, so the only connection
            // that can linger afterwards is the one under test.
            var writer = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = serverDb,
                Pooling = false,
            };
            using (var w = new Microsoft.Data.Sqlite.SqliteConnection(writer.ToString()))
            {
                w.Open();
                using var cmd = w.CreateCommand();
                cmd.CommandText =
                    "CREATE TABLE Section (Detail INTEGER, SX INTEGER, SZ INTEGER, "
                    + "Data BLOB, ApplyToParent INTEGER, ModifiedMs INTEGER);"
                    + "INSERT INTO Section VALUES (0, 1, 2, x'00', 0, 0);";
                cmd.ExecuteNonQuery();
            }

            var logger = new CaptureLogger();
            LodLocalOfferSource? offers = LodLocalOfferSource.TryOpen(clientDb, logger);
            c.True(offers != null, "the server-side cache beside a client path opens");
            if (offers == null) return;

            c.Eq(1, offers.Keys().Length, "and lists its sections");
            offers.Dispose();

            if (OperatingSystem.IsLinux())
            {
                c.False(ProcessHoldsHandleTo(serverDb),
                    "Dispose releases the file handle instead of pooling it for the rest of the process");
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp dir; best effort */ }
        }
    }

    static bool ProcessHoldsHandleTo(string path)
    {
        foreach (string fd in Directory.EnumerateFiles("/proc/self/fd"))
        {
            try
            {
                if (new FileInfo(fd).LinkTarget == path) return true;
            }
            catch
            {
                // A descriptor can close between listing and reading; not this check's business.
            }
        }
        return false;
    }

    /// <summary>
    /// Bad input must come back null, never throw. The storage thread deserializes rows off
    /// the main thread and the network path deserializes whatever a server sent; an
    /// exception on either takes down more than the one bad section.
    /// </summary>
    static void Rejection(Check c)
    {
        var store = new LodStore(null!);
        byte[] good = LodStore.Serialize(Fixtures.Snapshot(Fixtures.SolidSection()));

        c.NoThrow(() => store.DeserializeForeign(Array.Empty<byte>(), null), "an empty blob does not throw");
        c.Eq(null, store.DeserializeForeign(Array.Empty<byte>(), null), "an empty blob returns null");

        c.Eq(null, store.DeserializeForeign(new byte[] { 4 }, null), "a one-byte blob returns null");

        byte[] wrongVersion = (byte[])good.Clone();
        wrongVersion[0] = 99;
        c.Eq(null, store.DeserializeForeign(wrongVersion, null), "a blob from a future format returns null");

        // Truncation is the realistic corruption: a partial write, or a section cut short
        // in transit.
        byte[] truncated = good[..(good.Length / 2)];
        c.NoThrow(() => store.DeserializeForeign(truncated, null), "a truncated blob does not throw");
        c.Eq(null, store.DeserializeForeign(truncated, null), "a truncated blob returns null");

        byte[] garbage = (byte[])good.Clone();
        for (int i = 1; i < garbage.Length; i += 3) garbage[i] ^= 0xA5;
        c.NoThrow(() => store.DeserializeForeign(garbage, null), "a corrupted blob does not throw");

        c.True(store.DeserializeForeign(good, null) != null, "a good blob still deserializes");
    }

    /// <summary>
    /// 0.7.51 skip-leaves captures poison a zip rollback. Missing stamp is current
    /// (do not wipe a good world). Only the known skip-leaves value purges.
    /// </summary>
    static void CapturePolicyStamp(Check c)
    {
        c.False(LodStore.MustPurgeSkipLeavesStamp(null),
            "a missing CapturePolicyVersion is current — do not wipe a fresh world");
        c.False(LodStore.MustPurgeSkipLeavesStamp(""),
            "an empty stamp is not a skip-leaves cache");
        c.False(LodStore.MustPurgeSkipLeavesStamp(LodStore.CapturePolicyLeavesSolid),
            "leaves-solid is current");
        c.True(LodStore.MustPurgeSkipLeavesStamp(LodStore.CapturePolicyLeavesSkipped),
            "leaves-skipped is the only stamp that purges");
        c.False(LodStore.MustPurgeSkipLeavesStamp("unknown-policy"),
            "an unknown stamp is not treated as skip-leaves");
    }

    static void FlagBakedSurvivesReclassify(Check c)
    {
        var section = new LodSection();
        section.FindOrAddPaletteEntry(blockId: 42, color: 0x00407040, flags: LodPaletteEntry.FlagBaked);
        byte[] blob = LodStore.Serialize(Fixtures.Snapshot(section, codes: new[] { "game:grass" }));

        var store = new LodStore(null!);
        store.ClassifyBlock = _ => ((byte)LodPaletteEntry.FlagThin, (byte)7);

        LodSection? back = store.DeserializeForeign(blob, null);
        c.True(back != null, "baked section deserializes");
        store.ResolvePendingPalette(back!, code => code == "game:grass" ? 42 : 0);

        LodPaletteEntry e = back!.Palette[0];
        c.True((e.Flags & LodPaletteEntry.FlagBaked) != 0,
            "FlagBaked survives palette reclassify on load");
        c.Eq((byte)LodTintRegistry.SlotNone, e.TintSlot,
            "baked entries keep tint slot 0 after reclassify");
        c.True((e.Flags & LodPaletteEntry.FlagThin) != 0,
            "live block flags are still refreshed on load");
    }

    static LodSection Restore(Check c, LodSection section, string[]? codes = null)
    {
        byte[] blob = LodStore.Serialize(Fixtures.Snapshot(section, codes: codes));
        LodSection? back = new LodStore(null!).DeserializeForeign(blob, null);
        c.True(back != null, "the blob deserializes");
        return back!;
    }
}
