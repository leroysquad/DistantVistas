namespace VintageHorizons;

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
    /// Farseer's dialog writes "Enabled" into farseer-client.json, and Farseer rewrites
    /// that file every time it loads, so the value on disk is always current. Verified
    /// against Farseer 1.4.0 by writing a one-field file and reading back what it stored.
    ///
    /// ChunkLOD and TopoHorizon expose no config file we could find, so they get null and
    /// we keep deferring to them. VistasBeyond is "side": "server", so it is never in a
    /// client's mod list at all; it stays listed because installing it client-side is not
    /// something we can rule out.
    /// </summary>
    public static readonly (string ModId, string? SwitchFile)[] Known =
    {
        ("farseer", "farseer-client.json"),
        ("chunklod", null),
        ("vistasbeyond", null),
        ("topohorizon", null),
    };

    /// <summary>
    /// The first loaded mod that is still drawing, plus every loaded mod we found
    /// switched off. <paramref name="switchedOn"/> takes a config file name and returns
    /// null when the file is missing or unreadable, which counts as on.
    /// </summary>
    public static (string? Drawing, string[] SwitchedOff) Inspect(
        Func<string, bool> isLoaded, Func<string, bool?> switchedOn)
    {
        string? drawing = null;
        var switchedOff = new List<string>();

        foreach ((string modid, string? switchFile) in Known)
        {
            if (!isLoaded(modid)) continue;

            if (switchFile != null && switchedOn(switchFile) == false)
            {
                switchedOff.Add(modid);
                continue;
            }

            // Keep going rather than return, so the report names every mod that is
            // installed, not just the first one that stops us.
            drawing ??= modid;
        }

        return (drawing, switchedOff.ToArray());
    }
}
