using System.Text.RegularExpressions;

namespace DistantVistas.Checks;

/// <summary>
/// Invariants that live across file boundaries, where no compiler or runtime check can
/// reach. Everything here reads committed files off disk and touches no game type at all.
/// </summary>
public static class StaticAssetChecks
{
    public static void Run(Check c)
    {
        AsciiOnly(c);
        TintSlotAgreement(c);
        AlphaPacking(c);
        VersionAgreement(c);
        LiveSeasonClock(c);
        NoCameraLockedNearDiscard(c);
    }

    /// <summary>
    /// Shader source must be pure ASCII. OpenTK passes managed strings to GL by handing
    /// over a char count where the driver reads utf8 bytes, so a single non-ASCII
    /// character silently truncates the source by (utf8 bytes - chars) characters. The
    /// tail of the shader just disappears; there is no error, only wrong output.
    ///
    /// Scans the whole asset tree rather than a list of known shaders, so a file added
    /// later is covered without anyone remembering to add it here.
    /// </summary>
    static void AsciiOnly(Check c)
    {
        string assets = Path.Combine(GameAssemblies.RepoRoot, "DistantVistas", "assets");
        c.True(Directory.Exists(assets), "asset directory exists");

        var offenders = new List<string>();
        int scanned = 0;

        foreach (string path in Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories))
        {
            // Binary assets are exempt: a PNG is full of high bytes by definition.
            if (Path.GetExtension(path) is ".png" or ".jpg" or ".ogg" or ".wav") continue;

            scanned++;
            byte[] bytes = File.ReadAllBytes(path);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] >= 0x80)
                {
                    offenders.Add($"{Path.GetRelativePath(GameAssemblies.RepoRoot, path)} byte {i} = 0x{bytes[i]:X2}");
                    break;
                }
            }
        }

        c.True(scanned > 0, "found asset files to scan");
        c.SeqEq(Array.Empty<string>(), offenders, $"all {scanned} text assets are pure ASCII");
    }

    /// <summary>
    /// The shaders carry their own `const int TINT_SLOTS` because this game version offers
    /// no way to inject a #define, and a mismatch decodes water as opaque and thin plants
    /// as water with no compile error.
    ///
    /// This used to be guarded at shader load by comparing MaxSlots against a second C#
    /// constant that mirrored the shader's value by hand - two constants in the same file,
    /// which cannot detect a shader being edited at all. The compiler said as much: that
    /// branch raised CS0162, unreachable code. Both the mirror and the dead guard are gone.
    ///
    /// Reading the shader files is the only check that can actually close it, and it also
    /// catches the .vsh and .fsh disagreeing with each other, which nothing did before.
    /// </summary>
    static void TintSlotAgreement(Check c)
    {
        string shaders = Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "assets", "distantvistas", "shaders");

        var found = new Dictionary<string, int>();
        foreach (string path in Directory.EnumerateFiles(shaders, "*.*sh"))
        {
            Match m = Regex.Match(File.ReadAllText(path), @"const\s+int\s+TINT_SLOTS\s*=\s*(\d+)\s*;");
            if (m.Success) found[Path.GetFileName(path)] = int.Parse(m.Groups[1].Value);
        }

        c.True(found.ContainsKey("lodterrain.vsh"), "lodterrain.vsh declares TINT_SLOTS");
        c.True(found.ContainsKey("lodterrain.fsh"), "lodterrain.fsh declares TINT_SLOTS");

        foreach ((string file, int value) in found)
        {
            c.Eq(LodTintRegistry.MaxSlots, value, $"{file} TINT_SLOTS matches LodTintRegistry.MaxSlots");
        }

        c.Eq(1, found.Values.Distinct().Count(), "the vertex and fragment shaders agree with each other");
    }

    /// <summary>
    /// LodMesher packs the tint slot into a vertex alpha byte in three bands: opaque at
    /// slot, water at MaxSlots + slot, thin at MaxSlots * 2 + slot. Alpha is a byte, so the
    /// largest encodable value is MaxSlots * 3 - 1. Raise MaxSlots past 85 and the thin band
    /// wraps into the opaque band with no error anywhere - thin plants would render as solid
    /// terrain of an arbitrary tint.
    /// </summary>
    static void AlphaPacking(Check c)
    {
        c.True(LodTintRegistry.MaxSlots * 3 <= 256,
            $"tint bands fit in a byte (MaxSlots {LodTintRegistry.MaxSlots} * 3 <= 256)");
    }

    /// <summary>
    /// scripts/package.sh names the release zip from modinfo.json, while the assembly
    /// identity comes from the csproj. Drift between them ships an artifact whose filename
    /// disagrees with the version the game reports.
    /// </summary>
    static void VersionAgreement(Check c)
    {
        CheckPair(c, "DistantVistas", Path.Combine("DistantVistas", "DistantVistas.csproj"),
            Path.Combine("DistantVistas", "modinfo.json"));
        CheckPair(c, "DistantVistasBench",
            Path.Combine("bench", "DistantVistasBench", "DistantVistasBench.csproj"),
            Path.Combine("bench", "DistantVistasBench", "modinfo.json"));
    }

    static void CheckPair(Check c, string label, string csprojRel, string modinfoRel)
    {
        string csproj = Path.Combine(GameAssemblies.RepoRoot, csprojRel);
        string modinfo = Path.Combine(GameAssemblies.RepoRoot, modinfoRel);

        if (!File.Exists(csproj) || !File.Exists(modinfo))
        {
            c.True(false, $"{label}: both csproj and modinfo.json exist");
            return;
        }

        Match fromCsproj = Regex.Match(File.ReadAllText(csproj), @"<Version>([^<]+)</Version>");
        Match fromModinfo = Regex.Match(File.ReadAllText(modinfo), @"""version""\s*:\s*""([^""]+)""");

        c.True(fromCsproj.Success, $"{label}: csproj declares a Version");
        c.True(fromModinfo.Success, $"{label}: modinfo.json declares a version");

        if (fromCsproj.Success && fromModinfo.Success)
        {
            c.Eq(fromCsproj.Groups[1].Value, fromModinfo.Groups[1].Value,
                $"{label}: csproj Version matches modinfo.json version");
        }
    }

    /// <summary>
    /// Far season is a live shader clock, like night ambient. If these tokens
    /// disappear, backing out of vanilla range will snap autumn to grey-green again.
    /// </summary>
    static void LiveSeasonClock(Check c)
    {
        string vsh = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "assets", "distantvistas", "shaders", "lodterrain.vsh"));
        c.True(vsh.Contains("uniform float seasonRel"), "lodterrain.vsh has live seasonRel");
        c.True(vsh.Contains("seasonTints"), "lodterrain.vsh has seasonTints");
        c.True(vsh.Contains("band != 1"), "lodterrain.vsh skips season on water");
        c.True(vsh.Contains("seasonTempX"), "lodterrain.vsh has seasonTempX for vanilla seasonWeight");
    }

    /// <summary>
    /// dist is camera-relative XZ. Discarding dist &lt; 0 cuts a circle that
    /// moves with the player through hills (sky outline, chopped vanilla ring,
    /// square seam in front). Near LOD must stay and sit under vanilla by
    /// sinking, not by punching sky.
    /// </summary>
    static void NoCameraLockedNearDiscard(Check c)
    {
        string fsh = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "assets", "distantvistas", "shaders", "lodterrain.fsh"));
        string vsh = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "assets", "distantvistas", "shaders", "lodterrain.vsh"));

        c.False(Regex.IsMatch(fsh, @"dist\s*<\s*0"),
            "lodterrain.fsh does not discard the camera skip disc");
        c.True(vsh.Contains("lookDown"),
            "lodterrain.vsh receives look-down so the near sink can let go");
        c.True(Regex.IsMatch(vsh, @"max\s*\(\s*0\.0\s*,\s*dist\s*\)"),
            "lodterrain.vsh floors dist at 0 (GLSL clamp(dist, 0, dist) is undefined when dist < 0)");
    }
}
