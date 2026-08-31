namespace DistantVistas;

/// <summary>
/// Child→parent section downsampling: every 2×2 child columns merge into one parent
/// column via a y-boundary slice sweep (DH's approach). A slice is solid when at
/// least two of the four child columns cover it; the slice takes the most common
/// covering block. Adjacent same-block slices re-merge into runs.
/// </summary>
public static class LodMip
{
    [ThreadStatic] static List<int>? boundaries;
    [ThreadStatic] static List<ulong>? outRuns;

    /// <summary>Merge the whole child section into one parent quadrant. Returns true if the parent changed.</summary>
    public static bool DownsampleIntoParent(LodSection child, LodSection parent, int qx, int qz)
    {
        const int gs = LodSection.GridSize;
        const int half = gs / 2;

        // Child palette id → parent palette id, registered lazily.
        var paletteMap = new int[child.Palette.Count];
        for (int i = 0; i < paletteMap.Length; i++) paletteMap[i] = -1;

        var batch = new ulong[]?[gs * gs];

        // The four child columns are described by where they sit in the child's own run
        // array, rather than copied out of it. Copying cost four allocations per parent
        // column and there are 1024 of those per call, three calls per tick, on the game
        // thread. Hoisted out of the loops: a stackalloc inside one grows the frame every
        // iteration.
        Span<int> colStart = stackalloc int[4];
        Span<int> colEnd = stackalloc int[4];

        for (int pz = 0; pz < half; pz++)
        {
            for (int px = 0; px < half; px++)
            {
                int captured = 0;
                for (int dz = 0; dz < 2; dz++)
                {
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int ci = LodSection.ColumnIndex(px * 2 + dx, pz * 2 + dz);
                        if (!child.Captured[ci]) continue;
                        colStart[captured] = child.ColumnStart[ci];
                        colEnd[captured] = child.ColumnStart[ci + 1];
                        captured++;
                    }
                }
                if (captured == 0) continue;

                ulong[] merged = MergeColumns(child, colStart, colEnd, captured);

                // Remap child palette ids to the parent palette.
                for (int i = 0; i < merged.Length; i++)
                {
                    int cpid = LodSection.RunPaletteId(merged[i]);
                    int ppid = paletteMap[cpid];
                    if (ppid < 0)
                    {
                        LodPaletteEntry e = child.Palette[cpid];
                        // Parent rebuild used to copy the child colour as-is. Fill()
                        // neighbour-steal only runs on disk load, so a missing-tex L0
                        // captured on MP before the atlas was ready promoted white
                        // onto the parent that the walk-away handoff actually draws.
                        int color = e.Color;
                        if (LodPaletteRepair.NeedsColor(color))
                            color = LodPaletteRepair.Sanitize(color, LodPaletteRepair.NeighborTerrainColor(child, cpid));
                        ppid = parent.FindOrAddPaletteEntry(e.BlockId, color, e.Flags, e.TintSlot);
                        paletteMap[cpid] = ppid;
                    }
                    merged[i] = LodSection.PackRun(ppid, LodSection.RunYTop(merged[i]), LodSection.RunYBottom(merged[i]));
                }

                batch[LodSection.ColumnIndex(qx * half + px, qz * half + pz)] = merged;
            }
        }

