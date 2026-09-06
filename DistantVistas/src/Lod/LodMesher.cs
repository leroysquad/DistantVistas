using System.Buffers;
namespace DistantVistas;

/// <summary>
/// Turns a section snapshot into raw vertex data (worker thread). Every run is a box;
/// faces are collected first, then greedy-merged before emission:
///
/// - Horizontal faces (tops/bottoms) group into Y-planes per (y, palette, side) and
///   merge into maximal rectangles with a standard 2D greedy sweep - ocean surfaces
///   and plains collapse from thousands of quads to a handful.
/// - Vertical faces (walls) merge along their strip axis where adjacent columns
///   expose identical (span, palette) segments - cliffs and frontier walls collapse
///   into long ribbons.
///
/// Coverage rules: solid faces are only culled by solid neighbors so terrain shows
/// through translucent water; water faces are culled by any coverage. A missing
/// neighbor snapshot is unknown coverage and does not become a full-height wall.
///
/// Underwater seal (0.7.11): water does not cull solids, so raw meshing would draw
/// cave ceilings/walls through the ocean ("cake slice"). Per column with FlagWater,
/// only the contiguous opaque seabed skin from the water interface down to the first
/// air gap is meshed; opaques below that gap are dropped. Water faces, seabed tops,
/// and walls that open into water are unchanged. Remesh-only — no recapture.
/// </summary>
public static class LodMesher
{
    const int W = 0, E = 1, N = 2, S = 3;

    // The tint slot rides in the alpha byte, so the vertex format stays position+colour:
    //   0..63    opaque,     tint slot = alpha
    //   64..127  water,      tint slot = alpha - 64
    //   128..191 thin plant, tint slot = alpha - 128
    //   192..255 baked,      identity tint — stored RGB is final (band 3)
    // Slot 0 is the identity tint. The band picks the blend factor in the shader, so
    // water and flowers can be see-through by different amounts.
    const byte WaterBase = LodTintRegistry.MaxSlots;
    const byte ThinBase = LodTintRegistry.MaxSlots * 2;
    const byte BakedBase = LodTintRegistry.MaxSlots * 3;

    static byte AlphaFor(byte paletteFlags, byte tintSlot, int color)
    {
        bool water = (paletteFlags & LodPaletteEntry.FlagWater) != 0;
        bool thin = (paletteFlags & LodPaletteEntry.FlagThin) != 0;
        if ((paletteFlags & LodPaletteEntry.FlagBaked) != 0)
        {
            // Visit-baked RGB is final (band 3). Thin band 2 still multiplies live tint
            // in the shader and painted lavender canopies after leave (0.8.36).
            if (water) return (byte)(WaterBase + LodTintRegistry.SlotNone);
            return BakedBase;
        }

        byte slot = tintSlot < LodTintRegistry.MaxSlots ? tintSlot : (byte)LodTintRegistry.SlotNone;
        // Skip live tint for stored colours that are already brown earth, or
        // snow/ice that would turn green if a grass high-tint were multiplied
        // on. Greyscale and dull-olive grass MUST keep the climate slot or far
        // LOD stays the raw atlas grey. Remesh-only: old caches keep albedo
        // and only drop the slot.
        // Water keeps its climate slot. Treating pale/foam water as snow/ice
        // stripped the tint and left a white stream.
        if (!water && (LodPaletteRepair.IsRockLikeAlbedo(color) || LodPaletteRepair.IsSnowOrIceAlbedo(color)))
            slot = (byte)LodTintRegistry.SlotNone;
        if (water) return (byte)(WaterBase + slot);
        if ((paletteFlags & LodPaletteEntry.FlagThin) != 0) return (byte)(ThinBase + slot);
        return slot;
    }

    class Buffers
    {
        public readonly List<float> Xyz = new(16384);
        public readonly List<byte> Rgba = new(8192);
        public readonly List<int> Indices = new(12288);

        public void Clear()
        {
            Xyz.Clear();
            Rgba.Clear();
            Indices.Clear();
        }
    }

    /// <summary>One exposed horizontal face: column cell + plane y + palette.</summary>
    readonly record struct HFace(short Cx, short Cz, short Y, short Pid, bool Bottom, bool Water, bool Thin, short YBottom);

    /// <summary>One exposed wall segment on a column edge.</summary>
    readonly record struct VFace(byte Dir, short Fixed, short Along, short YTop, short YBottom, short Pid, bool Water);

