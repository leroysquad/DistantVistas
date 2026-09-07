using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vintagestory.API.Client;

namespace DistantVistas;

/// <summary>
/// Machine-local login-sweep timing. First source is the last measured rate on this PC;
/// if that file is missing, harvest budgeted passes from Vintage Story client logs.
/// </summary>
public sealed class LodLoginSweepTimingStore
{
    public const string FileName = "login-sweep-timing.json";
    public const int SchemaVersion = 1;

    public int Schema { get; set; } = SchemaVersion;
    public double SecPerStop { get; set; }
    public double LastWallSec { get; set; }
    public int LastStops { get; set; }
    public int Samples { get; set; }
    public string Source { get; set; } = "";

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PathFor(ICoreClientAPI capi) =>
        System.IO.Path.Combine(capi.GetOrCreateDataPath("ModData/distantvistas"), FileName);

    public static void EnsureApplied(ICoreClientAPI capi, LodLoginSweepTiming timing)
    {
        LodLoginSweepTimingStore data = TryLoad(capi) ?? HarvestAndSave(capi);
        // Hop-era 0.5s+ samples undercount scout-viewer density. Plan from the
        // no-hop fallback unless this PC already measured faster.
        double sec = data.SecPerStop;
        if (sec > LodLoginSweepTiming.InitialSecPerStop)
            sec = LodLoginSweepTiming.InitialSecPerStop;
        LodLoginSweepTiming.SetMachineSecPerStop(sec);
        timing.BeginSession(sec);
        capi.Logger.Notification(
            "[DistantVistas] Login visit sweep: ETA from this PC -- {0:0.00}s/stop ({1}, {2} samples) -> ~{3} first pass / {4} retry.",
            LodLoginSweepTiming.MachineSecPerStop,
            data.Source,
            data.Samples,
            LodLoginSweepBootstrap.RevisitMaxVisitStops,
            LodLoginSweepBootstrap.RetryMaxVisitStops);
        // #region agent log
        try
        {
            System.IO.File.AppendAllText(
                @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                "{\"sessionId\":\"40cccb\",\"runId\":\"post-fix-4\",\"hypothesisId\":\"H-T-eta\",\"location\":\"LodLoginSweepTimingStore.EnsureApplied\",\"message\":\"timing-seed\",\"data\":{\"secPerStop\":"
                + LodLoginSweepTiming.MachineSecPerStop.ToString(CultureInfo.InvariantCulture)
                + ",\"source\":\"" + data.Source.Replace("\\", "\\\\").Replace("\"", "'")
                + "\",\"samples\":" + data.Samples
                + ",\"firstStops\":" + LodLoginSweepBootstrap.RevisitMaxVisitStops
                + ",\"retryStops\":" + LodLoginSweepBootstrap.RetryMaxVisitStops
                + ",\"etaSec\":" + (LodLoginSweepTiming.MachineSecPerStop * LodLoginSweepBootstrap.RevisitMaxVisitStops).ToString(CultureInfo.InvariantCulture)
                + "},\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n");
        }
        catch { }
        // #endregion
    }

    public static void RecordRun(ICoreClientAPI capi, LodLoginSweepTiming timing)
    {
        if (timing.SampleCount <= 0) return;
        double measured = timing.SecondsPerStop;
        LodLoginSweepTimingStore? prior = TryLoad(capi);
        double blended = prior != null && prior.Samples > 0
            ? prior.SecPerStop * 0.6 + measured * 0.4
            : measured;
        var data = new LodLoginSweepTimingStore
        {
            SecPerStop = Math.Clamp(blended, 0.75, 6.0),
            LastWallSec = timing.WallSec,
            LastStops = timing.SampleCount,
            Samples = (prior?.Samples ?? 0) + timing.SampleCount,
            Source = "measured",
        };
        data.Save(capi);
        LodLoginSweepTiming.SetMachineSecPerStop(data.SecPerStop);
        capi.Logger.Notification(
            "[DistantVistas] Login visit sweep: saved this PC's rate {0:0.00}s/stop (run {1:0.00}s/stop, {2:0}s wall, {3} stops).",
            data.SecPerStop, measured, timing.WallSec, timing.SampleCount);
        // #region agent log
        try
        {
            System.IO.File.AppendAllText(
                @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                "{\"sessionId\":\"40cccb\",\"runId\":\"post-fix-4\",\"hypothesisId\":\"H-T-eta\",\"location\":\"LodLoginSweepTimingStore.RecordRun\",\"message\":\"timing-save\",\"data\":{\"secPerStop\":"
                + data.SecPerStop.ToString(CultureInfo.InvariantCulture)
                + ",\"runSecPerStop\":" + measured.ToString(CultureInfo.InvariantCulture)
                + ",\"wallSec\":" + timing.WallSec.ToString(CultureInfo.InvariantCulture)
                + ",\"stops\":" + timing.SampleCount
                + "},\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n");
        }
        catch { }
        // #endregion
    }

    public static LodLoginSweepTimingStore? TryLoad(ICoreClientAPI capi)
    {
        string path = PathFor(capi);
        if (!File.Exists(path)) return null;
        try
        {
            var data = JsonSerializer.Deserialize<LodLoginSweepTimingStore>(File.ReadAllText(path), JsonOptions);
            if (data == null || data.Schema != SchemaVersion) return null;
            if (data.SecPerStop < 0.75 || data.SecPerStop > 6.0) return null;
            return data;
        }
        catch
        {
            return null;
        }
    }

