using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// One entry in a section's palette: a block resolved to its LOD appearance.
/// Color is RGBA-packed (R in the low byte), resolved on the main thread when the
/// palette entry is first registered (Block.GetColor touches client-only state).
/// </summary>
public struct LodPaletteEntry
{
    public int BlockId;

    /// <summary>Untinted base color; seasonal/climate tint is applied live in the shader.</summary>
    public int Color;

    public byte Flags;

    /// <summary>
    /// Which live tint applies (see LodTintRegistry). Derived from the block, never
    /// persisted: an existing cache gets correct per-species tints without re-capturing,
    /// and the mapping stays right if a game update moves a block to a different map.
    /// </summary>
    public byte TintSlot;

    public const byte FlagWater = 1;
    // Bits 2 and 4 are free: they held tint classes, now superseded by TintSlot.

    /// <summary>
    /// Not terrain at all (fire, meta markers): dropped at capture so it never becomes
    /// geometry. Thin ground cover is NOT skipped - see FlagThin.
    /// </summary>
    public const byte FlagSkip = 8;

    /// <summary>
    /// Thin decorative geometry (flowers) that vanilla draws as crossed quads. As a
    /// solid LOD cube it reads as a grey blob; drawn see-through it reads as a plant
    /// and the ground shows through it.
    /// </summary>
    public const byte FlagThin = 16;
}

/// <summary>
/// The M4 leaf data model: a section holds 64×64 vertical RLE columns over a local
/// palette. At level L each column covers (ColumnStepBlocks &lt;&lt; L) blocks, so a
/// section spans SectionBlocks &lt;&lt; L. Runs are packed ulongs, stored top-down,
/// contiguous per column, addressed by a prefix-offset table - compact, fast to
/// serialize, and cheap to mip (concepts per DESIGN.md §4, informed by DH/Voxy).
/// </summary>
public class LodSection
{
    public const int GridSize = 64;                 // columns per section edge
    public const int ColumnStepBlocks = 1;          // blocks per column at level 0 - full DH-parity resolution
    public const int SectionBlocks = GridSize * ColumnStepBlocks; // 64 at level 0

    /// <summary>Run packing: paletteId(16) | yTop(14) | yBottom(14). Run spans [yBottom, yTop).</summary>
    public static ulong PackRun(int paletteId, int yTop, int yBottom) =>
        ((ulong)(uint)paletteId << 28) | ((ulong)(uint)(yTop & 0x3FFF) << 14) | (uint)(yBottom & 0x3FFF);

    public static int RunPaletteId(ulong run) => (int)(run >> 28);
    public static int RunYTop(ulong run) => (int)((run >> 14) & 0x3FFF);
    public static int RunYBottom(ulong run) => (int)(run & 0x3FFF);

    /// <summary>Prefix offsets into Runs; column c owns Runs[ColumnStart[c] .. ColumnStart[c+1]).</summary>
    public int[] ColumnStart = new int[GridSize * GridSize + 1];

    public ulong[] Runs = Array.Empty<ulong>();

    public readonly List<LodPaletteEntry> Palette = new();

    // Snapshot copies of the palette, rebuilt when an entry is added. The mesher
    // only reads these, so every SectionSnapshot can share the same arrays.
    int[]? snapPaletteColors;
    byte[]? snapPaletteFlags;
    byte[]? snapPaletteTintSlots;
    int snapPaletteCount = -1;

    /// <summary>Columns that have been captured at least once (empty column ≠ uncaptured column).</summary>
    public readonly bool[] Captured = new bool[GridSize * GridSize];

    public int CapturedColumns;

    /// <summary>
    /// Quadrants (one vanilla chunk column each, bit = qz*2+qx) whose columns did NOT
    /// come from this side's own capture of a loaded chunk: a server peek (worldgen
    /// stopped at the Terrain pass, so no trees, no ponds), a sweep, or another
    /// player's cache. Real terrain observed here replaces them; until then the
    /// quadrant counts as captured for drawing but not for QueueColumn's skip, or a
    /// visited forest keeps drawing as the bare hills the peek produced. Persisted
    /// with the row, because the visit usually comes a session after the peek.
    /// Level 0 only; mips leave it 0.
    /// </summary>
    public byte ProvisionalQuadrants;

    public const int QuadrantColumns = GridSize / 2;
    public const int QuadrantCount = 4;

    /// <summary>Quadrant of a section-local column coordinate.</summary>
    public static int QuadrantOf(int colX, int colZ) =>
        (colZ / QuadrantColumns) * 2 + colX / QuadrantColumns;

