namespace DistantVistas.Checks;

public static class ExploreBakeChecks
{
    public static void Run(Check c)
    {
        Budget(c);
        PipelineHook(c);
        SpringSnowAcceptance(c);
        ShaderSafetyNet(c);
    }

    static void Budget(Check c)
    {
        c.Eq(1, LodExploreBake.SectionsPerTick, "explore bake drains one L0 section per tick");
        c.Eq(2, LodExploreBake.SectionsPerTickBusy, "explore bake drains two when capture is busy");
    }

    static void PipelineHook(Check c)
    {
        string pipeline = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Lod", "LodPipeline.cs"));
        c.True(pipeline.Contains("ExploreBake.Queue"),
            "capture apply queues explore bake for changed L0 sections");
        c.True(pipeline.Contains("DrainExploreBake"),
            "pipeline drains explore bake on game tick");
        c.True(pipeline.Contains("ExploreUntintedOf"),
            "pipeline wires explore untinted resolver from mod system");
        c.True(pipeline.Contains("CurrentCaptureProvisional"),
            "pipeline exposes provisional flag for palette registration");
        c.True(pipeline.Contains("FinalizeL0DiscoverBake"),
            "capture apply finalizes L0 live visit bake before first mesh upload");
        c.True(pipeline.Contains("explore drain visit-bakes"),
            "discover finalize defers visit bake to explore drain");
        c.False(ContainsBetween(pipeline, "void FinalizeL0DiscoverBake", "int RegisterPaletteEntry", "UpgradeLegacyEntries"),
            "discover finalize does not substitute shader-repro for live GetColor");
        c.True(pipeline.Contains("InvalidateMipAncestors"),
            "visit bake still walks parent keys after L0 bake");
        c.True(pipeline.Contains("ProcessPropagation(propagationBudget, World.RequestGpuSwap)"),
            "play remip keeps the old GPU mesh until the new one uploads");
        c.True(pipeline.Contains("ProcessPropagation(budget, World.RequestGpuSwap)"),
            "login mip drain keeps GPU meshes until swap-in (does not punch holes)");
        c.True(pipeline.Contains("SweepLaneSpawn") && pipeline.Contains("SweepLaneScout"),
            "overlay spawn-disk sweep and scout rings keep separate row cursors");
        c.True(pipeline.Contains("World.RequestGpuSwap"),
            "walk-time bake swaps GPU meshes instead of disposing first");

        string world = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Lod", "LodWorld.cs"));
        c.True(world.Contains("onParentRemipped"),
            "mip propagation callback when parent palette changes");

        string explore = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodExploreBake.cs"));
        string season = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodSeasonBake.cs"));
        c.True(explore.Contains("BakeSectionFromVisit"),
            "explore bake uses exact GetColor visit bake");
        c.True(explore.Contains("DebugVisitKind = \"walk\""),
            "walk visit bake tags the shared GetColor path");
        string login = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBake.cs"));
        c.True(login.Contains("BakeSectionFromVisit"),
            "overlay visit bake uses the same GetColor path as walk");
        c.True(login.Contains("DebugVisitKind = \"overlay\""),
            "overlay visit bake tags the shared GetColor path");
        c.True(season.Contains("FinishColumnPaint"),
            "overlay and walk share FinishColumnPaint season-ground mix");
        c.True(season.Contains("SampleTextureMean"),
            "visit bake samples texture mean for winter camouflage specks");
        c.True(season.Contains("BeginSectionTextureMeans"),
            "visit bake opens the per-section texture-mean cache");
        c.True(season.Contains("MixVisitBlock"),
            "expire leftover uses the same season-ground mix as overlay and walk");
        string mix = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodSurfaceMix.cs"));
        c.True(mix.Contains("MixSeasonGround"),
            "overlay and walk share MixSeasonGround from calendar winter amount");
        c.True(mix.Contains("MixVisitBlock"),
            "one-block expire path shares MixSeasonGround");
        c.True(mix.Contains("ProbeWinter"),
            "column stack stores winter amount for the shared mix");
        c.True(season.Contains("KeepVisitSnowColor"),
            "pale grass RGB is not kept as snow");
        c.True(explore.Contains("SectionHasLiveTint"),
            "explore bake still exposes live-tint helper for capture skip");
        c.True(season.Contains("CanVisitBake"),
            "visit bake accepts snow and climate-untinted tops");
        c.True(season.Contains("TryResolveLiveSurface"),
            "visit bake reads the loaded column top, not only the stored run");
        c.False(explore.Contains("if (!SectionHasLiveTint(section)) return;"),
            "explore bake queues FlagBaked L0 so live snow and canopy can overwrite");
        c.True(explore.Contains("int remaining = pending.Count"),
            "explore drain snapshots queue length so not-ready keys cannot livelock Tick");
        c.True(explore.Contains("readyAttempted"),
            "explore drain stops retrying a live-tint L0 that already baked with chunks loaded");
        c.False(pipeline.Contains("ExploreBake.ResetAttempt"),
            "capture apply does not reset the L0 bake latch every quadrant");
        c.True(explore.Contains("public void ResetAttempt"),
            "ResetAttempt stays available for a genuine new-land retry");
        c.True(season.Contains("IsColumnMapLoaded"),
            "visit bake is per-column when map chunk is resident");
        c.False(ContainsBetween(pipeline, "void FinalizeL0DiscoverBake", "int RegisterPaletteEntry", "CanBakeSectionNow"),
            "discover finalize visit-bakes per column, not all-or-nothing section gate");
        c.True(ContainsBetween(pipeline, "void AfterSectionLoaded(long key, LodSection section, ref int repaired)",
                "void AfterSectionLoaded(long key, LodSection section)", "ExploreBake.Queue"),
            "disk load queues visit bake for L0 live-tint sections");

        string mesher = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Lod", "LodMesher.cs"));
        c.True(mesher.Contains("Thin band 2 still multiplies live tint"),
            "mesher documents visit-baked canopy must use band 3");

        string mod = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "DistantVistasModSystem.cs"));
        c.True(mod.Contains("QueueExploreBakeNearPlayer"),
            "mod re-queues live-tint L0 near player on walk-back");
        c.True(mod.Contains("ExplorePlantTintFallback"),
            "mod wires plant tint fallback for explore bake");
        c.True(mod.Contains("TryDiscoverBake"),
            "mod bakes palette at capture when map chunk is loaded");
        c.True(mod.Contains("SampleVanillaColor"),
            "discover bake uses live GetColor like login visit sweep");
        c.False(ContainsBetween(mod, "bool TryDiscoverBake", "bool IsCaptureColumnMapLoaded", "BakePaletteColor"),
            "discover bake does not use shader-repro BakePaletteColor");
        c.True(mod.Contains("ColumnSurfaceIsSnowy"),
            "real snow caps bake GetColor instead of spring live-tint");
        c.False(mod.Contains("cx >= 0 && cz >= 0"),
            "map-chunk probe works in negative world coordinates");
        c.True(mod.Contains("return (bakedColor, (byte)LodTintRegistry.SlotNone, true)"),
            "discover bake returns FlagBaked palette row");
        c.False(mod.Contains("Baked=false ALWAYS"),
            "DescribePalette no longer always returns live-tint path");
    }

    static bool ContainsBetween(string text, string start, string end, string needle)
    {
        int i0 = text.IndexOf(start, StringComparison.Ordinal);
        if (i0 < 0) return false;
        int i1 = text.IndexOf(end, i0 + start.Length, StringComparison.Ordinal);
        if (i1 < 0) return text[i0..].Contains(needle, StringComparison.Ordinal);
        return text[i0..i1].Contains(needle, StringComparison.Ordinal);
    }

    static void SpringSnowAcceptance(Check c)
    {
        // May/spring: snow-row high sample is low-chroma bright — must not lavenderize valley grass.
        float hr = 0.96f, hg = 0.97f, hb = 0.98f;
        float lr = 0.48f, lg = 0.61f, lb = 0.05f;
        c.True(LodTintRegistry.IsSnowLikeTint(hr, hg, hb),
            "spring snow-band climate row is recognised as snow-like");
        var low = new float[LodTintRegistry.MaxSlots * 4];
        var high = new float[LodTintRegistry.MaxSlots * 4];
        low[4] = lr; low[5] = lg; low[6] = lb; low[7] = 1f;
        high[4] = hr; high[5] = hg; high[6] = hb; high[7] = 1f;
        LodTintRegistry.ProtectHighTintFromSnow(low, high, slot: 1);
        c.Near(lr, high[4], 0.0001, "valley green replaces snow-row high (no lavender sheet)");
        // Live-tint safety net: per-channel climate ratio skews greener than luminance scale.
        float sr = 0.50f, sg = 0.70f, sb = 0.20f;
        float kr = 0.50f, kg = 0.80f, kb = 0.20f;
        float localR = 0.62f, localG = 0.52f, localB = 0.18f;
        float oldR = sr * LodClimateField.SafeRatio(localR, kr);
        float oldG = sg * LodClimateField.SafeRatio(localG, kg);
        LodClimateField.ApplyLocalClimate(
            sr, sg, sb, kr, kg, kb, localR, localG, localB, out float nR, out float nG, out float _);
        c.True(nG / nR > oldG / oldR,
            "luminance climate shift beats per-channel ratio for spring lavender guard");
    }

    static void ShaderSafetyNet(Check c)
    {
        string vsh = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "assets", "distantvistas", "shaders", "lodterrain.vsh"));
        c.True(vsh.Contains("snow-row high sample must not bleach"),
            "vertex shader documents snow-row clamp for live-tint grass");
        c.True(vsh.Contains("Luminance scale preserves topsoil hue"),
            "vertex shader documents luminance climate shift");
        c.True(vsh.Contains("localLum / max(keepLum"),
            "vertex shader uses luminance scale not per-channel ratio");
        c.True(!vsh.Contains("localCl.rgb / keepRgb"),
            "vertex shader no longer multiplies per-channel local/keep ratio");
    }
}
