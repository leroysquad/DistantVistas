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
        c.True(pipeline.Contains("TryBakeCapturedSection"),
            "capture apply runs visit bake when L0 chunks are resident");

        string explore = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodExploreBake.cs"));
        c.True(explore.Contains("BakeSectionFromVisit"),
            "explore bake uses exact GetColor visit bake");
        c.True(explore.Contains("SectionHasLiveTint"),
            "explore bake skips already-baked sections");
        c.True(explore.Contains("CanBakeSectionNow"),
            "explore bake waits for map chunks before GetColor");

        string mod = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "DistantVistasModSystem.cs"));
        c.True(mod.Contains("QueueExploreBakeNearPlayer"),
            "mod re-queues live-tint L0 near player on walk-back");
        c.True(mod.Contains("ExplorePlantTintFallback"),
            "mod wires plant tint fallback for explore bake");
        c.True(mod.Contains("TryDiscoverBake"),
            "mod bakes palette at capture when map chunk is loaded");
        c.True(mod.Contains("BakePaletteColor"),
            "discover bake falls back to snow-guarded tint repro when GetColor misses");
        c.True(mod.Contains("ColumnSurfaceIsSnowy"),
            "real snow caps bake GetColor instead of spring live-tint");
        c.True(mod.Contains("return (bakedColor, (byte)LodTintRegistry.SlotNone, true)"),
            "discover bake returns FlagBaked palette row");
        c.False(mod.Contains("Baked=false ALWAYS"),
            "DescribePalette no longer always returns live-tint path");
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
