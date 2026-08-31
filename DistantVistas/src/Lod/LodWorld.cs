using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// The in-memory section pyramid: all detail levels of LodSections, dirty tracking,
/// and childâ†’parent mip propagation. All mutation happens on the main thread; the
/// worker thread only ever reads immutable snapshots (Runs/ColumnStart arrays are
/// replaced wholesale, never edited in place).
/// </summary>
public class LodWorld
{
    public const int MaxLevel = 6; // L6 sections span 4096 blocks (64-block columns at the horizon)

    /// <summary>Coarsest level the renderer may select. L2 means four blocks per column.</summary>
    public static int MaxVisualLevel = MaxLevel;

    /// <summary>
    /// Width of each LOD band past the vanilla view-distance cut (DH-style).
    /// L0 covers roughly [VD .. VD+DetailDistance], then each level doubles and
    /// halves resolution. L2+ thresholds pull in slightly for a cheaper skyline.
    /// Tunable live via .dvdetail; ViewDistanceAnchor is updated every frame.
    /// </summary>
    /// <summary>0.7.10: one step up from 256 so mid-far keeps detail a rung longer.</summary>
    public static double DetailDistance = 320;

    /// <summary>
    /// Incremental fidelity bump (0 = 0.7.9 aggressiveness, 1 = one step up).
    /// Bump again later without rewriting ladder math. Kept modest for FPS.
    /// </summary>
    public static double FidelityStep = 1.0;

    public const double MinDetailDistance = 128;
    public const double MaxDetailDistance = 4096;

    /// <summary>
    /// Live vanilla view distance (blocks). The LOD ladder is anchored just
    /// inside this cut so changing graphics view distance retargets LODs
    /// immediately. Updated every frame from the client renderer.
    /// </summary>
    public static double ViewDistanceAnchor = 512;

    /// <summary>
    /// DH-style overdraw fraction (live from renderer). Ladder origin = ViewDistanceAnchor * OverdrawStart
    /// so L0 detail engages in the overlap band under vanilla fog, not only past the cut.
    /// </summary>
    public static double OverdrawStart = 0.55;

    public readonly Dictionary<long, LodSection> Sections = new();

    /// <summary>Set by the coordinator when persistence is available: reload an evicted section from disk.</summary>
    public Func<long, LodSection?>? LoadFromStore;

    public int EvictedSectionsTotal { get; private set; }

    /// <summary>Sections whose mesh is stale.</summary>
    public readonly HashSet<long> RenderDirty = new();

    /// <summary>Sections whose DB row is stale.</summary>
    public readonly HashSet<long> SaveDirty = new();

    /// <summary>Sections whose parent still needs to absorb their content (persisted as ApplyToParent).</summary>
    public readonly HashSet<long> MipDirty = new();

    /// <summary>Every key (all levels) that holds data or has any descendant with data. Drives quadtree descent.</summary>
    public readonly HashSet<long> HasDataSet = new();

    /// <summary>L0 keys known to hold only a thin/partial capture (pre-0.7.7 one-quadrant).</summary>
    public readonly HashSet<long> SparseL0Keys = new();

    /// <summary>L0 keys that do not yet contain every terrain column and must not be drawn alone.</summary>
    public readonly HashSet<long> IncompleteL0Keys = new();

    /// <summary>L0 keys whose CapturedColumns were inspected (complete or sparse).</summary>
    public readonly HashSet<long> SparseL0Classified = new();

    /// <summary>Top-level (MaxLevel) ancestor keys - the quadtree roots.</summary>
    public readonly HashSet<long> TopLevelKeys = new();

    // ---- Key packing: level(4) | sz(30) | sx(30). VS world coords are non-negative. ----

    public static long SectionKey(int level, int sx, int sz) =>
        ((long)level << 60) | ((long)(sz & 0x3FFFFFFF) << 30) | (uint)(sx & 0x3FFFFFFF);

    public static int KeyLevel(long key) => (int)(key >>> 60);
    public static int KeySx(long key) => (int)(key & 0x3FFFFFFF);
    public static int KeySz(long key) => (int)((key >> 30) & 0x3FFFFFFF);

    public static long ParentKey(long key) =>
        SectionKey(KeyLevel(key) + 1, KeySx(key) >> 1, KeySz(key) >> 1);

    public static long ChildKey(long key, int qx, int qz) =>
        SectionKey(KeyLevel(key) - 1, (KeySx(key) << 1) + qx, (KeySz(key) << 1) + qz);

    public static long NeighborKey(long key, int dx, int dz) =>
        SectionKey(KeyLevel(key), KeySx(key) + dx, KeySz(key) + dz);

    /// <summary>Section footprint in blocks at this key's level.</summary>
    public static int KeyFootprintBlocks(long key) => LodSection.SectionBlocks << KeyLevel(key);

