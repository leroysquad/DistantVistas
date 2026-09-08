using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Stretch vanilla / FluffyClouds cloud tiles out to the Distant Vistas horizon.
/// Stock InitCloudTiles uses 8x view distance (half-extent 4x VD). Our LOD rim is
/// HorizonDrawScale (4.5x), and captured land can sit farther still, so clouds die
/// short and the sky looks like a nearby box over far terrain.
/// </summary>
public static class LodCloudHorizon
{
    const string HarmonyId = "distantvistas.cloud.horizon";
    /// <summary>Vanilla max CloudTileLength. Raised so high VD + 4.5x still fits.</summary>
    const int MaxCloudTileLength = 400;
    const int VanillaMaxCloudTileLength = 200;
    const int MinCloudTileSize = 50;

    static Harmony? harmony;
    static ICoreClientAPI? capi;
    static LodTerrainRenderer? renderer;
    static readonly List<object> knownRenderers = new();
    static float lastHalfExtent = -1f;
    static bool logged;

    public static bool Active => harmony != null;

    public static void Bind(ICoreClientAPI api)
    {
        capi = api;
        if (harmony != null) return;

        harmony = new Harmony(HarmonyId);
        int patched = 0;
        patched += PatchInitCloudTiles("Vintagestory.GameContent.CloudRenderer");
        patched += PatchInitCloudTiles("FluffyClouds.CloudRendererMap");
        if (patched == 0)
        {
            api.Logger.Warning("Cloud horizon: no InitCloudTiles targets found");
            harmony.UnpatchAll(HarmonyId);
            harmony = null;
            return;
        }

        api.Logger.Notification(
            "Cloud horizon on: stretch to LOD rim (max {0} tiles, was {1})",
            MaxCloudTileLength, VanillaMaxCloudTileLength);
    }

    public static void AttachRenderer(LodTerrainRenderer lodRenderer) => renderer = lodRenderer;

    public static void DetachRenderer() => renderer = null;

    public static void Unbind()
    {
        harmony?.UnpatchAll(HarmonyId);
        harmony = null;
        knownRenderers.Clear();
        renderer = null;
        capi = null;
        lastHalfExtent = -1f;
        logged = false;
    }

    /// <summary>
    /// Call after EffectiveFarDistance / ZFar grows so cloud grids rebuild.
    /// </summary>
    public static void NotifyFarDistanceChanged()
    {
        if (harmony == null || capi == null) return;
        float need = NeededHalfExtentBlocks();
        if (lastHalfExtent > 0f && Math.Abs(need - lastHalfExtent) < 256f) return;
        lastHalfExtent = need;

        for (int i = 0; i < knownRenderers.Count; i++)
        {
            object inst = knownRenderers[i];
            FieldInfo? flag = AccessTools.Field(inst.GetType(), "requireTileRebuild");
            if (flag != null && flag.FieldType == typeof(bool))
                flag.SetValue(inst, true);
        }
    }

    static int PatchInitCloudTiles(string typeName)
    {
        Type? type = AccessTools.TypeByName(typeName);
        if (type == null) return 0;
        MethodInfo? method = AccessTools.Method(type, "InitCloudTiles", new[] { typeof(int) });
        if (method == null) return 0;

        harmony!.Patch(method,
            prefix: new HarmonyMethod(typeof(LodCloudHorizon), nameof(InitCloudTilesPrefix)),
            transpiler: new HarmonyMethod(typeof(LodCloudHorizon), nameof(InitCloudTilesTranspiler)));
        return 1;
    }

    static void InitCloudTilesPrefix(object __instance, ref int viewDistance)
    {
        RememberRenderer(__instance);

        int tileSize = ReadCloudTileSize(__instance);
        if (tileSize < 1) tileSize = MinCloudTileSize;

        int needHalf = NeededHalfExtentBlocks();
        // InitCloudTiles: length = viewDistance / tileSize; half-extent = viewDistance / 2.
        int needArg = needHalf * 2;

        // Prefer coarser tiles over exploding tile count when the rim is huge.
        int lengthAtMinSize = needArg / MinCloudTileSize;
        if (lengthAtMinSize > MaxCloudTileLength)
        {
            int grown = (needHalf + MaxCloudTileLength / 2 - 1) / (MaxCloudTileLength / 2);
            grown = Math.Max(MinCloudTileSize, grown);
            WriteCloudTileSize(__instance, grown);
            tileSize = grown;
        }
        else if (tileSize < MinCloudTileSize)
        {
            WriteCloudTileSize(__instance, MinCloudTileSize);
            tileSize = MinCloudTileSize;
        }

        if (viewDistance < needArg) viewDistance = needArg;

        if (!logged && capi != null)
        {
            logged = true;
            int expectLen = GameMath.Clamp(viewDistance / tileSize, 20, MaxCloudTileLength);
            capi.Logger.Notification(
                "Cloud horizon InitCloudTiles: half={0} tiles~{1} tileSize={2} (VD arg {3})",
                needHalf, expectLen, tileSize, viewDistance);
        }
    }

    static IEnumerable<CodeInstruction> InitCloudTilesTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction ins in instructions)
        {
            // GameMath.Clamp(viewDistance / CloudTileSize, 20, 200) - raise the ceiling.
            if (ins.opcode == OpCodes.Ldc_I4 && ins.operand is int i && i == VanillaMaxCloudTileLength)
            {
                yield return new CodeInstruction(OpCodes.Ldc_I4, MaxCloudTileLength);
                continue;
            }
            yield return ins;
        }
    }

    static int NeededHalfExtentBlocks()
    {
        int vd = 512;
        if (capi?.World?.Player?.WorldData != null)
            vd = Math.Max(64, capi.World.Player.WorldData.DesiredViewDistance);

        // Match LOD silhouette onset / draw rim (4.5x), not stock cloud 4x.
        float fromPolicy = LodCoveragePolicy.HorizonDrawScale * vd;

        float fromLod = 0f;
        if (renderer != null)
        {
            fromLod = Math.Max(renderer.EffectiveFarDistance, renderer.LiveViewDistance * LodCoveragePolicy.HorizonDrawScale);
        }

        // Same floor ApplyZFar uses so clouds do not die inside the far clip.
        float zFarFloor = 28000f * 0.5f; // half-extent covers looking out to ~ZFar on flat ground
        float need = Math.Max(fromPolicy, fromLod);
        // Do not force the full 14km half-extent until LOD actually has far land;
        // still always beat stock 4x VD.
        if (renderer != null && renderer.EffectiveFarDistance > vd * 2f)
            need = Math.Max(need, Math.Min(zFarFloor, renderer.EffectiveFarDistance));

        return GameMath.Clamp((int)Math.Ceiling(need), vd * 4, 20000);
    }

    static void RememberRenderer(object inst)
    {
        for (int i = 0; i < knownRenderers.Count; i++)
            if (ReferenceEquals(knownRenderers[i], inst)) return;
        knownRenderers.Add(inst);
    }

    static int ReadCloudTileSize(object inst)
    {
        PropertyInfo? prop = AccessTools.Property(inst.GetType(), "CloudTileSize");
        if (prop?.GetMethod == null) return MinCloudTileSize;
        try { return Convert.ToInt32(prop.GetValue(inst)); }
        catch { return MinCloudTileSize; }
    }

    static void WriteCloudTileSize(object inst, int size)
    {
        PropertyInfo? prop = AccessTools.Property(inst.GetType(), "CloudTileSize");
        if (prop?.SetMethod == null) return;
        try { prop.SetValue(inst, size); }
        catch { /* base may be get-only on some builds */ }
    }
}
