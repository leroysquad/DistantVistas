namespace DistantVistas.Checks;

/// <summary>
/// Section key packing and the quadtree coordinate math built on it. Everything the
/// renderer's descent, the storage rows and the network manifest agree about is encoded
/// in these few functions, so a silent change here corrupts all three at once.
/// </summary>
public static class KeyMathChecks
{
    public static void Run(Check c)
    {
        Packing(c);
        Family(c);
        Footprint(c);
        Distance(c);
        WantedLevelFromSquaredDistance(c);
        WantedLevelHonoursVisualCap(c);
        CaptureSamplePositionIsTheBlockItself(c);
    }

    /// <summary>
    /// The palette describer must get the exact position of the block it describes.
    /// Chiselled blocks answer GetColorWithoutTint from the block entity at that
    /// position, so a stand-in position silently degrades to the placeholder texture's
    /// average - a real cache held that near-white for every chisel in it. The old code
    /// probed the chunk-column centre, one block above the run's top, and this check
    /// fails against that arithmetic on every axis.
    /// </summary>
    static void CaptureSamplePositionIsTheBlockItself(Check c)
    {
        // Section (0, 3, 2) spans blocks [192,256) x [128,192). Column (5, 60) inside it
        // is the block column at world x=197, z=188 - deliberately nowhere near a chunk
        // centre on either axis.
        long key = LodWorld.SectionKey(0, 3, 2);
        int col = LodSection.ColumnIndex(5, 60);
        ulong run = LodSection.PackRun(7, 71, 64);

        (int x, int y, int z) = LodPipeline.CaptureBlockPos(key, col, run);
        c.Eq(3 * LodSection.SectionBlocks + 5, x, "x is the column's own block, not a chunk centre");
        c.Eq(2 * LodSection.SectionBlocks + 60, z, "z is the column's own block, not a chunk centre");
        c.Eq(70, y, "y is the run's top block: yTop is exclusive, so the block sits at yTop - 1");

        // The corner column of the origin section, so an off-by-one on the decode
        // direction (x from col/GridSize instead of col%GridSize) cannot hide.
        (x, y, z) = LodPipeline.CaptureBlockPos(LodWorld.SectionKey(0, 0, 0), LodSection.ColumnIndex(63, 1),
            LodSection.PackRun(1, 2, 1));
        c.Eq(63, x, "x decodes from the fast axis of the column index");
        c.Eq(1, z, "z decodes from the slow axis of the column index");
        c.Eq(1, y, "a one-block run at y=1 samples y=1");
    }

    /// <summary>
    /// WantedLevelForSq must be the same function as WantedLevelFor, which it replaces on
    /// the two hot paths: the quadtree walk asks it once per visited node, and the prune
    /// pass once per dirty key per frame. The old form cost the caller a square root and
    /// then took a logarithm; the new one compares squared distances against a table.
    ///
    /// Squaring changes rounding, so if the two ever disagree it is at a boundary. This
    /// lands exactly on every boundary and sweeps finely either side, at several
    /// DetailDistance settings, because .vhdetail can change that live and the table has
    /// to be rebuilt when it does.
    ///
    /// A wrong answer here is not a slow frame, it is terrain drawn at the wrong detail
    /// level, so the bar is exact agreement rather than close agreement.
    /// </summary>
    static void WantedLevelFromSquaredDistance(Check c)
    {
        double original = LodWorld.DetailDistance;
        int mismatches = 0, compared = 0;

        try
        {
            foreach (double detail in new[] { 256.0, 512.0, 1024.0, 4096.0, 333.7 })
            {
                LodWorld.DetailDistance = detail;

                var probes = new List<double>();
                for (int level = 0; level <= LodWorld.MaxLevel + 1; level++)
                {
                    double boundary = detail * (1 << level);
                    foreach (double nudge in new[] { -1.0, -1e-3, -1e-9, 0.0, 1e-9, 1e-3, 1.0 })
                    {
                        probes.Add(Math.Max(0.0, boundary + nudge));
                    }
                }
                for (int i = -50; i < 400; i++) probes.Add(detail * Math.Pow(2, i / 41.0));
                probes.Add(0);
                probes.Add(1e9);

                foreach (double distance in probes)
                {
                    compared++;
                    if (LodWorld.WantedLevelFor(distance)
                        != LodWorld.WantedLevelForSq(distance * distance))
                    {
                        mismatches++;
                    }
                }
            }
        }
        finally
        {
            LodWorld.DetailDistance = original;
        }

        c.True(compared > 2000, "the sweep actually covered a wide range of distances");
        c.Eq(0, mismatches, "the squared form agrees with the logarithm at every distance");

        // And that the table follows DetailDistance rather than caching the first one it
        // saw. Without a rebuild this is the failure that would not show up above,
        // because every probe there is taken after the setting changes.
        LodWorld.DetailDistance = 512;
        int atFiveTwelve = LodWorld.WantedLevelForSq(2000.0 * 2000.0);
        LodWorld.DetailDistance = 2048;
        int atTwoThousand = LodWorld.WantedLevelForSq(2000.0 * 2000.0);
        LodWorld.DetailDistance = original;

        c.True(atFiveTwelve != atTwoThousand,
            "changing the detail distance changes the answer, so the table is rebuilt");
    }