    /// <summary>
    /// Distance from a point to the nearest edge of a section's footprint, squared.
    /// Nearest-edge rather than centre: an L6 section spans 4096 blocks, so centre distance
    /// would rank a section the viewer is standing inside as far away.
    /// </summary>
    public static double NearestDistanceSqTo(long key, double x, double z)
    {
        int footprint = KeyFootprintBlocks(key);
        double minX = KeySx(key) * (double)footprint;
        double minZ = KeySz(key) * (double)footprint;
        double dx = Math.Max(0, Math.Max(minX - x, x - (minX + footprint)));
        double dz = Math.Max(0, Math.Max(minZ - z, z - (minZ + footprint)));
        return dx * dx + dz * dz;
    }

    public static int ColumnStepBlocks(int level) => LodSection.ColumnStepBlocks << level;

    public LodSection GetOrCreateSection(long key)
    {
        if (Sections.TryGetValue(key, out LodSection? section)) return section;

        // A previously-evicted section must come back from disk, not start empty -
        // capture merges and mip propagation would otherwise clobber stored data.
        if (HasDataSet.Contains(key))
        {
            section = LoadFromStore?.Invoke(key);
            if (section != null)
            {
                Sections[key] = section;
                ClassifySparseL0(key, section);
                return section;
            }
        }

        Sections[key] = section = new LodSection();
        LoadFailed.Remove(key); // it has data again; a past miss must not block reloads
        RegisterInTree(key);
        return section;
    }

    /// <summary>Get a section from RAM or disk without creating an empty one. For mesh scheduling.</summary>
    public bool TryGetOrLoad(long key, out LodSection section)
    {
        if (Sections.TryGetValue(key, out section!)) return true;
        if (!HasDataSet.Contains(key)) return false;

        LodSection? loaded = LoadFromStore?.Invoke(key);
        if (loaded == null) return false;

        Sections[key] = section = loaded;
        return true;
    }

    /// <summary>Ask the storage thread to reload an evicted section; null when unavailable.</summary>
    public Action<long>? RequestAsyncLoad;

    /// <summary>Keys with a reload in flight, so the render path stops re-requesting them.</summary>
    public readonly HashSet<long> LoadsInFlight = new();

    /// <summary>
    /// Keys whose reload came back empty (row missing, or deleted as unreadable).
    /// Without this the selection walk would re-request them every single frame
    /// forever, since the section never becomes resident.
    /// </summary>
    public readonly HashSet<long> LoadFailed = new();

    /// <summary>
    /// Non-blocking variant for the render path: returns false and starts a background
    /// reload rather than stalling the frame on a decompress. The selection walk
    /// re-requests the mesh on later frames, so the section is picked up once it lands.
    /// </summary>
    public bool TryGetForRender(long key, out LodSection section)
    {
        if (Sections.TryGetValue(key, out section!)) return true;
        if (!HasDataSet.Contains(key) || LoadFailed.Contains(key)) return false;

        if (RequestAsyncLoad == null)
        {
            // No storage thread (no persistence this session): fall back to inline.
            return TryGetOrLoad(key, out section);
        }

        if (LoadsInFlight.Add(key)) RequestAsyncLoad(key);
        return false;
    }

    /// <summary>
    /// Install a section that finished loading in the background. A section that
    /// became resident while the read was in flight (a capture created or reloaded it
    /// inline) is strictly newer, so the arriving copy is discarded.
    /// </summary>
    public void InstallLoaded(long key, LodSection? section)
    {
        LoadsInFlight.Remove(key);
        if (section == null)
        {
            LoadFailed.Add(key);
            return;
        }
        if (Sections.ContainsKey(key)) return;

        section.RefreshSurfaceBounds();
        Sections[key] = section;
        ClassifySparseL0(key, section);

        // Deliberately not marked render-dirty: reloads are requested by the render
        // path AND by mip propagation, and the selection walk re-requests a mesh by
        // itself on the next frame if it still wants one here. Marking every arrival
        // would mesh sections that only propagation asked for.
    }

    public void ClassifySparseL0(long key, LodSection section)
    {
        if (KeyLevel(key) != 0) return;
        SparseL0Classified.Add(key);
        int captured = section.CapturedColumns;
        int full = LodSection.GridSize * LodSection.GridSize;
        bool sparse = captured > 0 && captured <= full / 4;
        if (sparse) SparseL0Keys.Add(key);
        else SparseL0Keys.Remove(key);
        if (captured > 0 && captured < full) IncompleteL0Keys.Add(key);
        else IncompleteL0Keys.Remove(key);
    }

    /// <summary>
    /// Drop cold sections from RAM (their rows stay on disk; HasDataSet keeps the
    /// quadtree semantics intact). Cold = the walk wants this area at least two
    /// levels coarser than this section, and nothing dirty references it.
    /// </summary>
    public int LastSweepChecked { get; private set; }
    public int LastSweepPinned { get; private set; }
    public int LastSweepCold { get; private set; }

