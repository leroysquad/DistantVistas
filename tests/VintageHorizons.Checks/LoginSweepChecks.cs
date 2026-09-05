using DistantVistas;

namespace DistantVistas.Checks;

public static class LoginSweepChecks
{
    public static void Run(Check c)
    {
        L0ChunkColumns(c);
        VisitedL0Only(c);
        BackdropHook(c);
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
}
