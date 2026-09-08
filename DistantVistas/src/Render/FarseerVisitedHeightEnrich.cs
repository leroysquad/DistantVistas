using System.Collections;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Past HorizonDrawScale visited land is Farseer's late band; unvisited may
/// onset earlier (FarseerVisitOnset). Unvisited keeps stock worldgen heightmaps.
/// Visited cells we already captured overwrite those samples with our column
/// tops so the silhouette matches real land instead of soft generator noise.
/// Still Farseer's mesh and shader.
/// </summary>
public static class FarseerVisitedHeightEnrich
{
    const string HarmonyId = "distantvistas.farseer.visitedheights";

    static Harmony? harmony;
    static LodWorld? world;
    static ICoreClientAPI? capi;
    static int lastVisitStamp = -1;
    static long lastRefreshMs;

    public static bool Active => harmony != null && world != null;

    public static void Bind(ICoreClientAPI api, LodWorld lodWorld)
    {
        capi = api;
        world = lodWorld;
        if (!api.ModLoader.IsModEnabled("farseer")) return;
        if (harmony != null) return;

        Type? rendererType = AccessTools.TypeByName("Farseer.Client.FarRegionRenderer");
        Type? dataType = AccessTools.TypeByName("Farseer.FarRegionData");
        if (rendererType == null || dataType == null)
        {
            api.Logger.Warning("Farseer visited-height enrich: FarRegion types missing");
            return;
        }

        MethodInfo? build = AccessTools.Method(rendererType, "BuildRegion",
            new[] { dataType, typeof(bool) });
        if (build == null)
        {
            api.Logger.Warning("Farseer visited-height enrich: BuildRegion not found");
            return;
        }

        harmony = new Harmony(HarmonyId);
        harmony.Patch(build,
            prefix: new HarmonyMethod(typeof(FarseerVisitedHeightEnrich),
                nameof(BuildRegionPrefix)));
        api.Logger.Notification(
            "Farseer visited-height enrich on: captured land sharpens past-handoff silhouettes");
    }

    public static void Unbind()
    {
        harmony?.UnpatchAll(HarmonyId);
        harmony = null;
        DetachWorld();
        capi = null;
    }

    /// <summary>
    /// World leave: keep the Harmony patch, drop the LodWorld so enrich no-ops
    /// until the next join reattaches.
    /// </summary>
    public static void DetachWorld()
    {
        world = null;
        lastVisitStamp = -1;
        lastRefreshMs = 0;
    }

    public static void AttachWorld(LodWorld lodWorld) => world = lodWorld;

    /// <summary>
    /// Re-run enrich + rebuild for regions already loaded. Login bake and late
    /// walks fill HasDataSet after Farseer's first BuildRegion.
    /// </summary>
    public static void RefreshLoadedIfNeeded(bool force = false)
    {
        if (!Active || capi == null || world == null) return;

        int stamp = world.HasDataSet.Count;
        long now = capi.ElapsedMilliseconds;
        if (!force)
        {
            if (stamp == lastVisitStamp) return;
            if (now - lastRefreshMs < 2500) return;
        }
        lastVisitStamp = stamp;
        lastRefreshMs = now;

        if (!TryGetRenderer(out object? renderer) || renderer == null) return;

        FieldInfo? modelsField = AccessTools.Field(renderer.GetType(), "activeRegionModels");
        MethodInfo? build = AccessTools.Method(renderer.GetType(), "BuildRegion");
        if (modelsField?.GetValue(renderer) is not IDictionary models || build == null) return;

        // Snapshot first. BuildRegion mutates activeRegionModels; enumerating
        // the live dictionary while invoking it crashes with Collection was modified.
        var sources = new List<object>(models.Count);
        foreach (DictionaryEntry entry in models)
        {
            object? perModel = entry.Value;
            if (perModel == null) continue;
            PropertyInfo? srcProp = AccessTools.Property(perModel.GetType(), "SourceData");
            object? sourceData = srcProp?.GetValue(perModel);
            if (sourceData != null) sources.Add(sourceData);
        }

        // #region agent log
        FarseerFlickerDiag.NoteEnrichRebuild(stamp, sources.Count);
        // #endregion

        int rebuilt = 0;
        for (int i = 0; i < sources.Count; i++)
        {
            object sourceData = sources[i];
            if (!TryEnrich(sourceData)) continue;
            build.Invoke(renderer, new[] { sourceData, true });
            rebuilt++;
            if (rebuilt >= 4 && !force) break;
        }
    }

