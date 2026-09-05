using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Discover-bake: at capture (or budgeted season repaint) resolve climate AND
/// season into grass-top / foliage RGB via the game's colour maps at that block's
/// lat/long/height. <see cref="LodPaletteEntry.FlagBaked"/> uses alpha band 3 with
/// <see cref="LodTintRegistry.SlotNone"/> — identity tint, no live season mix.
///
/// All seasons share one path: calendar (<c>YearRel</c> / season epoch rebake) ×
/// ClimateMap temp+rain at XZ (+ height). Autumn oranges, summer greens, winter
/// browns are that overlay — not a separate invent. Far LOD borrows neighbor
/// ClimateMap (or height lapse for frost only) when the column's map is unloaded;
/// walking in rebakes to the exact sample.
///
/// Vanilla December season maps are brown/olive (birchtint/needletint), not white —
/// near-field frost is snow cover. Distant LOD has no snow-overlay mesh, so after the
/// colormap multiply we lerp Frostable albedo toward frost when air is cold
/// (<see cref="ApplyWinterFrostApprox"/>). Proven by debug e8d91d probe-h.
///
/// 0.7.84–85 tried climate-only bake + live <c>seas/clim</c> on band 3. Dividing
/// season RGB by keep-origin climate greens produced purple flicker (and purple
/// mountains) as season tables refreshed. Full bake into RGB + identity band 3
/// is the path that matches vanilla ColorOverlay without a second shader clock.
/// Join/month <see cref="HealOrRepaintSection"/> rebakes when the calendar moves.
/// </summary>
public static class LodSeasonBake
{
    // === Canopy frost: restore the white trees that worked (0.7.92 band). ===
    // Soft pale targets + leaf-look crush (0.8.2–0.8.4) left December brown
    // (e8d91d post-fix-decfrost: frostTarget dark, bakedLum≈0.2, changed:0).
    /// <summary>Bump when frost bake math changes — forces one SeasonDirty sweep.</summary>
    public const int FrostBakeRevision = 35;

    // === Winter colour ===
    // Reference look: pine biome spotty frost (player: ~48203, -335 E) — mottled over
    // biome grass, not a gray plate. Bake that XZ mottle into FlagBaked (world-locked).
    // Leaves: winter white. Wood/trunks/branches: never frost (brown contrast).
    // Real snowlayer/snowblock: FlagSnow white by depth; MarkedDirty ForceQueues.
    // No camera-locked fragment speckles (those slid with the view).

    /// <summary>
    /// Legacy soft winter invent (°C). Kept for probe names only — not used in bake.
    /// </summary>
    public const float FrostStartTempC = 16f;

    /// <summary>Legacy full-frost °C for the unused soft invent curve.</summary>
    public const float FrostFullTempC = -2f;

    /// <summary>Leaf frost, not a snow hat. Keep well below 0.5 so canopy stays gray.</summary>
    public const float FrostMaxMixCanopy = 0.28f;

    /// <summary>
    /// Grass tops only (never snowlayer/snowblock). Peak mottles get this much cool frost;
    /// valleys stay biome grass. Real snow is FlagSnow bright white — separate path.
    /// </summary>
    public const float FrostMaxMixGround = 0.40f;

    /// <summary>World-locked ground-frost bins per soil BlockId (palette rows).</summary>
    public const int FrostMottleBins = 8;

    /// <summary>
    /// Landscape frost blobs are ~40 blocks so a patch can straddle a 64-block L0 tile.
    /// Power-of-two steps (32/64) snap to section edges and make checkerboard plates.
    /// </summary>
    public const int FrostLandscapeStep = 40;

    /// <summary>Legacy alias = canopy.</summary>
    public const float FrostMaxMix = FrostMaxMixCanopy;

    /// <summary>December/Jan floor (0–1) before × mix cap.</summary>
    public const float WinterMonthFrostFloor = 0.62f;

    /// <summary>
    /// Hard freeze-line canopy strength vs mixCap (near/below freezing).
    /// </summary>
    public const float FreezeLineFrostScale = 0.72f;

    /// <summary>
    /// Mild cold-pocket tip frost vs mixCap — "a little white on the tops" from the
    /// heatmap without plastic-white valleys (player ask, April hills).
    /// </summary>
    public const float FreezeLineTipScale = 0.36f;

    /// <summary>°C where heatmap tip frost begins (cold pocket, not full freeze).</summary>
    public const float FreezeLineTipStartC = 8f;

    /// <summary>°C where tip is full and hard freeze begins (vanilla ~0; allow a kiss).</summary>
    public const float FreezeLineStartC = 1f;

    /// <summary>°C for full hard freeze-line canopy frost.</summary>
    public const float FreezeLineFullC = -6f;

    /// <summary>Alias — older spring-alpine names map onto the year-round freeze line.</summary>
    public const float SpringAlpineFrostScale = FreezeLineFrostScale;
    public const float SpringFreezeStartC = FreezeLineStartC;
    public const float SpringFreezeFullC = FreezeLineFullC;

    /// <summary>Unused (kept for probe fields); white path does not luminance-gate mix.</summary>
    public const float FrostCanopyBrightestLum = 0.92f;

    // GameCalendar.GetSeason thresholds — keep in sync with LodSeasonBakeEpoch.
    public const float SeasonRelWinterEnd = LodSeasonBakeEpoch.SeasonRelWinterEnd;
    public const float SeasonRelSpringEnd = LodSeasonBakeEpoch.SeasonRelSpringEnd;
    public const float SeasonRelSummerEnd = LodSeasonBakeEpoch.SeasonRelSummerEnd;
    public const float SeasonRelWinterStart = LodSeasonBakeEpoch.SeasonRelWinterStart;

    /// <summary>
    /// ClientWorldMap.GetClimate fallback when the map region is not loaded
    /// (VintagestoryLib). Mild fake heatmap — frost bake then under-frosts far LOD.
    /// </summary>
    public const int PlaceholderClimatePacked = 11842740;

    /// <summary>Muted frost-gray for leaves (R-low packing) — not near-white snow.</summary>
    public static readonly int FrostRgbCanopy = unchecked((int)0xFFB8B4AE);

    /// <summary>
    /// Sample ClimateMap (same data ClientWorldMap / minimap uses: R=temp, G=rain).
    /// BiomeMap is unused in the API — do not scrape minimap pixels.
    /// Own region first; else nearest loaded neighbor (±2) for a far-LOD preview.
    /// When you walk in, MapRegionLoaded rebakes to the exact column climate.
    /// </summary>
    public static bool TryProbeClimateAvailability(
        IClientWorldAccessor world,
        int x,
        int z,
        out bool regionLoaded,
        out bool climateMapOk,
        out int climatePacked) =>
        TryProbeClimateAvailability(world, x, z, out regionLoaded, out climateMapOk, out climatePacked, out _);

    public static bool TryProbeClimateAvailability(
        IClientWorldAccessor world,
        int x,
        int z,
        out bool regionLoaded,
        out bool climateMapOk,
        out int climatePacked,
        out bool fromNeighbor)
    {
        regionLoaded = false;
        climateMapOk = false;
        climatePacked = PlaceholderClimatePacked;
        fromNeighbor = false;
        try
        {
            int rs = world.BlockAccessor.RegionSize;
            if (rs <= 0) rs = 512;
            int rx = x >= 0 ? x / rs : (x - (rs - 1)) / rs;
            int rz = z >= 0 ? z / rs : (z - (rs - 1)) / rs;

            if (TrySampleRegionClimate(world, rs, rx, rz, x, z, out climatePacked))
            {
                regionLoaded = true;
                climateMapOk = true;
                return true;
            }

            regionLoaded = world.BlockAccessor.GetMapRegion(rx, rz) != null;

            const int NeighborRadius = 2;
            int bestDist = int.MaxValue;
            int bestPacked = PlaceholderClimatePacked;
            bool found = false;
            for (int drx = -NeighborRadius; drx <= NeighborRadius; drx++)
            {
                for (int drz = -NeighborRadius; drz <= NeighborRadius; drz++)
                {
                    if (drx == 0 && drz == 0) continue;
                    if (!TrySampleRegionClimate(world, rs, rx + drx, rz + drz, x, z, out int packed))
                        continue;
                    int dist = Math.Abs(drx) + Math.Abs(drz);
                    if (dist >= bestDist) continue;
                    bestDist = dist;
                    bestPacked = packed;
                    found = true;
                }
            }

            if (found)
            {
                climatePacked = bestPacked;
                climateMapOk = true;
                fromNeighbor = true;
                return true;
            }
        }
        catch
        {
            climatePacked = PlaceholderClimatePacked;
        }

        return false;
    }

    static bool TrySampleRegionClimate(
        IClientWorldAccessor world, int rs, int rx, int rz, int worldX, int worldZ, out int packed)
    {
        packed = PlaceholderClimatePacked;
        IMapRegion? region = world.BlockAccessor.GetMapRegion(rx, rz);
        if (region?.ClimateMap == null || region.ClimateMap.InnerSize <= 0)
            return false;

        int localX = GameMath.Clamp(worldX - rx * rs, 0, rs - 1);
        int localZ = GameMath.Clamp(worldZ - rz * rs, 0, rs - 1);
        float fx = (float)localX / rs * region.ClimateMap.InnerSize;
        float fz = (float)localZ / rs * region.ClimateMap.InnerSize;
        packed = region.ClimateMap.GetColorLerpedCorrectly(fx, fz);
        return !IsPlaceholderClimate(packed);
    }