    [ThreadStatic] static List<HFace>? hFaces;
    [ThreadStatic] static List<VFace>? vFaces;

    // Pooled per thread for the same reason the face lists are, and missed when they were
    // done: a fresh pair cost 240 KB per mesh job even for a section that emits five
    // quads, and their lists grew past the 85 KB large-object threshold and back on every
    // job for a dense one. Reused, they grow once per thread and stay grown.
    [ThreadStatic] static Buffers? opaqueBuffers;
    [ThreadStatic] static Buffers? waterBuffers;

    public static MeshResult BuildMesh(MeshJob job)
    {
        int level = LodWorld.KeyLevel(job.Key);
        int step = LodSection.ColumnStepBlocks << level;
        int gs = LodSection.GridSize;
        SectionSnapshot self = job.Self;

        var opaque = opaqueBuffers ??= new Buffers();
        var water = waterBuffers ??= new Buffers();
        var hf = hFaces ??= new List<HFace>(8192);
        var vf = vFaces ??= new List<VFace>(8192);
        opaque.Clear();
        water.Clear();
        hf.Clear();
        vf.Clear();

        // ---- Phase 1: collect exposed faces ----

        for (int cz = 0; cz < gs; cz++)
        {
            for (int cx = 0; cx < gs; cx++)
            {
                int col = LodSection.ColumnIndex(cx, cz);
                if (!self.Captured[col]) continue;

                Span<ulong> runs = self.ColumnRuns(col);
                bool sealUnderwater = TryGetUnderwaterSealFloor(self, runs, out int sealFloorY);

                for (int r = 0; r < runs.Length; r++)
                {
                    ulong run = runs[r];
                    int yTop = LodSection.RunYTop(run);
                    int yBottom = LodSection.RunYBottom(run);
                    int pid = LodSection.RunPaletteId(run);
                    bool isTranslucent = IsTranslucent(self.PaletteFlags[pid]);

                    // Sealed underwater hull: drop opaque cave geometry below the
                    // first air gap under the surface-water / seabed skin.
                    if (sealUnderwater && !isTranslucent && yTop < sealFloorY) continue;

                    bool isThin = (self.PaletteFlags[pid] & LodPaletteEntry.FlagThin) != 0;
            // FidelityStep 1: keep thin mats through L1 (was drop at L1+); still cull L2+.
            int thinDropLevel = LodWorld.FidelityStep >= 0.5 ? 2 : 1;
            if (isThin && level >= thinDropLevel) continue;

                    // Ground cover is a few centimetres of plant in a one-block cell, so
                    // it is drawn as a low mat: top face only, dropped to just above the
                    // soil. Rendering it as a full cube turned meadows into fields of
                    // solid blocks, which was the real problem -- most flowers store
                    // perfectly good colour.
                    if (isThin)
                    {
                        // Thin mats must sit on a solid run beneath; otherwise they float.
                        bool thinGrounded = r < runs.Length - 1
                            && LodSection.RunYTop(runs[r + 1]) >= yBottom - 1
                            && !IsThinRun(self, runs[r + 1])
                            && !IsTranslucentRun(self, runs[r + 1]);
                        if (!thinGrounded && level >= 1) continue;
                        hf.Add(new HFace((short)cx, (short)cz, (short)yTop, (short)pid, false, true, true, (short)yBottom));
                        continue;
                    }

                    // Mid-far anti-floater: skip short plant scraps that hang in air
                    // (sparse leaf pixels after mip) unless supported within 1 block.
                    // Plant only: a short soil or ore run at a cave ceiling is the
                    // bottom of the terrain above it, and skipping it slit the face.
                    if (level >= 1 && !isTranslucent
                        && self.PaletteTintSlots[pid] != LodTintRegistry.SlotNone)
                    {
                        int runH = yTop - yBottom;
                        bool supported = r < runs.Length - 1
                            && LodSection.RunYTop(runs[r + 1]) >= yBottom - 1;
                        if (!supported) supported = yBottom <= 2;
                        if (!supported && runH <= 4) continue; // floating leaf/veg scrap
                    }

                    bool topCovered = r > 0
                        && LodSection.RunYBottom(runs[r - 1]) == yTop
                        && !IsThinRun(self, runs[r - 1])
                        && IsTranslucentRun(self, runs[r - 1]) == isTranslucent;
                    // YBottom is carried for thin cover only, which is the one face that
                    // reads it (it sits a quarter block above its own base). A surface
                    // face is drawn at its own y whatever depth the run beneath it
                    // reaches, so recording that depth here only splits the plane: a flat
                    // sea would break up along the contour of the seabed under it.
                    if (!topCovered) hf.Add(new HFace((short)cx, (short)cz, (short)yTop, (short)pid, false, isTranslucent, false, 0));

                    bool bottomCovered = r < runs.Length - 1
                        && LodSection.RunYTop(runs[r + 1]) == yBottom
                        && !IsThinRun(self, runs[r + 1])
                        && IsTranslucentRun(self, runs[r + 1]) == isTranslucent;
                    if (!bottomCovered && yBottom > 1 && !isTranslucent)
                    {
                        // A floor's own plane IS the run's bottom, so Y already carries it.
                        hf.Add(new HFace((short)cx, (short)cz, (short)yBottom, (short)pid, true, false, false, 0));
                    }

                    bool solidCoverOnly = !isTranslucent;
                    CollectSide(vf, job, W, cx, cz, cx - 1, cz, yTop, yBottom, pid, isTranslucent, solidCoverOnly);
                    CollectSide(vf, job, E, cx, cz, cx + 1, cz, yTop, yBottom, pid, isTranslucent, solidCoverOnly);
                    CollectSide(vf, job, N, cx, cz, cx, cz - 1, yTop, yBottom, pid, isTranslucent, solidCoverOnly);
                    CollectSide(vf, job, S, cx, cz, cx, cz + 1, yTop, yBottom, pid, isTranslucent, solidCoverOnly);
                }
            }
        }

        // ---- Phase 2: greedy-merge and emit ----

        EmitHorizontalGreedy(hf, self, opaque, water, step);
        EmitVerticalMerged(vf, self, opaque, water, step);

        return new MeshResult
        {
            Key = job.Key,
            Xyz = RentCopy(opaque.Xyz),
            Rgba = RentCopy(opaque.Rgba),
            Indices = RentCopy(opaque.Indices),
            VertexCount = opaque.Xyz.Count / 3,
            IndexCount = opaque.Indices.Count,
            WaterXyz = water.Xyz.Count > 0 ? RentCopy(water.Xyz) : null,
            WaterRgba = water.Xyz.Count > 0 ? RentCopy(water.Rgba) : null,
            WaterIndices = water.Xyz.Count > 0 ? RentCopy(water.Indices) : null,
            WaterVertexCount = water.Xyz.Count / 3,
            WaterIndexCount = water.Indices.Count,
        };
    }