    public bool IsProvisionalQuadrant(int quadrant) => (ProvisionalQuadrants & (1 << quadrant)) != 0;

    public int QuadrantCapturedCount(int quadrant)
    {
        int x0 = (quadrant & 1) * QuadrantColumns;
        int z0 = (quadrant >> 1) * QuadrantColumns;
        int n = 0;
        for (int z = z0; z < z0 + QuadrantColumns; z++)
        {
            int row = z * GridSize + x0;
            for (int x = 0; x < QuadrantColumns; x++)
            {
                if (Captured[row + x]) n++;
            }
        }
        return n;
    }

    public bool QuadrantFullyCaptured(int quadrant) =>
        QuadrantCapturedCount(quadrant) == QuadrantColumns * QuadrantColumns;

    /// <summary>
    /// Copy neighbour columns into uncaptured holes that do not touch the section
    /// edge. A capture that skipped the middle of a quadrant (chunk disposed mid-read,
    /// rain map 0) left those columns undrawn: no top, and CollectSide emits no wall
    /// toward uncaptured, so the mountain was a tunnel of sky and cave interiors.
    /// Frontier / missing-quadrant uncaptured (touches the 64-edge) stays empty so we
    /// do not invent a plateau past where we have actually looked. Real caves are
    /// captured columns with air in the runs and are not touched.
    /// </summary>
    public bool SealInteriorHoles()
    {
        int total = GridSize * GridSize;
        if (CapturedColumns == 0 || CapturedColumns == total) return false;

        var seen = new byte[total];
        var stack = new int[total];
        var comp = new int[total];
        bool changed = false;

        for (int start = 0; start < total; start++)
        {
            if (Captured[start] || seen[start] != 0) continue;

            int sp = 0;
            stack[sp++] = start;
            seen[start] = 1;
            int n = 0;
            bool touchesEdge = false;

            while (sp > 0)
            {
                int i = stack[--sp];
                comp[n++] = i;
                int x = i % GridSize;
                int z = i / GridSize;
                if (x == 0 || z == 0 || x == GridSize - 1 || z == GridSize - 1)
                    touchesEdge = true;

                void Try(int nx, int nz)
                {
                    if ((uint)nx >= GridSize || (uint)nz >= GridSize) return;
                    int ni = nz * GridSize + nx;
                    if (Captured[ni] || seen[ni] != 0) return;
                    seen[ni] = 1;
                    stack[sp++] = ni;
                }
                Try(x - 1, z);
                Try(x + 1, z);
                Try(x, z - 1);
                Try(x, z + 1);
            }

            if (touchesEdge) continue;

            bool progress = true;
            while (progress)
            {
                progress = false;
                for (int k = 0; k < n; k++)
                {
                    int i = comp[k];
                    if (Captured[i]) continue;
                    int x = i % GridSize;
                    int z = i / GridSize;
                    int src = -1;
                    void Take(int nx, int nz)
                    {
                        if (src >= 0) return;
                        if ((uint)nx >= GridSize || (uint)nz >= GridSize) return;
                        int ni = nz * GridSize + nx;
                        if (Captured[ni]) src = ni;
                    }
                    Take(x - 1, z);
                    Take(x + 1, z);
                    Take(x, z - 1);
                    Take(x, z + 1);
                    if (src < 0) continue;
                    ulong[] copy = ColumnRuns(src).ToArray();
                    SetColumn(i, copy);
                    progress = true;
                    changed = true;
                }
            }
        }

        return changed;
    }

    public bool ClearProvisional(int quadrant)
    {
        byte mask = (byte)(1 << quadrant);
        if ((ProvisionalQuadrants & mask) == 0) return false;
        ProvisionalQuadrants &= (byte)~mask;
        return true;
    }

    /// <summary>
    /// True when every captured column in this L0 came from a peek / sweep /
    /// foreign cache. Mixed tiles (a real visit next to a peek) are not peek-only:
    /// Distant Vistas still owns the visited half.
    /// </summary>
    public bool IsPeekOnly()
    {
        if (ProvisionalQuadrants == 0) return false;
        for (int q = 0; q < QuadrantCount; q++)
        {
            if (IsProvisionalQuadrant(q)) continue;
            if (QuadrantCapturedCount(q) > 0) return false;
        }
        return true;
    }