    public static bool IsPlaceholderClimate(int climatePacked) =>
        climatePacked == PlaceholderClimatePacked;

    /// <summary>ClimateMap R channel → °C at height (vanilla colormap.vsh path).</summary>
    public static float AirTempFromClimatePacked(IClientWorldAccessor world, int climatePacked, int y) =>
        Climate.GetScaledAdjustedTemperatureFloatClient(
            (climatePacked >> 16) & 0xFF,
            y - world.SeaLevel);

    /// <summary>Murky frost for ground only.</summary>
    /// <summary>Legacy murky plate — unused.</summary>
    static readonly int FrostRgbMurky = unchecked((int)0xFFD4D0C6);

    /// <summary>Cool speckled frost over grass (not cream, not snow-field white).</summary>
    static readonly int FrostRgbGround = unchecked((int)0xFFE2E8E6);

    public static bool CanBake(Block block, int color, Block? plantTintFallback)
    {
        // Snow/ice: bright white FlagSnow path only — never FlagBaked frost mottling.
        if (LodBlockPolicy.IsSnowLayer(block) || LodBlockPolicy.IsClimateUntinted(block))
            return false;
        if (LodPaletteRepair.IsRockLikeAlbedo(color) || LodPaletteRepair.IsSnowOrIceAlbedo(color))
            return false;
        if ((LodBlockPolicy.FlagsFor(block) & LodPaletteEntry.FlagWater) != 0) return false;
        if (block.EntityClass != null) return false;

        if (block.ClimateColorMapResolved != null || block.SeasonColorMapResolved != null)
            return true;

        return block.BlockMaterial == EnumBlockMaterial.Plant && plantTintFallback != null;
    }

    /// <summary>
    /// Untinted atlas (or topsoil composite) times vanilla climate×season at this X/Y/Z,
    /// then cold-air frost approx for plants (season maps alone stay brown/olive in Dec).
    /// </summary>
    public static int BakePaletteColor(
        IClientWorldAccessor world,
        Block block,
        int untintedColor,
        int x,
        int y,
        int z,
        LodUntintedShare share,
        Block? plantTintFallback)
    {
        SampleFinalTint(world, block, x, y, z, share, plantTintFallback, out float tr, out float tg, out float tb);
        int baked = MultiplyRgb(untintedColor, tr, tg, tb);
        return ApplyWinterFrostApprox(world, block, x, y, z, baked);
    }

    /// <summary>
    /// Leaf frost white, or light mottled frost on grass. Wood and bare soil stay as baked.
    /// </summary>
    public static int ApplyWinterFrostApprox(
        IClientWorldAccessor world,
        Block block,
        int x,
        int y,
        int z,
        int bakedRgb)
    {
        // Real snow / ice: atlas white scaled by snowlayer depth (not frost paint).
        if (LodBlockPolicy.IsClimateUntinted(block) || LodBlockPolicy.IsSnowLayer(block))
            return ApplySnowSurfaceWhite(block, bakedRgb);

        // Trunks / logs / bare branches keep brown — winter contrast against leaves.
        if (IsWoodyTrunk(block)) return bakedRgb;

        float mixCap = FrostMixCap(block);
        if (mixCap <= 0.01f) return bakedRgb;

        float climateAmt = WinterFrostAmountWithTemp(world, block, x, y, z, out _);
        if (climateAmt <= 0.01f) return bakedRgb;
        float amt = FinalFrostMix(bakedRgb, climateAmt, mixCap, x, z, block);
        if (amt <= 0.01f) return bakedRgb;
        return ColorOverlayRgb(bakedRgb, FrostTargetRgb(block, bakedRgb), amt);
    }

    /// <summary>
    /// Clamp climateAmt to mixCap. No leaf-look crush — that killed winter white
    /// (e8d91d: dark frostTarget + soft mix → bakedLum≈0.2 forever).
    /// </summary>
    public static float FinalFrostMix(int bakedRgb, float climateAmt, float mixCap, int x, int z) =>
        FinalFrostMix(bakedRgb, climateAmt, mixCap, x, z, groundHint: null);

    public static float FinalFrostMix(
        int bakedRgb, float climateAmt, float mixCap, int x, int z, Block? groundHint)
    {
        if (climateAmt <= 0f || mixCap <= 0f) return 0f;
        _ = bakedRgb;
        float amt = GameMath.Clamp(climateAmt, 0f, mixCap);
        // Ground grass only: XZ mottle. Foliage uses the leaf's own climate×season
        // bake — never world-XZ bins, never FlagFrostGround.
        if (groundHint != null && IsGroundFrost(groundHint))
        {
            // World-locked speckles in FlagBaked RGB (block XZ). Peaks get frost;
            // valleys keep biome grass — same idea as the old fragment noise, but
            // fixed to the terrain so it cannot slide with the camera.
            float m = FrostMottle01(x, z);
            float t = GameMath.Clamp((m - 0.26f) / 0.42f, 0f, 1f);
            t = t * t * (3f - 2f * t);
            amt *= t;
        }
        else
        {
            _ = x;
            _ = z;
        }
        return amt;
    }

    /// <summary>
    /// Real snow on the ground → bright white. Frost paint never touches this path.
    /// Even snowlayer-1 is snow from the sky, not mottled grass frost.
    /// </summary>
    public static int ApplySnowSurfaceWhite(Block block, int atlasRgb)
    {
        float cover = LodBlockPolicy.SnowCover01(block);
        if (cover <= 0f) return atlasRgb;
        // Bright white field. Thin layers still read as snow (near-full white).
        float mix = GameMath.Clamp(0.92f + 0.08f * cover, 0.92f, 1f);
        return ColorOverlayRgb(atlasRgb, FrostRgbSnowField, mix);
    }

    /// <summary>Bright snow-field white (R-low packing) — matches near-field snow tops.</summary>
    public static readonly int SnowFieldRgb = unchecked((int)0xFFFAFCFB);

    static readonly int FrostRgbSnowField = SnowFieldRgb;

    /// <summary>Probe-only; bake no longer scales by leaf look.</summary>
    public static float LeafLookFrostScale(int bakedRgb)
    {
        _ = bakedRgb;
        return 1f;
    }

    /// <summary>Grass speckles vs muted leaf frost-gray.</summary>
    public static int FrostTargetRgb(Block block, int bakedRgb)
    {
        _ = bakedRgb;
        _ = FrostRgbMurky;
        if (IsFoliageFrost(block)) return FrostRgbCanopy;
        return FrostRgbGround;
    }

    /// <summary>
    /// Same seasonal look: merge winter grays and near-identical hues.
    /// Green vs autumn must stay different. Luminance (height) is ignored.
    /// </summary>
    public static bool SameFoliageLook(int rgbA, int rgbB)
    {
        int r1 = rgbA & 0xFF, g1 = (rgbA >> 8) & 0xFF, b1 = (rgbA >> 16) & 0xFF;
        int r2 = rgbB & 0xFF, g2 = (rgbB >> 8) & 0xFF, b2 = (rgbB >> 16) & 0xFF;
        float c1 = RgbChroma(r1, g1, b1);
        float c2 = RgbChroma(r2, g2, b2);
        const float GrayChroma = 22f;
        if (c1 < GrayChroma && c2 < GrayChroma) return true;
        if (c1 < GrayChroma || c2 < GrayChroma) return false;
        float dh = Math.Abs(RgbHueDeg(r1, g1, b1) - RgbHueDeg(r2, g2, b2));
        if (dh > 180f) dh = 360f - dh;
        return dh <= 28f;
    }

    /// <summary>
    /// Spring/summer grass or leaves: chromatic green-to-yellow, not frost-gray
    /// and not autumn brown. Jack's rule: if that look is already on the column,
    /// inferred ground snow does not belong there.
    /// </summary>
    public static bool LooksUnfrostedGreen(int rgb)
    {
        int r = rgb & 0xFF, g = (rgb >> 8) & 0xFF, b = (rgb >> 16) & 0xFF;
        if (RgbChroma(r, g, b) < 22f) return false;
        float h = RgbHueDeg(r, g, b);
        return h >= 55f && h <= 175f;
    }

    static float RgbChroma(int r, int g, int b)
    {
        int mx = Math.Max(r, Math.Max(g, b));
        int mn = Math.Min(r, Math.Min(g, b));
        return mx - mn;
    }

    static float RgbHueDeg(int r, int g, int b)
    {
        float rf = r / 255f, gf = g / 255f, bf = b / 255f;
        float mx = Math.Max(rf, Math.Max(gf, bf));
        float mn = Math.Min(rf, Math.Min(gf, bf));
        float d = mx - mn;
        if (d < 1e-5f) return 0f;
        float h;
        if (mx == rf) h = (gf - bf) / d;
        else if (mx == gf) h = 2f + (bf - rf) / d;
        else h = 4f + (rf - gf) / d;
        h *= 60f;
        if (h < 0f) h += 360f;
        return h;
    }

    public static float RgbLum01(int rgb)
    {
        int r = rgb & 0xFF, g = (rgb >> 8) & 0xFF, b = (rgb >> 16) & 0xFF;
        return (r + g + b) / (3f * 255f);
    }

