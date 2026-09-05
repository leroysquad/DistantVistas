using System.Reflection;
using HarmonyLib;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace DistantVistas;

/// <summary>
/// Defers world-load handover while the login visit sweep runs, and suppresses running-game
/// world draws when the vanilla loader is held.
/// </summary>
public static class LodLoginBakeHarmony
{
    static Harmony? harmony;

    /// <summary>When true, block <see cref="GuiScreenRunningGame.handOverRenderingToRunningGame"/>.</summary>
    public static Func<bool>? IsLoginSweepEnabled { get; set; }

    /// <summary>Advance sweep ticks at the start of each ScreenManager frame, before any screen draw.</summary>
    public static Action<float>? RenderPulse { get; set; }

    /// <summary>Paint DV splash from OnNewFrame while sweep is active, and as backup before RunningGame framebuffer present.</summary>
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
            RenderPulse?.Invoke(dt);
            // Present-path backup alone is unreliable while world draws are suppressed —
            // paint every frame from the frame pulse so the splash reaches the screen.
            // PaintSweepFrame must OrthoMode+PerspectiveMode pair (see screen renderer).
            if (LodLoginBakeSweepGate.SweepActive)
                PaintSplashCover?.Invoke();
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
    sealed class PaintSplashBeforeRunningFramebuffer
    {
        static void Prefix()
        {
            if (!SkipRunningGameRender()) return;
            PaintSplashCover?.Invoke();
        }
    }
}
