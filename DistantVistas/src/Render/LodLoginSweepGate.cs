using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DistantVistas;

/// <summary>
/// Decides whether the login visit sweep overlay should run or the player can enter play
/// immediately with persisted canvases.
/// </summary>
public static class LodLoginSweepGate
{
    public readonly record struct Result(bool RunSweep, string Reason);

    public static Result Decide(
        ICoreClientAPI capi,
        LodWorld world,
        LodPipeline pipeline,
        IList<Block> blocks,
        Block? plantTintFallback,
        System.Func<Block, (int Color, LodUntintedShare Share)> untintedOf)
    {
        string worldId = LodWorldKey.For(capi.World);

        LodLoginSweepResume? resume = LodLoginSweepResume.TryLoad(capi);
        if (resume != null && resume.IsEligible(capi.World))
            return LogDecide(capi, world, blocks, null, -1, new Result(true, "resuming cancelled mid-sweep checkpoint"));

        if (resume != null)
            LodLoginSweepResume.Delete(capi);

        int visited = LodLoginSweep.VisitedL0Keys(world).Count();
        if (visited == 0)
            return LogDecide(capi, world, blocks, null, visited, new Result(true, "empty canvas needs bootstrap sweep"));

        LodLoginSweepComplete? complete = LodLoginSweepComplete.TryLoad(capi);

        // First successful sweep for this world must expand land (bootstrap), not
        // refresh a tiny walked set — decide run before miss-repair / revisit paths.
        if (complete == null
            || string.IsNullOrEmpty(complete.WorldId)
            || !string.Equals(complete.WorldId, worldId, StringComparison.Ordinal))
            return LogDecide(capi, world, blocks, complete, visited, new Result(true, "no successful sweep recorded yet for this world"));

        // Expire (30 in-game days, 30 real days, stale paint, or calendar month) before
        // miss-repair so leftovers cannot hide a recapture behind "still incomplete".
        string? expire = LodLoginSweepWindow.RecaptureReason(capi.World, complete);
        if (expire != null)
            return LogDecide(capi, world, blocks, complete, visited, new Result(true, expire));

        // Prefer skip when a successful sweep is still in-window and the canvas did not grow —
        // even if FindMisses reports leftovers. User intent: do not re-canvas the same world
        // within 30 in-game days or 30 real days of the stamped first day. Never skip across worlds (0.8.24).
        if (complete.VisitedKeyCount >= visited)
        {
            if (complete.WindowStartedUtcMs <= 0)
            {
                complete.WindowStartedUtcMs = LodLoginSweepWindow.NowUtcMs();
                complete.Save(capi);
            }
            return LogDecide(capi, world, blocks, complete, visited, new Result(false,
                "visited canvas complete within 30-day window (skip re-canvas)"));
        }

        List<LodLoginBakeAudit.Miss> misses = LodLoginBakeAudit.FindMisses(
            world, pipeline, blocks, plantTintFallback, untintedOf);
        if (misses.Count > 0)
            return LogDecide(capi, world, blocks, complete, visited,
                new Result(true, $"{misses.Count} visited region(s) still incomplete"));

        if (complete.VisitedKeyCount < visited)
            return LogDecide(capi, world, blocks, complete, visited,
                new Result(true, "visited canvas grew since last successful sweep"));

        return LogDecide(capi, world, blocks, complete, visited, new Result(false, "visited canvas complete within 30-day window"));
    }

