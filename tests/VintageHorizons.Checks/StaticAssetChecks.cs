using System.Text.RegularExpressions;
using DistantVistas;

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
        FarseerOverlay(c);
        NoFakeOptionalDependencies(c);
        LoginOverlayAssets(c);
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
    /// LodMesher packs the tint slot into a vertex alpha byte in four bands: opaque at
    /// slot, water at MaxSlots + slot, thin at MaxSlots * 2 + slot, baked at MaxSlots * 3.
    /// Alpha is a byte, so the largest encodable value is MaxSlots * 4 - 1.
    /// </summary>
    static void AlphaPacking(Check c)
    {
        c.True(LodTintRegistry.MaxSlots * 4 <= 256,
            $"tint bands fit in a byte (MaxSlots {LodTintRegistry.MaxSlots} * 4 <= 256)");
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
        CheckPair(c, "VintageHorizonsBench",
            Path.Combine("bench", "VintageHorizonsBench", "VintageHorizonsBench.csproj"),
            Path.Combine("bench", "VintageHorizonsBench", "modinfo.json"));
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
        c.True(vsh.Contains("band == 3"), "lodterrain.vsh bypasses live tint on baked band");
        c.False(vsh.Contains("uniform float seasonTempX"),
            "lodterrain.vsh does not drive vegetation with a global seasonTempX");
        c.True(vsh.Contains("keepClimateLow") && vsh.Contains("climateLow00"),
            "lodterrain.vsh looks up the coarse climate field at vertex XZ");
        c.True(vsh.Contains("localCl.a"),
            "lodterrain.vsh seasonWeight uses the local climate temperature");
        c.False(vsh.Contains("(yLevel - tintYLow) * 1.5"),
            "lodterrain.vsh does not add canopy altitude onto worldgen seasonTempX");
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
        c.False(fsh.Contains("skipR"),
            "lodterrain.fsh does not punch a view-distance sphere (sky circle)");
        c.True(vsh.Contains("grassPullWeight"), "lodterrain.vsh tags vegetation slots for coarse plant-pull");
        c.True(fsh.Contains("COARSE_PLATE_COLUMNS"), "lodterrain.fsh gates plate noise to coarse LOD");
        c.True(fsh.Contains("grassPullWeight"), "lodterrain.fsh receives grassPullWeight for coarse plant-pull");
        c.True(fsh.Contains("bool baked = band == 3"), "lodterrain.fsh skips snow/noise on baked band");
        c.True(fsh.Contains("baked ? vertexColor.rgb"),
            "lodterrain.fsh uses stored RGB directly on baked band");
        c.True(fsh.Contains("band == 1") && fsh.Contains("0.18, 0.38, 0.50"),
            "lodterrain.fsh recolors foam-white water to lake blue");
        c.True(vsh.Contains("lookDown"),
            "lodterrain.vsh receives look-down so the near sink can let go");
        c.True(Regex.IsMatch(vsh, @"max\s*\(\s*0\.0\s*,\s*dist\s*\)"),
            "lodterrain.vsh floors dist at 0 (GLSL clamp(dist, 0, dist) is undefined when dist < 0)");
        c.False(Regex.IsMatch(vsh, @"farViewDistance\s*-\s*distStart\s*-\s*512"),
            "lodterrain.vsh dist == 1 is the real far rim, not 512 blocks inside the land we hold");
        c.True(fsh.Contains("if (dist > 1.0) discard;"),
            "lodterrain.fsh keeps the far discard as the one true far clip");
        c.False(Regex.IsMatch(fsh, @"float\s+flat\b"),
            "lodterrain.fsh does not use GLSL reserved identifier float flat (C7537)");
        c.True(fsh.Contains("float flatness"),
            "lodterrain.fsh names low-ground mist flatness");
        c.True(fsh.Contains("lowMist"),
            "lodterrain.fsh keeps the 1.0.25 low-ground mist wash");
        c.True(fsh.Contains("flatness * 0.03"),
            "lodterrain.fsh thins near-ground mist so login does not wash desert into a white blob");
        c.True(fsh.Contains("lowMistCol"),
            "lodterrain.fsh lightens low-ground mist toward pale fog");

        // Gap fill: the renderer draws a parent mesh clipped to one child
        // footprint. Both halves of that contract live in the shaders.
        c.True(vsh.Contains("out vec2 localXZ") && vsh.Contains("localXZ = vertexPositionIn.xz"),
            "lodterrain.vsh passes section-local XZ for the gap clip");
        c.True(fsh.Contains("in vec2 localXZ") && fsh.Contains("uniform vec4 clipRect"),
            "lodterrain.fsh receives localXZ and the clipRect uniform");
        c.True(Regex.IsMatch(fsh, @"localXZ\.x\s*<\s*clipRect\.x") && Regex.IsMatch(fsh, @"localXZ\.y\s*>\s*clipRect\.w"),
            "lodterrain.fsh discards outside the clip rectangle (minX, minZ, maxX, maxZ)");
    }

    /// <summary>
    /// We overlay Farseer's region shaders so their sky cylinder and
    /// bleach-to-sky tint are not in the player's view. No Harmony.
    /// </summary>
    static void FarseerOverlay(Check c)
    {
        string vsh = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "assets", "farseer", "shaders", "region.vsh"));
        string fsh = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "assets", "farseer", "shaders", "region.fsh"));
        c.True(FarseerShaderOverlay.OverlayActive,
            "Farseer overlay inject is on (visit-aware onset + mist silhouette)");
        // Visit-onset uniforms replace stock 0.785; keep a safe floor so the
        // spawn disc still rasterizes (see farseer-region.vsh).
        c.True(vsh.Contains("distStart < viewDistance * 0.5"),
            "farseer overlay keeps a half-VD floor so the spawn region can rasterize");
        c.False(vsh.Contains("distStart = 24.0"),
            "farseer overlay inner disc is not a 24-block hole under the player");
        c.False(vsh.Contains("distStart = viewDistance * 1.5"),
            "farseer overlay does not start at 1.5x (that discarded the spawn region)");
        c.False(vsh.Contains("distStart = viewDistance * 0.92"),
            "farseer overlay does not use the 0.92 inner start");
        c.False(Regex.IsMatch(vsh, @"farViewDistance\s*-\s*distStart\s*-\s*512"),
            "farseer overlay dist == 1 is the real far rim, not 512 blocks inside the hills");
        c.False(fsh.Contains("applySpheresFog"),
            "farseer overlay does not run sphere fog (sky ring)");
        c.True(fsh.Contains("clamp(skyTint, 0.0, 0.4)"),
            "farseer overlay clamps SkyTint so 5-10 cannot bleach the heightmap");
        c.True(fsh.Contains("min(colorTint.a, 0.28)"),
            "farseer overlay clamps ColorTint so slate wash cannot hide relief");
        c.True(fsh.Contains("smoothstep(0.82, 1.0, dist)"),
            "farseer overlay only mixes sky at the far rim");
        c.True(vsh.Contains("DV_FARSEER_OVERLAY") && fsh.Contains("DV_FARSEER_OVERLAY"),
            "farseer overlay carries a marker the boot log can see");
        c.False(vsh.Contains("yLevel > 340.0"),
            "farseer overlay does not sink heightmaps (that buried the silhouette)");
        c.False(fsh.Contains("0.35 * radial"),
            "farseer overlay does not discard overhead (that ate the heightmap disc)");
        c.False(fsh.Contains("terraColor.rgb *= 0.92"),
            "farseer overlay uses ridge inkAmt, not a global 0.92 multiply");
        c.True(fsh.Contains("mix(terraColor.rgb, mistCol, mist)"),
            "farseer overlay mixes paler mist for a misty silhouette");
        c.True(fsh.Contains("mist * 0.12"),
            "farseer overlay thins sky-mix so mist does not bleach the skyline");
        c.True(fsh.Contains("0.0, 0.38)"),
            "farseer overlay mist cap is thinner than the 1.0.25 0.62 wash");
        c.True(fsh.Contains("height01 * height01"),
            "farseer overlay darkens mountain tips so ridges read against sky");
        c.True(fsh.Contains("smokeGray"),
            "farseer overlay uses grayish smoke (gray tent body, black tips)");
        c.True(fsh.Contains("inkAmt"),
            "farseer overlay keeps a little far/top ink, not a hard wall");
        c.True(fsh.Contains("0.0, 0.24)"),
            "1.0.28 ridge-ink cap kept -- coverage must not flatten the silhouette to empty black");
        c.True(fsh.Contains("(1.0 - height01) * 0.06"),
            "farseer overlay cuts low-ground smoke so near terrain is not a white blob");
        c.False(fsh.Contains("onsetMist"),
            "farseer overlay is the 1.0.18 mist wash (no onsetMist lean cloud)");
        c.False(vsh.Contains("nearBand"),
            "farseer overlay does not lean the near rim (1.0.18 geometry)");
        c.False(fsh.Contains("terraColor.rgb *= 0.78"),
            "farseer overlay no longer uses the hard 0.78 ink darken");

        string overlayCs = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "src", "DistantVistasModSystem.cs"));
        c.False(overlayCs.Contains("capi.Shader.ReloadShaders"),
            "client system does not ReloadShaders after overlay (that reloads Farseer's zip)");
        c.False(overlayCs.Contains("RegisterFileShaderProgram"),
            "client system does not re-register Farseer's region program");
        c.False(overlayCs.Contains("RecompileRegion"),
            "client system does not Compile Farseer's live region program");

        string srcVsh = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "assets", "distantvistas", "shaders", "farseer-region.vsh"));
        string srcFsh = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "assets", "distantvistas", "shaders", "farseer-region.fsh"));
        c.Eq(vsh, srcVsh, "distantvistas domain vsh is the same overlay we inject");
        c.Eq(fsh, srcFsh, "distantvistas domain fsh is the same overlay we inject");
    }

    static void NoFakeOptionalDependencies(Check c)
    {
        string json = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "modinfo.json"));
        c.False(json.Contains("optionaldependencies"),
            "modinfo does not lie about optionaldependencies (VS has no such field)");
        c.False(json.Contains("\"farseer\":"),
            "modinfo does not require Farseer (companion only)");
    }

    /// <summary>
    /// Login visit-sweep overlay ships both GUI textures in the mod zip.
    /// </summary>
    static void LoginOverlayAssets(Check c)
    {
        string gui = Path.Combine(
            GameAssemblies.RepoRoot, "DistantVistas", "assets", "distantvistas", "textures", "gui");
        string backdrop = Path.Combine(gui, "login-backdrop.png");
        c.True(File.Exists(backdrop),
            "login backdrop is packaged at assets/distantvistas/textures/gui/login-backdrop.png");
        c.True(new FileInfo(backdrop).Length > 50000,
            "login backdrop is a real splash photo, not an 8 KB placeholder");
        c.True(File.Exists(Path.Combine(gui, "login-title-rainbow.png")),
            "login title is packaged at assets/distantvistas/textures/gui/login-title-rainbow.png");
    }
}
