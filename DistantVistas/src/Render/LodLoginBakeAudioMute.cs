using System.Globalization;
using System.Reflection;
using Vintagestory.API.Client;

namespace DistantVistas;

/// <summary>
/// Mutes sweep audio by dropping the OpenAL listener gain — not the user's volume
/// sliders. Writing 0 into ClientSettings left AL gain at 0 after restore; moving
/// the in-game sliders then often wrote the same stored values and never retriggered
/// the master-gain watcher.
/// </summary>
public sealed class LodLoginBakeAudioMute
{
    public static readonly string[] VolumeKeys =
    {
        "masterSoundLevel",
        "soundLevel",
        "entitySoundLevel",
        "ambientSoundLevel",
        "weatherSoundLevel",
        "musicLevel",
    };

    readonly ICoreClientAPI capi;
    PropertyInfo? listenerGainProp;
    object? platform;
    bool resolvedPlatform;
    float savedListenerGain = 1f;
    readonly Dictionary<string, int> savedSliders = new();
    bool muted;
    bool usedSliderFallback;

    public LodLoginBakeAudioMute(ICoreClientAPI capi) => this.capi = capi;

    public bool IsMuted => muted;

    /// <summary>
    /// Skip-join / disabled-overlay path. A previous overlay can leave AL gain at 0
    /// with sliders still looking fine. Restore never ran, so EnsureMuted never will.
    /// </summary>
    public static void ForceUnmuteIfSilent(ICoreClientAPI capi)
    {
        new LodLoginBakeAudioMute(capi).UnmuteListenerFromSliders("force-skip");
    }

    /// <summary>First call captures listener gain and zeros it. Later ticks are no-ops.</summary>
    public void EnsureMuted()
    {
        if (muted) return;

        CaptureSliderSnapshot();
        savedListenerGain = CaptureRestoreGain();

        if (TrySetListenerGain(0f))
        {
            muted = true;
            usedSliderFallback = false;
            AgentAudioLog("mute-listener", "H-A1",
                "{\"gainBefore\":" + Fmt(savedListenerGain) + ",\"gainAfter\":" + Fmt(ReadListenerGainOrNeg()) + "}");
            return;
        }

        MuteViaSliders();
    }

    public void Restore()
    {
        if (!muted)
        {
            UnmuteListenerFromSliders("restore-not-muted");
            return;
        }

        try
        {
            RestoreSliders();
            TrySetListenerGain(savedListenerGain);
            BounceMasterSliderWatcher();
            capi.CurrentMusicTrack?.UpdateVolume();
            if (StillSilent())
                UnmuteListenerFromSliders("restore-still-silent");

            AgentAudioLog("restore-audio", "H-A1",
                "{\"fallback\":" + (usedSliderFallback ? "true" : "false")
                + ",\"savedGain\":" + Fmt(savedListenerGain)
                + ",\"gainAfter\":" + Fmt(ReadListenerGainOrNeg())
                + ",\"master\":" + ReadMasterSlider() + "}");
        }
        finally
        {
            muted = false;
            usedSliderFallback = false;
        }
    }

    void MuteViaSliders()
    {
        ISettingsClass<int> ints = capi.Settings.Int;
        savedListenerGain = SliderGainOrDefault();
        foreach (string key in VolumeKeys)
        {
            if (!ints.Exists(key)) continue;
            ints[key] = 0;
        }

        muted = true;
        usedSliderFallback = true;
        capi.CurrentMusicTrack?.UpdateVolume();
        AgentAudioLog("mute-sliders", "H-A1",
            "{\"master\":" + ReadMasterSlider() + ",\"gainAfter\":" + Fmt(ReadListenerGainOrNeg()) + "}");
    }

    void CaptureSliderSnapshot()
    {
        savedSliders.Clear();
        ISettingsClass<int> ints = capi.Settings.Int;
        foreach (string key in VolumeKeys)
        {
            if (!ints.Exists(key)) continue;
            savedSliders[key] = ints[key];
        }
    }

