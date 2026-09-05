using System.Reflection;
using HarmonyLib;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace DistantVistas;

/// <summary>
/// Stops <see cref="GuiScreenRunningGame"/> from drawing the live world while the login
/// visit sweep holds the vanilla loading screen (prevents sky/vegetation flash).
/// </summary>
public static class LodLoginBakeHarmony
{
    static Harmony? harmony;

    public static void Apply(Vintagestory.API.Common.Mod mod)
    {
        if (harmony != null) return;
        harmony = new Harmony(mod.Info.ModID + ".loginbake");
        harmony.PatchAll(typeof(LodLoginBakeHarmony).Assembly);
    }

    public static void Remove()
    {
        harmony?.UnpatchAll(harmony.Id);
        harmony = null;
    }

    static bool SkipRunningGameRender() => LodLoginBakeSweepGate.SuppressRunningGameRender;

    [HarmonyPatch(typeof(GuiScreenRunningGame), "RenderToPrimary")]
    sealed class SkipRenderToPrimary
    {
        static bool Prefix() => !SkipRunningGameRender();
    }

    [HarmonyPatch(typeof(GuiScreenRunningGame), "RenderAfterPostProcessing")]
    sealed class SkipRenderAfterPostProcessing
    {
        static bool Prefix() => !SkipRunningGameRender();
    }

    [HarmonyPatch(typeof(GuiScreenRunningGame), "RenderAfterFinalComposition")]
    sealed class SkipRenderAfterFinalComposition
    {
        static bool Prefix() => !SkipRunningGameRender();
    }

    [HarmonyPatch(typeof(GuiScreenRunningGame), "RenderAfterBlit")]
    sealed class SkipRenderAfterBlit
    {
        static bool Prefix() => !SkipRunningGameRender();
    }

    [HarmonyPatch(typeof(GuiScreenRunningGame), "RenderToDefaultFramebuffer")]
    sealed class SkipRunningGameRenderToDefaultFramebuffer
    {
        static bool Prefix() => !SkipRunningGameRender();
    }
}