    // #region agent log
    static Result LogDecide(
        ICoreClientAPI capi,
        LodWorld world,
        IList<Block> blocks,
        LodLoginSweepComplete? complete,
        int visited,
        Result result)
    {
        try
        {
            IGameCalendar cal = capi.World.Calendar;
            var pos = new Vintagestory.API.MathTools.BlockPos(
                (int)capi.World.Player.Entity.Pos.X,
                capi.World.SeaLevel,
                (int)capi.World.Player.Entity.Pos.Z);
            string nowSeason = LodLoginSweepResume.SeasonSlug(cal.GetSeason(pos));
            double days = cal.TotalDays;
            double savedDays = complete?.SavedTotalDays ?? -1;
            double gap = complete == null ? -1 : days - savedDays;
            bool within = complete != null
                && LodLoginSweepWindow.IsWithin(capi.World, complete.Season, complete.SavedTotalDays);
            bool seasonChanged = complete != null
                && !LodLoginSweepWindow.IsSameSeason(complete.Season, nowSeason);
            System.IO.File.AppendAllText(
                @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                "{\"sessionId\":\"40cccb\",\"runId\":\"season-2\",\"hypothesisId\":\"H-S-gate\",\"location\":\"LodLoginSweepGate.Decide\",\"message\":\"sweep-gate\",\"data\":{\"run\":"
                + (result.RunSweep ? "true" : "false")
                + ",\"reason\":\"" + result.Reason.Replace("\\", "\\\\").Replace("\"", "\\\"")
                + "\",\"nowSeason\":\"" + nowSeason
                + "\",\"savedSeason\":\"" + (complete?.Season ?? "")
                + "\",\"month\":" + cal.Month
                + ",\"year\":" + cal.Year
                + ",\"dayOfYear\":" + cal.DayOfYear.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"totalDays\":" + days.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                + ",\"savedDays\":" + savedDays.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                + ",\"gapDays\":" + gap.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                + ",\"within30\":" + (within ? "true" : "false")
                + ",\"seasonChanged\":" + (seasonChanged ? "true" : "false")
                + ",\"visited\":" + visited
                + ",\"savedVisited\":" + (complete?.VisitedKeyCount ?? -1)
                + ",\"paintRev\":" + (complete?.PaintRevision ?? -1)
                + ",\"needPaint\":" + LodSurfaceMix.PaintRevision
                + "},\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n");
            long nowUtc = LodLoginSweepWindow.NowUtcMs();
            long savedUtc = complete?.WindowStartedUtcMs ?? 0;
            long gapWall = complete == null || savedUtc <= 0 ? -1 : nowUtc - savedUtc;
            bool withinDay = complete != null
                && LodLoginSweepWindow.IsDayGapWithin(days, savedDays);
            bool withinWall = complete != null
                && LodLoginSweepWindow.IsWallGapWithin(nowUtc, savedUtc);
            System.IO.File.AppendAllText(
                @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                "{\"sessionId\":\"40cccb\",\"runId\":\"post-fix\",\"hypothesisId\":\"H-W-CLOCK\",\"location\":\"LodLoginSweepGate.Decide\",\"message\":\"window-clock\",\"data\":{\"run\":"
                + (result.RunSweep ? "true" : "false")
                + ",\"reason\":\"" + result.Reason.Replace("\\", "\\\\").Replace("\"", "\\\"")
                + "\",\"totalDays\":" + days.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                + ",\"savedDays\":" + savedDays.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                + ",\"gapDays\":" + gap.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                + ",\"nowUtc\":" + nowUtc
                + ",\"savedUtc\":" + savedUtc
                + ",\"gapWallMs\":" + gapWall
                + ",\"withinDay\":" + (withinDay ? "true" : "false")
                + ",\"withinWall\":" + (withinWall ? "true" : "false")
                + ",\"legacyUtc\":" + (complete != null && savedUtc <= 0 ? "true" : "false")
                + "},\"timestamp\":" + nowUtc + "}\n");
            System.IO.File.AppendAllText(
                @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                "{\"sessionId\":\"40cccb\",\"runId\":\"frost-1\",\"hypothesisId\":\"H-OVERLAY-SKIP\",\"location\":\"LodLoginSweepGate.Decide\",\"message\":\"frost-gate\",\"data\":{\"frostProbe\":1,\"run\":"
                + (result.RunSweep ? "true" : "false")
                + ",\"reason\":\"" + result.Reason.Replace("\\", "\\\\").Replace("\"", "\\\"")
                + "\",\"worldId\":\"" + LodWorldKey.For(capi.World)
                + "\",\"sections\":" + world.Sections.Count
                + ",\"nowSeason\":\"" + nowSeason
                + "\",\"savedSeason\":\"" + (complete?.Season ?? "")
                + "\",\"month\":" + cal.Month
                + ",\"paintRev\":" + (complete?.PaintRevision ?? -1)
                + ",\"needPaint\":" + LodSurfaceMix.PaintRevision
                + "},\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n");
            int dumped = 0;
            int leafEntries = 0;
            int pineEntries = 0;
            int whiteLeaves = 0;
            int grayLeaves = 0;
            int greenLeaves = 0;
            int bakedLeaves = 0;
            foreach (KeyValuePair<long, LodSection> kv in world.Sections)
            {
                if (LodWorld.KeyLevel(kv.Key) != 0) continue;
                LodSection section = kv.Value;
                for (int i = 0; i < section.Palette.Count; i++)
                {
                    LodPaletteEntry entry = section.Palette[i];
                    if (entry.BlockId <= 0 || entry.BlockId >= blocks.Count) continue;
                    string? path = blocks[entry.BlockId].Code?.Path;
                    if (path == null
                        || !(path.Contains("leaves", StringComparison.Ordinal)
                            || path.Contains("frosted", StringComparison.Ordinal)
                            || path.Contains("bush", StringComparison.Ordinal)))
                        continue;
                    leafEntries++;
                    bool pine = path.Contains("pine", StringComparison.Ordinal);
                    if (pine) pineEntries++;
                    if ((entry.Flags & LodPaletteEntry.FlagBaked) != 0) bakedLeaves++;
                    LodPaletteRepair.Channels(entry.Color, out int r, out int g, out int b, out int luma, out int chroma);
                    if (luma >= 180) whiteLeaves++;
                    if (chroma <= 40 && g <= r + 8) grayLeaves++;
                    if (g >= r + 12) greenLeaves++;
                    bool dump = dumped < 8 && (pine || dumped < 4);
                    if (!dump) continue;
                    dumped++;
                    System.IO.File.AppendAllText(
                        @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                        "{\"sessionId\":\"40cccb\",\"runId\":\"gray-1\",\"hypothesisId\":\"H-GRAY-3\",\"location\":\"LodLoginSweepGate.Decide\",\"message\":\"stored-leaf\",\"data\":{\"path\":\""
                        + path.Replace("\\", "/").Replace("\"", "'")
                        + "\",\"r\":" + r + ",\"g\":" + g + ",\"b\":" + b
                        + ",\"luma\":" + luma + ",\"chroma\":" + chroma
                        + ",\"baked\":" + ((entry.Flags & LodPaletteEntry.FlagBaked) != 0 ? "true" : "false")
                        + ",\"missingW\":" + (LodPaletteRepair.IsMissingTextureWhite(entry.Color) ? "true" : "false")
                        + ",\"brightCap\":" + (LodPaletteRepair.IsBrightCap(entry.Color) ? "true" : "false")
                        + ",\"skyMiss\":" + (LodPaletteRepair.IsMissingTextureSky(entry.Color) ? "true" : "false")
                        + "},\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n");
                }
            }
            System.IO.File.AppendAllText(
                @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                "{\"sessionId\":\"40cccb\",\"runId\":\"gray-1\",\"hypothesisId\":\"H-GRAY-3\",\"location\":\"LodLoginSweepGate.Decide\",\"message\":\"stored-leaf-tally\",\"data\":{\"dumped\":"
                + dumped + ",\"leafEntries\":" + leafEntries + ",\"pineEntries\":" + pineEntries
                + ",\"whiteLeaves\":" + whiteLeaves + ",\"grayLeaves\":" + grayLeaves
                + ",\"greenLeaves\":" + greenLeaves + ",\"bakedLeaves\":" + bakedLeaves
                + "},\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n");
        }
        catch { }
        return result;
    }
    // #endregion
}