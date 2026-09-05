using System.Reflection;
using HarmonyLib;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace DistantVistas;

/// <summary>
/// Defers world-load handover while the login visit sweep runs. Sweep ticks advance
/// from OnNewFrame; splash paints via registered Ortho/AfterFinalComposition IRenderers
/// on the RunningGame present pipeline (world flash blocked by WorldHide, not by
/// skipping RenderToPrimary). The DV splash MUST present every frame during the sweep.
/// </summary>
public static class LodLoginBakeHarmony
{
    static Harmony? harmony;

    /// <summary>When true, block <see cref="GuiScreenRunningGame.handOverRenderingToRunningGame"/>.</summary>
    public static Func<bool>? IsLoginSweepEnabled { get; set; }

    /// <summary>Advance sweep ticks at the start of each ScreenManager frame, before any screen draw.</summary>
    public static Action<float>? RenderPulse { get; set; }

    public static void Apply(Vintagestory.API.Common.Mod mod)
    {
        if (harmony != null) return;
        harmony = new Harmony(mod.Info.ModID + ".loginbake");
        harmony.PatchAll(typeof(LodLoginBakeHarmony).Assembly);
    }

    public static void Remove()
    {
        harmony?.UnpatchAll(harmony?.Id);
        harmony = null;
        IsLoginSweepEnabled = null;
        RenderPulse = null;
    }

    static bool SkipRunningGameRender() => LodLoginBakeSweepGate.SuppressRunningGameRender;

    static bool SkipLoadingGameDraw() => LodLoginBakeSweepGate.SweepActive;

    [HarmonyPatch(typeof(ScreenManager), "OnNewFrame")]
    sealed class PulseBeforeScreenFrame
    {
        static void Prefix(float dt)
        {
            // Tick only — do not OrthoMode-paint here. Splash must come from RunningGame
            // Ortho/AfterFinalComposition IRenderers so the framebuffer actually presents.
            RenderPulse?.Invoke(dt);
        }
    }

    [HarmonyPatch(typeof(GuiScreenLoadingGame), "RenderToDefaultFramebuffer")]
    sealed class SkipLoadingGameRenderDuringSweep
    {
        static bool Prefix() => !SkipLoadingGameDraw();
    }

    [HarmonyPatch(typeof(GuiScreenRunningGame), "handOverRenderingToRunningGame")]
    sealed class DeferHandoverToRunningGame
    {
    static bool Prefix()
    {
        if (LodLoginBakeSweepGate.AllowHandoverPassThrough) return true;
        if (IsLoginSweepEnabled?.Invoke() != true) return true;
        LodLoginBakeSweepGate.MarkHandoverDeferred();
        return false;
    }
    }

    // Kept for optional/legacy SuppressRunningGameRender=true; login sweep leaves it false
    // so RenderToPrimary runs and Ortho splash IRenderers can present.
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
}
