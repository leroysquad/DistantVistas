namespace DistantVistas.Checks;

/// <summary>
/// Child to parent downsampling. Four child columns become one parent column via a
/// y-boundary slice sweep, with a slice surviving only when enough columns cover it.
///
/// The whole quadtree's appearance rests on this: every level above 0 is produced here
/// and nowhere else, so a fault shows up as distant terrain that is subtly wrong in a
/// way no one can trace back to a single block.
/// </summary>
public static class MipChecks
{
    const int Half = LodSection.GridSize / 2;

    public static void Run(Check c)
    {
        QuadrantPlacement(c);
        MajorityOccupancy(c);
        CliffFaceKeepsTheTallColumn(c);
        RunMerging(c);
        PaletteRemap(c);
        NothingToDo(c);
        BrightMinorityDoesNotPaintTheCap(c);
        NearWhiteMinorityDoesNotPaintTheCap(c);
        BrightMajorityKeepsSnow(c);
        TerrainOverACaveKeepsItsSurface(c);
        FloatingLeafCrownIsStillDropped(c);
        SkippedCanopyDoesNotBecomeParentSurface(c);
    }

    static void SkippedCanopyDoesNotBecomeParentSurface(Check c)
    {
        var child = new LodSection();
        int rock = child.FindOrAddPaletteEntry(blockId: 1, color: 0x00707070, flags: 0);
        int leaves = child.FindOrAddPaletteEntry(blockId: 2, color: 0x00407040,
            flags: LodPaletteEntry.FlagSkip, tintSlot: 5);
        ulong[] col = { LodSection.PackRun(leaves, 40, 28), LodSection.PackRun(rock, 20, 1) };
        for (int dz = 0; dz < 2; dz++)
        {
            for (int dx = 0; dx < 2; dx++) child.SetColumn(LodSection.ColumnIndex(dx, dz), col);
        }
        var parent = new LodSection();
        LodMip.DownsampleIntoParent(child, parent, 0, 0);
        ulong[] merged = parent.ColumnRuns(LodSection.ColumnIndex(0, 0)).ToArray();
        c.True(merged.Length > 0, "terrain under a skipped canopy still mips");
        c.Eq(20, LodSection.RunYTop(merged[0]), "skipped canopy is not the parent surface");
    }

    /// <summary>
    /// The mountain-chop bug. Four children share a cave room: rock from bedrock to 44,
    /// air to 48, rock and soil up to a surface at 88. The merged column has the same
    /// air gap, and the old anti-floater treated everything above it as unsupported and
    /// threw it away, so the parent's surface was the cave floor - 44 blocks down.
    /// Measured on a real cache that was one L1 column in six. Terrain over a cave is
    /// terrain; only plant scraps may float away.
    /// </summary>
    static void TerrainOverACaveKeepsItsSurface(Check c)
    {
        var child = new LodSection();
        int rock = child.FindOrAddPaletteEntry(blockId: 1, color: 0x00707070, flags: 0);
        int soil = child.FindOrAddPaletteEntry(blockId: 2, color: 0x00305070, flags: 0);
        int grass = child.FindOrAddPaletteEntry(blockId: 3, color: 0x00509050, flags: 0, tintSlot: 3);
        ulong[] overCave =
        {
            LodSection.PackRun(grass, 88, 87),
            LodSection.PackRun(soil, 87, 84),
            LodSection.PackRun(rock, 84, 48),
            LodSection.PackRun(rock, 44, 1),
        };
        for (int dz = 0; dz < 2; dz++)
        {
            for (int dx = 0; dx < 2; dx++) child.SetColumn(LodSection.ColumnIndex(dx, dz), overCave);
        }

        var parent = new LodSection();
        LodMip.DownsampleIntoParent(child, parent, 0, 0);
        ulong[] merged = parent.ColumnRuns(LodSection.ColumnIndex(0, 0)).ToArray();

        c.True(merged.Length >= 2, "the merged column keeps runs on both sides of the cave");
        c.Eq(88, LodSection.RunYTop(merged[0]), "the parent surface is the real surface, not the cave floor");
        c.Eq(3, parent.Palette[LodSection.RunPaletteId(merged[0])].BlockId, "the grass top survives the mip");
        c.Eq(1, LodSection.RunYBottom(merged[^1]), "the rock under the cave is still there");
        bool caveKept = false;
        for (int i = 0; i + 1 < merged.Length; i++)
        {
            if (LodSection.RunYBottom(merged[i]) == 48 && LodSection.RunYTop(merged[i + 1]) == 44) caveKept = true;
        }
        c.True(caveKept, "the cave itself stays an air gap rather than being filled");

        // A one-block soil roof is still terrain: thickness is not the test, plant matter is.
        var thinRoof = new LodSection();
        int soil2 = thinRoof.FindOrAddPaletteEntry(blockId: 2, color: 0x00305070, flags: 0);
        int rock2 = thinRoof.FindOrAddPaletteEntry(blockId: 1, color: 0x00707070, flags: 0);
        ulong[] roof = { LodSection.PackRun(soil2, 60, 59), LodSection.PackRun(rock2, 50, 1) };
        for (int dz = 0; dz < 2; dz++)
        {
            for (int dx = 0; dx < 2; dx++) thinRoof.SetColumn(LodSection.ColumnIndex(dx, dz), roof);
        }
        var roofParent = new LodSection();
        LodMip.DownsampleIntoParent(thinRoof, roofParent, 0, 0);
        ulong[] roofMerged = roofParent.ColumnRuns(LodSection.ColumnIndex(0, 0)).ToArray();
        c.Eq(60, LodSection.RunYTop(roofMerged[0]), "a thin untinted roof over a cavern is kept");
    }

