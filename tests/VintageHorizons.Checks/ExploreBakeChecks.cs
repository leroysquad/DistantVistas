namespace DistantVistas.Checks;

public static class ExploreBakeChecks
{
    public static void Run(Check c)
    {
        Budget(c);
        PipelineHook(c);
        ShaderSafetyNet(c);
        PerChannelLavenderGuard(c);
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
    }

    static void ShaderSafetyNet(Check c)
    {
        string vsh = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "assets", "distantvistas", "shaders", "lodterrain.vsh"));
        c.True(vsh.Contains("Luminance scale preserves topsoil hue"),
            "vertex shader documents luminance climate shift");
        c.True(vsh.Contains("localLum / max(keepLum"),
            "vertex shader uses luminance scale not per-channel ratio");
        c.True(!vsh.Contains("localCl.rgb / keepRgb"),
            "vertex shader no longer multiplies per-channel local/keep ratio");
    }

    static void PerChannelLavenderGuard(Check c)
    {
        float sr = 0.50f, sg = 0.70f, sb = 0.20f;
        float kr = 0.50f, kg = 0.80f, kb = 0.20f;
        float lr = 0.62f, lg = 0.52f, lb = 0.18f;
        float oldR = sr * LodClimateField.SafeRatio(lr, kr);
        float oldG = sg * LodClimateField.SafeRatio(lg, kg);
        LodClimateField.ApplyLocalClimate(
            sr, sg, sb, kr, kg, kb, lr, lg, lb, out float nR, out float nG, out float _);
        c.True(nG / nR > oldG / oldR,
            "luminance climate shift is greener than per-channel ratio (lavender guard)");
    }
}