    public void EvictColdSections(double camX, double camZ, int budget)
    {
        List<long>? evict = null;
        LastSweepChecked = 0;
        LastSweepPinned = 0;
        LastSweepCold = 0;

        foreach ((long key, LodSection section) in Sections)
        {
            LastSweepChecked++;
            int level = KeyLevel(key);
            if (level >= MaxLevel) continue;
            // Captured near tiles stay resident so a fast turn does not wait on reload.
            if (LodCoveragePolicy.IsVisitedKeepLevel(level) && section.CapturedColumns > 0)
            {
                LastSweepPinned++;
                continue;
            }
            // Unsaved or unpropagated data pins a section; a pending mesh rebuild does
            // NOT - the scheduler demand-reloads from disk when its turn comes.
            if (SaveDirty.Contains(key) || MipDirty.Contains(key)) { LastSweepPinned++; continue; }

            int footprint = KeyFootprintBlocks(key);
            double minX = KeySx(key) * (double)footprint;
            double minZ = KeySz(key) * (double)footprint;
            double dx = Math.Max(0, Math.Max(minX - camX, camX - (minX + footprint)));
            double dz = Math.Max(0, Math.Max(minZ - camZ, camZ - (minZ + footprint)));
            double dist = Math.Sqrt(dx * dx + dz * dz);

            // Visible / just-left stay resident. Spill farther tiles to disk; GPU
            // meshes are not disposed just because RAM dropped.
            // 0.7.22 pinned L0/L1 inside view distance plus 16 tiles (~1k).
            // That was the moving window. Tenfold so a long walk does not dump
            // the trail from RAM before we can mesh it. Beyond that we still
            // spill; the renderer pages the same-quality mesh back from disk.
            if (level <= 1 && dist < ViewDistanceAnchor + LodSection.SectionBlocks * 160)
            {
                LastSweepPinned++;
                continue;
            }
            if (WantedLevelFor(dist) < level + 2) continue;

            LastSweepCold++;
            (evict ??= new List<long>()).Add(key);
            if (evict.Count >= budget) break;
        }

        if (evict == null) return;
        foreach (long key in evict)
        {
            Sections.Remove(key);
            EvictedSectionsTotal++;
        }
    }

    public static int WantedLevelFor(double distance) =>
        WantedLevelForSq(distance * distance);

    /// <summary>
    /// The same answer as <see cref="WantedLevelFor"/>, from the SQUARED distance.
    ///
    /// The quadtree walk asks this once per visited node, and every caller had to take a
    /// square root to ask, after which this took a logarithm to answer. Both are
    /// avoidable: level L is wanted from DetailDistance * 2^L outward, so the question is
    /// a comparison against a fixed radius per level, and comparisons survive squaring.
    ///
    /// Measured at 951 resident sections, the walk cost 387us a frame and the prune pass
    /// runs the same test over the whole dirty set on top of that.
    ///
    /// The table is rebuilt when DetailDistance changes, which .vhdetail can do live.
    /// Callers are the render frame and the eviction sweep, both on the main thread, so
    /// no lock is needed; a worker must not call this.
    /// </summary>
    public static int WantedLevelForSq(double distanceSq)
    {
        if (wantedTableForDetail != DetailDistance || wantedTableForVd != ViewDistanceAnchor
            || wantedTableForOverdraw != OverdrawStart || wantedTableForFidelity != FidelityStep)
            RebuildWantedTable();

        int maxVisual = Math.Clamp(MaxVisualLevel, 0, MaxLevel);
        for (int level = maxVisual; level > 0; level--)
        {
            if (distanceSq >= wantedThresholdSq[level]) return level;
        }
        return 0;
    }

    static double wantedTableForDetail = double.NaN;
    static double wantedTableForVd = double.NaN;
    static double wantedTableForOverdraw = double.NaN;
    static double wantedTableForFidelity = double.NaN;
    static readonly double[] wantedThresholdSq = new double[MaxLevel + 1];