    /// <summary>
    /// What the anti-floater is for: a leaf crown whose trunk lost the 2-of-4 vote hangs
    /// in the air above the ground and must go, snow cap and all. A crown that touches
    /// the ground (within the one-block crack) is a bush and stays.
    /// </summary>
    static void FloatingLeafCrownIsStillDropped(Check c)
    {
        var child = new LodSection();
        int rock = child.FindOrAddPaletteEntry(blockId: 1, color: 0x00707070, flags: 0);
        int leaves = child.FindOrAddPaletteEntry(blockId: 2, color: 0x00407040, flags: 0, tintSlot: 5);
        int snow = child.FindOrAddPaletteEntry(blockId: 3, color: unchecked((int)0x00FAFAFA), flags: 0);
        ulong[] crownInAir =
        {
            LodSection.PackRun(snow, 33, 32),
            LodSection.PackRun(leaves, 32, 28),
            LodSection.PackRun(rock, 20, 1),
        };
        for (int dz = 0; dz < 2; dz++)
        {
            for (int dx = 0; dx < 2; dx++) child.SetColumn(LodSection.ColumnIndex(dx, dz), crownInAir);
        }

        var parent = new LodSection();
        LodMip.DownsampleIntoParent(child, parent, 0, 0);
        ulong[] merged = parent.ColumnRuns(LodSection.ColumnIndex(0, 0)).ToArray();
        c.Eq(1, merged.Length, "a snow-capped leaf crown floating over the ground is dropped whole");
        c.Eq(20, LodSection.RunYTop(merged[0]), "the ground under the dropped crown is the surface");

        var bush = new LodSection();
        int rock2 = bush.FindOrAddPaletteEntry(blockId: 1, color: 0x00707070, flags: 0);
        int leaves2 = bush.FindOrAddPaletteEntry(blockId: 2, color: 0x00407040, flags: 0, tintSlot: 5);
        ulong[] grounded = { LodSection.PackRun(leaves2, 24, 21), LodSection.PackRun(rock2, 20, 1) };
        for (int dz = 0; dz < 2; dz++)
        {
            for (int dx = 0; dx < 2; dx++) bush.SetColumn(LodSection.ColumnIndex(dx, dz), grounded);
        }
        var bushParent = new LodSection();
        LodMip.DownsampleIntoParent(bush, bushParent, 0, 0);
        ulong[] bushMerged = bushParent.ColumnRuns(LodSection.ColumnIndex(0, 0)).ToArray();
        c.Eq(24, LodSection.RunYTop(bushMerged[0]), "leaves within a one-block crack of the ground are kept");
    }