    /// <summary>
    /// Flag every quadrant that holds a captured column, for a section that arrived
    /// from somewhere other than local capture. Empty quadrants stay clear: there is
    /// nothing in them for a real capture to correct, and QueueColumn already treats
    /// an uncaptured quadrant as work.
    /// </summary>
    public void MarkCapturedQuadrantsProvisional()
    {
        byte mask = 0;
        for (int q = 0; q < QuadrantCount; q++)
        {
            int x0 = (q & 1) * QuadrantColumns;
            int z0 = (q >> 1) * QuadrantColumns;
            for (int z = z0; z < z0 + QuadrantColumns && (mask & (1 << q)) == 0; z++)
            {
                int row = z * GridSize + x0;
                for (int x = 0; x < QuadrantColumns; x++)
                {
                    if (Captured[row + x]) { mask |= (byte)(1 << q); break; }
                }
            }
        }
        ProvisionalQuadrants = mask;
    }

    /// <summary>Min/max top-surface Y across captured columns. Used to coarsen flats.</summary>
    public int SurfaceYMin = int.MaxValue;
    public int SurfaceYMax = int.MinValue;
    public bool HasSurfaceBounds => SurfaceYMax >= SurfaceYMin && SurfaceYMax != int.MinValue;
    public int SurfaceRelief => HasSurfaceBounds ? SurfaceYMax - SurfaceYMin : 0;


    /// <summary>
    /// Set when a section was deserialized off the main thread: palette BlockIds are
    /// not resolved yet, because the game's block registry may only be touched from
    /// the main thread. Resolved and cleared on install, before anything reads ids.
    /// </summary>
    public string[]? PendingPaletteCodes;

    public bool IsEmpty => Runs.Length == 0;

    /// <summary>
    /// Scan column tops (first run is topmost). Flat plains = low relief; peaks = high.
    /// </summary>
    public void RefreshSurfaceBounds()
    {
        SurfaceYMin = int.MaxValue;
        SurfaceYMax = int.MinValue;
        int cols = GridSize * GridSize;
        for (int c = 0; c < cols; c++)
        {
            if (!Captured[c]) continue;
            int n = RunCount(c);
            if (n <= 0) continue;
            int yTop = RunYTop(Runs[ColumnStart[c]]);
            if (yTop < SurfaceYMin) SurfaceYMin = yTop;
            if (yTop > SurfaceYMax) SurfaceYMax = yTop;
        }
        if (SurfaceYMax < SurfaceYMin)
        {
            SurfaceYMin = 0;
            SurfaceYMax = 0;
        }
    }


    public int RunCount(int col) => ColumnStart[col + 1] - ColumnStart[col];

    /// <summary>Enumerate a column's runs: callback(paletteId, yTop, yBottom).</summary>
    public Span<ulong> ColumnRuns(int col) =>
        Runs.AsSpan(ColumnStart[col], ColumnStart[col + 1] - ColumnStart[col]);

    public int FindOrAddPaletteEntry(int blockId, int color, byte flags, byte tintSlot = 0)
    {
        for (int i = 0; i < Palette.Count; i++)
        {
            if (Palette[i].BlockId == blockId) return i;
        }
        Palette.Add(new LodPaletteEntry
        {
            BlockId = blockId,
            Color = color,
            Flags = flags,
            TintSlot = tintSlot,
        });
        snapPaletteCount = -1;
        return Palette.Count - 1;
    }

    /// <summary>
    /// Palette arrays for a mesh snapshot. Rebuilt when the palette grows; shared
    /// across snapshots until then. The mesher never writes these.
    /// </summary>
    public void FillPaletteSnapshot(out int[] colors, out byte[] flags, out byte[] slots)
    {
        if (snapPaletteCount == Palette.Count && snapPaletteColors != null)
        {
            colors = snapPaletteColors;
            flags = snapPaletteFlags!;
            slots = snapPaletteTintSlots!;
            return;
        }

        int n = Palette.Count;
        colors = new int[n];
        flags = new byte[n];
        slots = new byte[n];
        for (int i = 0; i < n; i++)
        {
            colors[i] = Palette[i].Color;
            flags[i] = Palette[i].Flags;
            slots[i] = Palette[i].TintSlot;
        }
        snapPaletteColors = colors;
        snapPaletteFlags = flags;
        snapPaletteTintSlots = slots;
        snapPaletteCount = n;
    }