    float CaptureRestoreGain()
    {
        if (TryGetListenerGain(out float gain) && gain > 0.01f)
            return GameMathClampF(gain, 0f, 1f);
        return SliderGainOrDefault();
    }

    float SliderGainOrDefault()
    {
        int master = ReadMasterSlider();
        if (master > 0) return GameMathClampF(master / 100f, 0.01f, 1f);
        return 1f;
    }

    void RestoreSliders()
    {
        if (savedSliders.Count == 0) return;
        ISettingsClass<int> ints = capi.Settings.Int;
        foreach (KeyValuePair<string, int> pair in savedSliders)
        {
            if (!ints.Exists(pair.Key)) continue;
            int v = pair.Value;
            // Same stored value does not retrigger the AL gain watcher.
            ints[pair.Key] = v == 0 ? 1 : v - 1;
            ints[pair.Key] = v;
        }

        capi.CurrentMusicTrack?.UpdateVolume();
    }

    void UnmuteListenerFromSliders(string reason)
    {
        int master = ReadMasterSlider();
        if (master <= 0) return;
        if (!StillSilent()) return;

        float gain = GameMathClampF(master / 100f, 0.01f, 1f);
        TrySetListenerGain(gain);
        BounceMasterSliderWatcher();
        capi.CurrentMusicTrack?.UpdateVolume();
        AgentAudioLog("unmute-silent", "H-A1",
            "{\"reason\":\"" + reason + "\",\"gainAfter\":" + Fmt(ReadListenerGainOrNeg())
            + ",\"master\":" + master + "}");
    }

    bool StillSilent()
    {
        if (!TryGetListenerGain(out float gain)) return ReadMasterSlider() > 0;
        return gain < 0.01f;
    }

    void BounceMasterSliderWatcher()
    {
        ISettingsClass<int> ints = capi.Settings.Int;
        if (!ints.Exists("masterSoundLevel")) return;
        int master = ints["masterSoundLevel"];
        ints["masterSoundLevel"] = master == 0 ? 1 : master - 1;
        ints["masterSoundLevel"] = master;
    }

    bool TryGetListenerGain(out float gain)
    {
        gain = 0f;
        ResolvePlatform();
        if (platform == null || listenerGainProp == null) return false;
        try
        {
            object? v = listenerGainProp.GetValue(platform);
            if (v is float f)
            {
                gain = f;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    bool TrySetListenerGain(float gain)
    {
        ResolvePlatform();
        if (platform == null || listenerGainProp == null) return false;
        try
        {
            listenerGainProp.SetValue(platform, GameMathClampF(gain, 0f, 1f));
            return true;
        }
        catch
        {
            return false;
        }
    }

    void ResolvePlatform()
    {
        if (resolvedPlatform) return;
        resolvedPlatform = true;
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy;
            Type worldType = capi.World.GetType();
            platform = worldType.GetProperty("Platform", flags)?.GetValue(capi.World)
                ?? worldType.GetField("Platform", flags)?.GetValue(capi.World);
            listenerGainProp = platform?.GetType().GetProperty("MasterSoundLevel", flags);
        }
        catch
        {
            platform = null;
            listenerGainProp = null;
        }
    }

    float ReadListenerGainOrNeg() => TryGetListenerGain(out float g) ? g : -1f;

    int ReadMasterSlider()
    {
        try
        {
            ISettingsClass<int> ints = capi.Settings.Int;
            return ints.Exists("masterSoundLevel") ? ints["masterSoundLevel"] : -1;
        }
        catch
        {
            return -1;
        }
    }

    static float GameMathClampF(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

    static string Fmt(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    void AgentAudioLog(string message, string hypothesisId, string data)
    {
        // #region agent log
        try
        {
            System.IO.File.AppendAllText(
                @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                "{\"sessionId\":\"40cccb\",\"runId\":\"audio-2x\",\"hypothesisId\":\"" + hypothesisId
                + "\",\"location\":\"LodLoginBakeAudioMute\",\"message\":\"" + message
                + "\",\"data\":" + data + ",\"timestamp\":"
                + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n");
        }
        catch
        {
        }
        // #endregion
    }
}