    /// <summary>A child section fills exactly one quadrant of its parent, and only that one.</summary>
    static void QuadrantPlacement(Check c)
    {
        for (int qz = 0; qz < 2; qz++)
        {
            for (int qx = 0; qx < 2; qx++)
            {
                LodSection child = Solid(0, 10, 0);
                var parent = new LodSection();

                c.True(LodMip.DownsampleIntoParent(child, parent, qx, qz),
                    $"quadrant ({qx},{qz}) reports the parent changed");

                // A full child section covers a half-by-half block of parent columns.
                c.Eq(Half * Half, parent.CapturedColumns, $"quadrant ({qx},{qz}) captures a quarter of the parent");

                int inside = LodSection.ColumnIndex(qx * Half, qz * Half);
                int alsoInside = LodSection.ColumnIndex(qx * Half + Half - 1, qz * Half + Half - 1);
                c.Eq(1, parent.ColumnRuns(inside).Length, $"quadrant ({qx},{qz}) fills its near corner");
                c.Eq(1, parent.ColumnRuns(alsoInside).Length, $"quadrant ({qx},{qz}) fills its far corner");

                // The other three quadrants must be untouched, or siblings would overwrite
                // each other and only the last one downsampled would survive.
                int outsideX = qx == 0 ? Half : 0;
                int outsideZ = qz == 0 ? Half : 0;
                c.False(parent.Captured[LodSection.ColumnIndex(outsideX, qz * Half)],
                    $"quadrant ({qx},{qz}) leaves the x-adjacent quadrant alone");
                c.False(parent.Captured[LodSection.ColumnIndex(qx * Half, outsideZ)],
                    $"quadrant ({qx},{qz}) leaves the z-adjacent quadrant alone");
            }
        }

        // All four siblings into one parent: together they cover it exactly once.
        var shared = new LodSection();
        for (int qz = 0; qz < 2; qz++)
        {
            for (int qx = 0; qx < 2; qx++) LodMip.DownsampleIntoParent(Solid(0, 10, 0), shared, qx, qz);
        }
        c.Eq(Fixtures.Total, shared.CapturedColumns, "four children cover the whole parent");
    }

    /// <summary>
    /// A slice survives when at least two of the four child columns cover it, so a lone
    /// spire does not become a full-width pillar at the coarser level. With only one
    /// column captured the threshold drops to one, or the frontier of explored terrain
    /// would silently lose its edge columns.
    /// </summary>
    static void MajorityOccupancy(Check c)
    {
        // One column reaches to 20, the other three stop at 10.
        var child = new LodSection();
        child.FindOrAddPaletteEntry(blockId: 1, color: 0x00445566, flags: 0);
        child.SetColumn(LodSection.ColumnIndex(0, 0), new[] { LodSection.PackRun(0, 20, 0) });
        child.SetColumn(LodSection.ColumnIndex(1, 0), new[] { LodSection.PackRun(0, 10, 0) });
        child.SetColumn(LodSection.ColumnIndex(0, 1), new[] { LodSection.PackRun(0, 10, 0) });
        child.SetColumn(LodSection.ColumnIndex(1, 1), new[] { LodSection.PackRun(0, 10, 0) });

        var parent = new LodSection();
        LodMip.DownsampleIntoParent(child, parent, 0, 0);

        ulong[] merged = parent.ColumnRuns(LodSection.ColumnIndex(0, 0)).ToArray();
        c.Eq(20, LodSection.RunYTop(merged[0]),
            "the tall column is a cliff face, not a scrap - L1 keeps it or the mountain is a sky hole");
        c.Eq(0, LodSection.RunYBottom(merged[^1]), "the merged run keeps the shared floor");

        // A single captured column has no majority to lose to.
        var lonely = new LodSection();
        lonely.FindOrAddPaletteEntry(blockId: 1, color: 0x00445566, flags: 0);
        lonely.SetColumn(LodSection.ColumnIndex(0, 0), new[] { LodSection.PackRun(0, 20, 0) });

        var lonelyParent = new LodSection();
        LodMip.DownsampleIntoParent(lonely, lonelyParent, 0, 0);

        ulong[] survived = lonelyParent.ColumnRuns(LodSection.ColumnIndex(0, 0)).ToArray();
        c.Eq(1, survived.Length, "a lone captured column still produces a run");
        c.Eq(20, LodSection.RunYTop(survived[0]), "the lone column keeps its full height");
    }

