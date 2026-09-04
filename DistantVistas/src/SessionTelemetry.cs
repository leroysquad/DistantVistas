using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Vintagestory.API.Client;

namespace DistantVistas;

/// <summary>
/// Live Logs/distantvistas.json for agents. Snapshot on the game tick, write on a
/// worker thread. Chat .dvistas and the 15s LogStats line stay as they are; this is
/// the always-on file so you do not have to scrape client-main.log.
/// </summary>
public sealed class SessionTelemetry
{
    readonly ICoreClientAPI capi;
    readonly string metricsPath;
    readonly object writeGate = new();
    readonly Stopwatch sinceWrite = Stopwatch.StartNew();
    int writeQueued;

    int lastGen0;
    int lastGen1;
    int lastGen2;
    bool gcBaselineSet;

    public SessionTelemetry(ICoreClientAPI capi)
    {
        this.capi = capi;
        metricsPath = Path.Combine(capi.GetOrCreateDataPath("Logs"), "distantvistas.json");
    }

    /// <summary>
    /// Call from the game tick while the renderer is active. Cadence is internal (~1s).
    /// </summary>
    public void Tick(
        LodPipeline pipeline,
        LodTerrainRenderer renderer,
        string? deferring,
        string version)
    {
        if (sinceWrite.ElapsedMilliseconds < 1000) return;
        sinceWrite.Restart();
        ScheduleWrite(pipeline, renderer, deferring, version);
    }