    // Thread-static lists stay grown. The result arrays come from the pool so a
    // finished mesh is not a second copy sitting next to the list until GC.
    static T[] RentCopy<T>(List<T> src)
    {
        if (src.Count == 0) return Array.Empty<T>();
        T[] rented = ArrayPool<T>.Shared.Rent(src.Count);
        src.CopyTo(0, rented, 0, src.Count);
        return rented;
    }

    // ---- Horizontal faces: per-plane 2D greedy rectangles ----

    [ThreadStatic] static int[]? planeGrid;
    [ThreadStatic] static int planeGridStamp;

    static void EmitHorizontalGreedy(List<HFace> faces, SectionSnapshot self, Buffers opaque, Buffers water, int step)
    {
        if (faces.Count == 0) return;
        int gs = LodSection.GridSize;

        // Group faces into planes by (y, pid, side, thin, run bottom); grid cells hold a
        // stamp so we never clear the array between planes.
        //
        // Every field the group scan below tests must appear here, ahead of Cz/Cx. The
        // scan walks a contiguous span of this order, so a field it tests but the sort
        // ignores ends the plane at the first cell that differs, wherever that lands.
        // Thin and YBottom were both missing, and a flat surface over an uneven base
        // split into one strip per row as a result.
        //
        // Sorting on them is only half of it. The other half is not recording a value a
        // face never reads: see the top and floor faces above, which now carry YBottom 0.
        // Otherwise the plane still breaks once per distinct depth underneath it.
        faces.Sort((a, b) =>
        {
            int c = a.Y.CompareTo(b.Y);
            if (c != 0) return c;
            c = a.Pid.CompareTo(b.Pid);
            if (c != 0) return c;
            c = a.Bottom.CompareTo(b.Bottom);
            if (c != 0) return c;
            c = a.Thin.CompareTo(b.Thin);
            if (c != 0) return c;
            c = a.YBottom.CompareTo(b.YBottom);
            if (c != 0) return c;
            c = a.Cz.CompareTo(b.Cz);
            return c != 0 ? c : a.Cx.CompareTo(b.Cx);
        });

        var grid = planeGrid ??= new int[gs * gs];

        int start = 0;
        while (start < faces.Count)
        {
            HFace first = faces[start];
            int end = start;
            while (end < faces.Count
                && faces[end].Y == first.Y
                && faces[end].Pid == first.Pid
                && faces[end].Bottom == first.Bottom
                && faces[end].Thin == first.Thin
                && faces[end].YBottom == first.YBottom)
            {
                end++;
            }

            int stamp = ++planeGridStamp;
            for (int i = start; i < end; i++)
            {
                grid[faces[i].Cz * gs + faces[i].Cx] = stamp;
            }

            Buffers buf = first.Water ? water : opaque;
            int color = self.PaletteColors[first.Pid];
            if (first.Water) color = LodPaletteRepair.WaterDrawColor(color);
            byte alpha = AlphaFor(self.PaletteFlags[first.Pid], self.PaletteTintSlots[first.Pid], color);

            for (int i = start; i < end; i++)
            {
                int cx = faces[i].Cx;
                int cz = faces[i].Cz;
                if (grid[cz * gs + cx] != stamp) continue; // already consumed

                // Extend width along +X, then height along +Z while the full row matches.
                int wRect = 1;
                while (cx + wRect < gs && grid[cz * gs + cx + wRect] == stamp) wRect++;

                int hRect = 1;
                while (cz + hRect < gs)
                {
                    bool rowOk = true;
                    for (int dx = 0; dx < wRect; dx++)
                    {
                        if (grid[(cz + hRect) * gs + cx + dx] != stamp) { rowOk = false; break; }
                    }
                    if (!rowOk) break;
                    hRect++;
                }

                for (int dz = 0; dz < hRect; dz++)
                {
                    for (int dx = 0; dx < wRect; dx++)
                    {
                        grid[(cz + dz) * gs + cx + dx] = 0;
                    }
                }

                float x0 = cx * step;
                float x1 = (cx + wRect) * step;
                float z0 = cz * step;
                float z1 = (cz + hRect) * step;
                // Ground cover sits a quarter block above the soil it stands on: clear
                // of z-fighting, invisible as a step. Measured UP from the run's bottom,
                // never down from its top -- mip merging fuses adjacent thin runs, so at
                // coarse levels one run spans several blocks and a fixed drop from the
                // top left the mat floating in mid-air. Clamped so it can never rise
                // above the run it represents.
                // Y is absolute blocks and is NOT scaled by step, so neither is the offset.
                float y = first.Thin ? Math.Min(first.YBottom + 0.25f, first.Y) : first.Y;

                if (first.Bottom)
                {
                    AddQuad(buf, color, alpha, x0, y, z0, x0, y, z1, x1, y, z1, x1, y, z0);
                }
                else
                {
                    AddQuad(buf, color, alpha, x0, y, z0, x1, y, z0, x1, y, z1, x0, y, z1);
                }
            }

            start = end;
        }
    }