    /// <summary>
    /// Spawn-hill cliffs: one column of the 2x2 is the face, the other three are the
    /// ground below. 2-of-4 dropped that face, so backing away from spawn punched a
    /// vertical sky slit exactly where L1 took over. 1-of-4 solid keeps it.
    /// </summary>
    static void CliffFaceKeepsTheTallColumn(Check c)
    {
        var child = new LodSection();
        int rock = child.FindOrAddPaletteEntry(blockId: 1, color: 0x00707070, flags: 0);
        child.SetColumn(LodSection.ColumnIndex(0, 0), new[] { LodSection.PackRun(rock, 80, 1) });
        child.SetColumn(LodSection.ColumnIndex(1, 0), new[] { LodSection.PackRun(rock, 40, 1) });
        child.SetColumn(LodSection.ColumnIndex(0, 1), new[] { LodSection.PackRun(rock, 40, 1) });
        child.SetColumn(LodSection.ColumnIndex(1, 1), new[] { LodSection.PackRun(rock, 40, 1) });

        var parent = new LodSection();
        LodMip.DownsampleIntoParent(child, parent, 0, 0);
        ulong[] merged = parent.ColumnRuns(LodSection.ColumnIndex(0, 0)).ToArray();
        c.Eq(80, LodSection.RunYTop(merged[0]), "the cliff top survives the 2x2 merge");
    }

    /// <summary>
    /// Slices are walked one boundary at a time, so a uniform column arrives as several
    /// abutting slices and must come back out as one run. Without the merge every parent
    /// column would carry a run per distinct y in its children - the run arrays would grow
    /// with depth instead of shrinking, which is the entire point of the pyramid.
    /// </summary>
    static void RunMerging(Check c)
    {
        var child = new LodSection();
        child.FindOrAddPaletteEntry(blockId: 1, color: 0x00112233, flags: 0);
        child.FindOrAddPaletteEntry(blockId: 2, color: 0x00445566, flags: 0);

        // Two stacked runs of the SAME block, split at y=10.
        ulong[] sameBlock = { LodSection.PackRun(0, 20, 10), LodSection.PackRun(0, 10, 0) };
        for (int dz = 0; dz < 2; dz++)
        {
            for (int dx = 0; dx < 2; dx++) child.SetColumn(LodSection.ColumnIndex(dx, dz), sameBlock);
        }

        var parent = new LodSection();
        LodMip.DownsampleIntoParent(child, parent, 0, 0);

        ulong[] merged = parent.ColumnRuns(LodSection.ColumnIndex(0, 0)).ToArray();
        c.Eq(1, merged.Length, "abutting slices of one block fuse into a single run");
        c.Eq(20, LodSection.RunYTop(merged[0]), "the fused run keeps the top");
        c.Eq(0, LodSection.RunYBottom(merged[0]), "the fused run keeps the bottom");

        // Different blocks must NOT fuse, or stone would swallow the soil above it.
        var layered = new LodSection();
        layered.FindOrAddPaletteEntry(blockId: 1, color: 0x00112233, flags: 0);
        layered.FindOrAddPaletteEntry(blockId: 2, color: 0x00445566, flags: 0);
        ulong[] twoBlocks = { LodSection.PackRun(1, 20, 10), LodSection.PackRun(0, 10, 0) };
        for (int dz = 0; dz < 2; dz++)
        {
            for (int dx = 0; dx < 2; dx++) layered.SetColumn(LodSection.ColumnIndex(dx, dz), twoBlocks);
        }

        var layeredParent = new LodSection();
        LodMip.DownsampleIntoParent(layered, layeredParent, 0, 0);

        ulong[] kept = layeredParent.ColumnRuns(LodSection.ColumnIndex(0, 0)).ToArray();
        c.Eq(2, kept.Length, "runs of different blocks stay separate");
        c.Eq(20, LodSection.RunYTop(kept[0]), "the upper layer keeps its top");
        c.Eq(10, LodSection.RunYBottom(kept[0]), "the layers meet where they should");
        c.Eq(0, LodSection.RunYBottom(kept[1]), "the lower layer keeps its floor");
    }

