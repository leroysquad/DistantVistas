using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Visit-aware Farseer silhouette onset. Undiscovered land fades in near vanilla
/// view distance (fills the sky gap at the discovery frontier). Visited / swept
/// land stays late (HorizonDrawScale) so DV midground is not replaced by a
/// floating rim that follows the player across land already in the capture
/// envelope.
///
/// Uploads a coarse L0-section visit mask and injects uniforms into Farseer's
/// region program after it sets farViewDistance each frame.
/// </summary>
public static class FarseerVisitOnset
{
    const string HarmonyId = "distantvistas.farseer.visitonset";
    const int MaskTexels = 256;
    const int TextureUnit = 4;

    /// <summary>
    /// Extra blocks past the farthest captured L0 so sparse visit stops still
    /// suppress early onset between hops (about half a login chunk-sweep ring).
    /// </summary>
    public const double EnvelopePadBlocks = 400.0;

    static Harmony? harmony;
    static LodWorld? world;
    static ICoreClientAPI? capi;
    static LoadedTexture? maskTex;
    static int[]? pixels;
    static int lastOriginSx = int.MinValue;
    static int lastOriginSz = int.MinValue;
    static int lastVisitCount = -1;
    static int lastEnvelopeBlocks = -1;
    static bool inFarseerFrame;
    static bool loggedBind;

    public static bool Active => harmony != null;

    public static void Bind(ICoreClientAPI api, LodWorld lodWorld)
    {
        capi = api;
        world = lodWorld;
        if (!api.ModLoader.IsModEnabled("farseer")) return;
        if (harmony != null) return;

        Type? rendererType = AccessTools.TypeByName("Farseer.Client.FarRegionRenderer");
        Type? shaderBase = AccessTools.TypeByName("Vintagestory.Client.NoObf.ShaderProgramBase");
        if (rendererType == null || shaderBase == null)
        {
            api.Logger.Warning("Farseer visit-onset: renderer or ShaderProgramBase missing");
            return;
        }

        MethodInfo? onRender = AccessTools.Method(rendererType, "OnRenderFrame",
            new[] { typeof(float), typeof(EnumRenderStage) });
        MethodInfo? uniformFloat = AccessTools.Method(shaderBase, "Uniform",
            new[] { typeof(string), typeof(float) });
        if (onRender == null || uniformFloat == null)
        {
            api.Logger.Warning("Farseer visit-onset: OnRenderFrame / Uniform not found");
            return;
        }

        harmony = new Harmony(HarmonyId);
        harmony.Patch(onRender,
            prefix: new HarmonyMethod(typeof(FarseerVisitOnset), nameof(OnRenderPrefix)),
            postfix: new HarmonyMethod(typeof(FarseerVisitOnset), nameof(OnRenderPostfix)));
        harmony.Patch(uniformFloat,
            postfix: new HarmonyMethod(typeof(FarseerVisitOnset), nameof(UniformFloatPostfix)));

        maskTex = new LoadedTexture(api) { Width = MaskTexels, Height = MaskTexels };
        pixels = new int[MaskTexels * MaskTexels];
        api.Logger.Notification(
            "Farseer visit-onset on: late-only ({0:0.#}x VD); scout fills midground",
            LodCoveragePolicy.HorizonDrawScale);
    }

    public static void Unbind()
    {
        harmony?.UnpatchAll(HarmonyId);
        harmony = null;
        DetachWorld();
        if (maskTex != null)
        {
            maskTex.Dispose();
            maskTex = null;
        }
        pixels = null;
        capi = null;
        loggedBind = false;
    }

    public static void DetachWorld()
    {
        world = null;
        lastVisitCount = -1;
        lastEnvelopeBlocks = -1;
        lastOriginSx = int.MinValue;
        lastOriginSz = int.MinValue;
    }

    public static void AttachWorld(LodWorld lodWorld) => world = lodWorld;

    /// <summary>
    /// Radius of the capture envelope around <paramref name="originX"/>/<paramref name="originZ"/>:
    /// farthest L0 in HasDataSet plus pad. Zero when nothing is captured yet.
    /// </summary>
    public static double CaptureEnvelopeRadiusBlocks(
        LodWorld lodWorld,
        double originX,
        double originZ,
        double padBlocks = EnvelopePadBlocks)
    {
        int sectionBlocks = LodSection.SectionBlocks;
        double maxR2 = 0;
        foreach (long key in lodWorld.HasDataSet)
        {
            if (LodWorld.KeyLevel(key) != 0) continue;
            double cx = (LodWorld.KeySx(key) + 0.5) * sectionBlocks;
            double cz = (LodWorld.KeySz(key) + 0.5) * sectionBlocks;
            double dx = cx - originX;
            double dz = cz - originZ;
            double d2 = dx * dx + dz * dz;
            if (d2 > maxR2) maxR2 = d2;
        }
        if (maxR2 <= 0) return 0;
        return Math.Sqrt(maxR2) + Math.Max(0, padBlocks);
    }

