using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Records the last successful login visit sweep so a later join can skip the overlay
/// when the visited canvas is still complete within the season / day window.
/// </summary>
public sealed class LodLoginSweepComplete
{
    public const string RelPath = "ModData/distantvistas/login-sweep-complete.json";
    public const int SchemaVersion = 1;

    public int Schema { get; set; } = SchemaVersion;
    public string Season { get; set; } = "";
    public string CalendarToken { get; set; } = "";
    public double SavedTotalDays { get; set; }
    public int VisitedKeyCount { get; set; }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PathFor(ICoreClientAPI capi) =>
        Path.Combine(capi.GetOrCreateDataPath("ModData/distantvistas"), "login-sweep-complete.json");

    public static LodLoginSweepComplete? TryLoad(ICoreClientAPI capi)
    {
        string path = PathFor(capi);
        if (!File.Exists(path)) return null;
        try
        {
            string json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<LodLoginSweepComplete>(json, JsonOptions);
            if (data == null || data.Schema != SchemaVersion) return null;
            return data;
        }
        catch
        {
            return null;
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

    public static LodLoginSweepComplete CaptureCalendar(ICoreClientAPI capi, LodWorld world)
    {
        IGameCalendar cal = capi.World.Calendar;
        var pos = new BlockPos(
            (int)capi.World.Player.Entity.Pos.X,
            capi.World.SeaLevel,
            (int)capi.World.Player.Entity.Pos.Z);
        EnumSeason season = cal.GetSeason(pos);
        string seasonSlug = LodLoginSweepResume.SeasonSlug(season);
        string token = string.Format(CultureInfo.InvariantCulture,
            "Y{0}M{1}D{2}H{3:0.#}_{4}",
            cal.Year, cal.Month, cal.DayOfYear, cal.HourOfDay, seasonSlug);

        return new LodLoginSweepComplete
        {
            Season = seasonSlug,
            CalendarToken = token,
            SavedTotalDays = cal.TotalDays,
            VisitedKeyCount = LodLoginSweep.VisitedL0Keys(world).Count(),
        };
    }

    public static void RecordSuccess(ICoreClientAPI capi, LodWorld world)
    {
        CaptureCalendar(capi, world).Save(capi);
    }
}