    /// <summary>
    /// Parent and child hold independent palettes, so ids must be translated rather than
    /// copied. A raw copy would silently reinterpret every run as whatever block happened
    /// to occupy that index in the parent.
    /// </summary>
    static void PaletteRemap(Check c)
    {
        var child = new LodSection();
        // Deliberately not index 0: an identity mapping would hide the bug.
        child.FindOrAddPaletteEntry(blockId: 100, color: 0x00AAAAAA, flags: 0);
        child.FindOrAddPaletteEntry(blockId: 200, color: 0x00BBCCDD,
            flags: LodPaletteEntry.FlagWater, tintSlot: 7);

        ulong[] runs = { LodSection.PackRun(1, 10, 0) };
        for (int dz = 0; dz < 2; dz++)
        {
            for (int dx = 0; dx < 2; dx++) child.SetColumn(LodSection.ColumnIndex(dx, dz), runs);
        }

        // Give the parent an unrelated entry first, so the child's id 1 cannot coincide.
        var parent = new LodSection();
        parent.FindOrAddPaletteEntry(blockId: 999, color: 0x00000001, flags: 0);

        LodMip.DownsampleIntoParent(child, parent, 0, 0);

        ulong[] merged = parent.ColumnRuns(LodSection.ColumnIndex(0, 0)).ToArray();
        c.Eq(1, merged.Length, "the remapped column has one run");

        int pid = LodSection.RunPaletteId(merged[0]);
        c.True(pid < parent.Palette.Count, "the remapped id is inside the parent palette");
        c.Eq(200, parent.Palette[pid].BlockId, "the run still names the child's block");
        c.Eq(0x00BBCCDD, parent.Palette[pid].Color, "colour survives the remap");
        c.Eq(LodPaletteEntry.FlagWater, parent.Palette[pid].Flags, "flags survive the remap");
        c.Eq((byte)7, parent.Palette[pid].TintSlot, "tint slot survives the remap");
        c.Eq(999, parent.Palette[0].BlockId, "the parent's existing palette entry is undisturbed");
    }

    static void NothingToDo(Check c)
    {
        var parent = new LodSection();
        c.False(LodMip.DownsampleIntoParent(new LodSection(), parent, 0, 0),
            "an empty child leaves the parent unchanged");
        c.Eq(0, parent.CapturedColumns, "an empty child captures nothing");

        // Re-running an identical downsample must be a no-op, or every mip pass would
        // mark the parent dirty and re-queue a save and a re-mesh forever.
        LodSection child = Solid(0, 10, 0);
        var target = new LodSection();
        c.True(LodMip.DownsampleIntoParent(child, target, 0, 0), "the first downsample changes the parent");
        c.False(LodMip.DownsampleIntoParent(child, target, 0, 0), "an identical re-run changes nothing");
    }

    /// <summary>
    /// One bright column on three rock columns must not become a white parent cap.
    /// Boyer-Moore used to keep the snow pid after cancelling; closer in those blocks are rock.
    /// </summary>
    static void BrightMinorityDoesNotPaintTheCap(Check c)
    {
        var child = new LodSection();
        child.FindOrAddPaletteEntry(blockId: 1, color: 0x00445566, flags: 0);
        child.FindOrAddPaletteEntry(blockId: 2, color: unchecked((int)0x00FCFCFC), flags: 0);

        child.SetColumn(LodSection.ColumnIndex(0, 0), new[] { LodSection.PackRun(1, 22, 20), LodSection.PackRun(0, 20, 0) });
        child.SetColumn(LodSection.ColumnIndex(1, 0), new[] { LodSection.PackRun(0, 20, 0) });
        child.SetColumn(LodSection.ColumnIndex(0, 1), new[] { LodSection.PackRun(0, 20, 0) });
        child.SetColumn(LodSection.ColumnIndex(1, 1), new[] { LodSection.PackRun(0, 20, 0) });

        var parent = new LodSection();
        LodMip.DownsampleIntoParent(child, parent, 0, 0);
        ulong[] merged = parent.ColumnRuns(LodSection.ColumnIndex(0, 0)).ToArray();
        c.True(merged.Length >= 1, "mixed columns still produce a run");
        int topPid = LodSection.RunPaletteId(merged[0]);
        c.Eq(1, parent.Palette[topPid].BlockId, "a 1-of-4 bright cap does not become the parent surface");
        c.Eq(20, LodSection.RunYTop(merged[0]), "parent height follows the rock majority");
    }

