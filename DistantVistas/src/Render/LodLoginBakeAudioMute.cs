using Vintagestory.API.Client;

namespace DistantVistas;

/// <summary>
/// Saves client volume sliders, mutes for the login visit sweep, and restores exactly on release.
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
    readonly Dictionary<string, int> saved = new();
    bool muted;

    public LodLoginBakeAudioMute(ICoreClientAPI capi) => this.capi = capi;

    public bool IsMuted => muted;

    /// <summary>First call saves levels; later calls keep sliders at zero if something changed them.</summary>
    public void EnsureMuted()
    {
        ISettingsClass<int> ints = capi.Settings.Int;

        if (!muted)
        {
            foreach (string key in VolumeKeys)
            {
                if (!ints.Exists(key)) continue;
                saved[key] = ints[key];
            }
            muted = true;
        }

        foreach (string key in VolumeKeys)
        {
            if (!ints.Exists(key)) continue;
            if (ints[key] != 0)
                ints.Set(key, 0, true);
        }

        RefreshMusicVolume();
    }

    public void Restore()
    {
        if (!muted) return;

        try
        {
            ISettingsClass<int> ints = capi.Settings.Int;
            foreach (KeyValuePair<string, int> kv in saved)
            {
                if (ints.Exists(kv.Key))
                    ints.Set(kv.Key, kv.Value, true);
            }
        }
        finally
        {
            saved.Clear();
            muted = false;
            RefreshMusicVolume();
        }
    }

    void RefreshMusicVolume() => capi.CurrentMusicTrack?.UpdateVolume();
}
