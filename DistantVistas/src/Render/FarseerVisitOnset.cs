using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Late-only Farseer silhouette onset. Early and late uniforms both use
/// HorizonDrawScale so midground stays Distant Vistas. LodFrontierScout grows
/// capture toward that rim; the visit mask remains for enrich / diagnostics.
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
    /// HasDataSet churn during overlay must not rebuild the 256×256 mask every
    /// captured L0. Origin moves still upload immediately.
    /// </summary>
    const int MaskRebuildMinMs = 500;

    /// <summary>
    /// Extra blocks past the farthest captured L0 so sparse visit stops still
    /// count as swept between hops (about one login chunk-sweep ring).
    /// </summary>
    public const double EnvelopePadBlocks = 1024.0;

    static Harmony? harmony;
    static LodWorld? world;
    static ICoreClientAPI? capi;
    static LoadedTexture? maskTex;
    static int[]? pixels;
    static int lastOriginSx = int.MinValue;
    static int lastOriginSz = int.MinValue;
    static int lastVisitCount = -1;
    static int lastEnvelopeBlocks = -1;
    static long lastMaskMs;
    static bool inFarseerFrame;
    static bool loggedBind;
    // #region agent log
    static float lastFarViewDistanceLogged;
    static long lastGapLogMs;
    static int gapLogCount;
    static double debugMeshedDist;
    static double debugCapturedDist;
    static double debugEffFar;
    static int lastRimDrawCount;

    /// <summary>Called from LodTerrainRenderer each frame for gap diagnostics.</summary>
    public static void DebugSetMeshDistances(double meshed, double captured, double effectiveFar)
    {
        debugMeshedDist = meshed;
        debugCapturedDist = captured;
        debugEffFar = effectiveFar;
    }

    /// <summary>DV draw-list size this frame — rim thrash probe for flicker.</summary>
    public static void DebugSetRimDrawCount(int drawCount) => lastRimDrawCount = drawCount;

    /// <summary>
    /// Renderer-side handoff probe (does not need Farseer UniformFloat). Uses live VD.
    /// </summary>
    public static void LogHandoffFromRenderer(float liveVd, float desiredVd, float approvedVd)
    {
        if (capi == null || world == null) return;
        Vec3d cam = capi.World.Player.Entity.CameraPos;
        LogHandoffGap(cam, liveVd, desiredVd, approvedVd, "renderer");
    }
    // #endregion

    public static bool Active => harmony != null;

    public static void Bind(ICoreClientAPI api, LodWorld lodWorld)
    {
        capi = api;
        world = lodWorld;
        // #region agent log
        gapLogCount = 0;
        lastGapLogMs = 0;
        FarseerFlickerDiag.ResetSession();
        // #endregion
        if (!api.ModLoader.IsModEnabled("farseer"))
        {
            // #region agent log
            LogBindOutcome("farseer-disabled", false);
            // #endregion
            return;
        }
        if (harmony != null) return;

        Type? rendererType = AccessTools.TypeByName("Farseer.Client.FarRegionRenderer");
        Type? shaderBase = AccessTools.TypeByName("Vintagestory.Client.NoObf.ShaderProgramBase");
        if (rendererType == null || shaderBase == null)
        {
            api.Logger.Warning("Farseer visit-onset: renderer or ShaderProgramBase missing");
            // #region agent log
            LogBindOutcome("missing-types", false);
            // #endregion
            return;
        }

        MethodInfo? onRender = AccessTools.Method(rendererType, "OnRenderFrame",
            new[] { typeof(float), typeof(EnumRenderStage) });
        MethodInfo? uniformFloat = AccessTools.Method(shaderBase, "Uniform",
            new[] { typeof(string), typeof(float) });
        if (onRender == null || uniformFloat == null)
        {
            api.Logger.Warning("Farseer visit-onset: OnRenderFrame / Uniform not found");
            // #region agent log
            LogBindOutcome("missing-methods", false);
            // #endregion
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
            "Farseer visit-onset on: silhouette {0:0.#}x VD (empty-stop {1:0.#}x); scout fills midground",
            LodCoveragePolicy.FarseerSilhouetteOnsetScale,
            LodCoveragePolicy.HorizonDrawScale);
        // #region agent log
        LogBindOutcome("ok", true);
        // #endregion
    }

    // #region agent log
    static void LogBindOutcome(string reason, bool ok)
    {
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            System.IO.File.AppendAllText(
                @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                "{\"sessionId\":\"40cccb\",\"runId\":\"gap-2\",\"hypothesisId\":\"H-F\",\"location\":\"FarseerVisitOnset.Bind\",\"message\":\"visit-onset-bind\",\"data\":{\"ok\":"
                + (ok ? "true" : "false") + ",\"reason\":\"" + reason
                + "\",\"active\":" + (Active ? "true" : "false")
                + "},\"timestamp\":" + now + "}\n");
        }
        catch { }
    }
    // #endregion

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
        lastMaskMs = 0;
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
        // #region agent log
        lastFarViewDistanceLogged = value;
        // #endregion
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
        float vd = 0f;
        try { vd = capi.World.Player.WorldData.DesiredViewDistance; } catch { }
        if (vd <= 0f) vd = 512f;
        float onsetScale = LodCoveragePolicy.FarseerSilhouetteOnsetScaleForView(vd);
        prog.Uniform("visitOnsetEarly", onsetScale);
        prog.Uniform("visitOnsetLate", onsetScale);
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

        // #region agent log
        float liveVd = 0f;
        try
        {
            if (capi.World.Player?.Entity != null)
                liveVd = capi.World.Player.WorldData.DesiredViewDistance;
        }
        catch { }
        FarseerFlickerDiag.NoteFarseerFrame(
            capi, lastFarViewDistanceLogged, liveVd, lastRimDrawCount);

        float desired = 0f, approved = 0f;
        try
        {
            desired = capi.World.Player.WorldData.DesiredViewDistance;
            approved = capi.World.Player.WorldData.LastApprovedViewDistance;
        }
        catch { }
        LogHandoffGap(cam, desired, desired, approved, "farseer-uniform");
        // #endregion
    }

    // #region agent log
    /// <summary>
    /// NDJSON handoff metrics: capture envelope vs Farseer onset vs Far View.
    /// H-A envelope short of onset; H-B FarView clamps onset; H-C zero overlap;
    /// H-D envelope from stamp not live mesh; H-E mesh short of rim; H-F visit-onset dead.
    /// </summary>
    static void LogHandoffGap(
        Vec3d cam, float liveVd, float desiredVd, float approvedVd, string source)
    {
        if (capi == null || world == null) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (gapLogCount >= 40) return;
        if (now - lastGapLogMs < 1500) return;
        lastGapLogMs = now;
        gapLogCount++;

        float vd = liveVd > 0 ? liveVd : desiredVd;

        ResolveEnvelopeOrigin(out double envX, out double envZ, out double envRadius);
        double liveEnv = CaptureEnvelopeRadiusBlocks(world, envX, envZ);
        double onsetScale = LodCoveragePolicy.FarseerSilhouetteOnsetScale;
        double horizonScale = LodCoveragePolicy.HorizonDrawScale;
        double onsetBlocks = vd * onsetScale;
        double horizonBlocks = vd * horizonScale;
        double farVd = lastFarViewDistanceLogged;
        double distStart = onsetBlocks;
        if (farVd > 0 && distStart > farVd) distStart = farVd;
        if (vd > 0 && distStart < vd * 0.5) distStart = vd * 0.5;
        double bandThickness = farVd > 0 ? farVd - distStart : -1;
        double gapEnvToOnset = onsetBlocks - envRadius;
        double gapLiveToOnset = onsetBlocks - liveEnv;
        double gapMeshToOnset = onsetBlocks - debugMeshedDist;
        double gapCapToOnset = onsetBlocks - debugCapturedDist;
        double camToEnvOrigin = Math.Sqrt(
            (cam.X - envX) * (cam.X - envX) + (cam.Z - envZ) * (cam.Z - envZ));

        int l0 = 0;
        foreach (long k in world.HasDataSet)
            if (LodWorld.KeyLevel(k) == 0) l0++;

        string hyp = gapEnvToOnset > 256 ? "H-A"
            : (farVd > 0 && farVd + 64 < onsetBlocks ? "H-B"
            : (bandThickness >= 0 && bandThickness < 128 ? "H-B"
            : (Math.Abs(onsetBlocks - horizonBlocks) < 1 && gapMeshToOnset > 128 ? "H-C"
            : (liveEnv + 256 < envRadius ? "H-D"
            : (gapMeshToOnset > 256 || gapCapToOnset > 256 ? "H-E" : "ok")))));

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            System.IO.File.AppendAllText(
                @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                "{\"sessionId\":\"40cccb\",\"runId\":\"gap-2\",\"hypothesisId\":\"" + hyp
                + "\",\"location\":\"FarseerVisitOnset.LogHandoffGap\",\"message\":\"handoff-gap\",\"data\":{"
                + "\"source\":\"" + source + "\""
                + ",\"liveVd\":" + liveVd.ToString("0.#", inv)
                + ",\"desiredVd\":" + desiredVd.ToString("0.#", inv)
                + ",\"approvedVd\":" + approvedVd.ToString("0.#", inv)
                + ",\"vd\":" + vd.ToString("0.#", inv)
                + ",\"farView\":" + farVd.ToString("0.#", inv)
                + ",\"bandThickness\":" + bandThickness.ToString("0.#", inv)
                + ",\"onsetScale\":" + onsetScale.ToString("0.##", inv)
                + ",\"horizonScale\":" + horizonScale.ToString("0.##", inv)
                + ",\"onsetBlocks\":" + onsetBlocks.ToString("0.#", inv)
                + ",\"horizonBlocks\":" + horizonBlocks.ToString("0.#", inv)
                + ",\"distStart\":" + distStart.ToString("0.#", inv)
                + ",\"envRadius\":" + envRadius.ToString("0.#", inv)
                + ",\"liveEnv\":" + liveEnv.ToString("0.#", inv)
                + ",\"meshedDist\":" + debugMeshedDist.ToString("0.#", inv)
                + ",\"capturedDist\":" + debugCapturedDist.ToString("0.#", inv)
                + ",\"effFar\":" + debugEffFar.ToString("0.#", inv)
                + ",\"gapEnvToOnset\":" + gapEnvToOnset.ToString("0.#", inv)
                + ",\"gapLiveToOnset\":" + gapLiveToOnset.ToString("0.#", inv)
                + ",\"gapMeshToOnset\":" + gapMeshToOnset.ToString("0.#", inv)
                + ",\"gapCapToOnset\":" + gapCapToOnset.ToString("0.#", inv)
                + ",\"camToEnvOrigin\":" + camToEnvOrigin.ToString("0.#", inv)
                + ",\"pad\":" + EnvelopePadBlocks.ToString("0.#", inv)
                + ",\"l0Count\":" + l0
                + ",\"hasData\":" + world.HasDataSet.Count
                + ",\"visitOnsetActive\":" + (Active ? "true" : "false")
                + ",\"maskReady\":" + (maskTex != null && maskTex.TextureId > 0 ? "true" : "false")
                + ",\"n\":" + gapLogCount
                + "},\"timestamp\":" + now + "}\n");
        }
        catch { }
    }
    // #endregion

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
        long now = capi.ElapsedMilliseconds;

        bool originMoved = originSx != lastOriginSx || originSz != lastOriginSz;
        if (!originMoved && visitCount == lastVisitCount && lastEnvelopeBlocks >= 0)
            return;
        if (!originMoved && lastVisitCount >= 0 && now - lastMaskMs < MaskRebuildMinMs)
            return;

        ResolveEnvelopeOrigin(out double envX, out double envZ, out double envRadius);
        int envBlocks = (int)Math.Ceiling(envRadius);

        lastOriginSx = originSx;
        lastOriginSz = originSz;
        lastVisitCount = visitCount;
        lastEnvelopeBlocks = envBlocks;
        lastMaskMs = now;

        // #region agent log
        FarseerFlickerDiag.NoteMaskUpload(originSx, originSz, visitCount, envBlocks);
        // #endregion

        Array.Clear(pixels, 0, pixels.Length);
        const int white = unchecked((int)0xFFFFFFFFu);

        if (envRadius > 0)
        {
            double r2 = envRadius * envRadius;
            int rTex = (int)Math.Ceiling(envRadius / sectionBlocks) + 1;
            int envTx = (int)Math.Floor(envX / sectionBlocks) - originSx;
            int envTz = (int)Math.Floor(envZ / sectionBlocks) - originSz;
            int tz0 = Math.Max(0, envTz - rTex);
            int tz1 = Math.Min(MaskTexels - 1, envTz + rTex);
            int tx0 = Math.Max(0, envTx - rTex);
            int tx1 = Math.Min(MaskTexels - 1, envTx + rTex);
            for (int tz = tz0; tz <= tz1; tz++)
            {
                int sz = originSz + tz;
                if (sz < 0) continue;
                double cz = (sz + 0.5) * sectionBlocks - envZ;
                double cz2 = cz * cz;
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    int sx = originSx + tx;
                    if (sx < 0) continue;
                    double cx = (sx + 0.5) * sectionBlocks - envX;
                    if (cx * cx + cz2 <= r2)
                        pixels[tz * MaskTexels + tx] = white;
                }
            }
        }

        foreach (long key in world.HasDataSet)
        {
            if (LodWorld.KeyLevel(key) != 0) continue;
            int sx = LodWorld.KeySx(key);
            int sz = LodWorld.KeySz(key);
            if (sx < 0 || sz < 0) continue;
            int tx = sx - originSx;
            int tz = sz - originSz;
            if ((uint)tx >= MaskTexels || (uint)tz >= MaskTexels) continue;
            pixels[tz * MaskTexels + tx] = white;
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