    /// <summary>Scale RGB toward black so mean luminance ≤ maxLum (keeps hue).</summary>
    public static int ClampRgbLum(int rgb, float maxLum)
    {
        float lum = RgbLum01(rgb);
        if (lum <= maxLum || lum < 1e-4f) return rgb;
        float s = maxLum / lum;
        int r = Math.Clamp((int)((rgb & 0xFF) * s + 0.5f), 0, 255);
        int g = Math.Clamp((int)(((rgb >> 8) & 0xFF) * s + 0.5f), 0, 255);
        int b = Math.Clamp((int)(((rgb >> 16) & 0xFF) * s + 0.5f), 0, 255);
        return unchecked((int)0xFF000000) | (b << 16) | (g << 8) | r;
    }

    public static float WinterFrostAmount(
        IClientWorldAccessor world,
        Block block,
        int x,
        int y,
        int z) =>
        WinterFrostAmountWithTemp(world, block, x, y, z, out _);

    /// <summary>
    /// Frost = Max(calendar, freeze-line).
    /// Non-winter: date-adjusted <see cref="IBlockAccessor.GetClimateAt"/> only —
    /// ClimateMap R is annual/base and stays cold in green May biomes (0.8.14 miss).
    /// Neighbor packed without a live date sample → no spring invent.
    /// Winter: tip + hard from packed/live/lapse as far preview.
    /// </summary>
    public static float WinterFrostAmountWithTemp(
        IClientWorldAccessor world,
        Block block,
        int x,
        int y,
        int z,
        out float airTempC)
    {
        airTempC = float.NaN;
        if (!IsFrostCandidate(block)) return 0f;

        float mixCap = FrostMixCap(block);
        var pos = new BlockPos(x, y, z);
        bool winter = IsCalendarWinter(world, pos);
        float calendar = CalendarFrostAmount(world, block, x, y, z, mixCap);

        float freeze = 0f;
        try
        {
            if (!winter)
            {
                // May sea-level white tops: annual ClimateMap °C was freezing while
                // the date-adjusted climate (and near leaves) were green. Only trust
                // ForSuppliedDate here; no packed invent, no tip, no height lapse.
                ClimateCondition? cl = world.BlockAccessor.GetClimateAt(
                    pos,
                    EnumGetClimateMode.ForSuppliedDate_TemperatureOnly,
                    world.Calendar.TotalDays);
                if (cl != null)
                {
                    airTempC = cl.Temperature;
                    freeze = FreezeLineFrostAmount(
                        world, block, y, airTempC, mixCap, allowTip: false);
                }
            }
            else
            {
                TryProbeClimateAvailability(
                    world, x, z, out _, out bool climateMapOk, out int packed, out bool fromNeighbor);
                bool havePacked = climateMapOk && !IsPlaceholderClimate(packed);

                if (havePacked)
                {
                    airTempC = AirTempFromClimatePacked(world, packed, y);
                    if (!fromNeighbor)
                    {
                        ClimateCondition? cl = world.BlockAccessor.GetClimateAt(
                            pos,
                            EnumGetClimateMode.ForSuppliedDate_TemperatureOnly,
                            world.Calendar.TotalDays);
                        if (cl != null)
                            airTempC = cl.Temperature;
                    }
                    freeze = FreezeLineFrostAmount(
                        world, block, y, airTempC, mixCap, allowTip: true);
                }
                else
                {
                    float guess = GuessAirTempFromHeightLapse(world, y);
                    if (!float.IsNaN(guess))
                    {
                        airTempC = guess;
                        freeze = FreezeLineFrostAmount(
                            world, block, y, guess, mixCap, allowTip: true);
                    }
                }
            }
        }
        catch
        {
            /* calendar stands */
        }

        return Math.Max(calendar, freeze);
    }

    /// <summary>True when GetSeasonRel is Winter (incl. year-wrap past WinterStart).</summary>
    public static bool IsCalendarWinter(IClientWorldAccessor world, BlockPos pos) =>
        LodSeasonBakeEpoch.SeasonIndexFromRel(SafeSeasonRel(world, pos)) == 0;

    /// <summary>
    /// Ground snow (not leaf frost). Air at/below freeze-line → snow. Missing
    /// temperature in calendar winter/Nov–Feb → snow, so far L0 plates are not
    /// left bare when ClimateMap and chunks are unloaded.
    /// </summary>
    public static bool WantsGroundSnow(float airTempC, bool winterSeasonOrMonth)
    {
        if (!float.IsNaN(airTempC)) return airTempC <= FreezeLineStartC;
        return winterSeasonOrMonth;
    }

    /// <summary>
    /// Months that may invent ground snow from a missing climate sample.
    /// May (5) through October (10) are melt/green: NaN must not paint a snow field.
    /// Freeze-line alpine with a real °C reading can still snow in June.
    /// </summary>
    public static bool CalendarMonthAllowsInventedGroundSnow(int month) =>
        month < 5 || month > 10;

    /// <summary>
    /// Inferred FlagSnow only. Missing climate in May–October never invents a
    /// snow field. A real freeze-line reading still wants snow at that height —
    /// June alpine and high trees keep frost and ground snow. Green unfrosted
    /// vegetation on the same column vetoes cover separately.
    /// </summary>
    public static bool WantsInferredGroundSnow(
        float airTempC, bool winterSeasonOrMonth, int month)
    {
        if (float.IsNaN(airTempC) && !CalendarMonthAllowsInventedGroundSnow(month))
            return false;
        return WantsGroundSnow(airTempC, winterSeasonOrMonth);
    }

    /// <summary>
    /// May–October: recapture a fully captured non-provisional L0 that still has
    /// FlagSnow or a leftover snowlayer top. Token equality must not suppress
    /// that — Cover after a prior visit used to stamp the token and leave snow.
    /// Winter still infers after capture (snowlayer is often missing from RLE).
    /// <paramref name="pendingVisitRecapture"/> is alpine land Cover stripped
    /// while far: first loaded visit this epoch restores vanilla snow.
    /// </summary>
    public static bool RecaptureLoadedSnowForMelt(
        int month, bool hasSnow, bool pendingVisitRecapture = false)
    {
        if (CalendarMonthAllowsInventedGroundSnow(month)) return false;
        return hasSnow || pendingVisitRecapture;
    }

    /// <summary>
    /// May–October on a real (non-provisional) quadrant: do not invent FlagSnow.
    /// The loaded capture is the look. Winter and peek/unvisited plates still infer.
    /// </summary>
    public static bool SkipInferredSnowOnVisitedMelt(int month, bool provisionalQuadrant)
    {
        if (provisionalQuadrant) return false;
        return !CalendarMonthAllowsInventedGroundSnow(month);
    }

    /// <summary>
    /// Idle sweep: skip plates whose mesh already matches this epoch and that
    /// have nothing inferred or leftover seasonal snowlayer to melt. Glacier ice
    /// is not leftover snowlayer. Recapture still owns alpine snow once chunks load.
    /// </summary>
    public static bool SectionNeedsIdleSeasonPass(
        LodSection section, int seasonToken, int month, IList<Block>? blocks = null)
    {
        _ = seasonToken;
        if (SectionNeedsLegacyHeal(section)) return true;
        if (CalendarMonthAllowsInventedGroundSnow(month)) return false;
        if (section.HasInferredSnowSurface()) return true;
        return SectionHasLeftoverSeasonalSnow(section, blocks);
    }

    /// <summary>
    /// Captured snowlayer/snowblock still in the RLE. Glacier / packed ice is ice,
    /// not this — those stay white in June.
    /// </summary>
    public static bool SectionHasLeftoverSeasonalSnow(
        LodSection section, IList<Block>? blocks)
    {
        int cols = LodSection.GridSize * LodSection.GridSize;
        for (int col = 0; col < cols; col++)
        {
            if (!section.Captured[col]) continue;
            int from = section.ColumnStart[col];
            int to = section.ColumnStart[col + 1];
            for (int r = from; r < to; r++)
            {
                int pid = LodSection.RunPaletteId(section.Runs[r]);
                if ((uint)pid >= (uint)section.Palette.Count) continue;
                LodPaletteEntry entry = section.Palette[pid];
                if (!IsLeftoverSeasonalSnow(entry, BlockOf(entry, blocks))) continue;
                return true;
            }
        }
        return false;
    }

    static Block? BlockOf(LodPaletteEntry entry, IList<Block>? blocks)
    {
        if (blocks == null || entry.BlockId <= 0) return null;
        if ((uint)entry.BlockId >= (uint)blocks.Count) return null;
        return blocks[entry.BlockId];
    }

    /// <summary>
    /// Packed ClimateMap + height lapse for Cover. Live GetClimateAt only when
    /// the map cell is missing. GuessAirTempFromHeightLapse is the last fallback.
    /// </summary>
    public static float SampleAirTempForCover(
        IClientWorldAccessor world, int packed, bool havePacked, int x, int y, int z)
    {
        if (havePacked) return AirTempFromClimatePacked(world, packed, y);
        try
        {
            ClimateCondition? cl = world.BlockAccessor.GetClimateAt(
                new BlockPos(x, y, z),
                EnumGetClimateMode.ForSuppliedDate_TemperatureOnly,
                world.Calendar.TotalDays);
            if (cl != null) return cl.Temperature;
        }
        catch { /* unloaded / tests */ }
        return GuessAirTempFromHeightLapse(world, y);
    }