    // ---- Vertical faces: merge along the strip axis ----

    static void EmitVerticalMerged(List<VFace> faces, SectionSnapshot self, Buffers opaque, Buffers water, int step)
    {
        if (faces.Count == 0) return;

        faces.Sort((a, b) =>
        {
            int c = a.Dir.CompareTo(b.Dir);
            if (c != 0) return c;
            c = a.Fixed.CompareTo(b.Fixed);
            if (c != 0) return c;
            c = a.YTop.CompareTo(b.YTop);
            if (c != 0) return c;
            c = a.YBottom.CompareTo(b.YBottom);
            if (c != 0) return c;
            c = a.Pid.CompareTo(b.Pid);
            return c != 0 ? c : a.Along.CompareTo(b.Along);
        });

        int i = 0;
        while (i < faces.Count)
        {
            VFace seg = faces[i];
            int alongEnd = seg.Along + 1;
            int j = i + 1;
            while (j < faces.Count)
            {
                VFace next = faces[j];
                if (next.Dir != seg.Dir || next.Fixed != seg.Fixed || next.YTop != seg.YTop
                    || next.YBottom != seg.YBottom || next.Pid != seg.Pid || next.Along != alongEnd)
                {
                    break;
                }
                alongEnd++;
                j++;
            }

            Buffers buf = seg.Water ? water : opaque;
            int color = self.PaletteColors[seg.Pid];
            if (seg.Water) color = LodPaletteRepair.WaterDrawColor(color);
            byte alpha = AlphaFor(self.PaletteFlags[seg.Pid], self.PaletteTintSlots[seg.Pid], color);

            // W/E walls run along Z at fixed X; N/S walls run along X at fixed Z.
            bool xWall = seg.Dir is W or E;
            float fixedCoord = seg.Dir switch
            {
                W => seg.Fixed * step,
                E => (seg.Fixed + 1) * step,
                N => seg.Fixed * step,
                _ => (seg.Fixed + 1) * step,
            };
            float a0 = seg.Along * step;
            float a1 = alongEnd * step;

            if (xWall)
            {
                AddQuad(buf, color, alpha,
                    fixedCoord, seg.YBottom, a0, fixedCoord, seg.YBottom, a1,
                    fixedCoord, seg.YTop, a1, fixedCoord, seg.YTop, a0);
            }
            else
            {
                AddQuad(buf, color, alpha,
                    a0, seg.YBottom, fixedCoord, a1, seg.YBottom, fixedCoord,
                    a1, seg.YTop, fixedCoord, a0, seg.YTop, fixedCoord);
            }

            i = j;
        }
    }

