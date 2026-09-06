using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Persists an in-progress login visit sweep so the player can cancel and resume later
/// when still within <see cref="MaxResumeDayGap"/> in-game days (season alone does not qualify).
/// Scoped per world via <see cref="WorldId"/> / filename (same key as the LOD .db).
/// </summary>
public sealed class LodLoginSweepResume
{
    public const string LegacyFileName = "login-sweep-resume.json";
    public const string FileNamePrefix = "login-sweep-resume-";
    public const string RelPath = "ModData/distantvistas/login-sweep-resume-<worldId>.json";
    public const int SchemaVersion = 2;
    public const double MaxResumeDayGap = 30.0;

    public int Schema { get; set; } = SchemaVersion;
    public string WorldId { get; set; } = "";
    public string Season { get; set; } = "";
    public string CalendarToken { get; set; } = "";
    public double SavedTotalDays { get; set; }
    public LodLoginSweepPlanMode SweepMode { get; set; }
    public string SweepModeLabel { get; set; } = "";
    public int PlannedTotal { get; set; }
    public int Finished { get; set; }
    public int ResweepRound { get; set; }
    public bool RetryingMisses { get; set; }
    public List<long> Pending { get; set; } = new();
    public List<long> Completed { get; set; } = new();
    public double RestoreX { get; set; }
    public double RestoreY { get; set; }
    public double RestoreZ { get; set; }
    public float RestoreYaw { get; set; }
    public float RestorePitch { get; set; }
    public double RestoreCameraX { get; set; }
    public double RestoreCameraY { get; set; }
    public double RestoreCameraZ { get; set; }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PathFor(ICoreClientAPI capi) =>
        PathFor(capi, LodWorldKey.For(capi.World));

    public static string PathFor(ICoreClientAPI capi, string worldId) =>
        Path.Combine(capi.GetOrCreateDataPath("ModData/distantvistas"), FileNamePrefix + worldId + ".json");

    public static string LegacyPathFor(ICoreClientAPI capi) =>
        Path.Combine(capi.GetOrCreateDataPath("ModData/distantvistas"), LegacyFileName);

    public static LodLoginSweepResume? TryLoad(ICoreClientAPI capi)
    {
        string worldId = LodWorldKey.For(capi.World);
        LodLoginSweepResume? data = TryRead(PathFor(capi, worldId));
        if (IsUsable(data, worldId)) return data;

        // Legacy global (pre-0.8.24): only honor when WorldId matches current world.
        data = TryRead(LegacyPathFor(capi));
        if (IsUsable(data, worldId)) return data;
        return null;
    }

    static LodLoginSweepResume? TryRead(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            string json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<LodLoginSweepResume>(json, JsonOptions);
            if (data == null || data.Schema != SchemaVersion) return null;
            if (data.Pending.Count == 0) return null;
            return data;
        }
        catch
        {
            return null;
        }
    }

    static bool IsUsable(LodLoginSweepResume? data, string worldId) =>
        data != null
        && !string.IsNullOrEmpty(data.WorldId)
        && string.Equals(data.WorldId, worldId, StringComparison.Ordinal)
        && data.Pending.Count > 0;

    public static void Delete(ICoreClientAPI capi)
    {
        try
        {
            string path = PathFor(capi);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort.
        }
    }

    public void Save(ICoreClientAPI capi)
    {
        try
        {
            if (string.IsNullOrEmpty(WorldId))
                WorldId = LodWorldKey.For(capi.World);
            string path = PathFor(capi, WorldId);
            string? dir = Path.GetDirectoryName(path);
            if (dir != null) Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(path, json);
        }
        catch
        {
            // Best-effort.
        }
    }

    public bool IsEligible(IClientWorldAccessor world)
    {
        if (Pending.Count == 0) return false;
        string current = LodWorldKey.For(world);
        if (string.IsNullOrEmpty(WorldId)
            || !string.Equals(WorldId, current, StringComparison.Ordinal))
            return false;
        return LodLoginSweepWindow.IsWithin(world, Season, SavedTotalDays);
    }

    /// <summary>
    /// Pre-0.8.16 resumes queued every visited L0 key; reclaim into a budgeted plan instead.
    /// </summary>
    public bool IsOversizedForCurrentBudget()
    {
        if (SweepMode != LodLoginSweepPlanMode.RevisitVisited) return false;
        int budget = LodLoginSweepBootstrap.RevisitMaxVisitStops;
        return PlannedTotal > budget || Pending.Count > budget;
    }

    public static LodLoginSweepResume CaptureCalendar(ICoreClientAPI capi)
    {
        IGameCalendar cal = capi.World.Calendar;
        var pos = new BlockPos(
            (int)capi.World.Player.Entity.Pos.X,
            capi.World.SeaLevel,
            (int)capi.World.Player.Entity.Pos.Z);
        EnumSeason season = cal.GetSeason(pos);
        string seasonSlug = SeasonSlug(season);
        string token = string.Format(CultureInfo.InvariantCulture,
            "Y{0}M{1}D{2}H{3:0.#}_{4}",
            cal.Year, cal.Month, cal.DayOfYear, cal.HourOfDay, seasonSlug);

        return new LodLoginSweepResume
        {
            WorldId = LodWorldKey.For(capi.World),
            Season = seasonSlug,
            CalendarToken = token,
            SavedTotalDays = cal.TotalDays,
        };
    }

    public static string SeasonSlug(EnumSeason season) => season switch
    {
        EnumSeason.Winter => "winter",
        EnumSeason.Spring => "spring",
        EnumSeason.Summer => "summer",
        EnumSeason.Fall => "fall",
        _ => season.ToString().ToLowerInvariant(),
    };
}