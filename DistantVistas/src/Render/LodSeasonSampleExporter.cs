using System.Globalization;
using System.Text;
using System.Text.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Append-friendly JSONL export of per-column season research samples during the login
/// visit sweep. Batched writes keep allocation and IO overhead low.
/// </summary>
public sealed class LodSeasonSampleExporter : IDisposable
{
    public const string SamplesSubdir = "ModData/distantvistas/season-samples";
    public const int SchemaVersion = 1;

    /// <summary>1 = every captured column; 2/4 = sparser subsample for slow disks.</summary>
    public const int ColumnStride = 1;

    public const int FlushEveryLines = 96;

    static ReadOnlySpan<byte> Newline => "\n"u8;

    readonly ICoreClientAPI capi;
    readonly BlockPos climatePos = new();
    readonly MemoryStream batchStream = new(65536);

    Utf8JsonWriter? writer;
    FileStream? fileStream;
    string? filePath;
    bool sessionWritten;
    int linesSinceFlush;
    LodLoginSweepPlanMode sweepMode;
    string sweepModeLabel = "";

    public LodSeasonSampleExporter(ICoreClientAPI capi) => this.capi = capi;

    public void BeginSession(LodLoginSweepPlanMode mode, string modeLabel, int plannedStops)
    {
        sweepMode = mode;
        sweepModeLabel = modeLabel;
        EnsureFileOpen();
        if (sessionWritten) return;

        WriteSessionHeader(plannedStops);
        sessionWritten = true;
        FlushBatch();
    }

    public void RecordSection(long sectionKey, LodSection section)
    {
        if (section.CapturedColumns == 0) return;
        EnsureFileOpen();

        IClientWorldAccessor world = capi.World;
        Block[] blocks = world.Blocks;
        LodSeasonBake.SnowVote snowVote = LodSeasonBake.ComputeSnowVote(section, blocks, sectionKey);
        int sx = LodWorld.KeySx(sectionKey);
        int sz = LodWorld.KeySz(sectionKey);
        CalendarSnapshot cal = CalendarSnapshot.Capture(world);

        int cols = LodSection.GridSize * LodSection.GridSize;
        for (int col = 0; col < cols; col += ColumnStride)
        {
            if (!section.Captured[col]) continue;
            if (!section.TryGetTopRun(col, out ulong topRun)) continue;

            WriteColumnLine(
                sectionKey, sx, sz, col, topRun, section, blocks, world,
                snowVote, cal);

            if (linesSinceFlush >= FlushEveryLines)
                FlushBatch();
        }

        if (linesSinceFlush > 0)
            FlushBatch();
    }

    public void Flush() => FlushBatch();

    public void Dispose()
    {
        try
        {
            FlushBatch();
        }
        catch
        {
            // Best-effort on teardown.
        }

        writer?.Dispose();
        writer = null;
        fileStream?.Dispose();
        fileStream = null;
    }

    void EnsureFileOpen()
    {
        if (fileStream != null) return;

        string dir = capi.GetOrCreateDataPath(SamplesSubdir);
        WriteReadmeOnce(dir);

        IGameCalendar cal = capi.World.Calendar;
        climatePos.Set((int)capi.World.Player.Entity.Pos.X, capi.World.SeaLevel, (int)capi.World.Player.Entity.Pos.Z);
        EnumSeason season = cal.GetSeason(climatePos);
        string seasonSlug = SeasonSlug(season);
        string stamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture);
        filePath = Path.Combine(dir, $"{stamp}_{seasonSlug}.jsonl");

