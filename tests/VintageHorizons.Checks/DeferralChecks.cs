namespace VintageHorizons.Checks;

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
        c.False(new VintageHorizonsConfig().IgnoreOtherLodMods,
            "an upgraded config defers rather than overriding");

        // The shapes, as the reader sees them after LoadModConfig has had its turn.
        c.Eq("farseer", Drawing(Only("farseer"), _ => true), "an explicit true defers");
        c.True(Drawing(Only("farseer"), _ => false) is null, "an explicit false draws");
        c.Eq("farseer", Drawing(Only("farseer"), _ => null),
            "a file that could not be read at all defers");

        // A parse failure reaches the decision as null, never as a thrown exception and
        // never as false: ReadOtherModSwitch catches and returns null. If that ever
        // changed to a rethrow, StartClientSide would die and take the whole mod with it.
        c.Eq("farseer", Drawing(Only("farseer"), _ => null),
            "a corrupt file defers rather than deciding for the player");
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

        c.Eq("farseer", Drawing(loaded: Only("farseer"), switches: AllOn),
            "an installed and switched-on mod stops us");

        // The reported defect, and the reason for the whole change.
        c.True(Drawing(loaded: Only("farseer"), switches: AllOff) is null,
            "an installed mod that is switched off does not stop us");

        // A player who has never run Farseer has no file for it. Cannot tell, so defer.
        c.Eq("farseer", Drawing(loaded: Only("farseer"), switches: _ => null),
            "a missing config file counts as switched on");

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
        (string? drawing, string[] off) = OtherLodMods.Inspect(Only("farseer"), AllOff);
        c.True(drawing is null, "the switched-off mod does not stop us");
        c.Eq(1, off.Length, "the switched-off mod is reported");
        // Indexed only after the length holds: a regression here empties the array, and a
        // crashing check reports nothing about the other cases behind it.
        c.Eq("farseer", off.FirstOrDefault(), "and it is named");

        (string? stillDrawing, string[] noneOff) = OtherLodMods.Inspect(Only("farseer"), AllOn);
        c.Eq("farseer", stillDrawing, "a switched-on mod stops us");
        c.Eq(0, noneOff.Length, "and nothing is reported as switched off");

        // A mod with no readable switch is not reported as switched off, because we never
        // established that it was.
        (_, string[] unknownOff) = OtherLodMods.Inspect(Only("chunklod"), AllOff);
        c.Eq(0, unknownOff.Length, "a mod with no readable switch is never reported as off");
    }

    static string? Drawing(Func<string, bool> loaded, Func<string, bool?> switches)
        => OtherLodMods.Inspect(loaded, switches).Drawing;

    static readonly Func<string, bool> None = _ => false;
    static Func<string, bool> Only(string modid) => name => name == modid;
    static readonly Func<string, bool?> AllOn = _ => true;
    static readonly Func<string, bool?> AllOff = _ => false;
}