    void ScheduleWrite(
        LodPipeline pipeline,
        LodTerrainRenderer renderer,
        string? deferring,
        string version)
    {
        if (Interlocked.CompareExchange(ref writeQueued, 1, 0) != 0) return;

        LodWorld world = pipeline.World;
        LodWorker worker = pipeline.Worker;

        int gen0 = GC.CollectionCount(0);
        int gen1 = GC.CollectionCount(1);
        int gen2 = GC.CollectionCount(2);
        long managedMb = GC.GetTotalMemory(false) / (1024 * 1024);
        int d0 = 0, d1 = 0, d2 = 0;
        if (gcBaselineSet)
        {
            d0 = gen0 - lastGen0;
            d1 = gen1 - lastGen1;
            d2 = gen2 - lastGen2;
        }
        lastGen0 = gen0;
        lastGen1 = gen1;
        lastGen2 = gen2;
        gcBaselineSet = true;

        int px = 0, pz = 0;
        try
        {
            var pos = capi.World?.Player?.Entity?.Pos;
            if (pos != null)
            {
                px = (int)pos.X;
                pz = (int)pos.Z;
            }
        }
        catch { }

        string levels = world.DescribeLevels();
        string drawnLevels = renderer.DescribeDrawnLevels();
        bool farseer = renderer.DrawAfterCompanion;
        bool pressureYield = renderer.PressureYieldActive;
        bool meshPressure = renderer.MeshPressureActive;
        int evictOutside2x = renderer.EvictedOutside2xTotal;
        int evictBlocked2x = renderer.EvictBlockedInside2xTotal;
        int pressureEnter = renderer.PressureEnterCount;
        int pressureClear = renderer.PressureClearCount;
        double pressureActiveMs = renderer.PressureActiveMsTotal;
        double renderOrder = renderer.RenderOrder;
        float overdrawStart = renderer.OverdrawStart;

        string stage;
        if (!string.IsNullOrEmpty(deferring))
            stage = "defer:" + deferring;
        else if (!pipeline.Active)
            stage = "inactive";
        else
            stage = "ok";

        // snapshot numbers now; serialize off-thread
        var snap = new Snapshot
        {
            Ts = DateTime.Now.ToString("o"),
            Ver = version ?? "",
            Sections = world.Sections.Count,
            Levels = levels ?? "",
            Meshes = renderer.MeshCount,
            MeshesEvicted = renderer.EvictedTotal,
            Drawn = renderer.LastDrawCount,
            DrawnLevels = drawnLevels ?? "",
            Culled = renderer.LastCulledCount,
            Occluded = renderer.LastOccludedCount,
            GapFills = renderer.LastGapDrawCount,
            UnfilledGaps = renderer.LastUnfilledGaps,
            Captured = pipeline.ColumnsCaptured,
            Pending = pipeline.PendingColumns,
            Dropped = pipeline.ColumnsDropped,
            Swept = pipeline.ColumnsSwept,
            PeekConfirmed = pipeline.ProvisionalQuadrantsConfirmed,
            ProvisionalL0 = world.ProvisionalL0Keys.Count,
            PendingCaptures = worker.PendingCaptures,
            PendingMeshes = worker.PendingMeshes,
            CaptureErrors = worker.CaptureErrors,
            MeshErrors = worker.MeshErrors,
            MipDirty = world.MipDirty.Count,
            RenderDirty = world.RenderDirty.Count,
            Unsaved = world.SaveDirty.Count,
            FarCap = renderer.FarViewDistanceCap,
            FarEdge = (int)renderer.EffectiveFarDistance,
            DetailDist = (int)LodWorld.DetailDistance,
            Farseer = farseer,
            PressureYield = pressureYield,
            MeshPressure = meshPressure,
            EvictOutside2x = evictOutside2x,
            EvictBlockedInside2x = evictBlocked2x,
            PressureEnter = pressureEnter,
            PressureClear = pressureClear,
            PressureActiveMs = (int)pressureActiveMs,
            CompanionYield = renderer.LastCompanionYieldCount,
            PressureYieldCount = renderer.LastPressureYieldCount,
            Deferring = deferring ?? "",
            DGen0 = d0,
            DGen1 = d1,
            DGen2 = d2,
            ManagedMb = managedMb,
            HasPhaseCosts = renderer.WalkCost.Calls > 0,
            PruneUsAvg = renderer.PruneCost.AvgUs,
            PruneUsMax = renderer.PruneCost.MaxUs,
            ScheduleUsAvg = renderer.ScheduleCost.AvgUs,
            ScheduleUsMax = renderer.ScheduleCost.MaxUs,
            WalkUsAvg = renderer.WalkCost.AvgUs,
            WalkUsMax = renderer.WalkCost.MaxUs,
            DrawUsAvg = renderer.DrawCost.AvgUs,
            DrawUsMax = renderer.DrawCost.MaxUs,
            Px = px,
            Pz = pz,
            Stage = stage,
            OverdrawStart = overdrawStart,
            RenderOrder = renderOrder,
            Path = metricsPath,
        };

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                string json = snap.ToJson();
                lock (writeGate)
                {
                    File.WriteAllText(snap.Path, json);
                }
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref writeQueued, 0);
            }
        });
    }

    struct Snapshot
    {
        public string Path;
        public string Ts;
        public string Ver;
        public int Sections;
        public string Levels;
        public int Meshes;
        public int MeshesEvicted;
        public int Drawn;
        public string DrawnLevels;
        public int Culled;
        public int Occluded;
        public int GapFills;
        public int UnfilledGaps;
        public int Captured;
        public int Pending;
        public int Dropped;
        public int Swept;
        public int PeekConfirmed;
        public int ProvisionalL0;
        public int PendingCaptures;
        public int PendingMeshes;
        public int CaptureErrors;
        public int MeshErrors;
        public int MipDirty;
        public int RenderDirty;
        public int Unsaved;
        public int FarCap;
        public int FarEdge;
        public int DetailDist;
        public bool Farseer;
        public bool PressureYield;
        public bool MeshPressure;
        public int EvictOutside2x;
        public int EvictBlockedInside2x;
        public int PressureEnter;
        public int PressureClear;
        public int PressureActiveMs;
        public int CompanionYield;
        public int PressureYieldCount;
        public string Deferring;
        public int DGen0, DGen1, DGen2;
        public long ManagedMb;
        public bool HasPhaseCosts;
        public double PruneUsAvg, PruneUsMax;
        public double ScheduleUsAvg, ScheduleUsMax;
        public double WalkUsAvg, WalkUsMax;
        public double DrawUsAvg, DrawUsMax;
        public int Px, Pz;
        public string Stage;
        public float OverdrawStart;
        public double RenderOrder;

        public string ToJson()
        {
            var sb = new StringBuilder(1024);
            sb.Append('{');
            Append(sb, "ts", Ts, true);
            Append(sb, "ver", Ver);
            Append(sb, "sections", Sections);
            Append(sb, "levels", Levels);
            Append(sb, "meshes", Meshes);
            Append(sb, "meshesEvicted", MeshesEvicted);
            Append(sb, "drawn", Drawn);
            Append(sb, "drawnLevels", DrawnLevels);
            Append(sb, "culled", Culled);
            Append(sb, "occCull", Occluded);
            Append(sb, "gapFills", GapFills);
            Append(sb, "unfilledGaps", UnfilledGaps);
            Append(sb, "captured", Captured);
            Append(sb, "pending", Pending);
            Append(sb, "dropped", Dropped);
            Append(sb, "swept", Swept);
            Append(sb, "peekConfirmed", PeekConfirmed);
            Append(sb, "provisionalL0", ProvisionalL0);
            Append(sb, "pendingCaptures", PendingCaptures);
            Append(sb, "pendingMeshes", PendingMeshes);
            Append(sb, "captureErrors", CaptureErrors);
            Append(sb, "meshErrors", MeshErrors);
            Append(sb, "mipDirty", MipDirty);
            Append(sb, "renderDirty", RenderDirty);
            Append(sb, "unsaved", Unsaved);
            Append(sb, "farCap", FarCap);
            Append(sb, "farEdge", FarEdge);
            Append(sb, "detailDist", DetailDist);
            Append(sb, "farseer", Farseer);
            Append(sb, "pressureYield", PressureYield);
            Append(sb, "meshPressure", MeshPressure);
            Append(sb, "evictOutside2x", EvictOutside2x);
            Append(sb, "evictBlockedInside2x", EvictBlockedInside2x);
            Append(sb, "pressureEnter", PressureEnter);
            Append(sb, "pressureClear", PressureClear);
            Append(sb, "pressureActiveMs", PressureActiveMs);
            Append(sb, "companionYield", CompanionYield);
            Append(sb, "pressureYieldCount", PressureYieldCount);
            Append(sb, "deferring", Deferring);
            Append(sb, "dGen0", DGen0);
            Append(sb, "dGen1", DGen1);
            Append(sb, "dGen2", DGen2);
            Append(sb, "managedMb", ManagedMb);
            if (HasPhaseCosts)
            {
                Append(sb, "pruneUsAvg", PruneUsAvg);
                Append(sb, "pruneUsMax", PruneUsMax);
                Append(sb, "scheduleUsAvg", ScheduleUsAvg);
                Append(sb, "scheduleUsMax", ScheduleUsMax);
                Append(sb, "walkUsAvg", WalkUsAvg);
                Append(sb, "walkUsMax", WalkUsMax);
                Append(sb, "drawUsAvg", DrawUsAvg);
                Append(sb, "drawUsMax", DrawUsMax);
            }
            Append(sb, "px", Px);
            Append(sb, "pz", Pz);
            Append(sb, "stage", Stage);
            Append(sb, "overdrawStart", OverdrawStart);
            Append(sb, "renderOrder", RenderOrder);
            // last field: no trailing comma handling via Append helpers ends with comma; strip
            if (sb[sb.Length - 1] == ',') sb.Length--;
            sb.Append('}');
            return sb.ToString();
        }

        static void Append(StringBuilder sb, string key, string value, bool first = false)
        {
            if (!first) { /* always keyed */ }
            sb.Append('"').Append(key).Append("\":\"");
            AppendEscaped(sb, value);
            sb.Append("\",");
        }

        static void Append(StringBuilder sb, string key, int value)
        {
            sb.Append('"').Append(key).Append("\":").Append(value).Append(',');
        }

        static void Append(StringBuilder sb, string key, long value)
        {
            sb.Append('"').Append(key).Append("\":").Append(value).Append(',');
        }

        static void Append(StringBuilder sb, string key, bool value)
        {
            sb.Append('"').Append(key).Append("\":").Append(value ? "true" : "false").Append(',');
        }

        static void Append(StringBuilder sb, string key, float value)
        {
            sb.Append('"').Append(key).Append("\":").Append(value.ToString("0.###")).Append(',');
        }

        static void Append(StringBuilder sb, string key, double value)
        {
            sb.Append('"').Append(key).Append("\":").Append(value.ToString("0.###")).Append(',');
        }

        static void AppendEscaped(StringBuilder sb, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\\' || c == '"') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append(' ');
                else sb.Append(c);
            }
        }
    }
}