    /// <summary>
    /// Winter: packed ClimateMap / live date sample / height lapse. Unloaded far
    /// columns return NaN so <see cref="WantsGroundSnow"/> can still cover them.
    /// Non-winter: date-adjusted climate only (alpine freeze), else NaN.
    /// </summary>
    public static float SampleAirTempForSnow(IClientWorldAccessor world, int x, int y, int z)
    {
        var pos = new BlockPos(x, y, z);
        bool winter = false;
        try
        {
            winter = IsCalendarWinter(world, pos) || CalendarMonthFrostFloor(world) > 0f;
        }
        catch { /* */ }

        if (!winter)
        {
            try
            {
                ClimateCondition? cl = world.BlockAccessor.GetClimateAt(
                    pos,
                    EnumGetClimateMode.ForSuppliedDate_TemperatureOnly,
                    world.Calendar.TotalDays);
                if (cl != null) return cl.Temperature;
            }
            catch { /* */ }
            return float.NaN;
        }

        try
        {
            TryProbeClimateAvailability(
                world, x, z, out _, out bool climateMapOk, out int packed, out bool fromNeighbor);
            bool havePacked = climateMapOk && !IsPlaceholderClimate(packed);
            if (havePacked)
            {
                float air = AirTempFromClimatePacked(world, packed, y);
                if (!fromNeighbor)
                {
                    ClimateCondition? cl = world.BlockAccessor.GetClimateAt(
                        pos,
                        EnumGetClimateMode.ForSuppliedDate_TemperatureOnly,
                        world.Calendar.TotalDays);
                    if (cl != null) air = cl.Temperature;
                }
                return air;
            }

            float guess = GuessAirTempFromHeightLapse(world, y);
            if (!float.IsNaN(guess)) return guess;
        }
        catch { /* */ }

        return float.NaN;
    }

    public static bool ColumnWantsGroundSnow(IClientWorldAccessor world, int x, int y, int z)
    {
        float air = SampleAirTempForSnow(world, x, y, z);
        bool winter = false;
        int month = 0;
        try
        {
            var pos = new BlockPos(x, y, z);
            winter = IsCalendarWinter(world, pos) || CalendarMonthFrostFloor(world) > 0f;
            month = world.Calendar.Month;
        }
        catch { /* */ }
        return WantsInferredGroundSnow(air, winter, month);
    }

    /// <summary>
    /// Per-column ground snow: a 64-tile must not pick one tallest sample and paint
    /// the whole plate bare. Any sky-visible ground that would hold a snowlayer
    /// (soil, grass, rock, sand, gravel, farmland, peat, clay, paths) becomes
    /// FlagSnow when <paramref name="wantsSnow"/> is true. Foliage and wood are
    /// skipped (not snow-hatted) so the surface under the canopy still covers;
    /// real snowlayer stays. Inferred grass melts back onto the frost row; other
    /// ground melts back onto its opaque row.
    /// </summary>
    public static int CoverGroundSnowColumns(
        LodSection section,
        long sectionKey,
        System.Func<int, int, int, bool> wantsSnow,
        IClientWorldAccessor? world = null,
        int loadedTruthEpoch = int.MinValue,
        int calendarMonth = 0)
    {
        int month = calendarMonth;
        if (world != null)
        {
            try { month = world.Calendar.Month; }
            catch { /* tests / shutdown */ }
        }

        int changed = 0;
        bool strippedLeftover = false;
        int cols = LodSection.GridSize * LodSection.GridSize;
        for (int col = 0; col < cols; col++)
        {
            if (!section.Captured[col]) continue;
            int from = section.ColumnStart[col];
            int to = section.ColumnStart[col + 1];
            bool greenVeg = ColumnHasUnfrostedGreenVegetation(section, from, to, world);
            int q = LodSection.QuadrantOf(col % LodSection.GridSize, col / LodSection.GridSize);
            bool skipInvent = SkipInferredSnowOnVisitedMelt(
                month, section.IsProvisionalQuadrant(q));
            for (int r = from; r < to; r++)
            {
                ulong run = section.Runs[r];
                int pid = LodSection.RunPaletteId(run);
                if ((uint)pid >= (uint)section.Palette.Count) continue;
                LodPaletteEntry entry = section.Palette[pid];
                if ((entry.Flags & LodPaletteEntry.FlagSkip) != 0) continue;
                if ((entry.Flags & LodPaletteEntry.FlagThin) != 0) continue;
                if ((entry.Flags & LodPaletteEntry.FlagWater) != 0) break;

                Block? block = null;
                if (world != null
                    && entry.BlockId > 0
                    && (uint)entry.BlockId < (uint)world.Blocks.Count)
                    block = world.Blocks[entry.BlockId];

                // Canopy/wood/thin plants sit above the ground. Breaking here left
                // whole forested 64-plates bare while neighbouring open grass snowed.
                // Visited melt, chunks loaded: leftover snowlayer waits for recapture.
                // Far plates never recapture — strip seasonal snowlayer so June LOD
                // is not last winter's white. Glacier ice is not seasonal snowlayer.
                if (IsLeftoverSeasonalSnow(entry, block))
                {
                    (int sx, int sy, int sz) = LodPipeline.CaptureBlockPos(sectionKey, col, run);
                    if (skipInvent && !VanillaChunkLoaded(world, sx, sy, sz))
                    {
                        int skipPid = section.FindOrAddPaletteEntry(
                            entry.BlockId, entry.Color, LodPaletteEntry.FlagSkip);
                        section.Runs[r] = LodSection.PackRun(
                            skipPid, LodSection.RunYTop(run), LodSection.RunYBottom(run));
                        changed++;
                        strippedLeftover = true;
                        continue;
                    }
                    if (skipInvent) continue;
                    break;
                }
                if (IsRealSnowSurface(entry, block))
                {
                    if (skipInvent) continue;
                    break;
                }
                if (IsSkyFoliageOrWood(entry, block)) continue;
                if (block != null
                    && block.BlockMaterial == EnumBlockMaterial.Plant
                    && !IsGroundFrost(block))
                    continue;
                if (!IsGroundSnowTarget(entry, block)) break;

                (int x, int y, int z) = LodPipeline.CaptureBlockPos(sectionKey, col, run);
                bool want = wantsSnow(x, y, z) && !greenVeg && !skipInvent;
                bool inferred = (entry.Flags & LodPaletteEntry.FlagSnow) != 0;
                bool grassLike = IsInferredGrassSnow(entry, block);
                int yTop = LodSection.RunYTop(run);
                int yBottom = LodSection.RunYBottom(run);
                // Cover is not a season paintbrush. Far snow is the shader snowline.
                // Melt leftover inferred FlagSnow from old caches; do not invent it.
                _ = grassLike;
                if (!want && inferred)
                {
                    int meltPid = RestoreOpaqueGroundPid(section, entry.BlockId);
                    if (meltPid != pid)
                    {
                        section.Runs[r] = LodSection.PackRun(meltPid, yTop, yBottom);
                        changed++;
                    }
                }
                break;
            }
        }

        if (strippedLeftover) section.RemoveRunsWithFlag(LodPaletteEntry.FlagSkip);
        if (changed > 0) section.InvalidatePaletteSnapshot();
        return changed;
    }

    public static int CoverGroundSnowColumns(
        IClientWorldAccessor world, LodSection section, long sectionKey, int loadedTruthEpoch = int.MinValue)
    {
        int month = 0;
        bool winter = false;
        int packed = 0;
        bool havePacked = false;
        try
        {
            month = world.Calendar.Month;
            int level = LodWorld.KeyLevel(sectionKey);
            int sectionBlocks = LodSection.SectionBlocks << level;
            int cx = LodWorld.KeySx(sectionKey) * sectionBlocks + sectionBlocks / 2;
            int cz = LodWorld.KeySz(sectionKey) * sectionBlocks + sectionBlocks / 2;
            var pos = new BlockPos(cx, world.SeaLevel, cz);
            winter = IsCalendarWinter(world, pos) || CalendarMonthFrostFloor(world) > 0f;
            TryProbeClimateAvailability(
                world, cx, cz, out _, out bool climateMapOk, out packed, out _);
            havePacked = climateMapOk && !IsPlaceholderClimate(packed);
        }
        catch { /* tests / shutdown */ }
        return CoverGroundSnowColumns(
            section, sectionKey,
            (x, y, z) =>
            {
                float air = SampleAirTempForCover(world, packed, havePacked, x, y, z);
                return WantsInferredGroundSnow(air, winter, month);
            },
            world, loadedTruthEpoch, month);
    }