    static void RebuildWantedTable()
    {
        // Ladder anchored at the live vanilla view-distance cut:
        //   L0 starts near ViewDistanceAnchor, then each level doubles the band
        //   (DetailDistance wide) and halves resolution â€” DH-style, retargeted
        //   whenever the player changes graphics view distance.
        double origin = ViewDistanceAnchor * OverdrawStart;
        for (int level = 0; level <= MaxLevel; level++)
        {
            // distance >= origin + DetailDistance * (2^level - 1)  ⇒  at least level
            // FidelityStep softens far coarsen by one notch vs 0.7.9 (was 0.85/0.65/0.42)
            // so mountains and mid-ring keep a sharper LOD without maxing poly count.
            double step = FidelityStep;
            double radius = origin + DetailDistance * ((1 << level) - 1);
            if (level >= 1) radius = origin + DetailDistance * ((1 << level) - 1) * (0.85 + 0.08 * step);
            if (level >= 2) radius = origin + DetailDistance * ((1 << level) - 1) * (0.65 + 0.12 * step);
            if (level >= 4) radius = origin + DetailDistance * ((1 << level) - 1) * (0.65 + 0.12 * step) * (0.65 + 0.10 * step);
            wantedThresholdSq[level] = radius * radius;
        }
        wantedTableForDetail = DetailDistance;
        wantedTableForVd = ViewDistanceAnchor;
        wantedTableForOverdraw = OverdrawStart;
        wantedTableForFidelity = FidelityStep;
    }

    void RegisterInTree(long key)
    {
        while (true)
        {
            HasDataSet.Add(key);
            if (KeyLevel(key) == MaxLevel)
            {
                TopLevelKeys.Add(key);
                return;
            }
            key = ParentKey(key);
        }
    }

    public void MarkChanged(long key)
    {
        if (Sections.TryGetValue(key, out LodSection? changed))
            changed.RefreshSurfaceBounds();
        RenderDirty.Add(key);
        SaveDirty.Add(key);
        if (KeyLevel(key) < MaxLevel) MipDirty.Add(key);

        // Neighbor meshes cull their faces against our edge columns; conservatively
        // refresh all four (change locality tracking can come later).
        for (int d = 0; d < 4; d++)
        {
            long nk = NeighborKey(key, d == 0 ? -1 : d == 1 ? 1 : 0, d == 2 ? -1 : d == 3 ? 1 : 0);
            if (Sections.ContainsKey(nk)) RenderDirty.Add(nk);
        }
    }

    /// <summary>
    /// Registers a stored section KEY from the persistent cache - no data attached.
    /// The quadtree skeleton (HasDataSet/TopLevelKeys) and pending-mip flags come
    /// from keys alone; section data demand-loads when first needed, so join time
    /// and RAM stay independent of how much was ever explored.
    /// </summary>
    public void InstallStoredKey(int level, int sx, int sz, bool applyToParent)
    {
        long key = SectionKey(level, sx, sz);
        RegisterInTree(key);
        if (applyToParent && level < MaxLevel) MipDirty.Add(key);
    }

    // ---- Mip propagation (child â†’ parent), main thread, budgeted ----

    public void ProcessPropagation(int maxSections)
    {
        if (MipDirty.Count == 0) return;

        List<long>? batch = null;
        foreach (long key in MipDirty)
        {
            (batch ??= new List<long>()).Add(key);
            if (batch.Count >= maxSections) break;
        }
        if (batch == null) return;

        foreach (long childKey in batch)
        {
            // Both sides must be in RAM before the flag may be cleared. Clearing it
            // while a section is still on disk would drop the propagation on the
            // floor, so a section awaiting a reload simply stays pending and is
            // retried on a later tick.
            long parentKey = ParentKey(childKey);
            if (!EnsureResident(childKey)) continue;
            if (!EnsureResident(parentKey)) continue;

            MipDirty.Remove(childKey);
            SaveDirty.Add(childKey); // persist the cleared ApplyToParent flag

            if (!Sections.TryGetValue(childKey, out LodSection? child) || child.CapturedColumns == 0) continue;

            LodSection parent = GetOrCreateSection(parentKey);

            if (LodMip.DownsampleIntoParent(child, parent, KeySx(childKey) & 1, KeySz(childKey) & 1))
            {
                MarkChanged(parentKey);
            }
        }
    }

    /// <summary>
    /// True when the section is in RAM, or when there is nothing to load for it so the
    /// caller may proceed. False means a background reload was started and the caller
    /// must leave its pending work alone and retry later.
    ///
    /// This is how mip propagation avoids blocking the frame on a decompress without
    /// ever creating an empty section that would shadow -- and then overwrite -- a
    /// stored row. It is TryGetForRender's policy with one difference: a key with
    /// nothing to load is "proceed" here, rather than "no mesh".
    /// </summary>
    public bool EnsureResident(long key) =>
        TryGetForRender(key, out _) || !LoadsInFlight.Contains(key);

    public string DescribeLevels()
    {
        var counts = new int[MaxLevel + 1];
        foreach (long key in Sections.Keys) counts[KeyLevel(key)]++;
        return string.Join(" ", counts.Select((c, i) => $"L{i}:{c}"));
    }

    public void Clear()
    {
        Sections.Clear();
        RenderDirty.Clear();
        SaveDirty.Clear();
        MipDirty.Clear();
        HasDataSet.Clear();
        TopLevelKeys.Clear();
        LoadsInFlight.Clear();
        LoadFailed.Clear();
    }
}
