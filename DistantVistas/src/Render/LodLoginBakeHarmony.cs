using System.Reflection;
using HarmonyLib;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace DistantVistas;

/// <summary>
/// Defers world-load handover only while the login visit sweep is actively running.
/// Splash paints on every present path: RunningGame and LoadingGame
/// <see cref="GuiScreenRunningGame.RenderToDefaultFramebuffer"/> Prefixes, plus
/// ScreenManager.OnNewFrame Postfix and <see cref="EnumRenderStage.Done"/> when earlier stages were skipped.
/// Splash GL is gated until vanilla <c>FinaliseTextureAtlas_StageC</c> completes so HD-pack
/// StageB worker BindTexture cannot race main-thread splash texture creation.
/// </summary>
public static class LodLoginBakeHarmony
{
    static Harmony? harmony;
    static int paintAttempts;

    /// <summary>When true, block <see cref="GuiScreenRunningGame.handOverRenderingToRunningGame"/>.</summary>
    public static Func<bool>? IsLoginSweepEnabled { get; set; }

    /// <summary>Advance sweep ticks at the start of each ScreenManager frame, before any screen draw.</summary>
    public static Action<float>? RenderPulse { get; set; }

    /// <summary>Paint DV splash on framebuffer present paths while <see cref="LodLoginBakeSweepGate.SweepActive"/>.</summary>
    public static Action? PaintSplashCover { get; set; }

    internal static void InvokePaintSplashCover(string path)
    {
        if (!LodLoginBakeSweepGate.SweepActive) return;
        if (!LodLoginBakeSweepGate.TextureAtlasesReady) return;
        paintAttempts++;
        try
        {
            PaintSplashCover?.Invoke();
        }
        catch
        {
            // Paint is best-effort; other present paths may succeed the same frame.
        }
    }

    public static void ResetPaintDiagnostics() => paintAttempts = 0;

    public static void Apply(Vintagestory.API.Common.Mod mod)
    {
        if (harmony != null) return;
        LodLoginBakeSweepGate.ResetTextureAtlasGate();
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
        paintAttempts = 0;
    }

    static bool SkipRunningGameRender() => LodLoginBakeSweepGate.SuppressRunningGameRender;

    static bool SkipLoadingGameDraw() => LodLoginBakeSweepGate.SweepActive;

    [HarmonyPatch(typeof(ScreenManager), "OnNewFrame")]
    sealed class PulseAndPaintScreenFrame
    {
        static void Prefix(float dt) => RenderPulse?.Invoke(dt);

        /// <summary>
        /// Safety present path when LoadingGame framebuffer was skipped and Ortho stages
        /// never ran (char-create → bootstrap black screen, 0.8.28–0.8.32).
        /// </summary>
        static void Postfix(float dt)
        {
            if (!LodLoginBakeSweepGate.SweepActive) return;
            InvokePaintSplashCover("on-new-frame");
        }
    }

    [HarmonyPatch(typeof(GuiScreenLoadingGame), "RenderToDefaultFramebuffer")]
    sealed class SkipLoadingGameRenderDuringSweep
    {
        /// <summary>Skip vanilla loader draw during sweep but paint DV splash on present first.</summary>
        static bool Prefix()
        {
            if (!SkipLoadingGameDraw()) return true;
            // Keep vanilla loader visible until atlas GPU compose finishes — painting earlier
            // races ComposeTextureAtlasses_StageB worker BindTexture (TrueScale HD crash).
            if (!LodLoginBakeSweepGate.TextureAtlasesReady) return true;
            InvokePaintSplashCover("loading-game-present");
            return false;
        }
    }

    [HarmonyPatch(typeof(GuiScreenRunningGame), "handOverRenderingToRunningGame")]
    sealed class DeferHandoverToRunningGame
    {
    static bool Prefix()
    {
        if (LodLoginBakeSweepGate.AllowHandoverPassThrough) return true;
        // Only defer handover while the sweep is actively armed — not merely because the
        // feature is enabled in config (blocked level-load handover → black screen).
        if (!LodLoginBakeSweepGate.SweepActive) return true;
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
    /// Registered Ortho/AfterFinal/Done IRenderers do not reliably run while handover is
    /// deferred — paint the splash on the RunningGame framebuffer present path instead.
    /// Postfix after the default-FB blit so the opaque cover sits on top.
    /// </summary>
    [HarmonyPatch(typeof(GuiScreenRunningGame), "RenderToDefaultFramebuffer")]
    sealed class PaintSplashBeforeRunningFramebuffer
    {
        static void Postfix() => InvokePaintSplashCover("running-game-present");
    }

    /// <summary>Mark atlas GPU compose complete after each block/item/entity StageC pass.</summary>
    [HarmonyPatch(typeof(ClientSystemStartup), "FinaliseTextureAtlas_StageC")]
    sealed class MarkTextureAtlasStageCComplete
    {
        static void Postfix() => LodLoginBakeSweepGate.NotifyAtlasStageCComplete();
    }

    /// <summary>Clear the ready flag when a new atlas compose wave starts after a prior load.</summary>
    [HarmonyPatch(typeof(ClientSystemStartup), "FinaliseTextureAtlas_StageB")]
    sealed class ResetTextureAtlasGateOnRecompose
    {
        static void Prefix()
        {
            if (LodLoginBakeSweepGate.TextureAtlasesReady)
                LodLoginBakeSweepGate.ResetTextureAtlasGate();
        }
    }
}