    static void WantedLevelHonoursVisualCap(Check c)
    {
        int original = LodWorld.MaxVisualLevel;
        try
        {
            LodWorld.MaxVisualLevel = 2;
            c.Eq(2, LodWorld.WantedLevelFor(1_000_000),
                "extreme distance never selects columns coarser than four blocks");
            c.True(LodWorld.WantedLevelFor(0) <= 2,
                "the visual cap does not disturb nearer levels");
        }
        finally
        {
            LodWorld.MaxVisualLevel = original;
        }
    }

    static void Packing(Check c)
    {
        // level(4) | sz(30) | sx(30)
        foreach (int level in new[] { 0, 1, 3, LodWorld.MaxLevel })
        {
            foreach ((int sx, int sz) in new[] { (0, 0), (1, 0), (0, 1), (12345, 67890), (0x3FFFFFFF, 0x3FFFFFFF) })
            {
                long key = LodWorld.SectionKey(level, sx, sz);
                c.Eq(level, LodWorld.KeyLevel(key), $"level round-trips at L{level} {sx},{sz}");
                c.Eq(sx, LodWorld.KeySx(key), $"sx round-trips at L{level} {sx},{sz}");
                c.Eq(sz, LodWorld.KeySz(key), $"sz round-trips at L{level} {sx},{sz}");
            }
        }

        // Distinctness matters more than the exact layout: the whole scheme is a
        // Dictionary key, so any collision silently merges two regions of the world.
        var seen = new HashSet<long>();
        for (int level = 0; level <= LodWorld.MaxLevel; level++)
        {
            for (int sx = 0; sx < 12; sx++)
            {
                for (int sz = 0; sz < 12; sz++) seen.Add(LodWorld.SectionKey(level, sx, sz));
            }
        }
        c.Eq((LodWorld.MaxLevel + 1) * 144, seen.Count, "no key collisions across levels and a 12x12 patch");

        // KeyLevel uses an unsigned shift, so the top bit of a maximal key must not
        // sign-extend into a negative level.
        long extreme = LodWorld.SectionKey(LodWorld.MaxLevel, 0x3FFFFFFF, 0x3FFFFFFF);
        c.Eq(LodWorld.MaxLevel, LodWorld.KeyLevel(extreme), "level survives a maximal sx/sz");
    }

