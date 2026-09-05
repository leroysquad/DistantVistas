using DistantVistas;

namespace DistantVistas.Checks;

public static class LoginSweepChecks
{
    public static void Run(Check c)
    {
        L0ChunkColumns(c);
        VisitedL0Only(c);
        BackdropHook(c);
        AudioMuteKeys(c);
        TimeFreezeKey(c);
        TeardownHook(c);
    }

    static void L0ChunkColumns(Check c)
    {
        long key = LodWorld.SectionKey(0, 3, 5);
        var cols = LodLoginSweep.ChunkColumnsForL0(key).ToArray();
        c.Eq(4, cols.Length, "L0 section covers four chunk columns");
        c.Eq(6, cols[0].Cx, "sx=3 starts at chunk cx 6");
        c.Eq(10, cols[0].Cz, "sz=5 starts at chunk cz 10");
    }

    static void VisitedL0Only(Check c)
    {
        var world = new LodWorld();
        world.InstallStoredKey(0, 1, 2, applyToParent: true, provisional: false);
        world.InstallStoredKey(1, 0, 0, applyToParent: true, provisional: false);
        world.InstallStoredKey(0, 9, 9, applyToParent: true, provisional: false);
        var keys = LodLoginSweep.VisitedL0Keys(world).ToArray();
        c.Eq(2, keys.Length, "only level-0 keys are swept");
        c.True(keys.All(k => LodWorld.KeyLevel(k) == 0), "every sweep key is L0");
    }

    static void BackdropHook(Check c)
    {
        c.Eq("distantvistas:textures/gui/login-backdrop.png",
            LodLoginBakeScreenRenderer.BackdropAsset.ToString(),
            "login backdrop asset hook");
        c.Eq("distantvistas:textures/gui/login-title-rainbow.png",
            LodLoginBakeScreenRenderer.TitleAsset.ToString(),
            "login title asset hook");
    }

    static void AudioMuteKeys(Check c)
    {
        c.Eq(6, LodLoginBakeAudioMute.VolumeKeys.Length, "all client volume sliders are muted");
        c.True(LodLoginBakeAudioMute.VolumeKeys.Contains("masterSoundLevel"), "master volume key");
        c.True(LodLoginBakeAudioMute.VolumeKeys.Contains("musicLevel"), "music volume key");
    }

    static void TimeFreezeKey(Check c)
    {
        c.Eq("distantvistas-loginbake", LodLoginBakeTimeFreeze.SpeedModifierKey,
            "login sweep calendar speed modifier key");
    }

    static void TeardownHook(Check c)
    {
        string bake = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBake.cs"));
        c.True(bake.Contains("void Teardown(bool success)"),
            "login bake uses a single Teardown path");
        c.True(bake.Contains("if (released) return"),
            "login bake teardown is idempotent");
        c.True(bake.Contains("Dispose() => Teardown(success: false)"),
            "dispose routes through Teardown");
        c.True(bake.Contains("Teardown(success: true)"),
            "finish routes through Teardown");

        string season = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodSeasonBake.cs"));
        c.True(season.Contains("block.GetColor(capi, pos)"),
            "login bake samples vanilla GetColor at column top");
        c.True(season.Contains("ApplyColorMapOnRgba(\n            climate, (string?)null,"),
            "login bake fallback samples climate without season on white");
    }
}