    /// <summary>
    /// Per-column inferred FlagSnow whose host block bakes unfrosted green at that
    /// XZ+Y melts. Alpine columns bake brown or frost-gray and keep snow. Real
    /// snowlayer is not inferred and is not touched.
    /// </summary>
    public static int MeltInferredSnowWhereHostIsGreen(
        IClientWorldAccessor world,
        LodSection section,
        long sectionKey,
        Block? plantTintFallback,
        System.Func<Block, int, int, int, (int Color, LodUntintedShare Share)> untintedOf)
    {
        int changed = 0;
        int cols = LodSection.GridSize * LodSection.GridSize;
        for (int col = 0; col < cols; col++)
        {
            if (!section.Captured[col]) continue;
            int from = section.ColumnStart[col];
            int to = section.ColumnStart[col + 1];
            for (int r = from; r < to; r++)
            {
                ulong run = section.Runs[r];
                int pid = LodSection.RunPaletteId(run);
                if ((uint)pid >= (uint)section.Palette.Count) continue;
                LodPaletteEntry entry = section.Palette[pid];
                if ((entry.Flags & LodPaletteEntry.FlagSkip) != 0) continue;
                if ((entry.Flags & LodPaletteEntry.FlagThin) != 0) continue;
                if ((entry.Flags & LodPaletteEntry.FlagWater) != 0) break;

                Block? block = null;
                if (entry.BlockId > 0 && (uint)entry.BlockId < (uint)world.Blocks.Count)
                    block = world.Blocks[entry.BlockId];

                if (IsRealSnowSurface(entry, block)) break;
                if (IsSkyFoliageOrWood(entry, block)) continue;
                if (block != null
                    && block.BlockMaterial == EnumBlockMaterial.Plant
                    && !IsGroundFrost(block))
                    continue;
                if (!IsGroundSnowTarget(entry, block)) break;
                if ((entry.Flags & LodPaletteEntry.FlagSnow) == 0) break;
                if (block == null) break;

                (int x, int y, int z) = LodPipeline.CaptureBlockPos(sectionKey, col, run);
                (int untinted, LodUntintedShare share) = untintedOf(block, x, y, z);
                int baked = BakePaletteColor(world, block, untinted, x, y, z, share, plantTintFallback);
                if (!LooksUnfrostedGreen(baked)) break;

                int yTop = LodSection.RunYTop(run);
                int yBottom = LodSection.RunYBottom(run);
                int meltPid = RestoreOpaqueGroundPid(section, entry.BlockId);
                LodPaletteEntry melt = section.Palette[meltPid];
                byte liveFlags = (byte)(melt.Flags
                    & ~LodPaletteEntry.FlagSnow
                    & ~LodPaletteEntry.FlagBaked
                    & ~LodPaletteEntry.FlagFrostGround);
                if (melt.Color != untinted || melt.Flags != liveFlags)
                {
                    melt.Color = untinted;
                    melt.Flags = liveFlags;
                    section.Palette[meltPid] = melt;
                    changed++;
                }
                if (meltPid != pid)
                {
                    section.Runs[r] = LodSection.PackRun(meltPid, yTop, yBottom);
                    changed++;
                }
                break;
            }
        }
        return changed;
    }

    /// <summary>
    /// Green or yellow-green grass/leaves that are not frost-gray. Winter forest
    /// frost-gray canopy does not trip this, so the ground under it can still snow.
    /// </summary>
    static bool ColumnHasUnfrostedGreenVegetation(
        LodSection section, int from, int to, IClientWorldAccessor? world)
    {
        for (int r = from; r < to; r++)
        {
            ulong run = section.Runs[r];
            int pid = LodSection.RunPaletteId(run);
            if ((uint)pid >= (uint)section.Palette.Count) continue;
            LodPaletteEntry entry = section.Palette[pid];
            if ((entry.Flags & LodPaletteEntry.FlagSkip) != 0) continue;
            if ((entry.Flags & LodPaletteEntry.FlagWater) != 0) break;

            Block? block = null;
            if (world != null
                && entry.BlockId > 0
                && (uint)entry.BlockId < (uint)world.Blocks.Count)
                block = world.Blocks[entry.BlockId];

            if (IsRealSnowSurface(entry, block)) continue;
            // Inferred snow RGB is near-white — that is not the vegetation look.
            if ((entry.Flags & LodPaletteEntry.FlagSnow) != 0) continue;
            if (!LooksUnfrostedGreen(entry.Color)) continue;

            bool foliage = IsSkyFoliageOrWood(entry, block);
            bool grass = IsInferredGrassSnow(entry, block);
            if (foliage || grass) return true;
        }
        return false;
    }

    static bool IsRealSnowSurface(LodPaletteEntry entry, Block? block)
    {
        if (block != null)
            return LodBlockPolicy.IsSnowLayer(block) || LodBlockPolicy.IsClimateUntinted(block);
        // Capture snowlayer is FlagSnow without FlagBaked. Inferred cover always
        // sets FlagBaked so melt/cover can still see it.
        return (entry.Flags & LodPaletteEntry.FlagSnow) != 0
            && (entry.Flags & LodPaletteEntry.FlagBaked) == 0;
    }

    /// <summary>
    /// Winter snowlayer/snowblock sitting on visited land. Not glacier ice.
    /// Without a live block, FlagSnow with no FlagBaked is the capture leftover.
    /// </summary>
    public static bool IsLeftoverSeasonalSnow(LodPaletteEntry entry, Block? block)
    {
        if (block != null) return block.BlockMaterial == EnumBlockMaterial.Snow;
        return (entry.Flags & LodPaletteEntry.FlagSnow) != 0
            && (entry.Flags & LodPaletteEntry.FlagBaked) == 0;
    }

    static bool VanillaChunkLoaded(IClientWorldAccessor? world, int x, int y, int z)
    {
        if (world == null) return false;
        try
        {
            return world.BlockAccessor.GetChunkAtBlockPos(x, y, z) != null;
        }
        catch
        {
            return false;
        }
    }

    static bool IsSkyFoliageOrWood(LodPaletteEntry entry, Block? block)
    {
        if (block != null) return IsFoliageFrost(block) || IsWoodyTrunk(block);
        return (entry.Flags & LodPaletteEntry.FlagBaked) != 0
            && (entry.Flags & LodPaletteEntry.FlagFrostGround) == 0
            && (entry.Flags & LodPaletteEntry.FlagSnow) == 0;
    }

    static bool IsInferredGrassSnow(LodPaletteEntry entry, Block? block)
    {
        if (block != null) return IsGroundFrost(block);
        return (entry.Flags & LodPaletteEntry.FlagFrostGround) != 0;
    }

    /// <summary>
    /// Vanilla <c>GenSnowLayer</c> / <c>AllowSnowCoverage</c> put snow on any
    /// solid-up surface. Far LOD infers that cover on the same ground materials
    /// when the snowlayer block was never captured. Not foliage, not wood.
    /// </summary>
    public static bool CanHoldInferredSnow(Block block)
    {
        if (LodBlockPolicy.IsSnowLayer(block) || LodBlockPolicy.IsClimateUntinted(block))
            return false;
        if (IsWoodyTrunk(block) || IsFoliageFrost(block)) return false;
        if (IsGroundFrost(block)) return true;
        switch (block.BlockMaterial)
        {
            case EnumBlockMaterial.Sand:
            case EnumBlockMaterial.Gravel:
            case EnumBlockMaterial.Stone:
            case EnumBlockMaterial.Ore:
            case EnumBlockMaterial.Brick:
                return true;
        }

        string? path = block.Code?.Path;
        if (path == null) return false;
        return path.StartsWith("peat", StringComparison.Ordinal)
            || path.StartsWith("farmland", StringComparison.Ordinal)
            || path.StartsWith("clay", StringComparison.Ordinal)
            || path.StartsWith("cobble", StringComparison.Ordinal)
            || path.StartsWith("stonepath", StringComparison.Ordinal)
            || path.StartsWith("gravel-", StringComparison.Ordinal)
            || path.StartsWith("sand-", StringComparison.Ordinal)
            || path.StartsWith("rock-", StringComparison.Ordinal)
            || path.StartsWith("crumbling", StringComparison.Ordinal);
    }

    static bool IsGroundSnowTarget(LodPaletteEntry entry, Block? block)
    {
        if (block != null) return CanHoldInferredSnow(block);
        if ((entry.Flags & LodPaletteEntry.FlagFrostGround) != 0) return true;
        if ((entry.Flags & LodPaletteEntry.FlagSnow) != 0) return true;
        // FlagBaked without frost-ground is canopy/wood (already continued).
        if ((entry.Flags & LodPaletteEntry.FlagBaked) != 0) return false;
        return true;
    }

    static int RestoreOpaqueGroundPid(LodSection section, int blockId)
    {
        for (int i = 0; i < section.Palette.Count; i++)
        {
            if (section.Palette[i].BlockId != blockId) continue;
            if ((section.Palette[i].Flags & LodPaletteEntry.FlagSnow) != 0) continue;
            return i;
        }

        return section.FindOrAddPaletteEntry(blockId, 0, 0);
    }

    /// <summary>
    /// Educated guess when ClimateMap is missing: measure lapse at the player
    /// (loaded climate), apply to the LOD column's Y. Same idea as
    /// <see cref="LodSnow.OverlayY"/> / Climate distToSealevel/1.5.
    /// </summary>
    public static float GuessAirTempFromHeightLapse(IClientWorldAccessor world, int y)
    {
        try
        {
            var p = world.Player?.Entity?.Pos;
            if (p == null) return float.NaN;
            int sea = world.SeaLevel;
            int px = (int)p.X;
            int pz = (int)p.Z;
            var lowPos = new BlockPos(px, sea, pz);
            var highPos = new BlockPos(px, sea + 150, pz);
            ClimateCondition? low = world.BlockAccessor.GetClimateAt(
                lowPos, EnumGetClimateMode.ForSuppliedDate_TemperatureOnly, world.Calendar.TotalDays);
            ClimateCondition? high = world.BlockAccessor.GetClimateAt(
                highPos, EnumGetClimateMode.ForSuppliedDate_TemperatureOnly, world.Calendar.TotalDays);
            if (low == null || high == null) return float.NaN;

            float lapsePerBlock = (low.Temperature - high.Temperature) / 150f;
            if (lapsePerBlock <= 0.001f)
            {
                // Flat stub climate — fall back to vanilla 1/1.5 °C per block.
                lapsePerBlock = 1f / 1.5f;
                return low.Temperature - lapsePerBlock * (y - sea);
            }

            return low.Temperature - lapsePerBlock * (y - sea);
        }
        catch
        {
            return float.NaN;
        }
    }