        return parent.ReplaceColumns(batch);
    }

    /// <summary>
    /// Merge up to four columns' runs into one, majority-occupancy per slice.
    ///
    /// The columns are given as ranges into the child section's own run array. That array
    /// is immutable once created (writes swap the whole array), which is the same property
    /// the worker snapshots rely on, so reading through it here needs no copy.
    ///
    /// Bright (missing-tex / snow-white) slices only become the parent surface when they
    /// cover at least 3 of the 2x2. A lone snow or chalk column used to win Boyer-Moore
    /// and paint a whole 64x64 mountain cap white; closer in, those blocks were rock.
    /// Real snow fields still win: they cover 3 or 4 children.
    /// </summary>
    static ulong[] MergeColumns(LodSection child, ReadOnlySpan<int> colStart, ReadOnlySpan<int> colEnd, int count)
    {
        ulong[] runs = child.Runs;
        var bounds = boundaries ??= new List<int>(64);
        bounds.Clear();

        // Sliced once per column and walked with foreach, not indexed with a running
        // offset: the JIT drops the bounds check on a foreach over a span, and cannot on
        // an index it is unable to prove in range.
        for (int c = 0; c < count; c++)
        {
            foreach (ulong run in runs.AsSpan(colStart[c], colEnd[c] - colStart[c]))
            {
                bounds.Add(LodSection.RunYTop(run));
                bounds.Add(LodSection.RunYBottom(run));
            }
        }
        if (bounds.Count == 0) return Array.Empty<ulong>();

        bounds.Sort();
        // Deduplicate in place, then walk slices top-down.
        int uniqueCount = 0;
        for (int i = 0; i < bounds.Count; i++)
        {
            if (uniqueCount == 0 || bounds[uniqueCount - 1] != bounds[i]) bounds[uniqueCount++] = bounds[i];
        }

        var result = outRuns ??= new List<ulong>(16);
        result.Clear();

        // FidelityStep 1: keep sparse foliage with 1-of-4 coverage when the slice is
        // short (canopy), but still require majority for thick solid rock/soil.
        int solidMajority = count >= 2 ? 2 : 1;
        int canopyMajority = LodWorld.FidelityStep >= 0.5 ? 1 : solidMajority;

        Span<int> pidList = stackalloc int[4];
        Span<int> pidN = stackalloc int[4];

        for (int i = uniqueCount - 1; i > 0; i--)
        {
            int sliceTop = bounds[i];
            int sliceBottom = bounds[i - 1];
            int mid = (sliceTop + sliceBottom) / 2;
            int sliceH = sliceTop - sliceBottom;

            int covering = 0;
            int nPids = 0;
            pidList.Clear();
            pidN.Clear();
            for (int c = 0; c < count; c++)
            {
                foreach (ulong run in runs.AsSpan(colStart[c], colEnd[c] - colStart[c]))
                {
                    if (LodSection.RunYBottom(run) <= mid && mid < LodSection.RunYTop(run))
                    {
                        covering++;
                        int pid = LodSection.RunPaletteId(run);
                        int found = -1;
                        for (int k = 0; k < nPids; k++)
                        {
                            if (pidList[k] == pid) { found = k; break; }
                        }
                        if (found < 0)
                        {
                            pidList[nPids] = pid;
                            pidN[nPids] = 1;
                            nPids++;
                        }
                        else pidN[found]++;
                        break;
                    }
                }
            }

            int need = sliceH <= 4 ? canopyMajority : solidMajority;
            if (covering < need) continue;

            int bestPid = PickSlicePalette(child, pidList, pidN, nPids);
            if (bestPid < 0) continue;

            // Merge with the previous run when contiguous and same block.
            if (result.Count > 0)
            {
                ulong prev = result[^1];
                if (LodSection.RunYBottom(prev) == sliceTop && LodSection.RunPaletteId(prev) == bestPid)
                {
                    result[^1] = LodSection.PackRun(bestPid, LodSection.RunYTop(prev), sliceBottom);
                    continue;
                }
            }
            result.Add(LodSection.PackRun(bestPid, sliceTop, sliceBottom));
        }

        // Anti-floater: drop runs that sit on air with a gap below (unsupported scraps).
        // Keep continuous stacks from the lowest solid down; orphan mid-air slices go.
        return DropUnsupportedFloaters(result);
    }

    /// <summary>
    /// Most common covering block. Bright-white (snow / missing tex) may win only with
    /// a true 3-of-4 majority; otherwise the rock/dirt neighbour in the same slice wins.
    /// </summary>
    static int PickSlicePalette(LodSection child, ReadOnlySpan<int> pidList, ReadOnlySpan<int> pidN, int nPids)
    {
        int bestPid = -1, bestN = -1;
        int bestEarthPid = -1, bestEarthN = -1;
        for (int k = 0; k < nPids; k++)
        {
            int pid = pidList[k];
            int n = pidN[k];
            if (n > bestN)
            {
                bestN = n;
                bestPid = pid;
            }
            bool bright = pid >= 0 && pid < child.Palette.Count
                && LodPaletteRepair.IsBrightCap(child.Palette[pid].Color);
            if (!bright && n > bestEarthN)
            {
                bestEarthN = n;
                bestEarthPid = pid;
            }
        }

        if (bestPid < 0) return -1;
        bool winnerBright = bestPid < child.Palette.Count
            && LodPaletteRepair.IsBrightCap(child.Palette[bestPid].Color);
        // 3-of-4 or unanimous bright is real snow (or a whole missing-tex plateau).
        // A 1-of-4 or 2-of-4 bright cap is patchy snow or a missing tex; closer in those blocks are rock.
        // Skip a bright slice that is not a 3-of-4 majority. Returning -1 drops a
        // lone snow/missing-tex cap so the rock below becomes the parent surface.
        if (winnerBright && bestN < 3)
            return bestEarthPid;
        return bestPid;
    }

    /// <summary>
    /// Remove mid-air scraps left after sparse canopy mip. A run may stay only when it
    /// rests on another kept run or on y&lt;=1 (bedrock/terrain base). Strengthens the
    /// continuous-range / no-floater rule for mid-far leaves on ridges.
    /// </summary>
    static ulong[] DropUnsupportedFloaters(List<ulong> topDown)
    {
        if (topDown.Count == 0) return Array.Empty<ulong>();
        // topDown is top→bottom. Walk bottom-up building support.
        var kept = new List<ulong>(topDown.Count);
        int supportTop = 1; // ground support starts at bedrock band
        for (int i = topDown.Count - 1; i >= 0; i--)
        {
            ulong run = topDown[i];
            int yBottom = LodSection.RunYBottom(run);
            int yTop = LodSection.RunYTop(run);
            // Allow a 1-block air crack (tree trunk quirks); bigger gaps = floater.
            if (yBottom > supportTop + 1) continue;
            kept.Add(run);
            if (yTop > supportTop) supportTop = yTop;
        }
        if (kept.Count == 0) return Array.Empty<ulong>();
        // Restore top-down order for callers.
        kept.Reverse();
        return kept.ToArray();
    }
}
