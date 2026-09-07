using System.Text.Json;
using System.Text.Json.Serialization;
using Vintagestory.API.Client;

namespace DistantVistas;

/// <summary>
/// The player's graphics view from before a login-visit hold. Written before the
/// first 750 write so a crash cannot leave 750 or the old 1000 as "what they had".
/// </summary>
public sealed class LodLoginBakeViewHoldStore
{
    public const string FileName = "login-view-hold.json";
    public const int SchemaVersion = 1;

    public int Schema { get; set; } = SchemaVersion;
    public bool Armed { get; set; }
    public int Slider { get; set; }
    public int Desired { get; set; }
    public int Approved { get; set; }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PathFor(ICoreClientAPI capi) =>
        Path.Combine(capi.GetOrCreateDataPath("ModData/distantvistas"), FileName);

    public static LodLoginBakeViewHoldStore? TryLoad(ICoreClientAPI capi)
    {
        string path = PathFor(capi);
        if (!File.Exists(path)) return null;
        try
        {
            var data = JsonSerializer.Deserialize<LodLoginBakeViewHoldStore>(
                File.ReadAllText(path), JsonOptions);
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
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Best-effort. Overlay still holds 750 in memory for this session.
        }
    }
}