    static LodLoginSweepTimingStore HarvestAndSave(ICoreClientAPI capi)
    {
        string dataRoot = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(capi.GetOrCreateDataPath("ModData/distantvistas"), "..", ".."));
        var samples = new List<double>();
        // Newest session first. Older archive passes were rain-drop-fast (~1.5s) and
        // must not pull the ETA under the live hold-ring rate.
        CollectLogSamples(System.IO.Path.Combine(dataRoot, "Logs", "client-main.log"), samples);
        if (samples.Count == 0)
        {
            string archive = System.IO.Path.Combine(dataRoot, "Logs", "Archive");
            if (Directory.Exists(archive))
            {
                foreach (string dir in Directory.GetDirectories(archive).OrderByDescending(d => d).Take(8))
                {
                    CollectLogSamples(System.IO.Path.Combine(dir, "client-main.log"), samples);
                    if (samples.Count > 0) break;
                }
            }
        }

        double sec = samples.Count > 0
            ? Median(samples)
            : LodLoginSweepTiming.InitialSecPerStop;
        var data = new LodLoginSweepTimingStore
        {
            SecPerStop = Math.Clamp(sec, 0.75, 6.0),
            Samples = samples.Count,
            Source = samples.Count > 0 ? "client-logs" : "fallback",
        };
        data.Save(capi);
        return data;
    }

    static void CollectLogSamples(string path, List<double> into)
    {
        if (!File.Exists(path)) return;
        try
        {
            into.AddRange(HarvestSecPerStop(File.ReadLines(path)));
        }
        catch
        {
            // Log file may be locked by a live client; ignore that source.
        }
    }

    /// <summary>
    /// Budgeted first-pass rate: time from "quiet teleports begin � N" to the next
    /// retry/finish, only when N is a planned subsample (not a 200+ hole hop).
    /// </summary>
    public static List<double> HarvestSecPerStop(IEnumerable<string> lines)
    {
        DateTime? beginAt = null;
        int beginStops = 0;
        var samples = new List<double>();
        foreach (string line in lines)
        {
            if (!TryParseStamp(line, out DateTime at)) continue;

            if (IsSweepBeginLog(line) && TryParseBeginStops(line, out int n))
            {
                beginAt = at;
                beginStops = n;
                continue;
            }

            if (beginAt == null) continue;
            bool ended = (line.Contains("retrying", StringComparison.Ordinal)
                    && line.Contains("missed regions", StringComparison.Ordinal))
                || line.Contains("Login visit sweep finished", StringComparison.Ordinal);
            if (!ended) continue;

            double sec = (at - beginAt.Value).TotalSeconds;
            // Wider than the live budget so older 16�36 stop logs and the 4x 64�96
            // first pass both harvest. Drop 200+ hole hops. Allow ~10 min walls.
            if (beginStops >= 12
                && beginStops <= 120
                && sec >= 20.0
                && sec <= 600.0)
            {
                samples.Add(sec / beginStops);
            }
            beginAt = null;
        }
        return samples;
    }

    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return LodLoginSweepTiming.InitialSecPerStop;
        var copy = values.ToList();
        copy.Sort();
        int mid = copy.Count / 2;
        return copy.Count % 2 == 1 ? copy[mid] : (copy[mid - 1] + copy[mid]) * 0.5;
    }

    static bool IsSweepBeginLog(string line) =>
        line.Contains("quiet teleports begin", StringComparison.Ordinal)
        || line.Contains("scout workers visit chunk columns", StringComparison.Ordinal)
        || line.Contains("scout viewer entities stream visit cells", StringComparison.Ordinal);

    static bool TryParseBeginStops(string line, out int stops)
    {
        stops = 0;
        int mark = line.IndexOf("quiet teleports begin", StringComparison.Ordinal);
        int markLen = "quiet teleports begin".Length;
        if (mark < 0)
        {
            mark = line.IndexOf("scout workers visit chunk columns", StringComparison.Ordinal);
            markLen = "scout workers visit chunk columns".Length;
        }
        if (mark < 0)
        {
            mark = line.IndexOf("scout viewer entities stream visit cells", StringComparison.Ordinal);
            markLen = "scout viewer entities stream visit cells".Length;
        }
        if (mark < 0) return false;
        for (int i = mark + markLen; i < line.Length; i++)
        {
            if (!char.IsDigit(line[i])) continue;
            int end = i;
            while (end < line.Length && char.IsDigit(line[end])) end++;
            return int.TryParse(line.AsSpan(i, end - i), out stops) && stops > 0;
        }
        return false;
    }

    static bool TryParseStamp(string line, out DateTime at)
    {
        at = default;
        int space = line.IndexOf(' ');
        if (space <= 0) return false;
        int space2 = line.IndexOf(' ', space + 1);
        if (space2 <= space) return false;
        return DateTime.TryParseExact(
            line.AsSpan(0, space2),
            "d.M.yyyy HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out at);
    }

    public void Save(ICoreClientAPI capi)
    {
        try
        {
            string path = PathFor(capi);
            string? dir = System.IO.Path.GetDirectoryName(path);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Best-effort.
        }
    }
}