    /// <summary>
    /// Drop every run whose palette entry carries <paramref name="flag"/>, rebuilding the
    /// run storage. Applied after a section is loaded so terrain already in the cache is
    /// corrected in place - no re-exploration, no cache wipe.
    /// </summary>
    public void RemoveRunsWithFlag(byte flag)
    {
        bool anyFlagged = false;
        for (int i = 0; i < Palette.Count; i++)
        {
            if ((Palette[i].Flags & flag) != 0) { anyFlagged = true; break; }
        }
        if (!anyFlagged) return;

        int total = GridSize * GridSize;
        var nextRuns = new ulong[Runs.Length];
        var nextStart = new int[total + 1];
        int offset = 0;

        for (int col = 0; col < total; col++)
        {
            nextStart[col] = offset;
            int from = ColumnStart[col], to = ColumnStart[col + 1];
            for (int r = from; r < to; r++)
            {
                if ((Palette[RunPaletteId(Runs[r])].Flags & flag) != 0) continue;
                nextRuns[offset++] = Runs[r];
            }
        }
        nextStart[total] = offset;

        Array.Resize(ref nextRuns, offset);
        Runs = nextRuns;
        ColumnStart = nextStart;
    }

    /// <summary>
    /// Replace one column's runs. Run values must already reference this section's
    /// palette. Returns true if the column content actually changed.
    /// </summary>
    public bool SetColumn(int col, ReadOnlySpan<ulong> newRuns)
    {
        Span<ulong> oldRuns = ColumnRuns(col);
        bool same = Captured[col] && oldRuns.Length == newRuns.Length;
        if (same)
        {
            for (int i = 0; i < newRuns.Length; i++)
            {
                if (oldRuns[i] != newRuns[i]) { same = false; break; }
            }
        }
        if (same) return false;

        if (!Captured[col])
        {
            Captured[col] = true;
            CapturedColumns++;
        }

        int oldLen = oldRuns.Length;
        int delta = newRuns.Length - oldLen;

        if (delta == 0)
        {
            newRuns.CopyTo(Runs.AsSpan(ColumnStart[col]));
            return true;
        }

        var next = new ulong[Runs.Length + delta];
        int start = ColumnStart[col];
        Runs.AsSpan(0, start).CopyTo(next);
        newRuns.CopyTo(next.AsSpan(start));
        Runs.AsSpan(start + oldLen).CopyTo(next.AsSpan(start + newRuns.Length));
        Runs = next;

        for (int c = col + 1; c < ColumnStart.Length; c++) ColumnStart[c] += delta;
        return true;
    }

    /// <summary>
    /// Replace many columns in one pass (one array rebuild total, not one per column) -
    /// the capture path applies a whole chunk column's worth of LOD columns at once.
    /// Entries in newRunsByCol may be null to leave that column untouched.
    /// Returns true if any column content changed.
    /// </summary>
    public bool ReplaceColumns(ulong[]?[] newRunsByCol)
    {
        int total = GridSize * GridSize;
        bool changed = false;
        int newLength = 0;

        for (int col = 0; col < total; col++)
        {
            ulong[]? repl = newRunsByCol[col];
            if (repl == null)
            {
                newLength += RunCount(col);
                continue;
            }

            Span<ulong> oldRuns = ColumnRuns(col);
            bool same = Captured[col] && oldRuns.Length == repl.Length;
            if (same)
            {
                for (int i = 0; i < repl.Length; i++)
                {
                    if (oldRuns[i] != repl[i]) { same = false; break; }
                }
            }

            if (same)
            {
                newRunsByCol[col] = null; // no-op, keep existing storage
                newLength += oldRuns.Length;
            }
            else
            {
                changed = true;
                if (!Captured[col])
                {
                    Captured[col] = true;
                    CapturedColumns++;
                }
                newLength += repl.Length;
            }
        }

        if (!changed) return false;

        var nextRuns = new ulong[newLength];
        var nextStart = new int[total + 1];
        int offset = 0;

        for (int col = 0; col < total; col++)
        {
            nextStart[col] = offset;
            ulong[]? repl = newRunsByCol[col];
            if (repl != null)
            {
                repl.CopyTo(nextRuns, offset);
                offset += repl.Length;
            }
            else
            {
                Span<ulong> keep = ColumnRuns(col);
                keep.CopyTo(nextRuns.AsSpan(offset));
                offset += keep.Length;
            }
        }
        nextStart[total] = offset;

        Runs = nextRuns;
        ColumnStart = nextStart;
        return true;
    }

    public static int ColumnIndex(int cx, int cz) => cz * GridSize + cx;
}