        fileStream = new FileStream(
            filePath, FileMode.Append, FileAccess.Write, FileShare.Read, 65536, FileOptions.SequentialScan);
        writer = new Utf8JsonWriter(batchStream, new JsonWriterOptions { Indented = false, SkipValidation = true });
    }

    void WriteSessionHeader(int plannedStops)
    {
        Utf8JsonWriter w = writer!;
        IGameCalendar cal = capi.World.Calendar;
        climatePos.Set((int)capi.World.Player.Entity.Pos.X, capi.World.SeaLevel, (int)capi.World.Player.Entity.Pos.Z);
        CalendarSnapshot snap = CalendarSnapshot.Capture(capi.World);

        w.WriteStartObject();
        w.WriteString("type", "session");
        w.WriteNumber("schema", SchemaVersion);
        w.WriteString("startedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        if (filePath != null) w.WriteString("file", Path.GetFileName(filePath));
        w.WriteString("sweepMode", sweepModeLabel);
        w.WriteString("sweepPlan", sweepMode.ToString());
        w.WriteNumber("plannedStops", plannedStops);
        w.WriteNumber("columnStride", ColumnStride);
        WriteCalendar(w, snap, climatePos.X, climatePos.Z);
        w.WriteEndObject();
        batchStream.Write(Newline);
        linesSinceFlush++;
    }

    void WriteColumnLine(
        long sectionKey,
        int sx,
        int sz,
        int col,
        ulong topRun,
        LodSection section,
        Block[] blocks,
        IClientWorldAccessor world,
        LodSeasonBake.SnowVote snowVote,
        CalendarSnapshot cal)
    {
        int pid = LodSection.RunPaletteId(topRun);
        if (pid < 0 || pid >= section.Palette.Count) return;

        LodPaletteEntry entry = section.Palette[pid];
        if (entry.BlockId <= 0 || entry.BlockId >= blocks.Length) return;

        Block surface = blocks[entry.BlockId];
        (int x, int y, int z) = LodPipeline.CaptureBlockPos(sectionKey, col, topRun);
        int localX = col % LodSection.GridSize;
        int localZ = col / LodSection.GridSize;

        SnowColumn snow = AnalyzeSnowColumn(section, blocks, col);
        CanopyColumn canopy = FindCanopyColumn(section, blocks, col, sectionKey, capi, surface, topRun);
        int surfaceRgb = entry.Color;
        if ((entry.Flags & LodPaletteEntry.FlagBaked) == 0)
        {
            int live = LodSeasonBake.SampleVanillaColor(capi, surface, x, y, z);
            if (live != 0) surfaceRgb = live;
        }

        ClimateSnapshot? climate = TryClimate(world, x, y, z);

        Utf8JsonWriter w = writer!;
        w.WriteStartObject();
        w.WriteString("type", "column");
        w.WriteNumber("sectionKey", sectionKey);
        w.WriteNumber("sx", sx);
        w.WriteNumber("sz", sz);
        w.WriteNumber("col", col);
        w.WriteNumber("localX", localX);
        w.WriteNumber("localZ", localZ);
        w.WriteNumber("x", x);
        w.WriteNumber("y", y);
        w.WriteNumber("z", z);

        WriteCalendar(w, cal, x, z);

        w.WriteNumber("surfaceBlockId", entry.BlockId);
        if (surface.Code != null) w.WriteString("surfaceBlock", surface.Code.ToShortString());

        w.WriteNumber("surfaceRgb", surfaceRgb);
        w.WriteBoolean("surfaceBaked", (entry.Flags & LodPaletteEntry.FlagBaked) != 0);

        if (LodSeasonBake.IsSnowEligibleGround(surface))
            w.WriteNumber("grassSoilRgb", surfaceRgb);

        w.WriteBoolean("groundSnow", snow.GroundSnow);
        w.WriteNumber("snowDepthBlocks", snow.DepthBlocks);
        if (snow.TopSnowBlockId > 0) w.WriteNumber("snowBlockId", snow.TopSnowBlockId);

        w.WriteBoolean("snowVoteMajority", snowVote.MajoritySnow);
        w.WriteNumber("snowVoteEligible", snowVote.Eligible);
        w.WriteNumber("snowVoteSnowy", snowVote.Snowy);

        if (canopy.Found)
        {
            w.WriteNumber("canopyBlockId", canopy.BlockId);
            if (canopy.BlockCode != null) w.WriteString("canopyBlock", canopy.BlockCode);
            w.WriteNumber("canopyY", canopy.Y);
            w.WriteNumber("canopyRgb", canopy.Rgb);
            w.WriteString("canopyClass", canopy.Class);
        }

        if (climate != null)
        {
            w.WriteNumber("tempC", climate.Value.TempC);
            w.WriteNumber("rainMm", climate.Value.RainMm);
        }

        w.WriteEndObject();
        batchStream.Write(Newline);
        linesSinceFlush++;
    }

    static void WriteCalendar(Utf8JsonWriter w, CalendarSnapshot cal, int x, int z)
    {
        w.WriteNumber("totalDays", cal.TotalDays);
        w.WriteNumber("year", cal.Year);
        w.WriteNumber("month", cal.Month);
        w.WriteString("monthName", cal.MonthName);
        w.WriteNumber("dayOfYear", cal.DayOfYear);
        w.WriteNumber("hourOfDay", cal.HourOfDay);
        w.WriteNumber("yearRel", cal.YearRel);
        w.WriteString("season", cal.Season);
        w.WriteNumber("seasonRel", cal.SeasonRel);
        w.WriteString("calendarToken", cal.Token);
        w.WriteNumber("sampleX", x);
        w.WriteNumber("sampleZ", z);
    }

    void FlushBatch()
    {
        if (batchStream.Length == 0 || fileStream == null) return;
        batchStream.TryGetBuffer(out ArraySegment<byte> buffer);
        fileStream.Write(buffer.Array!, buffer.Offset, buffer.Count);
        fileStream.Flush(true);
        batchStream.SetLength(0);
        batchStream.Position = 0;
        linesSinceFlush = 0;
    }

    static void WriteReadmeOnce(string dir)
    {
        string readmePath = Path.Combine(dir, "README.md");
        if (File.Exists(readmePath)) return;
        try
        {
            File.WriteAllText(readmePath, SeasonSamplesReadme.Text, Encoding.UTF8);
        }
        catch
        {
            // Best-effort.
        }
    }

    static string SeasonSlug(EnumSeason season) => season switch
    {
        EnumSeason.Winter => "winter",
        EnumSeason.Spring => "spring",
        EnumSeason.Summer => "summer",
        EnumSeason.Fall => "fall",
        _ => season.ToString().ToLowerInvariant(),
    };

    static ClimateSnapshot? TryClimate(IClientWorldAccessor world, int x, int y, int z)
    {
        try
        {
            var pos = new BlockPos(x, y, z);
            ClimateCondition? cl = world.BlockAccessor.GetClimateAt(pos);
            if (cl == null) return null;
            return new ClimateSnapshot(cl.Temperature, cl.Rainfall);
        }
        catch
        {
            return null;
        }
    }

    static SnowColumn AnalyzeSnowColumn(LodSection section, Block[] blocks, int col)
    {
        int depth = 0;
        int topSnowId = 0;
        bool groundSnow = false;

        foreach (ulong run in section.ColumnRuns(col))
        {
            int pid = LodSection.RunPaletteId(run);
            if (pid < 0 || pid >= section.Palette.Count) break;
            LodPaletteEntry entry = section.Palette[pid];
            if (entry.BlockId <= 0 || entry.BlockId >= blocks.Length) break;
            Block block = blocks[entry.BlockId];
            if (!LodSeasonBake.ColumnSurfaceIsSnowy(block)) break;

            int span = LodSection.RunYTop(run) - LodSection.RunYBottom(run);
            depth += span;
            if (topSnowId == 0) topSnowId = entry.BlockId;
        }

        if (section.TryGetTopRun(col, out ulong topRun))
        {
            int pid = LodSection.RunPaletteId(topRun);
            if (pid >= 0 && pid < section.Palette.Count)
            {
                int bid = section.Palette[pid].BlockId;
                if (bid > 0 && bid < blocks.Length)
                    groundSnow = LodSeasonBake.ColumnSurfaceIsSnowy(blocks[bid]);
            }
        }

        return new SnowColumn(groundSnow, depth, topSnowId);
    }

    static CanopyColumn FindCanopyColumn(
        LodSection section,
        Block[] blocks,
        int col,
        long sectionKey,
        ICoreClientAPI capi,
        Block surface,
        ulong topRun)
    {
        if (IsLeafLike(surface))
        {
            (int x, int y, int z) = LodPipeline.CaptureBlockPos(sectionKey, col, topRun);
            int rgb = LodSeasonBake.SampleVanillaColor(capi, surface, x, y, z);
            if (rgb == 0)
            {
                int pid = LodSection.RunPaletteId(topRun);
                rgb = section.Palette[pid].Color;
            }
            return new CanopyColumn(true, section.Palette[LodSection.RunPaletteId(topRun)].BlockId,
                surface.Code?.ToShortString(), y, rgb, ClassifyLeafTint(rgb));
        }

        foreach (ulong run in section.ColumnRuns(col))
        {
            int pid = LodSection.RunPaletteId(run);
            if (pid < 0 || pid >= section.Palette.Count) continue;
            Block block = blocks[section.Palette[pid].BlockId];
            if (!IsLeafLike(block)) continue;
            (int x, int y, int z) = LodPipeline.CaptureBlockPos(sectionKey, col, run);
            int rgb = LodSeasonBake.SampleVanillaColor(capi, block, x, y, z);
            if (rgb == 0) rgb = section.Palette[pid].Color;
            return new CanopyColumn(true, section.Palette[pid].BlockId,
                block.Code?.ToShortString(), y, rgb, ClassifyLeafTint(rgb));
        }

        return default;
    }

    public static bool IsLeafLike(Block block)
    {
        if (block.BlockMaterial == EnumBlockMaterial.Plant)
        {
            string? path = block.Code?.Path;
            if (path == null) return true;
            if (path.Contains("flower", StringComparison.Ordinal)
                || path.Contains("fern", StringComparison.Ordinal)
                || path.Contains("tallgrass", StringComparison.Ordinal))
                return false;
            return true;
        }

        string? p = block.Code?.Path;
        return p != null && (p.Contains("leaves", StringComparison.Ordinal)
            || p.Contains("foliage", StringComparison.Ordinal));
    }

    public static string ClassifyLeafTint(int argb)
    {
        int r = (argb >> 16) & 0xFF;
        int g = (argb >> 8) & 0xFF;
        int b = argb & 0xFF;
        float avg = (r + g + b) / 3f;
        float greenness = g - Math.Max(r, b);
        if (avg > 200f && greenness < 25f) return "white";
        if (greenness > 35f && g > r + 10 && g > b + 10) return "green";
        return "mixed";
    }

    readonly record struct SnowColumn(bool GroundSnow, int DepthBlocks, int TopSnowBlockId);
    readonly record struct CanopyColumn(bool Found, int BlockId, string? BlockCode, int Y, int Rgb, string Class)
    {
        public CanopyColumn(bool found, int blockId, string? blockCode, int y, int rgb, string cls)
        {
            Found = found;
            BlockId = blockId;
            BlockCode = blockCode;
            Y = y;
            Rgb = rgb;
            Class = cls;
        }
    }

    readonly record struct ClimateSnapshot(float TempC, float RainMm);

    readonly struct CalendarSnapshot
    {
        public double TotalDays { get; }
        public int Year { get; }
        public int Month { get; }
        public string MonthName { get; }
        public int DayOfYear { get; }
        public float HourOfDay { get; }
        public float YearRel { get; }
        public string Season { get; }
        public float SeasonRel { get; }
        public string Token { get; }

        public static CalendarSnapshot Capture(IClientWorldAccessor world)
        {
            IGameCalendar cal = world.Calendar;
            int px = (int)world.Player.Entity.Pos.X;
            int pz = (int)world.Player.Entity.Pos.Z;
            var pos = new BlockPos(px, world.SeaLevel, pz);
            EnumSeason season = cal.GetSeason(pos);
            float seasonRel = cal.GetSeasonRel(pos);
            string monthName = cal.MonthName.ToString();
            string token = string.Format(CultureInfo.InvariantCulture,
                "Y{0}M{1}D{2}H{3:0.#}_{4}",
                cal.Year, cal.Month, cal.DayOfYear, cal.HourOfDay, SeasonSlug(season));

            return new CalendarSnapshot(
                cal.TotalDays, cal.Year, cal.Month, monthName, cal.DayOfYear,
                cal.HourOfDay, cal.YearRel, SeasonSlug(season), seasonRel, token);
        }

        CalendarSnapshot(
            double totalDays, int year, int month, string monthName, int dayOfYear,
            float hourOfDay, float yearRel, string season, float seasonRel, string token)
        {
            TotalDays = totalDays;
            Year = year;
            Month = month;
            MonthName = monthName;
            DayOfYear = dayOfYear;
            HourOfDay = hourOfDay;
            YearRel = yearRel;
            Season = season;
            SeasonRel = seasonRel;
            Token = token;
        }

        static string SeasonSlug(EnumSeason season) => season switch
        {
            EnumSeason.Winter => "winter",
            EnumSeason.Spring => "spring",
            EnumSeason.Summer => "summer",
            EnumSeason.Fall => "fall",
            _ => season.ToString().ToLowerInvariant(),
        };
    }
}