    static void Family(Check c)
    {
        long parent = LodWorld.SectionKey(3, 10, 20);

        // Every child names its parent, and the four children are distinct.
        var children = new HashSet<long>();
        for (int qz = 0; qz < 2; qz++)
        {
            for (int qx = 0; qx < 2; qx++)
            {
                long child = LodWorld.ChildKey(parent, qx, qz);
                children.Add(child);
                c.Eq(2, LodWorld.KeyLevel(child), $"child ({qx},{qz}) is one level finer");
                c.Eq(parent, LodWorld.ParentKey(child), $"child ({qx},{qz}) round-trips to its parent");
            }
        }
        c.Eq(4, children.Count, "the four children are distinct");

        // A parent's footprint is exactly its children's, which is what lets the renderer
        // stop descending once all four are covered.
        c.Eq(LodWorld.KeyFootprintBlocks(parent),
            LodWorld.KeyFootprintBlocks(LodWorld.ChildKey(parent, 0, 0)) * 2,
            "a parent spans twice a child's edge");

        // Odd coordinates must floor toward the parent, not round.
        c.Eq(LodWorld.SectionKey(1, 5, 5), LodWorld.ParentKey(LodWorld.SectionKey(0, 11, 11)),
            "odd child coordinates floor into the parent");

        long origin = LodWorld.SectionKey(0, 4, 4);
        c.Eq(LodWorld.SectionKey(0, 3, 4), LodWorld.NeighborKey(origin, -1, 0), "west neighbour");
        c.Eq(LodWorld.SectionKey(0, 5, 4), LodWorld.NeighborKey(origin, 1, 0), "east neighbour");
        c.Eq(LodWorld.SectionKey(0, 4, 3), LodWorld.NeighborKey(origin, 0, -1), "north neighbour");
        c.Eq(LodWorld.SectionKey(0, 4, 5), LodWorld.NeighborKey(origin, 0, 1), "south neighbour");

        // Stepping west from sx=0 wraps to the top of the 30-bit field rather than going
        // negative. That is only safe because Vintage Story world coordinates are
        // non-negative, so the wrapped key names a section that cannot exist and simply
        // misses every lookup. If world coordinates ever go negative this becomes a
        // wrong-neighbour bug, not a miss.
        long wrapped = LodWorld.NeighborKey(LodWorld.SectionKey(0, 0, 0), -1, 0);
        c.Eq(0x3FFFFFFF, LodWorld.KeySx(wrapped), "stepping west of the origin wraps rather than going negative");
        c.Eq(0, LodWorld.KeyLevel(wrapped), "the wrap does not corrupt the level field");
    }

    static void Footprint(Check c)
    {
        c.Eq(LodSection.SectionBlocks, LodWorld.KeyFootprintBlocks(LodWorld.SectionKey(0, 0, 0)),
            "an L0 section spans SectionBlocks");
        c.Eq(4096, LodWorld.KeyFootprintBlocks(LodWorld.SectionKey(6, 0, 0)),
            "an L6 section spans 4096 blocks");

        for (int level = 0; level <= LodWorld.MaxLevel; level++)
        {
            c.Eq(LodSection.SectionBlocks << level, LodWorld.KeyFootprintBlocks(LodWorld.SectionKey(level, 7, 7)),
                $"L{level} footprint doubles per level");
            c.Eq(LodSection.ColumnStepBlocks << level, LodWorld.ColumnStepBlocks(level),
                $"L{level} column step doubles per level");
        }
    }

    static void Distance(Check c)
    {
        long key = LodWorld.SectionKey(0, 2, 3); // occupies [128,192) x [192,256) at 64-block sections
        int size = LodSection.SectionBlocks;
        double minX = 2 * size, minZ = 3 * size;

        // The reason this is nearest-edge and not centre-to-centre: a viewer standing
        // inside a section must rank it at distance zero. An L6 section spans 4096 blocks,
        // so centre distance would call it two kilometres away and refuse to descend.
        c.Eq(0.0, LodWorld.NearestDistanceSqTo(key, minX + 1, minZ + 1), "inside the footprint is distance zero");
        c.Eq(0.0, LodWorld.NearestDistanceSqTo(key, minX, minZ), "the min corner is distance zero");
        c.Eq(0.0, LodWorld.NearestDistanceSqTo(key, minX + size - 0.001, minZ + size - 0.001),
            "just inside the max corner is distance zero");

        long big = LodWorld.SectionKey(6, 0, 0);
        c.Eq(0.0, LodWorld.NearestDistanceSqTo(big, 2000, 2000), "inside a 4096-block L6 section is distance zero");

        // Axis-aligned: only the offending axis contributes.
        c.Eq(100.0, LodWorld.NearestDistanceSqTo(key, minX - 10, minZ + 1), "10 blocks west is 100");
        c.Eq(100.0, LodWorld.NearestDistanceSqTo(key, minX + 1, minZ - 10), "10 blocks north is 100");
        c.Eq(100.0, LodWorld.NearestDistanceSqTo(key, minX + size + 10, minZ + 1), "10 blocks east is 100");

        // Diagonal: both axes contribute.
        c.Eq(200.0, LodWorld.NearestDistanceSqTo(key, minX - 10, minZ - 10), "diagonal corner sums both axes");

        c.True(LodWorld.NearestDistanceSqTo(key, 0, 0) > 0, "the world origin is outside this section");
    }
}
