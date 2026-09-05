namespace DistantVistas.Checks;

/// <summary>
/// Section snapshot to vertex data. Two things are worth pinning here: the greedy merge,
/// which is the difference between five quads and four thousand for the same terrain, and
/// the coverage rules, which are deliberately asymmetric and were each arrived at by
/// finding the artefact the symmetric version produced.
/// </summary>
public static class MesherChecks
{
    const int Gs = LodSection.GridSize;

    public static void Run(Check c)
    {
        Empty(c);
        GreedyMerge(c);
        UnevenBase(c);
        LevelScaling(c);
        AlphaBands(c);
        WaterIsASeparatePass(c);
        ThinMats(c);
        CoverageRules(c);
        MissingNeighbourDoesNotBecomeCliff(c);
        UncapturedColumnDoesNotBecomeCliff(c);
        AntiFloaterSkipsPlantScrapsOnly(c);
        SkipFlagIsNotGeometry(c);
    }

    static void SkipFlagIsNotGeometry(Check c)
    {
        var s = new LodSection();
        int skip = s.FindOrAddPaletteEntry(blockId: 3, color: 0x0040C040, flags: LodPaletteEntry.FlagSkip);
        int soil = s.FindOrAddPaletteEntry(blockId: 1, color: 0x00305070, flags: 0);
        s.SetColumn(LodSection.ColumnIndex(5, 5),
            new[] { LodSection.PackRun(skip, 40, 20), LodSection.PackRun(soil, 10, 0) });
        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(s));
        c.Eq(0, QuadsOnPlane(mesh.Xyz, mesh.VertexCount, axis: 1, value: 40f),
            "FlagSkip canopy is not meshed");
        c.True(QuadsOnPlane(mesh.Xyz, mesh.VertexCount, axis: 1, value: 10f) > 0,
            "the soil under a skipped canopy still meshes");
    }

    /// <summary>
    /// The mid-far anti-floater exists for leaf pixels the mip left hanging. A short
    /// untinted run with air under it is the ceiling of a cave, i.e. the bottom of the
    /// terrain above it, and must be drawn at every level.
    /// </summary>
    static void AntiFloaterSkipsPlantScrapsOnly(Check c)
    {
        long l1 = LodWorld.SectionKey(1, 0, 0);

        var soilRoof = new LodSection();
        int soil = soilRoof.FindOrAddPaletteEntry(blockId: 1, color: 0x00305070, flags: 0);
        int rock = soilRoof.FindOrAddPaletteEntry(blockId: 2, color: 0x00707070, flags: 0);
        soilRoof.SetColumn(LodSection.ColumnIndex(5, 5),
            new[] { LodSection.PackRun(soil, 60, 59), LodSection.PackRun(rock, 50, 1) });
        MeshResult roof = LodMesher.BuildMesh(Fixtures.Job(soilRoof, l1));
        c.Eq(1, QuadsOnPlane(roof.Xyz, roof.VertexCount, axis: 1, value: 60f),
            "a one-block soil run over a cave keeps its top at L1");

        var leafScrap = new LodSection();
        int leaves = leafScrap.FindOrAddPaletteEntry(blockId: 3, color: 0x00407040, flags: 0, tintSlot: 5);
        int rock2 = leafScrap.FindOrAddPaletteEntry(blockId: 2, color: 0x00707070, flags: 0);
        leafScrap.SetColumn(LodSection.ColumnIndex(5, 5),
            new[] { LodSection.PackRun(leaves, 60, 59), LodSection.PackRun(rock2, 50, 1) });
        MeshResult scrap = LodMesher.BuildMesh(Fixtures.Job(leafScrap, l1));
        c.Eq(0, QuadsOnPlane(scrap.Xyz, scrap.VertexCount, axis: 1, value: 60f),
            "a one-block leaf scrap floating over the ground is still skipped at L1");
    }

    static void Empty(Check c)
    {
        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(new LodSection()));
        c.Eq(0, mesh.VertexCount, "an empty section produces no vertices");
        c.Eq(0, mesh.IndexCount, "an empty section produces no indices");
        c.Eq(null, mesh.WaterXyz, "an empty section produces no water pass");
    }

    /// <summary>
    /// The reason the mesher exists in this shape. A flat plain is 4096 columns with
    /// identical tops; naively that is 4096 quads for the surface plus a wall per column
    /// edge. With no loaded neighbours, unknown coverage must not become a cliff.
    /// </summary>
    static void GreedyMerge(Check c)
    {
        LodSection flat = Solid(yTop: 10, yBottom: 0);
        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(flat));

        // One top rectangle. Missing neighbours are unknown coverage, not world edges.
        // No bottom faces: yBottom is 0, and nothing can see under the world.
        c.Eq(1, Quads(mesh.VertexCount), "a flat section without neighbours has no frontier walls");
        c.Eq(4, mesh.VertexCount, "one quad is four vertices");
        c.Eq(6, mesh.IndexCount, "one quad is six indices");

        // The merged top must actually span the section, not just claim to.
        float[] xs = Every3rd(mesh.Xyz, mesh.VertexCount, 0);
        float[] zs = Every3rd(mesh.Xyz, mesh.VertexCount, 2);
        c.Eq(0f, xs.Min(), "the merged surface starts at the section's near edge");
        c.Eq((float)Gs, xs.Max(), "the merged surface reaches the section's far edge");
        c.Eq(0f, zs.Min(), "the merged surface starts at the near z edge");
        c.Eq((float)Gs, zs.Max(), "the merged surface reaches the far z edge");

        // A hole must break the merge, or the rectangle would pave over missing terrain.
        LodSection holed = Solid(yTop: 10, yBottom: 0);
        holed.SetColumn(LodSection.ColumnIndex(32, 32), Array.Empty<ulong>());
        MeshResult holedMesh = LodMesher.BuildMesh(Fixtures.Job(holed));
        c.True(Quads(holedMesh.VertexCount) > 5, "a hole in the plain prevents a single-rectangle merge");

        // Differing heights cannot merge into one plane either.
        LodSection stepped = Solid(yTop: 10, yBottom: 0);
        stepped.SetColumn(LodSection.ColumnIndex(32, 32), new[] { LodSection.PackRun(0, 11, 0) });
        MeshResult steppedMesh = LodMesher.BuildMesh(Fixtures.Job(stepped));
        c.True(Quads(steppedMesh.VertexCount) > 5, "a column at a different height breaks the plane");
    }

    /// <summary>
    /// A surface merges by its own plane, not by whatever sits underneath it. Every top
    /// face here shares a y and a palette entry, and only the depth of the run beneath
    /// them differs, stepping once halfway across the section. The surface is one
    /// rectangle, because a surface quad is drawn at its own y and never reads the depth
    /// below it.
    ///
    /// This is the ocean case, and it is why GreedyMerge above cannot see it: that plain
    /// gives every column the same yBottom. A water run reaches the seabed, so its bottom
    /// tracks the floor contour and changes every few columns while the surface stays
    /// flat. A merge that keys on the run's bottom therefore falls apart on exactly the
    /// terrain the mesher exists to collapse.
    /// </summary>
    static void UnevenBase(Check c)
    {
        var s = new LodSection();
        s.FindOrAddPaletteEntry(blockId: 1, color: 0x00607080, flags: 0);
        for (int cz = 0; cz < Gs; cz++)
        {
            for (int cx = 0; cx < Gs; cx++)
            {
                s.SetColumn(LodSection.ColumnIndex(cx, cz),
                    new[] { LodSection.PackRun(0, 10, cx < Gs / 2 ? 0 : 5) });
            }
        }

        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(s));

        // Counted on the surface plane alone: the walls and the floor under the deep half
        // are real geometry that has nothing to do with the claim being made here.
        c.Eq(1, QuadsOnPlane(mesh.Xyz, mesh.VertexCount, axis: 1, value: 10f),
            "one flat surface over a stepped base merges into a single rectangle");

        // The general form, over bases with no pattern to them at all. One step could be
        // merged by a rule that happens to sort the two depths into two contiguous
        // groups; nothing merges 40 scattered depths into one rectangle except not
        // keying the surface on depth in the first place.
        foreach (int seed in new[] { 1, 2, 3 })
        {
            var rnd = new Random(seed);
            var noisy = new LodSection();
            noisy.FindOrAddPaletteEntry(blockId: 1, color: 0x00607080, flags: 0);
            for (int col = 0; col < Fixtures.Total; col++)
            {
                noisy.SetColumn(col, new[] { LodSection.PackRun(0, 10, rnd.Next(1, 9)) });
            }

            MeshResult noisyMesh = LodMesher.BuildMesh(Fixtures.Job(noisy));
            c.Eq(1, QuadsOnPlane(noisyMesh.Xyz, noisyMesh.VertexCount, axis: 1, value: 10f),
                $"a flat surface merges whatever the depths beneath it do (seed {seed})");
        }
    }

    /// <summary>
    /// Horizontal extent scales with the level's block step, but Y does not: y values are
    /// absolute world blocks at every level. Scaling Y too would sink coarse terrain into
    /// the ground, progressively further at each level out.
    /// </summary>
    static void LevelScaling(Check c)
    {
        LodSection flat = Solid(yTop: 10, yBottom: 0);

        MeshResult l0 = LodMesher.BuildMesh(Fixtures.Job(flat, LodWorld.SectionKey(0, 0, 0)));
        MeshResult l2 = LodMesher.BuildMesh(Fixtures.Job(flat, LodWorld.SectionKey(2, 0, 0)));

        c.Eq(Quads(l0.VertexCount), Quads(l2.VertexCount), "level does not change the quad count");
        c.Eq((float)Gs, Every3rd(l0.Xyz, l0.VertexCount, 0).Max(), "L0 spans one block per column");
        c.Eq((float)(Gs * 4), Every3rd(l2.Xyz, l2.VertexCount, 0).Max(), "L2 spans four blocks per column");
        c.Eq(10f, Every3rd(l2.Xyz, l2.VertexCount, 1).Max(), "L2 keeps absolute block heights");
    }

    /// <summary>
    /// The tint slot rides in the vertex alpha byte in three bands, because the vertex
    /// format is position plus colour and there is nowhere else to put it. The shader
    /// divides by TINT_SLOTS to recover which band it is - so the band boundaries here and
    /// the constant in the GLSL are the same number seen from two sides.
    /// </summary>
    static void AlphaBands(Check c)
    {
        c.Eq((byte)5, AlphaOf(Column(flags: 0, tintSlot: 5)), "opaque encodes the slot directly");
        c.Eq((byte)(LodTintRegistry.MaxSlots + 5),
            AlphaOf(Column(LodPaletteEntry.FlagWater, tintSlot: 5)), "water sits in the second band");
        c.Eq((byte)(LodTintRegistry.MaxSlots * 2 + 5),
            AlphaOf(Column(LodPaletteEntry.FlagThin, tintSlot: 5)), "thin cover sits in the third band");

        // An out-of-range slot must fall back to the identity tint rather than wrap into
        // the next band and repaint the block as water.
        c.Eq((byte)LodTintRegistry.SlotNone,
            AlphaOf(Column(flags: 0, tintSlot: (byte)LodTintRegistry.MaxSlots)),
            "a slot at the limit falls back to no tint");
        c.Eq((byte)LodTintRegistry.SlotNone,
            AlphaOf(Column(flags: 0, tintSlot: 255)), "a wildly out-of-range slot falls back to no tint");

        c.Eq((byte)LodTintRegistry.SlotNone,
            AlphaOf(Column(flags: 0, tintSlot: 5, color: unchecked((int)0x00E8E8E8))),
            "bright snow albedo drops a stored grass slot on remesh");
        int glacier = 170 | (200 << 8) | (220 << 16);
        c.Eq((byte)LodTintRegistry.SlotNone,
            AlphaOf(Column(flags: 0, tintSlot: 5, color: glacier)),
            "glacier-ice albedo drops a stored grass slot on remesh");
        c.Eq((byte)(LodTintRegistry.MaxSlots + 5),
            AlphaOf(Column(LodPaletteEntry.FlagWater, tintSlot: 5, color: unchecked((int)0x00E8E8E8))),
            "foam-white water keeps the water band and its tint slot");
        c.Eq(LodPaletteRepair.WaterFallbackColor,
            LodPaletteRepair.WaterDrawColor(unchecked((int)0x00FCFCFC)),
            "missing-tex water is forced to lake blue");
        c.Eq((byte)5, AlphaOf(Column(flags: 0, tintSlot: 5)),
            "ordinary grey-green tops keep the climate slot");
        c.Eq((byte)(LodTintRegistry.MaxSlots * 3),
            AlphaOf(Column(LodPaletteEntry.FlagBaked, tintSlot: 5)),
            "baked opaque land uses band 3 with identity tint");
    }

    static void WaterIsASeparatePass(Check c)
    {
        LodSection sea = Solid(yTop: 10, yBottom: 0, flags: LodPaletteEntry.FlagWater);
        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(sea));

        c.Eq(0, mesh.VertexCount, "an all-water section contributes nothing to the opaque pass");
        c.True(mesh.WaterVertexCount > 0, "water geometry lands in the blended pass");
        c.True(mesh.WaterXyz != null && mesh.WaterIndices != null, "the water pass carries its own buffers");

        // Water has no floor quads: they would z-fight with the seabed below.
        LodSection land = Solid(yTop: 10, yBottom: 0);
        MeshResult landMesh = LodMesher.BuildMesh(Fixtures.Job(land));
        c.Eq(0, landMesh.WaterVertexCount, "an all-solid section contributes nothing to the water pass");
    }

    /// <summary>
    /// Ground cover is a few centimetres of plant in a one-block cell. Drawn as a cube it
    /// turned meadows into fields of solid colour, so it is drawn as a mat instead: top face
    /// only, no walls, lifted a quarter block off the soil.
    ///
    /// The offset is measured UP from the run's bottom, never down from its top. Mip merging
    /// fuses adjacent thin runs, so at coarse levels one run can span several blocks, and a
    /// fixed drop from the top left the mat floating in mid-air.
    /// </summary>
    static void ThinMats(Check c)
    {
        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(Column(LodPaletteEntry.FlagThin, yTop: 10, yBottom: 4)));

        c.Eq(0, mesh.VertexCount, "thin cover draws nothing in the opaque pass");
        c.Eq(1, Quads(mesh.WaterVertexCount), "thin cover is a single quad: a top face and no walls");
        c.Eq(4.25f, Every3rd(mesh.WaterXyz!, mesh.WaterVertexCount, 1).Max(), "the mat sits a quarter block above its own base");

        // A tall run left by mip merging must still sit on the ground, not at its top.
        MeshResult tall = LodMesher.BuildMesh(Fixtures.Job(Column(LodPaletteEntry.FlagThin, yTop: 40, yBottom: 4)));
        c.Eq(4.25f, Every3rd(tall.WaterXyz!, tall.WaterVertexCount, 1).Max(), "a mip-merged tall thin run still sits on its base");

        // Clamped so the mat can never rise above the run it stands for.
        MeshResult flat = LodMesher.BuildMesh(Fixtures.Job(Column(LodPaletteEntry.FlagThin, yTop: 5, yBottom: 5)));
        c.Eq(5f, Every3rd(flat.WaterXyz!, flat.WaterVertexCount, 1).Max(), "a zero-height thin run is clamped to its own top");
    }

    /// <summary>
    /// Three deliberately asymmetric rules, each one the fix for a specific artefact:
    ///   - solid is culled only by solid, so a seabed stays visible through the water;
    ///   - water is culled by anything, so a submerged cliff does not double up;
    ///   - thin cover never culls anything, because a fern on a shoreline was deleting the
    ///     wall of the pond beside it and letting you see through the water's edge.
    /// </summary>
    static void CoverageRules(Check c)
    {
        // Solid beside water: the solid wall survives.
        c.True(WallsBetween(c, LodPaletteEntry.FlagWater, 0) > 0,
            "a solid wall is not culled by water beside it");

        // Water beside solid: the water wall is culled.
        c.Eq(0, WallsBetween(c, 0, LodPaletteEntry.FlagWater),
            "a water wall is culled by solid beside it");

        // Solid beside solid: culled, the ordinary case.
        c.Eq(0, WallsBetween(c, 0, 0), "a solid wall is culled by solid beside it");

        // Solid beside thin: the wall survives, because a mat covers nothing.
        c.True(WallsBetween(c, LodPaletteEntry.FlagThin, 0) > 0,
            "a solid wall is not culled by thin cover beside it");
    }

    static void MissingNeighbourDoesNotBecomeCliff(Check c)
    {
        LodSection flat = Solid(yTop: 10, yBottom: 0);

        MeshResult alone = LodMesher.BuildMesh(Fixtures.Job(flat));
        c.Eq(1, Quads(alone.VertexCount),
            "a missing neighbour does not create a full-height white cliff");

        LodSection shorter = Solid(yTop: 4, yBottom: 0);
        var neighbors = new SectionSnapshot?[4];
        for (int i = 0; i < neighbors.Length; i++) neighbors[i] = Fixtures.Snap(shorter);

        MeshResult stepped = LodMesher.BuildMesh(Fixtures.Job(flat, neighbors: neighbors));
        c.Eq(5, Quads(stepped.VertexCount),
            "a loaded shorter neighbour still exposes a real cliff");
    }

    static void UncapturedColumnDoesNotBecomeCliff(Check c)
    {
        var partial = new LodSection();
        int rock = partial.FindOrAddPaletteEntry(blockId: 1, color: 0x00808080, flags: 0);
        partial.SetColumn(LodSection.ColumnIndex(10, 10),
            new[] { LodSection.PackRun(rock, 30, 0) });

        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(partial));
        c.Eq(1, Quads(mesh.VertexCount),
            "unknown columns inside a partial section do not form a skinny full-height tower");
    }

    // ---- helpers ----

    /// <summary>
    /// Walls the subject column emits on the edge it shares with its neighbour.
    ///
    /// Both columns' walls land on the same plane - the subject's east face and the
    /// neighbour's west face are both at x = 11 - so the plane alone cannot tell them
    /// apart. The pass does: a translucent column writes to the water buffer and an opaque
    /// one to the opaque buffer, and the two columns here always differ in exactly that.
    /// </summary>
    static int WallsBetween(Check c, byte neighborFlags, byte subjectFlags)
    {
        var s = new LodSection();
        int subject = s.FindOrAddPaletteEntry(blockId: 1, color: 0x00808080, flags: subjectFlags);
        int neighbor = s.FindOrAddPaletteEntry(blockId: 2, color: 0x00304050, flags: neighborFlags);

        s.SetColumn(LodSection.ColumnIndex(10, 10), new[] { LodSection.PackRun(subject, 10, 0) });
        s.SetColumn(LodSection.ColumnIndex(11, 10), new[] { LodSection.PackRun(neighbor, 10, 0) });

        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(s));

        bool subjectIsTranslucent =
            (subjectFlags & (LodPaletteEntry.FlagWater | LodPaletteEntry.FlagThin)) != 0;

        return subjectIsTranslucent
            ? QuadsOnPlane(mesh.WaterXyz, mesh.WaterVertexCount, axis: 0, value: 11f)
            : QuadsOnPlane(mesh.Xyz, mesh.VertexCount, axis: 0, value: 11f);
    }

    /// <summary>
    /// Quads whose four vertices all share one coordinate: axis 0 for an east/west wall,
    /// axis 1 for a horizontal surface. Counting on a plane keeps a claim about one face
    /// from being answered by the quad count of the whole section.
    /// </summary>
    static int QuadsOnPlane(float[]? xyz, int vertexCount, int axis, float value)
    {
        if (xyz == null || vertexCount <= 0) return 0;
        int count = 0;
        int floats = vertexCount * 3;
        for (int v = 0; v + 12 <= floats; v += 12)
        {
            bool onPlane = true;
            for (int k = 0; k < 4; k++)
            {
                if (Math.Abs(xyz[v + k * 3 + axis] - value) > 0.0001f) { onPlane = false; break; }
            }
            if (onPlane) count++;
        }
        return count;
    }

    static byte AlphaOf(LodSection section)
    {
        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(section));
        byte[]? rgba = mesh.VertexCount > 0 ? mesh.Rgba : mesh.WaterRgba;
        return rgba is { Length: >= 4 } ? rgba[3] : (byte)255;
    }

    /// <summary>A section with exactly one captured column.</summary>
    static LodSection Column(byte flags = 0, byte tintSlot = 0, int yTop = 10, int yBottom = 0, int color = 0x00A0B0C0)
    {
        var s = new LodSection();
        s.FindOrAddPaletteEntry(blockId: 1, color: color, flags: flags, tintSlot: tintSlot);
        s.SetColumn(LodSection.ColumnIndex(5, 5), new[] { LodSection.PackRun(0, yTop, yBottom) });
        return s;
    }

    static LodSection Solid(int yTop, int yBottom, byte flags = 0)
    {
        var s = new LodSection();
        s.FindOrAddPaletteEntry(blockId: 1, color: 0x00607080, flags: flags);
        ulong[] run = { LodSection.PackRun(0, yTop, yBottom) };
        for (int col = 0; col < Fixtures.Total; col++) s.SetColumn(col, run);
        return s;
    }

    static int Quads(int vertexCount) => vertexCount / 4;

    static float[] Every3rd(float[] xyz, int vertexCount, int offset)
    {
        var result = new float[vertexCount];
        for (int i = 0; i < result.Length; i++) result[i] = xyz[i * 3 + offset];
        return result;
    }
}