    /// <summary>
    /// Track A — winter seasonRel curve + Nov–Feb month floor. Spring/Summer/Fall
    /// skip the season curve (incl. fallTease); month floor still covers Dec in Fall.
    /// </summary>
    public static float CalendarFrostAmount(
        IClientWorldAccessor world, Block block, int x, int y, int z, float mixCap)
    {
        _ = block;
        var pos = new BlockPos(x, y, z);
        float monthAmt = CalendarMonthFrostFloor(world) * mixCap;
        if (!IsCalendarWinter(world, pos)) return monthAmt;
        float seasonAmt = FrostAmountFromSeasonRel(SafeSeasonRel(world, pos)) * mixCap;
        return Math.Max(seasonAmt, monthAmt);
    }

    /// <summary>
    /// Track B — climate heatmap frost for vanilla <see cref="Block.Frostable"/> blocks
    /// (API: shader frost overlay below freezing). Covers leaves, bushes, tallgrass,
    /// ferns, grass-covered soil, etc. Bare soil <c>*-none</c> is not Frostable.
    /// Tip: mild cold pockets (Winter only); hard: near/below freezing. Mix strength
    /// still uses <see cref="IsGroundFrost"/> (murky grass/soil vs bright canopy).
    /// </summary>
    public static float FreezeLineFrostAmount(
        IClientWorldAccessor world, Block block, int y, float airTempC, float mixCap) =>
        FreezeLineFrostAmount(world, block, y, airTempC, mixCap, allowTip: true);

    public static float FreezeLineFrostAmount(
        IClientWorldAccessor world, Block block, int y, float airTempC, float mixCap, bool allowTip)
    {
        if (float.IsNaN(airTempC)) return 0f;
        // Piggyback the game's own frost bit — do not invent plant/leaves lists, and
        // do not skip grass/soil (those were wrongly excluded via IsGroundFrost).
        if (!block.Frostable) return 0f;

        _ = world;
        _ = y;

        float tip = 0f;
        if (allowTip && airTempC < FreezeLineTipStartC)
        {
            float tipT = airTempC <= FreezeLineStartC
                ? 1f
                : GameMath.Clamp(
                    (FreezeLineTipStartC - airTempC) / (FreezeLineTipStartC - FreezeLineStartC),
                    0f, 1f);
            tip = tipT * mixCap * FreezeLineTipScale;
        }

        float hard = 0f;
        if (airTempC < FreezeLineStartC)
        {
            float hardT = (FreezeLineStartC - airTempC) / (FreezeLineStartC - FreezeLineFullC);
            hardT = GameMath.Clamp(hardT, 0f, 1f);
            hard = hardT * mixCap * FreezeLineFrostScale;
        }

        return Math.Max(tip, hard);
    }

    /// <summary>Alias for probes — freeze-line track (was spring-alpine residual).</summary>
    public static float SpringAlpineFreezeFrost(
        IClientWorldAccessor world, Block block, int y, float airTempC, float mixCap) =>
        FreezeLineFrostAmount(world, block, y, airTempC, mixCap, allowTip: false);

    public static float SafeSeasonRel(IClientWorldAccessor world, BlockPos pos)
    {
        try
        {
            return world.Calendar.GetSeasonRel(pos);
        }
        catch
        {
            return world.Calendar.YearRel;
        }
    }

    /// <summary>
    /// 0..1 frost intensity from GameCalendar.GetSeason / GetSeasonRel thresholds.
    /// Winter = seasonRel ≥ WinterStart or &lt; WinterEnd; peaks mid-winter.
    /// Late fall ramps hard into winter — Dec is often still Fall (rel &lt; 0.9726).
    /// </summary>
    public static float FrostAmountFromSeasonRel(float seasonRel)
    {
        seasonRel = GameMath.Mod(seasonRel, 1f);

        // Early winter ramp (Fall → Winter boundary → year wrap).
        if (seasonRel >= SeasonRelWinterStart)
        {
            float t = (seasonRel - SeasonRelWinterStart) / (1f - SeasonRelWinterStart);
            return GameMath.Lerp(0.55f, 0.95f, GameMath.Clamp(t, 0f, 1f));
        }

        // Deep winter through spring edge.
        if (seasonRel < SeasonRelWinterEnd)
        {
            float mid = SeasonRelWinterEnd * 0.42f;
            if (seasonRel <= mid)
                return GameMath.Lerp(0.90f, 1f, seasonRel / Math.Max(1e-4f, mid));
            float fade = (seasonRel - mid) / Math.Max(1e-4f, SeasonRelWinterEnd - mid);
            return GameMath.Lerp(1f, 0.20f, GameMath.Clamp(fade, 0f, 1f));
        }

        // Late-fall → winter: Dec often sits here (rel≈0.93) while Month==12.
        const float fallTease = 0.85f;
        if (seasonRel >= fallTease)
        {
            float t = (seasonRel - fallTease) / (SeasonRelWinterStart - fallTease);
            return GameMath.Lerp(0.30f, 0.85f, GameMath.Clamp(t, 0f, 1f));
        }

        return 0f;
    }

    /// <summary>World-locked multi-octave flecks (0..1) for baked ground frost.</summary>
    public static float FrostMottle01(int x, int z)
    {
        float fine = GameMath.MurmurHash3Mod(x, 19, z, 1024) / 1023f;
        float mid = GameMath.MurmurHash3Mod(x >> 1, 73, z >> 1, 1024) / 1023f;
        float coarse = GameMath.MurmurHash3Mod(x >> 3, 41, z >> 3, 1024) / 1023f;
        // Bilinear + smoothstep across the four neighboring 40-cells so a 64-edge
        // is not a hard integer plate. Finer octaves still speckle the interior.
        float landscape = FrostLandscapeBilinear(x, z);
        float blob = fine * 0.15f + mid * 0.18f + coarse * 0.22f + landscape * 0.45f;
        return GameMath.Clamp(blob, 0f, 1f);
    }

    static float FrostLandscapeBilinear(int x, int z)
    {
        int step = FrostLandscapeStep;
        int gx0 = FloorDiv(x, step);
        int gz0 = FloorDiv(z, step);
        int x0 = gx0 * step;
        int z0 = gz0 * step;
        float tx = (x - x0) / (float)step;
        float tz = (z - z0) / (float)step;
        tx = tx * tx * (3f - 2f * tx);
        tz = tz * tz * (3f - 2f * tz);
        float h00 = GameMath.MurmurHash3Mod(gx0, 91, gz0, 1024) / 1023f;
        float h10 = GameMath.MurmurHash3Mod(gx0 + 1, 91, gz0, 1024) / 1023f;
        float h01 = GameMath.MurmurHash3Mod(gx0, 91, gz0 + 1, 1024) / 1023f;
        float h11 = GameMath.MurmurHash3Mod(gx0 + 1, 91, gz0 + 1, 1024) / 1023f;
        float a = h00 + (h10 - h00) * tx;
        float b = h01 + (h11 - h01) * tx;
        return a + (b - a) * tz;
    }

    static int FloorDiv(int a, int b) =>
        a >= 0 ? a / b : (a - (b - 1)) / b;

    /// <summary>Bin for palette splitting (same BlockId, different speckles).</summary>
    public static int FrostMottleBin(int x, int z)
    {
        float m = FrostMottle01(x, z);
        int bin = (int)(m * FrostMottleBins);
        return GameMath.Clamp(bin, 0, FrostMottleBins - 1);
    }

    /// <summary>
    /// Wood / log / trunk / bare branch — never frost-white. Leaves stay foliage.
    /// </summary>
    public static bool IsWoodyTrunk(Block block)
    {
        if (block.BlockMaterial == EnumBlockMaterial.Wood) return true;
        if (block.BlockMaterial == EnumBlockMaterial.Leaves) return false;
        string? path = block.Code?.Path;
        if (path == null) return false;
        if (path.Contains("leaves", StringComparison.Ordinal)
            || (path.Contains("leaf", StringComparison.Ordinal)
                && !path.StartsWith("leaflitter", StringComparison.Ordinal)))
            return false;
        return path.Contains("log", StringComparison.Ordinal)
            || path.Contains("trunk", StringComparison.Ordinal)
            || path.Contains("branch", StringComparison.Ordinal);
    }

    /// <summary>
    /// Leaves / needle canopy — frost-gray in winter, own climate×season otherwise.
    /// Not wood, not soil. Never FlagFrostGround.
    /// </summary>
    public static bool IsFoliageFrost(Block block)
    {
        if (IsWoodyTrunk(block)) return false;
        if (block.BlockMaterial == EnumBlockMaterial.Leaves) return true;
        string? path = block.Code?.Path;
        if (path == null) return false;
        if (path.Contains("leaves", StringComparison.Ordinal)) return true;
        if (path.Contains("leaf", StringComparison.Ordinal)
            && !path.StartsWith("leaflitter", StringComparison.Ordinal))
            return true;
        // Needles / pine-style foliage often code as leaves-*; fruit-tree foliage too.
        if (path.Contains("foliage", StringComparison.Ordinal)) return true;
        return false;
    }

