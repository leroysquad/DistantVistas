using DistantVistas;

namespace DistantVistas.Checks;

/// <summary>
/// Whether to stay idle for another LOD mod.
///
/// 0.2.0 tested only whether the other mod was loaded, and a player on a Farseer server
/// cannot avoid loading Farseer: the game requires it to join and downloads it if needed.
/// That player had switched Farseer off in its own dialog and used this mod instead, and
/// the release took that away from them, leaving no distant terrain from either mod.
///
/// These cases pin the asymmetry that fix rests on. "Cannot tell" has to mean "defer",
/// because being wrong that way costs only our own terrain, while being wrong the other
/// way puts two mods on the same ground.
/// </summary>
public static class DeferralChecks
{
    public static void Run(Check c)
    {
        Table(c);
        Decisions(c);
        Reporting(c);
        SwitchFileShapes(c);
    }

    /// <summary>
    /// What the reader makes of the file itself. Every one of these is a file a player can
    /// actually have: never run the mod, ran it once, edited it by hand and broke it.
    /// Only an explicit false may let us draw, because everything else is a guess.
    /// </summary>
    static void SwitchFileShapes(Check c)
    {
        // The default is what an empty or partial file leaves behind, and it has to be
        // "on". A default of false would make an unreadable file look like consent.
        c.True(new OtherLodModSwitch().Enabled, "an unspecified switch reads as on");

        // Our own default decides what happens to everyone upgrading from 0.2.0, whose
        // config file has no such field at all: the reader leaves the field alone, so the
        // class default is the whole answer. True here would silently start every one of
        // those players drawing over their server's LOD mod.
        c.False(new DistantVistasConfig().IgnoreOtherLodMods,
            "an upgraded config defers rather than overriding");

        // Farseer is a companion: on/missing/corrupt never idles us.
        c.True(Drawing(Only("farseer"), _ => true) is null, "Farseer on still lets us draw");
        c.True(Drawing(Only("farseer"), _ => false) is null, "Farseer off still lets us draw");
        c.True(Drawing(Only("farseer"), _ => null) is null,
            "a missing Farseer config still lets us draw");
        c.True(Drawing(Only("farseer"), _ => null) is null,
            "a corrupt Farseer file still lets us draw");

        // ChunkLOD has no switch we can read. Cannot tell, so defer.
        c.Eq("chunklod", Drawing(Only("chunklod"), _ => true), "chunklod still defers");
        c.Eq("chunklod", Drawing(Only("chunklod"), _ => null),
            "a file that could not be read at all defers for non-companions");
    }

    static void Table(Check c)
    {
        // The list is the mod's whole idea of competition. Losing an entry silently
        // re-enables the z-fighting the deferral exists to prevent.
        var ids = OtherLodMods.Known.Select(k => k.ModId).ToArray();
        c.True(ids.Contains("farseer"), "farseer is a known LOD mod");
        c.True(ids.Contains("chunklod"), "chunklod is a known LOD mod");
        c.True(ids.Contains("topohorizon"), "topohorizon is a known LOD mod");
        c.Eq(ids.Length, ids.Distinct().Count(), "no mod id is listed twice");

        // Pinned as ABSENT, because it was present once, on a guess from the name.
        // Vistas Beyond is "side": "Server" and draws nothing: it is a worldgen config
        // mod (landforms.json), so there is no far plane to fight over. Deferring to it
        // cost a player all distant terrain for no conflict at all - reported from the
        // field. Every entry in this table must name a mod that actually RENDERS.
        c.False(ids.Contains("vistasbeyond"),
            "vistasbeyond does not draw, so it is not deferred to");
        c.False(ids.Contains("komet"),
            "komet is a vanilla render-loop patch, not an LOD renderer");

        // The file name is not cosmetic: it is what we hand to LoadModConfig, and a typo
        // reads as "file missing", which silently restores the 0.2.0 behaviour.
        c.Eq("farseer-client.json", OtherLodMods.Known.First(k => k.ModId == "farseer").SwitchFile,
            "farseer's switch is read from the file its own dialog writes");

        // Recorded as unknown rather than guessed. A wrong file name here would be worse
        // than none, because it would read as "switched off" and let us draw over them.
        c.True(OtherLodMods.Known.First(k => k.ModId == "chunklod").SwitchFile is null,
            "chunklod has no switch file we can read");
        c.True(OtherLodMods.Known.First(k => k.ModId == "topohorizon").SwitchFile is null,
            "topohorizon has no switch file we can read");
    }

