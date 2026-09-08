namespace DistantVistas.Checks;

public static class PlayModeBakeBudgetChecks
{
    public static void Run(Check c)
    {
        Constants(c);
        Wiring(c);
    }

    static void Constants(Check c)
    {
        c.True(PlayModeBakeBudget.BaseExploreWallMs <= 3.0,
            "play-mode GetColor wall budget stays below notice (~2 ms/tick baseline)");
        c.True(PlayModeBakeBudget.BaseExploreColumns < LodExploreBake.ColumnsPerDrain,
            "play-mode paints fewer columns per drain than catch-up overlay");
        c.True(PlayModeBakeBudget.BaseMeshSchedules < 12,
            "play-mode mesh schedules below full catch-up rate");
        c.True(PlayModeBakeBudget.IdleMultiplier > 1.5,
            "paused/idle allows higher background budget");
        c.True(PlayModeBakeBudget.MotionPenalty < 1.0,
            "fast movement cuts background budget");
    }

    static void Wiring(Check c)
    {
        string pipeline = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Lod", "LodPipeline.cs"));
        c.True(pipeline.Contains("PlayModeBakeBudget.Compute"),
            "pipeline computes play-mode budget each tick");
        c.True(pipeline.Contains("DrainExploreBake(captureBacklog, playBudget)"),
            "explore drain receives play budget");

        string explore = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodExploreBake.cs"));
        c.True(explore.Contains("HandoffFromLogin"),
            "overlay paint queue handoffs to explore bake on soft-release");
        c.True(explore.Contains("ReprioritizeNear"),
            "explore bake reorders pending near player on motion");
        c.True(explore.Contains("SeedUnfinishedSections"),
            "soft-release seeds resident live-tint L0 for background fill");

        string login = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBake.cs"));
        c.False(login.Contains("ExploreBake.Clear()"),
            "successful overlay release does not drop explore bake queue");
        c.True(login.Contains("HandoffFromLogin"),
            "login release hands off scoutReady to explore bake");

        string mod = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "DistantVistasModSystem.cs"));
        c.True(mod.Contains("PlayModeBakeBudget.NotePlayerFrame"),
            "mod tracks player motion for budget tiers");
        c.True(mod.Contains("AllowFrontierScout"),
            "frontier scout yields when play budget is tight");

        string renderer = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodTerrainRenderer.cs"));
        c.True(renderer.Contains("PlayModeBakeBudget.Last.MeshUploads"),
            "renderer throttles mesh uploads during play-mode background fill");
    }
}