    public static bool IsGroundFrost(Block block)
    {
        // Mottled grass tops only. Real snow/ice is FlagSnow bright white — never here.
        if (LodBlockPolicy.IsSnowLayer(block) || LodBlockPolicy.IsClimateUntinted(block))
            return false;
        if (IsWoodyTrunk(block) || IsFoliageFrost(block)) return false;
        if (block.BlockMaterial == EnumBlockMaterial.Soil) return true;
        string? path = block.Code?.Path;
        if (path != null
            && (path.StartsWith("soil-", StringComparison.Ordinal)
                || path.StartsWith("forestfloor", StringComparison.Ordinal)
                || path.StartsWith("tallgrass-", StringComparison.Ordinal)
                || path.StartsWith("fern-", StringComparison.Ordinal)))
            return true;
        string? season = block.SeasonColorMap;
        if (season != null
            && (season.Equals("seasonalGrass", StringComparison.OrdinalIgnoreCase)
                || season.IndexOf("Grass", StringComparison.OrdinalIgnoreCase) >= 0))
            return true;
        return false;
    }

    /// <summary>Leaf frost-gray; grass mottled spots; wood/rock none.</summary>
    public static float FrostMixCap(Block block)
    {
        if (IsWoodyTrunk(block)) return 0f;
        if (IsFoliageFrost(block)) return FrostMaxMixCanopy;
        if (IsGroundFrost(block)) return FrostMaxMixGround;
        return 0f;
    }

    /// <summary>
    /// Soft invent curve (16°C→−2°C). NOT used by bake — caused April mid white ring.
    /// Probes may still log it for comparison vs freeze-line.
    /// </summary>
    public static float FrostAmountFromAirTemp(float airTempC)
    {
        if (float.IsNaN(airTempC) || airTempC >= FrostStartTempC) return 0f;
        if (airTempC <= FrostFullTempC) return 1f;
        return GameMath.Clamp(
            (FrostStartTempC - airTempC) / (FrostStartTempC - FrostFullTempC), 0f, 1f);
    }

    /// <summary>0.7.92 restore: pass-through (probes still call this).</summary>
    public static float ScaleFrostByLocalAir(float frost, float airTempC) => frost;

    /// <summary>
    /// Calendar month frost floor (Dec/Jan strong). VS GetSeason still says Fall for
    /// early December (seasonRel &lt; 0.9726) — month is what players mean by winter.
    /// </summary>
    public static float CalendarMonthFrostFloor(IClientWorldAccessor world)
    {
        try
        {
            int month = world.Calendar.Month;
            if (month == 12 || month == 1) return WinterMonthFrostFloor;
            if (month == 2 || month == 11) return WinterMonthFrostFloor * 0.55f;
        }
        catch { /* */ }
        return 0f;
    }

    /// <summary>Probe/join floor: max(season curve, Dec/Jan month floor).</summary>
    public static float CalendarWinterFrostFloor(IClientWorldAccessor world)
    {
        try
        {
            var p = world.Player?.Entity?.Pos;
            var pos = p != null
                ? new BlockPos((int)p.X, (int)p.Y, (int)p.Z)
                : new BlockPos(0);
            float season = FrostAmountFromSeasonRel(SafeSeasonRel(world, pos));
            return Math.Max(season, CalendarMonthFrostFloor(world));
        }
        catch { /* */ }
        return CalendarMonthFrostFloor(world);
    }

    static bool IsFrostCandidate(Block block)
    {
        if (LodBlockPolicy.IsClimateUntinted(block)) return false;
        if ((LodBlockPolicy.FlagsFor(block) & LodPaletteEntry.FlagWater) != 0) return false;
        if (IsWoodyTrunk(block)) return false;
        // Leaves white + grass mottling only — not every SeasonColorMap block.
        if (IsFoliageFrost(block) || IsGroundFrost(block)) return true;
        return false;
    }

    /// <summary>R-low RGB lerp (same packing as palette / mesh).</summary>
    public static int ColorOverlayRgb(int from, int to, float w)
    {
        w = GameMath.Clamp(w, 0f, 1f);
        int fr = from & 0xFF, fg = (from >> 8) & 0xFF, fb = (from >> 16) & 0xFF;
        int tr = to & 0xFF, tg = (to >> 8) & 0xFF, tb = (to >> 16) & 0xFF;
        int r = (int)(fr + (tr - fr) * w + 0.5f);
        int g = (int)(fg + (tg - fg) * w + 0.5f);
        int b = (int)(fb + (tb - fb) * w + 0.5f);
        return unchecked((int)0xFF000000) | (b << 16) | (g << 8) | r;
    }

    /// <summary>
    /// Vanilla climate×season multiply at a column. Calendar season is always
    /// <c>YearRel</c> inside ApplyColorMap. Location needs ClimateMap: when the
    /// column's region is unloaded, feed rain+temp from a neighbor packed sample
    /// (succinct far preview) instead of the engine's mild placeholder.
    /// </summary>
    public static int ApplyLodColorMaps(
        IClientWorldAccessor world,
        string? climateMap,
        string? seasonMap,
        int color,
        int x,
        int y,
        int z)
    {
        TryProbeClimateAvailability(
            world, x, z, out _, out bool climateMapOk, out int packed, out bool fromNeighbor);

        if (climateMapOk && !IsPlaceholderClimate(packed) && fromNeighbor)
        {
            // Public rain/temp overload still uses Calendar.YearRel for season maps.
            // Pass height-adjusted temp because that overload fixes heightAboveSealevel=0.
            int unscaledTemp = (packed >> 16) & 0xFF;
            int rain = Climate.GetRainFall((packed >> 8) & 0xFF, y);
            int temp = Climate.GetAdjustedTemperature(unscaledTemp, y - world.SeaLevel);
            return world.ApplyColorMapOnRgba(climateMap, seasonMap, color, rain, temp, flipRb: true);
        }

        return world.ApplyColorMapOnRgba(climateMap, seasonMap, color, x, y, z);
    }

    public static void SampleFinalTint(
        IClientWorldAccessor world,
        Block block,
        int x,
        int y,
        int z,
        LodUntintedShare share,
        Block? plantTintFallback,
        out float r,
        out float g,
        out float b)
    {
        Block sample = block;
        if (sample.ClimateColorMapResolved == null && sample.SeasonColorMapResolved == null
            && sample.BlockMaterial == EnumBlockMaterial.Plant && plantTintFallback != null)
        {
            sample = plantTintFallback;
        }

        string? climate = sample.ClimateColorMapResolved != null ? sample.ClimateColorMap : null;
        string? season = sample.SeasonColorMapResolved != null ? sample.SeasonColorMap : null;

        if (climate == null && season == null)
        {
            r = g = b = 1f;
            return;
        }

        int rgba = ApplyLodColorMaps(
            world, climate, season, unchecked((int)0xFFFFFFFF), x, y, z);

        r = ((rgba >> 16) & 0xFF) / 255f;
        g = ((rgba >> 8) & 0xFF) / 255f;
        b = (rgba & 0xFF) / 255f;
        LodTintRegistry.ClampTintAwayFromWhite(ref r, ref g, ref b);

        r = LodTopSoil.Dilute(share.R, r);
        g = LodTopSoil.Dilute(share.G, g);
        b = LodTopSoil.Dilute(share.B, b);
    }

    /// <summary>Climate-only sample (slot tables). Season stays on the mesh bake / live clock.</summary>
    public static void SampleClimateTint(
        IClientWorldAccessor world,
        Block block,
        int x,
        int y,
        int z,
        LodUntintedShare share,
        Block? plantTintFallback,
        out float r,
        out float g,
        out float b)
    {
        Block sample = block;
        if (sample.ClimateColorMapResolved == null && sample.SeasonColorMapResolved == null
            && sample.BlockMaterial == EnumBlockMaterial.Plant && plantTintFallback != null)
        {
            sample = plantTintFallback;
        }

        string? climate = sample.ClimateColorMapResolved != null ? sample.ClimateColorMap : null;
        if (climate == null)
        {
            r = g = b = 1f;
            return;
        }

        int rgba = ApplyLodColorMaps(
            world, climate, (string?)null, unchecked((int)0xFFFFFFFF), x, y, z);

        r = ((rgba >> 16) & 0xFF) / 255f;
        g = ((rgba >> 8) & 0xFF) / 255f;
        b = (rgba & 0xFF) / 255f;
        LodTintRegistry.ClampTintAwayFromWhite(ref r, ref g, ref b);

        r = LodTopSoil.Dilute(share.R, r);
        g = LodTopSoil.Dilute(share.G, g);
        b = LodTopSoil.Dilute(share.B, b);
    }

    public static int MultiplyRgb(int color, float tr, float tg, float tb)
    {
        int ir = Math.Clamp((int)((color & 0xFF) * tr + 0.5f), 0, 255);
        int ig = Math.Clamp((int)(((color >> 8) & 0xFF) * tg + 0.5f), 0, 255);
        int ib = Math.Clamp((int)(((color >> 16) & 0xFF) * tb + 0.5f), 0, 255);
        return unchecked((int)0xFF000000) | ib << 16 | ig << 8 | ir;
    }