    /// <summary>
    /// Atlas near-white that is not quite 0xFFFFFF (TrueScale / server samples around
    /// luma 232) used to skip the 3-of-4 gate and paint the parent cap.
    /// </summary>
    static void NearWhiteMinorityDoesNotPaintTheCap(Check c)
    {
        var child = new LodSection();
        child.FindOrAddPaletteEntry(blockId: 1, color: 0x00445566, flags: 0);
        child.FindOrAddPaletteEntry(blockId: 2, color: unchecked((int)0x00E8E8E8), flags: 0);

        child.SetColumn(LodSection.ColumnIndex(0, 0), new[] { LodSection.PackRun(1, 22, 20), LodSection.PackRun(0, 20, 0) });
        child.SetColumn(LodSection.ColumnIndex(1, 0), new[] { LodSection.PackRun(0, 20, 0) });
        child.SetColumn(LodSection.ColumnIndex(0, 1), new[] { LodSection.PackRun(0, 20, 0) });
        child.SetColumn(LodSection.ColumnIndex(1, 1), new[] { LodSection.PackRun(0, 20, 0) });

        var parent = new LodSection();
        LodMip.DownsampleIntoParent(child, parent, 0, 0);
        ulong[] merged = parent.ColumnRuns(LodSection.ColumnIndex(0, 0)).ToArray();
        c.True(merged.Length >= 1, "mixed near-white columns still produce a run");
        int topPid = LodSection.RunPaletteId(merged[0]);
        c.Eq(1, parent.Palette[topPid].BlockId, "a 1-of-4 luma-232 cap does not become the parent surface");
        c.True(LodPaletteRepair.IsBrightCap(unchecked((int)0x00E8E8E8)), "luma 232 is a bright cap");
        c.False(LodPaletteRepair.IsMissingTextureWhite(unchecked((int)0x00E8E8E8)),
            "luma 232 is not unknown.png, so Fill must not steal real snow");
    }

    /// <summary>Four snow-capped columns stay snow: that is a real snow field, not a missing tex.</summary>
    static void BrightMajorityKeepsSnow(Check c)
    {
        var child = new LodSection();
        child.FindOrAddPaletteEntry(blockId: 1, color: 0x00445566, flags: 0);
        child.FindOrAddPaletteEntry(blockId: 2, color: unchecked((int)0x00FCFCFC), flags: 0);
        ulong[] snowOverRock = { LodSection.PackRun(1, 22, 18), LodSection.PackRun(0, 18, 0) };
        for (int dz = 0; dz < 2; dz++)
        {
            for (int dx = 0; dx < 2; dx++) child.SetColumn(LodSection.ColumnIndex(dx, dz), snowOverRock);
        }

        var parent = new LodSection();
        LodMip.DownsampleIntoParent(child, parent, 0, 0);
        ulong[] merged = parent.ColumnRuns(LodSection.ColumnIndex(0, 0)).ToArray();
        c.True(merged.Length >= 1, "unanimous snow still produces a run");
        int topPid = LodSection.RunPaletteId(merged[0]);
        c.Eq(2, parent.Palette[topPid].BlockId, "a 4-of-4 snow field stays snow");
        c.Eq(22, LodSection.RunYTop(merged[0]), "the snow cap keeps its height");
    }

    /// <summary>A fully captured child section, every column one run of palette id 0.</summary>
    static LodSection Solid(int paletteId, int yTop, int yBottom)
    {
        var s = new LodSection();
        s.FindOrAddPaletteEntry(blockId: paletteId + 1, color: 0x00708090, flags: 0);
        ulong[] run = { LodSection.PackRun(paletteId, yTop, yBottom) };
        for (int col = 0; col < Fixtures.Total; col++) s.SetColumn(col, run);
        return s;
    }
}