    /// <summary>
    /// True when this L0 cell should suppress early Farseer onset: exact capture,
    /// or inside the continuous capture envelope (login sweep / walk hull).
    /// </summary>
    public static bool IsVisitedForOnset(
        int sx,
        int sz,
        int sectionBlocks,
        double originX,
        double originZ,
        double envelopeRadiusBlocks,
        System.Func<long, bool> hasL0)
    {
        if (hasL0(LodWorld.SectionKey(0, sx, sz))) return true;
        if (envelopeRadiusBlocks <= 0) return false;
        double cx = (sx + 0.5) * sectionBlocks;
        double cz = (sz + 0.5) * sectionBlocks;
        double dx = cx - originX;
        double dz = cz - originZ;
        return dx * dx + dz * dz <= envelopeRadiusBlocks * envelopeRadiusBlocks;
    }

    static void OnRenderPrefix() => inFarseerFrame = true;

    static void OnRenderPostfix() => inFarseerFrame = false;

    static void UniformFloatPostfix(object __instance, string uniformName, float value)
    {
        if (!inFarseerFrame) return;
        if (uniformName != "farViewDistance") return;
        if (__instance is not IShaderProgram prog) return;
        BindUniforms(prog);
    }

    static void BindUniforms(IShaderProgram prog)
    {
        if (capi == null || maskTex == null || pixels == null) return;

        EnsureMaskUploaded();

        float ready = world != null && maskTex.TextureId > 0 ? 1f : 0f;
        Vec3d cam = capi.World.Player.Entity.CameraPos;

        // Sampler first so the uniform exists before we touch related floats.
        if (maskTex.TextureId > 0)
            prog.BindTexture2D("visitMask", maskTex.TextureId, TextureUnit);

        prog.Uniform("visitMaskReady", ready);
        prog.Uniform("visitOnsetEarly", LodCoveragePolicy.UnvisitedFarseerOnsetScale);
        prog.Uniform("visitOnsetLate", LodCoveragePolicy.HorizonDrawScale);
        prog.Uniform("camWorldXZ", (float)cam.X, (float)cam.Z);

        int sectionBlocks = LodSection.SectionBlocks;
        float originX = lastOriginSx * (float)sectionBlocks;
        float originZ = lastOriginSz * (float)sectionBlocks;
        float sizeBlocks = MaskTexels * (float)sectionBlocks;
        prog.Uniform("visitMaskOrigin", originX, originZ);
        prog.Uniform("visitMaskSize", sizeBlocks);

        if (!loggedBind && ready > 0f)
        {
            loggedBind = true;
            capi.Logger.Notification("Farseer visit-onset uniforms bound (mask {0}x{0})", MaskTexels);
        }
    }

    static void EnsureMaskUploaded()
    {
        if (capi == null || world == null || maskTex == null || pixels == null) return;

        Vec3d cam = capi.World.Player.Entity.Pos.XYZ;
        int sectionBlocks = LodSection.SectionBlocks;
        int camSx = (int)Math.Floor(cam.X / sectionBlocks);
        int camSz = (int)Math.Floor(cam.Z / sectionBlocks);
        int half = MaskTexels / 2;
        int originSx = camSx - half;
        int originSz = camSz - half;
        int visitCount = world.HasDataSet.Count;

        ResolveEnvelopeOrigin(out double envX, out double envZ, out double envRadius);
        int envBlocks = (int)Math.Ceiling(envRadius);

        if (originSx == lastOriginSx
            && originSz == lastOriginSz
            && visitCount == lastVisitCount
            && envBlocks == lastEnvelopeBlocks)
            return;

        lastOriginSx = originSx;
        lastOriginSz = originSz;
        lastVisitCount = visitCount;
        lastEnvelopeBlocks = envBlocks;

        Array.Clear(pixels, 0, pixels.Length);
        const int white = unchecked((int)0xFFFFFFFFu);
        LodWorld lod = world;

        // Exact L0 captures first (also feeds envelope radius).
        for (int tz = 0; tz < MaskTexels; tz++)
        {
            int sz = originSz + tz;
            if (sz < 0) continue;
            for (int tx = 0; tx < MaskTexels; tx++)
            {
                int sx = originSx + tx;
                if (sx < 0) continue;
                if (!IsVisitedForOnset(
                        sx, sz, sectionBlocks, envX, envZ, envRadius,
                        key => lod.HasDataSet.Contains(key)))
                    continue;
                pixels[tz * MaskTexels + tx] = white;
            }
        }

        // Nearest: hard visit frontier. Clamp-to-edge (2) keeps UV outside on the rim.
        LoadedTexture tex = maskTex;
        capi.Render.LoadOrUpdateTextureFromRgba(pixels, false, 2, ref tex);
        maskTex = tex;
    }

    static void ResolveEnvelopeOrigin(out double originX, out double originZ, out double radius)
    {
        originX = 0;
        originZ = 0;
        radius = 0;
        if (capi == null || world == null) return;

        var spawn = capi.World.DefaultSpawnPosition;
        originX = spawn.X;
        originZ = spawn.Z;

        LodLoginSweepComplete? complete = LodLoginSweepComplete.TryLoad(capi);
        if (complete != null
            && complete.SweepRadiusBlocks > 0
            && LodLoginSweepWindow.RecaptureReason(capi.World, complete) == null)
        {
            originX = complete.SweepOriginX;
            originZ = complete.SweepOriginZ;
            radius = complete.SweepRadiusBlocks;
        }

        // Live hull grows as walk/sweep adds L0 keys (stamp alone can be stale).
        double live = CaptureEnvelopeRadiusBlocks(world, originX, originZ);
        if (live > radius) radius = live;
    }
}
