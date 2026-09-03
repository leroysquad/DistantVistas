namespace DistantVistas;

/// <summary>
/// The one field we need out of another LOD mod's own config file. Nothing else is
/// declared on purpose: the JSON reader leaves unknown fields alone, and a file without
/// an "Enabled" field keeps this default, so anything we do not recognise reads as on.
/// </summary>
public class OtherLodModSwitch
{
    public bool Enabled = true;
}

/// <summary>
/// Which other LOD mod, if any, is actually drawing distant terrain.
///
/// Being loaded is not the same as drawing, and the difference is not a corner case.
/// Farseer, ChunkLOD and TopoHorizon are all Universal with requiredOnClient, so a
/// server that runs one makes every client load it, and the game downloads it for the
/// player if they do not have it. A player who then switches it off in its own settings
/// is left with no distant terrain at all if we stay idle as well. That is what was
/// reported against 0.2.0.
///
/// So where a mod keeps its switch somewhere we can read, we read it. Where it does not,
/// we defer. Being wrong in that direction costs only our own terrain; being wrong the
/// other way puts two mods on the same ground, fighting over the camera far plane.
/// </summary>
public static class OtherLodMods
{
    /// <summary>
    /// Mod id, and the mod's own client config file when its switch can be read from one.
    ///
    /// Farseer's dialog writes "Enabled" into farseer-client.json. Farseer is a
    /// companion, not a defer target: we read the file only so the log can say
    /// whether it is actually drawing behind us.
    ///
    /// ChunkLOD and TopoHorizon expose no config file we could find, so they get null and
    /// we keep deferring to them.
    ///
    /// Every entry must name a mod that actually RENDERS distant terrain. Vistas Beyond
    /// was listed here once, guessed from its name, and it does not belong: it is a
    /// server-side worldgen config mod (it adjusts landforms.json) and draws nothing, so
    /// there is no far plane to fight over. A player who installed it in singleplayer
    /// still had it show up as "loaded", and this mod went idle over a conflict that
    /// cannot exist - reported from the field, and the exact pairing (dramatic terrain
    /// plus long views) that both mods' users want.
    ///
    /// Komet is the same kind of miss: a client Harmony patch of vanilla's visibility
    /// sweep, occlusion, and chunk tesselation queue. It claims byte-identical triangles
    /// to vanilla and replaces no shaders. It does not draw far terrain, so listing it
    /// here would idle Distant Vistas for no conflict. It can help FPS at high vanilla
    /// view distance and close vanilla chunk-border holes while they stream in; it does
    /// not fill Distant Vistas skip-disc holes or paint snow.
    /// </summary>
    public static readonly (string ModId, string? SwitchFile)[] Known =
    {
        ("farseer", "farseer-client.json"),
        ("chunklod", null),
        ("topohorizon", null),
    };

    /// <summary>Background LOD. We draw with it. It does not idle us.</summary>
    public static bool IsCompanion(string modId) =>
        string.Equals(modId, "farseer", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The first loaded mod that is still drawing (never a companion), every loaded
    /// mod we found switched off, and every companion that is on. <paramref name="switchedOn"/>
    /// takes a config file name and returns null when the file is missing or unreadable,
    /// which counts as on.
    /// </summary>
    public static (string? Drawing, string[] SwitchedOff, string[] Companions) Inspect(
        Func<string, bool> isLoaded, Func<string, bool?> switchedOn)
    {
        string? drawing = null;
        var switchedOff = new List<string>();
        var companions = new List<string>();

        foreach ((string modid, string? switchFile) in Known)
        {
            if (!isLoaded(modid)) continue;

            if (switchFile != null && switchedOn(switchFile) == false)
            {
                switchedOff.Add(modid);
                continue;
            }

            if (IsCompanion(modid))
            {
                companions.Add(modid);
                continue;
            }

            // Keep going rather than return, so the report names every mod that is
            // installed, not just the first one that stops us.
            drawing ??= modid;
        }

        return (drawing, switchedOff.ToArray(), companions.ToArray());
    }
}