    /// <summary>
    /// Restore untinted atlas RGB + live tint slot on FlagBaked vegetation leftovers.
    /// FlagSnow / ice stay identity band 3. Never seas/clim on baked RGB.
    /// </summary>
    public static int UnbakeLiveTintEntries(
        IClientWorldAccessor world,
        LodSection section,
        long sectionKey,
        System.Func<Block, int, int, int, (int Color, LodUntintedShare Share)> untintedOf,
        System.Func<Block, byte> tintSlotOf)
    {
        int changed = 0;
        for (int pid = 0; pid < section.Palette.Count; pid++)
        {
            LodPaletteEntry entry = section.Palette[pid];
            if ((entry.Flags & LodPaletteEntry.FlagSnow) != 0) continue;
            if ((entry.Flags & LodPaletteEntry.FlagBaked) == 0) continue;
            if (entry.BlockId <= 0) continue;
            if ((uint)entry.BlockId >= (uint)world.Blocks.Count) continue;
            Block block = world.Blocks[entry.BlockId];
            if (LodBlockPolicy.IsSnowLayer(block) || LodBlockPolicy.IsClimateUntinted(block))
                continue;
            if (!section.TryFindPaletteTop(sectionKey, pid, out int x, out int y, out int z))
                continue;

            (int untinted, _) = untintedOf(block, x, y, z);
            byte slot = tintSlotOf(block);
            byte flags = (byte)(entry.Flags
                & ~LodPaletteEntry.FlagBaked
                & ~LodPaletteEntry.FlagFrostGround);
            if (untinted == entry.Color
                && entry.TintSlot == slot
                && entry.Flags == flags)
                continue;
            entry.Color = untinted;
            entry.Flags = flags;
            entry.TintSlot = slot;
            section.Palette[pid] = entry;
            changed++;
        }
        if (changed > 0) section.InvalidatePaletteSnapshot();
        return changed;
    }

    /// <summary>
    /// Unbake FlagBaked vegetation to live tint, melt leftover inferred Cover snow,
    /// strip leftover seasonal snowlayer. Does not bake climate×season into RGB
    /// and does not invent FlagSnow on far plates.
    /// </summary>
    public static int RebakeSection(
        IClientWorldAccessor world,
        LodSection section,
        long sectionKey,
        Block? plantTintFallback,
        System.Func<Block, int, int, int, (int Color, LodUntintedShare Share)> untintedOf,
        System.Func<Block, byte> tintSlotOf,
        int loadedTruthEpoch = int.MinValue)
    {
        int changed = UnbakeLiveTintEntries(world, section, sectionKey, untintedOf, tintSlotOf);
        // Rewrite live-tint topsoil RGB to greener composites (0.8.45). Old SQLite
        // rows kept dirt-mean browns; UpgradeLegacy only cleared FlagBaked.
        changed += RefreshGrassTopsoilAlbedo(world, section, sectionKey, untintedOf, tintSlotOf);
        changed += MeltInferredSnowWhereHostIsGreen(
            world, section, sectionKey, plantTintFallback, untintedOf);
        changed += CoverGroundSnowColumns(world, section, sectionKey, loadedTruthEpoch);
        if (changed > 0) section.InvalidatePaletteSnapshot();
        return changed;
    }

    /// <summary>
    /// Refresh TopSoil / grass-covered soil palette RGB from current untintedOf
    /// (greener composite). Marks change so SeasonDirty remesh replaces brown plates.
    /// Does not ForceRecapture. Snow/rock/climate-untinted skipped.
    /// </summary>
    public static int RefreshGrassTopsoilAlbedo(
        IClientWorldAccessor world,
        LodSection section,
        long sectionKey,
        System.Func<Block, int, int, int, (int Color, LodUntintedShare Share)> untintedOf,
        System.Func<Block, byte> tintSlotOf)
    {
        int changed = 0;
        for (int pid = 0; pid < section.Palette.Count; pid++)
        {
            LodPaletteEntry entry = section.Palette[pid];
            if ((entry.Flags & LodPaletteEntry.FlagSnow) != 0) continue;
            if ((entry.Flags & LodPaletteEntry.FlagBaked) != 0) continue;
            if (entry.BlockId <= 0) continue;
            if ((uint)entry.BlockId >= (uint)world.Blocks.Count) continue;
            Block block = world.Blocks[entry.BlockId];
            if (LodBlockPolicy.IsSnowLayer(block) || LodBlockPolicy.IsClimateUntinted(block))
                continue;
            // TopSoil render pass = grass-covered ground. Also seasonalGrass maps.
            bool topSoil = block.RenderPass == EnumChunkRenderPass.TopSoil;
            bool grassMap = !string.IsNullOrEmpty(block.SeasonColorMap)
                && (block.SeasonColorMap.Equals("seasonalGrass", StringComparison.OrdinalIgnoreCase)
                    || block.SeasonColorMap.IndexOf("Grass", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!topSoil && !grassMap) continue;
            if (!section.TryFindPaletteTop(sectionKey, pid, out int x, out int y, out int z))
                continue;

            (int untinted, _) = untintedOf(block, x, y, z);
            byte slot = tintSlotOf(block);
            if (untinted == entry.Color && entry.TintSlot == slot) continue;
            entry.Color = untinted;
            entry.TintSlot = slot;
            section.Palette[pid] = entry;
            changed++;
        }
        return changed;
    }

    /// <summary>
    /// Bake each foliage run at that column's world XZ and Y. Merge rows that share
    /// a seasonal look (including winter gray). Split green vs autumn. Never
    /// FlagFrostGround / FrostMottleBin on leaves.
    /// </summary>
    public static int RebakeFoliageColumns(
        IClientWorldAccessor world,
        LodSection section,
        long sectionKey,
        Block? plantTintFallback,
        System.Func<Block, int, int, int, (int Color, LodUntintedShare Share)> untintedOf)
    {
        int cols = LodSection.GridSize * LodSection.GridSize;
        int changed = 0;
        for (int col = 0; col < cols; col++)
        {
            if (!section.Captured[col]) continue;
            int from = section.ColumnStart[col];
            int to = section.ColumnStart[col + 1];
            for (int r = from; r < to; r++)
            {
                ulong run = section.Runs[r];
                int pid = LodSection.RunPaletteId(run);
                if ((uint)pid >= (uint)section.Palette.Count) continue;
                LodPaletteEntry entry = section.Palette[pid];
                if (entry.BlockId <= 0) continue;
                if ((uint)entry.BlockId >= (uint)world.Blocks.Count) continue;
                Block block = world.Blocks[entry.BlockId];
                if (!IsFoliageFrost(block)) continue;
                if (LodBlockPolicy.IsSnowLayer(block) || LodBlockPolicy.IsClimateUntinted(block))
                    continue;
                if ((entry.Flags & LodPaletteEntry.FlagSnow) != 0) continue;

                (int x, int y, int z) = LodPipeline.CaptureBlockPos(sectionKey, col, run);
                (int untinted, LodUntintedShare share) = untintedOf(block, x, y, z);
                int baked = BakePaletteColor(world, block, untinted, x, y, z, share, plantTintFallback);
                byte flags = (byte)((entry.Flags | LodPaletteEntry.FlagBaked)
                    & ~LodPaletteEntry.FlagFrostGround);
                int newPid = section.FindOrAddFoliageLook(entry.BlockId, baked, flags);
                if (newPid != pid)
                {
                    section.Runs[r] = LodSection.PackRun(
                        newPid, LodSection.RunYTop(run), LodSection.RunYBottom(run));
                    changed++;
                }
            }
        }
        return changed;
    }

    public static bool SectionHasBakedEntries(LodSection section)
    {
        for (int i = 0; i < section.Palette.Count; i++)
        {
            if ((section.Palette[i].Flags & LodPaletteEntry.FlagBaked) != 0) return true;
        }
        return false;
    }

    /// <summary>
    /// True when FlagBaked vegetation leftover must unbake to live tint, or when
    /// FlagBaked still carries a live season slot (0.7.84/85 seas/clim leftovers).
    /// Live-tint without FlagBaked is current. FlagSnow identity does not heal.
    /// </summary>
    public static bool SectionNeedsLegacyHeal(LodSection section)
    {
        for (int i = 0; i < section.Palette.Count; i++)
        {
            LodPaletteEntry e = section.Palette[i];
            if ((e.Flags & LodPaletteEntry.FlagSnow) != 0) continue;
            if ((e.Flags & LodPaletteEntry.FlagBaked) != 0) return true;
        }
        return false;
    }

    public static int HealOrRepaintSection(
        IClientWorldAccessor world,
        LodSection section,
        long sectionKey,
        Block? plantTintFallback,
        System.Func<Block, int, int, int, (int Color, LodUntintedShare Share)> untintedOf,
        System.Func<Block, byte> tintSlotOf,
        int loadedTruthEpoch = int.MinValue)
    {
        return RebakeSection(
            world, section, sectionKey, plantTintFallback, untintedOf, tintSlotOf, loadedTruthEpoch);
    }

    public static int UpgradeLegacyEntries(
        IClientWorldAccessor world,
        LodSection section,
        long sectionKey,
        Block? plantTintFallback,
        System.Func<Block, int, int, int, (int Color, LodUntintedShare Share)> untintedOf,
        System.Func<Block, byte> tintSlotOf)
    {
        _ = plantTintFallback;
        return UnbakeLiveTintEntries(world, section, sectionKey, untintedOf, tintSlotOf);
    }
}
