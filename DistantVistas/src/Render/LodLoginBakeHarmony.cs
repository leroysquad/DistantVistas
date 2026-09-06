using System.Reflection;
using HarmonyLib;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace DistantVistas;

/// <summary>
/// Defers world-load handover while the login visit sweep runs. Sweep ticks advance
/// from OnNewFrame; splash paints on <see cref="GuiScreenRunningGame.RenderToDefaultFramebuffer"/>
/// Postfix (present path) and via registered Ortho/AfterFinal IRenderers when they run.
/// World flash is blocked by <see cref="LodLoginBakeWorldHide"/>, not by skipping
/// RenderToPrimary. The DV splash MUST present every frame during the sweep.
/// </summary>
public static class LodLoginBakeHarmony
{
    static Harmony? harmony;

    /// <summary>When true, block <see cref="GuiScreenRunningGame.handOverRenderingToRunningGame"/>.</summary>
    public static Func<bool>? IsLoginSweepEnabled { get; set; }

    /// <summary>Advance sweep ticks at the start of each ScreenManager frame, before any screen draw.</summary>
    public static Action<float>? RenderPulse { get; set; }

    /// <summary>
    /// Paint DV splash on the running-game present path. Invoked from
    /// <see cref="PaintSplashBeforeRunningFramebuffer"/> when <see cref="LodLoginBakeSweepGate.SweepActive"/>.
    /// Do not call from OnNewFrame — that path does not present the framebuffer (0.8.25).
    /// </summary>
    public static Action? PaintSplashCover { get; set; }

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
        PaintSplashCover = null;
    }

    static bool SkipRunningGameRender() => LodLoginBakeSweepGate.SuppressRunningGameRender;

    static bool SkipLoadingGameDraw() => LodLoginBakeSweepGate.SweepActive;

    [HarmonyPatch(typeof(ScreenManager), "OnNewFrame")]
    sealed class PulseBeforeScreenFrame
    {
        static void Prefix(float dt)
        {
            // Tick only — splash paints on RunningGame RenderToDefaultFramebuffer Postfix.
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

    /// <summary>
    /// Registered Ortho/AfterFinal IRenderers do not reliably run while handover is
    /// deferred — paint the splash on the RunningGame framebuffer present path instead.
    /// Postfix so world/post passes finish first; splash clears and covers on top.
    /// </summary>
    [HarmonyPatch(typeof(GuiScreenRunningGame), "RenderToDefaultFramebuffer")]
    sealed class PaintSplashBeforeRunningFramebuffer
    {
        static void Postfix()
        {
            if (!LodLoginBakeSweepGate.SweepActive) return;
            PaintSplashCover?.Invoke();
        }
    }
}