/// <summary>README body written next to the season-samples folder on first export.</summary>
static file class SeasonSamplesReadme
{
    public const string Text = """
        # Distant Vistas season samples (JSONL)

        Append-friendly research export written during the **login visit sweep**.
        Run one sweep per in-game season (winter / spring / summer / fall) to build a
        four-season dataset for the same world.

        ## Files

        - `YYYY-MM-DD_HHmm_<season>.jsonl` — one file per sweep session (UTC timestamp + season at spawn).
        - Lines are newline-delimited JSON (JSONL). Safe to append; crash loses only the unflushed tail (~96 lines).

        ## Record types

        ### `session` (first line)

        | Field | Description |
        |---|---|
        | `schema` | Format version (currently `1`) |
        | `startedUtc` | ISO-8601 UTC start time |
        | `sweepMode` | UI label, e.g. `Bootstrap (coast guard)` or `Revisiting visited land` |
        | `sweepPlan` | Enum: `RevisitVisited`, `BootstrapCoastGuard`, `BootstrapRadius` |
        | `plannedStops` | L0 regions queued for this sweep |
        | `columnStride` | `1` = every captured column; higher = subsample |

        Calendar fields: `totalDays`, `year`, `month`, `monthName`, `dayOfYear`, `hourOfDay`, `yearRel`, `season`, `seasonRel`, `calendarToken`.

        ### `column` (one per sampled column per L0 stop)

        | Field | Description |
        |---|---|
        | `sectionKey`, `sx`, `sz`, `col`, `localX`, `localZ` | L0 section identity |
        | `x`, `y`, `z` | World block position of surface top |
        | `surfaceBlockId`, `surfaceBlock` | Top voxel block |
        | `surfaceRgb`, `surfaceBaked` | Captured / baked ARGB (`0xAARRGGBB`) |
        | `grassSoilRgb` | Present when surface is grass/topsoil/peat/forest floor |
        | `groundSnow` | Surface block is snow |
        | `snowDepthBlocks`, `snowBlockId` | Snow layers from column top downward |
        | `snowVote*` | Section majority snow vote over eligible ground columns |
        | `canopyBlockId`, `canopyBlock`, `canopyY`, `canopyRgb`, `canopyClass` | First leaf/plant canopy in column (`white` / `green` / `mixed`) |
        | `tempC`, `rainMm` | `GetClimateAt` at surface (when cheap) |

        Each column line repeats calendar fields so rows are self-contained for analysis.

        ## Tuning

        - `LodSeasonSampleExporter.ColumnStride` — default `1` (full density).
        - `LodSeasonSampleExporter.FlushEveryLines` — default `96` (batch fsync).

        ## Notes

        - Export does not replace LOD bake; it mirrors what the visit sweep captured.
        - Creative mode, overlay, mute, and time freeze during sweep are unchanged; gamemode restores on teardown.
        """;
}