    static void Decisions(Check c)
    {
        c.True(Drawing(loaded: None, switches: AllOn) is null,
            "nothing installed means nothing to defer to");

        c.True(Drawing(loaded: Only("farseer"), switches: AllOn) is null,
            "Farseer is a companion: it does not idle us");
        c.True(OtherLodMods.IsCompanion("farseer"), "farseer is the background companion");
        c.False(OtherLodMods.IsCompanion("chunklod"), "chunklod is not a companion");

        var farseerOn = OtherLodMods.Inspect(Only("farseer"), AllOn);
        c.Eq("farseer", farseerOn.Companions.FirstOrDefault(), "on Farseer is reported as companion");
        c.True(farseerOn.Drawing is null, "companion Farseer is not a defer target");

        // The reported defect, and the reason for the whole change.
        c.True(Drawing(loaded: Only("farseer"), switches: AllOff) is null,
            "an installed mod that is switched off does not stop us");

        // Missing Farseer config counts as on, but Farseer still does not idle us.
        var farseerMissing = OtherLodMods.Inspect(Only("farseer"), _ => null);
        c.True(farseerMissing.Drawing is null, "a missing Farseer config does not idle us");
        c.Eq("farseer", farseerMissing.Companions.FirstOrDefault(),
            "a missing Farseer config still counts as a companion");

        // Same rule for a file we could read but that carries no verdict.
        c.Eq("chunklod", Drawing(loaded: Only("chunklod"), switches: AllOff),
            "a mod with no readable switch stops us however the others are set");

        // Two installed, one off: the one still drawing is the one that stops us, and it
        // must be found even though it is not first in the table.
        c.Eq("chunklod",
            Drawing(loaded: name => name is "farseer" or "chunklod",
                    switches: file => file == "farseer-client.json" ? false : null),
            "a switched-off mod does not hide a switched-on one behind it");

        // The switch file is only ever consulted for the mod it belongs to.
        var asked = new List<string>();
        OtherLodMods.Inspect(Only("farseer"), file => { asked.Add(file); return true; });
        c.Eq(1, asked.Count, "exactly one switch file is read when one mod is installed");
        c.Eq("farseer-client.json", asked[0], "and it is that mod's own file");

        // Nothing is read for a mod that is not installed: the file may well exist, left
        // behind by a mod the player removed.
        var askedNone = new List<string>();
        OtherLodMods.Inspect(None, file => { askedNone.Add(file); return false; });
        c.Eq(0, askedNone.Count, "no switch file is read when no LOD mod is installed");
    }

    static void Reporting(Check c)
    {
        // The log line that tells a player we noticed their setting. Without it, a player
        // whose other mod is off has no way to tell this rule from the old behaviour.
        (string? drawing, string[] off, string[] offCompanions) = OtherLodMods.Inspect(Only("farseer"), AllOff);
        c.True(drawing is null, "the switched-off mod does not stop us");
        c.Eq(1, off.Length, "the switched-off mod is reported");
        // Indexed only after the length holds: a regression here empties the array, and a
        // crashing check reports nothing about the other cases behind it.
        c.Eq("farseer", off.FirstOrDefault(), "and it is named");
        c.Eq(0, offCompanions.Length, "a switched-off Farseer is not a companion");

        (string? stillDrawing, string[] noneOff, string[] onCompanions) = OtherLodMods.Inspect(Only("farseer"), AllOn);
        c.True(stillDrawing is null, "a switched-on Farseer does not idle us");
        c.Eq(0, noneOff.Length, "and nothing is reported as switched off");
        c.Eq("farseer", onCompanions.FirstOrDefault(), "on Farseer is the companion");

        // A mod with no readable switch is not reported as switched off, because we never
        // established that it was.
        (_, string[] unknownOff, _) = OtherLodMods.Inspect(Only("chunklod"), AllOff);
        c.Eq(0, unknownOff.Length, "a mod with no readable switch is never reported as off");
    }

    static string? Drawing(Func<string, bool> loaded, Func<string, bool?> switches)
        => OtherLodMods.Inspect(loaded, switches).Drawing;

    static readonly Func<string, bool> None = _ => false;
    static Func<string, bool> Only(string modid) => name => name == modid;
    static readonly Func<string, bool?> AllOn = _ => true;
    static readonly Func<string, bool?> AllOff = _ => false;
}
