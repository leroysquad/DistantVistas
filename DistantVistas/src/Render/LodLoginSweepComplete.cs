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
/// Scoped per world via <see cref="WorldId"/> / filename (same key as the LOD .db).
/// </summary>
public sealed class LodLoginSweepComplete
{
    public const string LegacyFileName = "login-sweep-complete.json";
    public const string FileNamePrefix = "login-sweep-complete-";
    public const string RelPath = "ModData/distantvistas/login-sweep-complete-<worldId>.json";
    public const int SchemaVersion = 2;

    public int Schema { get; set; } = SchemaVersion;
    public string WorldId { get; set; } = "";
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
        PathFor(capi, LodWorldKey.For(capi.World));

    public static string PathFor(ICoreClientAPI capi, string worldId) =>
        Path.Combine(capi.GetOrCreateDataPath("ModData/distantvistas"), FileNamePrefix + worldId + ".json");

    public static string LegacyPathFor(ICoreClientAPI capi) =>
        Path.Combine(capi.GetOrCreateDataPath("ModData/distantvistas"), LegacyFileName);

    public static LodLoginSweepComplete? TryLoad(ICoreClientAPI capi)
    {
        string worldId = LodWorldKey.For(capi.World);
        LodLoginSweepComplete? data = TryRead(PathFor(capi, worldId));
        if (MatchesWorld(data, worldId)) return data;

        // Legacy global file (pre-0.8.24): only honor when WorldId was migrated in and matches.
        // Schema-1 / missing WorldId → ignore (safest; prevents cross-world skip).
        data = TryRead(LegacyPathFor(capi));
        if (MatchesWorld(data, worldId)) return data;
        return null;
    }

    static LodLoginSweepComplete? TryRead(string path)
    {
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

    static bool MatchesWorld(LodLoginSweepComplete? data, string worldId) =>
        data != null
        && !string.IsNullOrEmpty(data.WorldId)
        && string.Equals(data.WorldId, worldId, StringComparison.Ordinal);

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
            WorldId = LodWorldKey.For(capi.World),
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