    // ---- Face collection helpers ----

    static bool IsTranslucent(byte flags) =>
        (flags & (LodPaletteEntry.FlagWater | LodPaletteEntry.FlagThin)) != 0;

    static bool IsWaterRun(SectionSnapshot s, ulong run) =>
        (s.PaletteFlags[LodSection.RunPaletteId(run)] & LodPaletteEntry.FlagWater) != 0;

    static bool IsTranslucentRun(SectionSnapshot s, ulong run) =>
        IsTranslucent(s.PaletteFlags[LodSection.RunPaletteId(run)]);

    /// <summary>
    /// Columns with surface water: compute the Y floor of the sealed seabed skin
    /// (contiguous opaques from the top water stack's bottom down until the first
    /// air gap). When this returns true, opaque runs with YTop &lt; sealFloorY are
    /// cave/hollow geometry under the ocean and must not be meshed. Water and thin
    /// runs are never sealed away. Dry columns return false (full opaque meshing).
    /// </summary>
    static bool TryGetUnderwaterSealFloor(SectionSnapshot self, Span<ulong> runs, out int sealFloorY)
    {
        sealFloorY = int.MinValue;

        int i = 0;
        while (i < runs.Length && !IsWaterRun(self, runs[i])) i++;
        if (i >= runs.Length) return false;

        // Top contiguous water stack (FlagWater only — thin mats do not extend it).
        int waterBottom = LodSection.RunYBottom(runs[i]);
        i++;
        while (i < runs.Length
            && IsWaterRun(self, runs[i])
            && LodSection.RunYTop(runs[i]) == waterBottom)
        {
            waterBottom = LodSection.RunYBottom(runs[i]);
            i++;
        }

        // Contiguous solid skin attached to that interface, downward until air gap.
        int expectedTop = waterBottom;
        bool anySolid = false;
        int floor = waterBottom;
        while (i < runs.Length)
        {
            ulong run = runs[i];
            int yTop = LodSection.RunYTop(run);
            int yBottom = LodSection.RunYBottom(run);
            if (yTop != expectedTop) break; // air gap ends the seal

            if (IsWaterRun(self, run)) break;

            if (IsThinRun(self, run))
            {
                // Thin mats occupy the column but are not seabed; skip through them
                // without breaking contiguity so a plant on the sea floor still seals.
                expectedTop = yBottom;
                i++;
                continue;
            }

            anySolid = true;
            floor = yBottom;
            expectedTop = yBottom;
            i++;
        }

        // No attached solid: still seal at waterBottom so every opaque at/below the
        // interface is dropped (nothing to draw as seabed in this column).
        sealFloorY = anySolid ? floor : waterBottom;
        return true;
    }

