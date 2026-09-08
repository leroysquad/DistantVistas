using System.Globalization;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Session 40cccb: far ground goes green / trees miss frost.
/// H-COL1 far lacks FlagBaked (live tint greens); H-COL2 ground mix ? GetColor;
/// H-COL3 unbaked capture atlas green; H-COL4 FlagFrost / LiveWinter gate;
/// H-COL5 near FlagBaked OK, far not.
/// </summary>
public static class ColorPathDiag
{
    const string LogPath =
        @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log";

    static long lastLogMs;
    static int logCount;
    static int bakeFrostGateBlocked;
    static int bakeFrostFlagged;
    static int bakeSamples;
    static float lastBakeWinter;

    static int overlayGetColorHits;
    static int overlayGetColorMisses;

    public static void NoteBakeFrostGate(float liveWinter, bool seasonAllows, bool flagged)
    {
        bakeSamples++;
        lastBakeWinter = liveWinter;
        if (!seasonAllows) bakeFrostGateBlocked++;
        if (flagged) bakeFrostFlagged++;
    }

    public static void MaybeLogDrawn(
        ICoreClientAPI? capi,
        LodWorld? world,
        List<long>? drawList,
        float liveVd)
    {
        if (capi == null || world == null || drawList == null || drawList.Count == 0) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (logCount >= 40) return;
        if (now - lastLogMs < 2500) return;
        lastLogMs = now;
        logCount++;

        Vec3d cam = capi.World.Player.Entity.CameraPos;
        double nearR = Math.Max(128, liveVd * 0.6);
        double farR = Math.Max(nearR + 256, liveVd * 2.0);

        int nearN = 0, farN = 0;
        int nearBaked = 0, farBaked = 0;
        int nearFrost = 0, farFrost = 0;
        int nearLeaf = 0, farLeaf = 0;
        int nearLeafFrost = 0, farLeafFrost = 0;
        long nearChroma = 0, farChroma = 0;
        long nearGreenBias = 0, farGreenBias = 0;
        int nearTinted = 0, farTinted = 0;

        for (int i = 0; i < drawList.Count; i++)
        {
            long key = drawList[i];
            if (!world.Sections.TryGetValue(key, out LodSection? sec) || sec == null || sec.Palette.Count == 0)
                continue;

            int level = LodWorld.KeyLevel(key);
            int sx = LodWorld.KeySx(key);
            int sz = LodWorld.KeySz(key);
            int blocks = LodSection.SectionBlocks << level;
            double cx = (sx + 0.5) * blocks;
            double cz = (sz + 0.5) * blocks;
            double dx = cx - cam.X;
            double dz = cz - cam.Z;
            double dist = Math.Sqrt(dx * dx + dz * dz);

            bool isNear = dist <= nearR;
            bool isFar = dist >= farR;
            if (!isNear && !isFar) continue;

            int sampled = 0;
            int cols = LodSection.GridSize * LodSection.GridSize;
            int step = Math.Max(1, cols / 16);
            for (int col = 0; col < cols && sampled < 16; col += step)
            {
                if (!sec.TryGetTopRun(col, out ulong run)) continue;
                int pid = LodSection.RunPaletteId(run);
                if (pid < 0 || pid >= sec.Palette.Count) continue;
                LodPaletteEntry e = sec.Palette[pid];
                sampled++;

                bool baked = (e.Flags & LodPaletteEntry.FlagBaked) != 0;
                bool frost = (e.Flags & LodPaletteEntry.FlagFrost) != 0;
                LodPaletteRepair.Channels(e.Color, out int r, out int g, out int b, out _, out int chroma);
                int greenBias = g - Math.Max(r, b);
                Block? block = null;
                try { block = capi.World.GetBlock(e.BlockId); } catch { }
                bool leaf = LodCanopyGray.IsSeasonFoliage(block);
                bool liveTint = !baked;

                if (isNear)
                {
                    nearN++;
                    if (baked) nearBaked++;
                    if (frost) nearFrost++;
                    if (leaf) { nearLeaf++; if (frost) nearLeafFrost++; }
                    nearChroma += chroma;
                    nearGreenBias += greenBias;
                    if (liveTint) nearTinted++;
                }
                else
                {
                    farN++;
                    if (baked) farBaked++;
                    if (frost) farFrost++;
                    if (leaf) { farLeaf++; if (frost) farLeafFrost++; }
                    farChroma += chroma;
                    farGreenBias += greenBias;
                    if (liveTint) farTinted++;
                }
            }
        }

        float winter = LodSeasonBake.LiveWinterAmount;
        bool frostSeason = LodSeasonBake.SeasonAllowsFrost;

        string hyp = "ok";
        if (farN > 8 && farBaked * 100 / farN < 40) hyp = "H-COL1";
        else if (farN > 8 && nearN > 8
            && farBaked * 100 / farN + 25 < nearBaked * 100 / Math.Max(1, nearN))
            hyp = "H-COL5";
        else if (farN > 8 && farGreenBias / farN > 25 && farBaked * 100 / farN < 70)
            hyp = "H-COL3";
        else if (frostSeason && farLeaf > 4 && farLeafFrost * 100 / farLeaf < 20)
            hyp = "H-COL4";
        else if (bakeSamples > 20 && bakeFrostGateBlocked > bakeFrostFlagged * 2)
            hyp = "H-COL4";

        var inv = CultureInfo.InvariantCulture;
        // #region agent log
        try
        {
            System.IO.File.AppendAllText(LogPath,
                "{\"sessionId\":\"40cccb\",\"runId\":\"color-1\",\"hypothesisId\":\"" + hyp
                + "\",\"location\":\"ColorPathDiag.MaybeLogDrawn\",\"message\":\"color-path\",\"data\":{"
                + "\"liveVd\":" + liveVd.ToString("0.#", inv)
                + ",\"winter\":" + winter.ToString("0.###", inv)
                + ",\"frostSeason\":" + (frostSeason ? "true" : "false")
                + ",\"nearN\":" + nearN
                + ",\"farN\":" + farN
                + ",\"nearBakedPct\":" + Pct(nearBaked, nearN)
                + ",\"farBakedPct\":" + Pct(farBaked, farN)
                + ",\"nearFrostPct\":" + Pct(nearFrost, nearN)
                + ",\"farFrostPct\":" + Pct(farFrost, farN)
                + ",\"nearLeaf\":" + nearLeaf
                + ",\"farLeaf\":" + farLeaf
                + ",\"nearLeafFrostPct\":" + Pct(nearLeafFrost, nearLeaf)
                + ",\"farLeafFrostPct\":" + Pct(farLeafFrost, farLeaf)
                + ",\"nearMeanChroma\":" + Mean(nearChroma, nearN)
                + ",\"farMeanChroma\":" + Mean(farChroma, farN)
                + ",\"nearGreenBias\":" + Mean(nearGreenBias, nearN)
                + ",\"farGreenBias\":" + Mean(farGreenBias, farN)
                + ",\"nearLiveTintPct\":" + Pct(nearTinted, nearN)
                + ",\"farLiveTintPct\":" + Pct(farTinted, farN)
                + ",\"bakeSamples\":" + bakeSamples
                + ",\"bakeFrostBlocked\":" + bakeFrostGateBlocked
                + ",\"bakeFrostFlagged\":" + bakeFrostFlagged
                + ",\"lastBakeWinter\":" + lastBakeWinter.ToString("0.###", inv)
                + ",\"n\":" + logCount
                + "},\"timestamp\":" + now + "}\n");
        }
        catch { }
        // #endregion

        bakeSamples = 0;
        bakeFrostGateBlocked = 0;
        bakeFrostFlagged = 0;
    }

    static int Pct(int part, int whole) =>
        whole <= 0 ? -1 : part * 100 / whole;

    static int Mean(long sum, int n) =>
        n <= 0 ? 0 : (int)(sum / n);

    public static void ResetOverlayCacheStats()
    {
        overlayGetColorHits = 0;
        overlayGetColorMisses = 0;
    }

    public static void NoteOverlayGetColorBatch()
    {
        overlayGetColorHits = LodBakeScratch.OverlayGetColorHits;
        overlayGetColorMisses = LodBakeScratch.OverlayGetColorMisses;
    }

    public static void ResetSession()
    {
        lastLogMs = 0;
        logCount = 0;
        bakeFrostGateBlocked = 0;
        bakeFrostFlagged = 0;
        bakeSamples = 0;
        lastBakeWinter = 0;
        overlayGetColorHits = 0;
        overlayGetColorMisses = 0;
    }
}
