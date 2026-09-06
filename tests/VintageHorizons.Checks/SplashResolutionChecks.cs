namespace DistantVistas.Checks;

public static class SplashResolutionChecks
{
    public static void Run(Check c)
    {
        CoverFitUltrawide(c);
        CoverFitPortrait(c);
        SourceHooks(c);
    }

    static void CoverFitUltrawide(Check c)
    {
        // Playtest failure size vs 1920×1080 authored backdrop.
        var fit = LodLoginSplashLayout.CoverFit(2560, 1369, 1920, 1080);
        c.True(fit.Width >= 2560 && fit.Height >= 1369,
            "cover-fit fills ultrawide frame");
        c.True(Math.Abs(fit.X) < 1f,
            "cover-fit is horizontally centered on ultrawide");
        c.Near(2560, fit.Width, 1f, "cover-fit width spans frame on ultrawide");
    }

    static void CoverFitPortrait(Check c)
    {
        var fit = LodLoginSplashLayout.CoverFit(1080, 1920, 1920, 1080);
        c.True(fit.Height >= 1920, "cover-fit fills tall frame");
        c.True(fit.Width >= 1080, "cover-fit fills narrow frame");
    }

    static void SourceHooks(Check c)
    {
        string screen = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginBakeScreenRenderer.cs"));
        c.True(screen.Contains("LodLoginSplashLayout.CoverFit"),
            "splash draws backdrop with cover-fit layout");
        c.True(screen.Contains("TryDrawSolidQuad"),
            "splash tiles large opaque quads for ultrawide frames");
        c.True(screen.Contains("OpaqueTileSize"),
            "splash uses configurable opaque tile size");

        string layout = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "Render", "LodLoginSplashLayout.cs"));
        c.True(layout.Contains("object-fit: cover"),
            "splash layout documents cover-fit semantics");
    }
}