    /// <summary>
    /// Thin ground cover is drawn as a mat a quarter block tall, so it fills almost none
    /// of its cell and must never occlude anything. Without this a fern on a shoreline
    /// counted as cover for the water beside it and deleted the water's wall, letting you
    /// see through the edge of the pond.
    /// </summary>
    static bool IsThinRun(SectionSnapshot s, ulong run) =>
        (s.PaletteFlags[LodSection.RunPaletteId(run)] & LodPaletteEntry.FlagThin) != 0;

    static (SectionSnapshot? snap, int col) NeighborColumn(MeshJob job, int cx, int cz)
    {
        int gs = LodSection.GridSize;

        if (cx >= 0 && cx < gs && cz >= 0 && cz < gs)
        {
            return (job.Self, LodSection.ColumnIndex(cx, cz));
        }

        SectionSnapshot? nb;
        int ncx = cx, ncz = cz;
        if (cx < 0) { nb = job.Neighbors[W]; ncx = gs - 1; }
        else if (cx >= gs) { nb = job.Neighbors[E]; ncx = 0; }
        else if (cz < 0) { nb = job.Neighbors[N]; ncz = gs - 1; }
        else { nb = job.Neighbors[S]; ncz = 0; }

        return (nb, nb == null ? 0 : LodSection.ColumnIndex(ncx, ncz));
    }

    /// <summary>Collect exposed wall segments for [yBottom, yTop) minus the neighbor's covered intervals.</summary>
    static void CollectSide(List<VFace> vf, MeshJob job, int dir, int cx, int cz, int ncx, int ncz,
        int yTop, int yBottom, int pid, bool isTranslucent, bool solidCoverOnly)
    {
        var (nb, ncol) = NeighborColumn(job, ncx, ncz);
        if (nb == null || !nb.Captured[ncol]) return;

        Span<ulong> neighborRuns = nb.ColumnRuns(ncol);

        // For W/E walls the strip axis is Z (fixed = cx); for N/S it's X (fixed = cz).
        short fix = (short)(dir is W or E ? cx : cz);
        short along = (short)(dir is W or E ? cz : cx);

        int cur = yTop;

        for (int i = 0; i < neighborRuns.Length && cur > yBottom; i++)
        {
            // A mat never covers anything; beyond that, solid faces are only culled by
            // solid neighbours so terrain stays visible through water.
            if (IsThinRun(nb, neighborRuns[i])) continue;
            if (solidCoverOnly && IsTranslucentRun(nb, neighborRuns[i])) continue;

            int nTop = LodSection.RunYTop(neighborRuns[i]);
            int nBottom = LodSection.RunYBottom(neighborRuns[i]);
            if (nTop <= yBottom) break;
            if (nBottom >= cur) continue;

            int coverTop = Math.Min(nTop, cur);
            if (coverTop < cur)
            {
                vf.Add(new VFace((byte)dir, fix, along, (short)cur, (short)coverTop, (short)pid, isTranslucent));
            }
            cur = Math.Max(nBottom, yBottom);
        }

        if (cur > yBottom)
        {
            vf.Add(new VFace((byte)dir, fix, along, (short)cur, (short)yBottom, (short)pid, isTranslucent));
        }
    }

    // ---- Emission primitives ----

    static void AddQuad(Buffers buf, int color, byte alpha,
        float x0, float y0, float z0, float x1, float y1, float z1,
        float x2, float y2, float z2, float x3, float y3, float z3)
    {
        int baseVert = buf.Xyz.Count / 3;
        AddVert(buf, color, alpha, x0, y0, z0);
        AddVert(buf, color, alpha, x1, y1, z1);
        AddVert(buf, color, alpha, x2, y2, z2);
        AddVert(buf, color, alpha, x3, y3, z3);
        buf.Indices.Add(baseVert);
        buf.Indices.Add(baseVert + 1);
        buf.Indices.Add(baseVert + 2);
        buf.Indices.Add(baseVert);
        buf.Indices.Add(baseVert + 2);
        buf.Indices.Add(baseVert + 3);
    }

    static void AddVert(Buffers buf, int color, byte alpha, float x, float y, float z)
    {
        buf.Xyz.Add(x);
        buf.Xyz.Add(y);
        buf.Xyz.Add(z);
        buf.Rgba.Add((byte)(color & 0xFF));
        buf.Rgba.Add((byte)((color >> 8) & 0xFF));
        buf.Rgba.Add((byte)((color >> 16) & 0xFF));
        buf.Rgba.Add(alpha);
    }
}