    static void BuildRegionPrefix(object sourceData) => TryEnrich(sourceData);

    static bool TryEnrich(object sourceData)
    {
        if (world == null || capi == null) return false;

        Type dataType = sourceData.GetType();
        object? heightmap = AccessTools.Property(dataType, "Heightmap")?.GetValue(sourceData)
            ?? AccessTools.Field(dataType, "Heightmap")?.GetValue(sourceData);
        if (heightmap == null) return false;

        Type hmType = heightmap.GetType();
        int gridSize = Convert.ToInt32(
            AccessTools.Property(hmType, "GridSize")?.GetValue(heightmap)
            ?? AccessTools.Field(hmType, "GridSize")?.GetValue(heightmap)
            ?? 0);
        int[]? points = AccessTools.Property(hmType, "Points")?.GetValue(heightmap) as int[]
            ?? AccessTools.Field(hmType, "Points")?.GetValue(heightmap) as int[];
        if (gridSize <= 0 || points == null || points.Length < gridSize * gridSize)
            return false;

        int regionX = Convert.ToInt32(
            AccessTools.Property(dataType, "RegionX")?.GetValue(sourceData)
            ?? AccessTools.Field(dataType, "RegionX")?.GetValue(sourceData)
            ?? 0);
        int regionZ = Convert.ToInt32(
            AccessTools.Property(dataType, "RegionZ")?.GetValue(sourceData)
            ?? AccessTools.Field(dataType, "RegionZ")?.GetValue(sourceData)
            ?? 0);
        int regionSize = Convert.ToInt32(
            AccessTools.Property(dataType, "RegionSize")?.GetValue(sourceData)
            ?? AccessTools.Field(dataType, "RegionSize")?.GetValue(sourceData)
            ?? 0);
        if (regionSize <= 0) return false;

        int originX = regionX * regionSize;
        int originZ = regionZ * regionSize;
        float cell = regionSize / (float)gridSize;
        int sea = capi.World.SeaLevel;
        int sectionBlocks = LodSection.SectionBlocks;
        bool any = false;

        for (int gz = 0; gz < gridSize; gz++)
        {
            for (int gx = 0; gx < gridSize; gx++)
            {
                int blockX = originX + (int)((gx + 0.5f) * cell);
                int blockZ = originZ + (int)((gz + 0.5f) * cell);
                if (blockX < 0 || blockZ < 0) continue;

                int sx = blockX / sectionBlocks;
                int sz = blockZ / sectionBlocks;
                long key = LodWorld.SectionKey(0, sx, sz);
                if (!world.HasDataSet.Contains(key)) continue;
                if (!world.Sections.TryGetValue(key, out LodSection? section) || section == null)
                    continue;

                int lx = blockX - sx * sectionBlocks;
                int lz = blockZ - sz * sectionBlocks;
                if ((uint)lx >= LodSection.GridSize || (uint)lz >= LodSection.GridSize)
                    continue;

                int col = lz * LodSection.GridSize + lx;
                if (!section.TryGetTopRun(col, out ulong run)) continue;

                int yTop = LodSection.RunYTop(run);
                int sample = GameMath.Max(yTop, sea);
                int idx = gz * gridSize + gx;
                if (points[idx] == sample) continue;
                points[idx] = sample;
                any = true;
            }
        }

        return any;
    }

    static bool TryGetRenderer(out object? renderer)
    {
        renderer = null;
        if (capi == null) return false;
        ModSystem? mod = capi.ModLoader.GetModSystem("Farseer.FarseerModSystem");
        if (mod == null) return false;
        object? client = AccessTools.Property(mod.GetType(), "Client")?.GetValue(mod);
        if (client == null) return false;
        renderer = AccessTools.Field(client.GetType(), "renderer")?.GetValue(client);
        return renderer != null;
    }
}
