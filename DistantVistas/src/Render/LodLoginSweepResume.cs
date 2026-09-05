using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Persists an in-progress login visit sweep so the player can cancel and resume later
/// when still the same season or within <see cref="MaxResumeDayGap"/> in-game days.
/// </summary>
public sealed class LodLoginSweepResume
{
    public const string RelPath = "ModData/distantvistas/login-sweep-resume.json";
    public const int SchemaVersion = 1;
    public const double MaxResumeDayGap = 30.0;

    public int Schema { get; set; } = SchemaVersion;
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

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PathFor(ICoreClientAPI capi) =>
        Path.Combine(capi.GetOrCreateDataPath("ModData/distantvistas"), "login-sweep-resume.json");

    public static LodLoginSweepResume? TryLoad(ICoreClientAPI capi)
    {
        string path = PathFor(capi);
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
            string path = PathFor(capi);
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
        return LodLoginSweepWindow.IsWithin(world, Season, SavedTotalDays);
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
