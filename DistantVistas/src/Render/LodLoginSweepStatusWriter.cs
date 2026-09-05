using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vintagestory.API.Client;

namespace DistantVistas;

/// <summary>
/// Heartbeat file for external monitors while the login visit sweep runs.
/// </summary>
public sealed class LodLoginSweepStatusWriter
{
    public const string RelPath = "ModData/Logs/login-sweep-status.json";
    public const double WriteIntervalSec = 3.0;
    public const double StuckThresholdSec = 10.0;

    readonly ICoreClientAPI capi;
    DateTime lastAdvanceUtc = DateTime.UtcNow;
    DateTime lastWriteUtc = DateTime.MinValue;
    string lastAdvanceHint = "armed";

    public LodLoginSweepStatusWriter(ICoreClientAPI capi) => this.capi = capi;

    public static string PathFor(ICoreClientAPI capi) =>
        Path.Combine(capi.GetOrCreateDataPath("ModData/Logs"), "login-sweep-status.json");

    public void TouchAdvance(string hint)
    {
        lastAdvanceUtc = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(hint))
            lastAdvanceHint = hint;
    }

    public void WriteNow(
        LodLoginBake.Phase phase,
        string mode,
        int regionsTotal,
        int regionsDone,
        bool force = false)
    {
        DateTime now = DateTime.UtcNow;
        if (!force && (now - lastWriteUtc).TotalSeconds < WriteIntervalSec)
            return;

        double staleSec = (now - lastAdvanceUtc).TotalSeconds;
        string? stuckHint = staleSec >= StuckThresholdSec
            ? $"no progress for {(int)Math.Floor(staleSec)}s (last: {lastAdvanceHint})"
            : null;

        var status = new StatusRecord
        {
            Schema = 1,
            Phase = phase.ToString(),
            Mode = mode,
            RegionsTotal = regionsTotal,
            RegionsDone = regionsDone,
            LastAdvanceUtc = lastAdvanceUtc.ToString("o", CultureInfo.InvariantCulture),
            WrittenUtc = now.ToString("o", CultureInfo.InvariantCulture),
            StuckHint = stuckHint,
        };

        try
        {
            string path = PathFor(capi);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(status, JsonOptions));
            lastWriteUtc = now;
        }
        catch (Exception ex)
        {
            capi.Logger.Warning("[DistantVistas] Login sweep status write failed: {0}", ex.Message);
        }
    }

    public void Clear()
    {
        try
        {
            string path = PathFor(capi);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup on release.
        }
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    sealed class StatusRecord
    {
        public int Schema { get; set; }
        public string Phase { get; set; } = "";
        public string Mode { get; set; } = "";
        public int RegionsTotal { get; set; }
        public int RegionsDone { get; set; }
        public string LastAdvanceUtc { get; set; } = "";
        public string WrittenUtc { get; set; } = "";
        public string? StuckHint { get; set; }
    }
}
