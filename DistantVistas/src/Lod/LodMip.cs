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
    /// Bright snow/ice <see cref="LodPaletteEntry.FlagSnow"/> on any child surface
    /// wins the parent cap (1-of-4). Painted frost / chalk that is merely bright RGB
    /// does not — zero real snow stays earth majority.
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

        // Any solid coverage stays. 2-of-4 was meant to kill lone dirt spikes; on a
        // cliff or ridge the 2x2 almost never shares the same Y, so the face (the
        // one tall column) was dropped and L1 drew a hole through to sky and caves.
        // Snow on any child surface (FlagSnow) wins the cap in PickSlicePalette.
        // Painted frost without FlagSnow follows ordinary majority. Plant scraps
        // still fall out in DropUnsupportedFloaters. A 1-of-4 rock slice is a cliff.
        int solidMajority = 1;
        int canopyMajority = 1;

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
            bool anySnow = false;
            for (int c = 0; c < count; c++)
            {
                foreach (ulong run in runs.AsSpan(colStart[c], colEnd[c] - colStart[c]))
                {
                    if (LodSection.RunYBottom(run) <= mid && mid < LodSection.RunYTop(run))
                    {
                        covering++;
                        int pid = LodSection.RunPaletteId(run);
                        if (pid >= 0 && pid < child.Palette.Count
                            && (child.Palette[pid].Flags & LodPaletteEntry.FlagSnow) != 0)
                            anySnow = true;
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

            // Thin surface slices: FlagSnow may sit 1-of-4. Bright paint / lone dirt
            // spikes need 2-of-4 so they do not raise a cream cap over earth majority.
            int need = sliceH <= 4
                ? (anySnow ? 1 : Math.Max(2, canopyMajority))
                : solidMajority;
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

        // Anti-floater: drop plant scraps that sit on air with a gap below. Terrain
        // over a cave is rock on air too and must stay - see DropUnsupportedFloaters.
        return DropUnsupportedFloaters(result, child);
    }

    /// <summary>
    /// Most common covering block — except a real snow/ice layer
    /// (<see cref="LodPaletteEntry.FlagSnow"/>): any such child (1-of-4) wins so a
    /// visible snow cap from the sky stays white. Bright painted frost alone does not.
    /// </summary>
    static int PickSlicePalette(LodSection child, ReadOnlySpan<int> pidList, ReadOnlySpan<int> pidN, int nPids)
    {
        int bestPid = -1, bestN = -1;
        int bestSnowPid = -1, bestSnowN = -1;
        for (int k = 0; k < nPids; k++)
        {
            int pid = pidList[k];
            int n = pidN[k];
            if (n > bestN)
            {
                bestN = n;
                bestPid = pid;
            }
            bool snowLayer = pid >= 0 && pid < child.Palette.Count
                && (child.Palette[pid].Flags & LodPaletteEntry.FlagSnow) != 0;
            if (snowLayer && n > bestSnowN)
            {
                bestSnowN = n;
                bestSnowPid = pid;
            }
        }

        if (bestPid < 0) return -1;
        if (bestSnowPid >= 0) return bestSnowPid;
        return bestPid;
    }

    /// <summary>
    /// Remove mid-air plant scraps left after sparse canopy mip, and nothing else.
    ///
    /// Runs are walked bottom-up in stacks: runs resting on each other (a 1-block crack
    /// is allowed for tree trunk quirks) form one stack, and a stack whose bottom hangs
    /// more than a block above everything kept so far is floating. A floating stack is
    /// dropped only when it is plant matter through and through - a leaf crown whose
    /// trunk lost the majority vote, snow cap included. Any other floating stack is
    /// terrain and stays, and becomes the support for whatever sits above it.
    ///
    /// That last rule is the whole point. A mountain over a cave room is rock on air,
    /// and 0.7.10 through 0.7.40 dropped everything above the first air gap: the merged
    /// column sank to the cave floor whenever two of the four children shared a cave at
    /// the same height. Measured on a real cache, 16% of L1 columns sat 12+ blocks
    /// below the L0 surface under them and 8% sat 40+ below - every one of them over a
    /// cave gap. On screen that was the ridge line chopped down to the cave floor from
    /// the L0/L1 ring outward, and the ring moves with the camera.
    /// </summary>
    static ulong[] DropUnsupportedFloaters(List<ulong> topDown, LodSection child)
    {
        if (topDown.Count == 0) return Array.Empty<ulong>();
        // topDown is top->bottom. Walk bottom-up building support.
        var kept = new List<ulong>(topDown.Count);
        int supportTop = 1; // ground support starts at bedrock band
        int bottom = topDown.Count - 1;
        while (bottom >= 0)
        {
            // The stack resting on run `bottom`: index `top` is its highest run.
            int top = bottom;
            int stackTop = LodSection.RunYTop(topDown[bottom]);
            while (top > 0 && LodSection.RunYBottom(topDown[top - 1]) <= stackTop + 1)
            {
                top--;
                int y = LodSection.RunYTop(topDown[top]);
                if (y > stackTop) stackTop = y;
            }

            bool floating = LodSection.RunYBottom(topDown[bottom]) > supportTop + 1;
            if (!floating || !IsPlantScrap(child, topDown, top, bottom))
            {
                for (int k = bottom; k >= top; k--) kept.Add(topDown[k]);
                if (stackTop > supportTop) supportTop = stackTop;
            }
            bottom = top - 1;
        }
        if (kept.Count == 0) return Array.Empty<ulong>();
        // Restore top-down order for callers.
        kept.Reverse();
        return kept.ToArray();
    }

    /// <summary>
    /// A floating stack is a scrap when every run in it is plant matter or the snow on
    /// top of plant matter, and at least one run is plant. Plant is anything the tint
    /// registry gave a climate/season slot (leaves, grass tops, ferns) or thin cover.
    /// Bright-only stacks are not plant: a chalk cliff over a cave keeps its cap.
    /// </summary>
    static bool IsPlantScrap(LodSection child, List<ulong> runs, int top, int bottom)
    {
        bool anyPlant = false;
        for (int k = top; k <= bottom; k++)
        {
            int pid = LodSection.RunPaletteId(runs[k]);
            if (pid < 0 || pid >= child.Palette.Count) return false;
            LodPaletteEntry e = child.Palette[pid];
            if ((e.Flags & LodPaletteEntry.FlagWater) != 0) return false;
            bool plant = (e.Flags & LodPaletteEntry.FlagThin) != 0
                || e.TintSlot != LodTintRegistry.SlotNone;
            if (plant) { anyPlant = true; continue; }
            if (!LodPaletteRepair.IsSnowOrIceAlbedo(e.Color)) return false;
        }
        return anyPlant;
    }
}
