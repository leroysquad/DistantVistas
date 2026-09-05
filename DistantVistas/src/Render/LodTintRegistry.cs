using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Maps each block to a tint SLOT, and keeps every slot's live colour up to date.
///
/// Vintage Story does not have one foliage tint: leaves pick a seasonal map per
/// species (seasonalOak, seasonalNeedles, seasonalBirch, seasonalMaple, ...) on top of
/// one of several climate maps, and water has its own climateWaterTint. Collapsing all
/// of that into a single "foliage" tint meant every leaf in the LOD took whichever
/// block the registry scan happened to hit first - a conifer, so nothing ever turned
/// for autumn - and water was left untinted grey.
///
/// A slot is one distinct (climate map, season map) pair. The captured colour stays
/// untinted and the slot's colour is recomputed from the game's own colour maps every
/// few seconds, so distant terrain follows the calendar without re-capturing anything.
///
/// Slots are derived from the live Block, never persisted: an existing cache picks up
/// correct per-species tints with no re-exploration, and the mapping stays right if a
/// game or mod update changes which map a block uses.
/// </summary>
/// <summary>
/// The share of a block's stored colour that the live tint must NOT touch, per channel.
///
/// Grass-covered ground is not one surface. Vanilla's own top-soil shader draws it as
/// <c>brownSoil * (1 - grass.a) + grass * grass.a</c>: the grass overlay is colour-mapped,
/// and the bare dirt showing through it is left exactly as it is. The overlay is only about
/// 69% opaque for full coverage and less for the sparse variants, so roughly a third of
/// every grassy block is untinted brown - which is what makes vanilla ground read olive
/// rather than green, and where nearly all of its blue comes from.
///
/// A LOD vertex carries one colour, so the split has to live here instead: the stored
/// colour is the composite, and the slot's tint is diluted to
/// <c>share + (1 - share) * tint</c>, which reproduces the shader exactly for the block the
/// slot was registered from.
/// </summary>
public readonly struct LodUntintedShare
{
    public readonly float R, G, B;

    public LodUntintedShare(float r, float g, float b) { R = r; G = g; B = b; }

    /// <summary>Everything is tinted: the ordinary case, and what a plain block gets.</summary>
    public static LodUntintedShare None => default;

    /// <summary>Coarse key so blocks of the same coverage share one slot, at 1/8 steps.</summary>
    public int Bucket => (int)((R + G + B) / 3f * 8f + 0.5f);
}

/// <summary>
/// Vanilla's top-soil compositing, as arithmetic: `chunktopsoil.fsh` draws grass-covered
/// ground as <c>brownSoil * (1 - grass.a) + grass * grass.a</c>, colour-mapping only the
/// grass. A LOD vertex carries one colour, so the composite is stored and the tint is
/// diluted by the share that came from untinted dirt.
///
/// The identity that makes that exact:
///   composite * (share + (1 - share) * tint) == soil * (1 - a) + grass * a * tint
/// where composite = soil * (1 - a) + grass * a and share = soil * (1 - a) / composite.
/// A check holds it, because everything the renderer does with these two values assumes it.
/// </summary>
public static class LodTopSoil
{
    /// <summary>One channel of the composite: what the face averages to, untinted.</summary>
    public static float Composite(float soil, float grass, float coverage) =>
        soil * (1f - coverage) + grass * coverage;

    /// <summary>The share of that channel the live tint must leave alone.</summary>
    public static float UntintedShare(float soil, float grass, float coverage)
    {
        float composite = Composite(soil, grass, coverage);
        return composite <= 0f ? 0f : Math.Clamp(soil * (1f - coverage) / composite, 0f, 1f);
    }

    /// <summary>The diluted tint a slot holds, from the sampled tint and the share.</summary>
    public static float Dilute(float share, float tint) => share + (1f - share) * tint;

    /// <summary>
    /// Slight coverage boost for topsoil composite so stable captures read greener
    /// than dirt-heavy atlas means (0.8.45 bake; kept in 0.8.46).
    /// </summary>
    public static float GreenerCoverage(float coverage) =>
        Math.Min(1f, coverage * 1.06f + 0.03f);
}

public class LodTintRegistry
{
    /// <summary>Slot 0 is the identity tint, used by everything with no colour map.</summary>
    public const int SlotNone = 0;

    /// <summary>
    /// Kept small on purpose: the alpha byte carries the slot, and the shader holds one
    /// vec3 per slot. 64 covers every map pair in the base game with room to spare.
    /// </summary>
    public const int MaxSlots = 64;

    // MaxSlots is also hardcoded as `const int TINT_SLOTS` in lodterrain.vsh/.fsh, because
    // this game version offers no way to inject a #define. There used to be a second C#
    // constant mirroring that number by hand, compared against MaxSlots at shader load -
    // but comparing two constants in the same file cannot detect a shader being edited,
    // and the compiler said so, flagging the branch as unreachable. The real check reads
    // the shader files: see StaticAssetChecks in the fast tier of scripts/check.sh.

    readonly Dictionary<(string?, string?, int), int> slotByMaps = new();
    readonly List<Block?> representative = new();

    /// <summary>Per slot, the share of the colour the tint must leave alone. See LodUntintedShare.</summary>
    readonly List<LodUntintedShare> untintedShare = new();

    // vec4 per slot: the uniform upload path takes 4 components per element.
    // Two altitude samples per slot, because the climate maps are indexed by
    // temperature and temperature falls with height - the same lapse rate the snow
    // line uses. Sampling once at the player's feet painted mountaintops with valley
    // green instead of the colder, redder grass that actually grows up there. The
    // shader interpolates between these by vertex height.
    readonly float[] tintsLow = new float[MaxSlots * 4];
    readonly float[] tintsHigh = new float[MaxSlots * 4];
    readonly float[] pendingTintsLow = new float[MaxSlots * 4];
    readonly float[] pendingTintsHigh = new float[MaxSlots * 4];
    // Season map colour at the current calendar seasonRel, one vec4 per slot.
    // RGB is the map sample; A is 1 when the slot has a season map, else 0.
    // Water is skipped in the shader even if A is 1. Uploaded every frame.
    readonly float[] seasonTints = new float[MaxSlots * 4];
    float pendingSampleYLow;
    float pendingSampleYHigh;

    /// <summary>Bumped by Refresh; lets the renderer skip re-uploading unchanged tints.</summary>
    public int Version { get; private set; }
    public float[] TintsLow => tintsLow;
    public float[] TintsHigh => tintsHigh;
    public float[] SeasonTints => seasonTints;
    public int SlotCount => representative.Count;

    /// <summary>World Y the two tint tables were sampled at.</summary>
    public float SampleYLow { get; private set; }
    public float SampleYHigh { get; private set; }

    public LodTintRegistry()
    {
        representative.Add(null);              // slot 0: no tint
        untintedShare.Add(LodUntintedShare.None);
        slotByMaps[(null, null, 0)] = SlotNone;
        for (int i = 0; i < tintsLow.Length; i++)
            tintsLow[i] = tintsHigh[i] = pendingTintsLow[i] = pendingTintsHigh[i] = 1f;
        for (int i = 0; i < seasonTints.Length; i += 4)
        {
            seasonTints[i] = seasonTints[i + 1] = seasonTints[i + 2] = 1f;
            seasonTints[i + 3] = 0f;
        }
    }

    /// <summary>
    /// A block carrying climatePlantTint, used for plants that declare no colour map of
    /// their own. Ferns are the case that forced this: their textures ship greyscale
    /// (stored colour is exactly RGB 148,148,148) and vanilla greens them from its block
    /// class rather than from JSON, so an untinted LOD cube came out grey.
    /// </summary>
    public Block? PlantTintFallback;

    /// <summary>
    /// Slot for this block, registering a new one if this (map pair, untinted share) is
    /// unseen. The share is part of the key because two blocks on the same colour maps can
    /// still need different amounts of it - full, sparse and very sparse grass coverage
    /// share `climatePlantTint`/`seasonalGrass` and show quite different amounts of bare
    /// dirt through it.
    /// </summary>
    public int SlotFor(Block? block, LodUntintedShare share)
    {
        if (block == null) return SlotNone;
        if (LodBlockPolicy.IsClimateUntinted(block)) return SlotNone;

        string? climate = block.ClimateColorMapResolved != null ? block.ClimateColorMap : null;
        string? season = block.SeasonColorMapResolved != null ? block.SeasonColorMap : null;

        if (climate == null && season == null)
        {
            return block.BlockMaterial == EnumBlockMaterial.Plant && PlantTintFallback != null
                ? SlotFor(PlantTintFallback, LodUntintedShare.None)
                : SlotNone;
        }

        var key = (climate, season, share.Bucket);
        if (slotByMaps.TryGetValue(key, out int slot)) return slot;

        if (representative.Count >= MaxSlots) return SlotNone; // out of slots: untinted beats wrong
        slot = representative.Count;
        representative.Add(block);
        untintedShare.Add(share);
        slotByMaps[key] = slot;
        return slot;
    }

    /// <summary>
    /// Recompute every slot's colour for the current season and climate, by applying the
    /// game's own maps to white at the given position.
    /// </summary>
    public void Refresh(IClientWorldAccessor world, int x, int z)
    {
        BeginRefresh(world);
        for (int slot = 1; slot < representative.Count; slot++) RefreshSlot(world, x, z, slot);
        CompleteRefresh();
    }

    /// <summary>Start an incremental refresh without changing the currently displayed table.</summary>
    public void BeginRefresh(IClientWorldAccessor world)
    {
        Array.Copy(tintsLow, pendingTintsLow, tintsLow.Length);
        Array.Copy(tintsHigh, pendingTintsHigh, tintsHigh.Length);
        // Valley floor plus a lapse-rate offset for colder mountain grass. 320 used to
        // put the high sample in snow-climate white, so greyscale grass on high peaks
        // multiplied to plastic white while rock sides (slot 0) stayed correct. 160
        // still cools the tint; snow is captured snow blocks plus the alpine overlay.
        pendingSampleYLow = world.SeaLevel;
        pendingSampleYHigh = world.SeaLevel + HighSampleOffsetBlocks;
    }

    /// <summary>Refresh one climate/season tint slot; safe to spread over render frames.</summary>
    public void RefreshSlot(IClientWorldAccessor world, int x, int z, int slot)
    {
        if (slot <= SlotNone || slot >= representative.Count) return;
        Block? block = representative[slot];
        if (block == null) return;

        Sample(world, block, x, (int)pendingSampleYLow, z,
            pendingTintsLow, slot, untintedShare[slot]);
        Sample(world, block, x, (int)pendingSampleYHigh, z,
            pendingTintsHigh, slot, untintedShare[slot]);
        ProtectHighTintFromSnow(pendingTintsLow, pendingTintsHigh, slot);
    }

    /// <summary>Atomically publish the completely refreshed table to the renderer.</summary>
    public void CompleteRefresh()
    {
        Array.Copy(pendingTintsLow, tintsLow, tintsLow.Length);
        Array.Copy(pendingTintsHigh, tintsHigh, tintsHigh.Length);
        SampleYLow = pendingSampleYLow;
        SampleYHigh = pendingSampleYHigh;
        Version++;
    }

    /// <summary>
    /// Positions each tint is averaged over, on a lattice of this many blocks. A seasonal
    /// map is not one colour: `seasonalGrass` is 128x16, and the engine picks the ROW from
    /// a hash of each block's own position, so what a field actually looks like is all
    /// sixteen rows mixed together. A single sample takes one row and paints every distant
    /// field with it - in midsummer the rows run #628100 to #97B825 around a true mean of
    /// #7B9C0D, so the green was off by up to a quarter in red, and it re-rolled every time
    /// the player moved far enough to change the hash.
    ///
    /// 64 positions eight blocks apart cover a 56-block square: wide enough for the hashes
    /// to decorrelate, narrow enough that the climate underneath them is still the player's
    /// own. The renderer updates one slot per frame and publishes the completed table at
    /// once, avoiding both a frame spike and a half-old/half-new seasonal palette.
    /// </summary>
    const int SampleGridSide = 8;
    const int SampleGridStride = 8;

    static void Sample(IClientWorldAccessor world, Block block, int x, int y, int z, float[] into,
        int slot, LodUntintedShare share)
    {
        // Clamped to the map: GetClimate answers 0 - freezing and bone dry - for a position
        // off the edge of the world, and one such sample drags the whole average with it.
        int maxX = world.BlockAccessor.MapSizeX - 1;
        int maxZ = world.BlockAccessor.MapSizeZ - 1;

        int r = 0, g = 0, b = 0;
        for (int i = 0; i < SampleGridSide; i++)
        {
            int sx = GameMath.Clamp(x + (i - SampleGridSide / 2) * SampleGridStride, 0, maxX);
            for (int j = 0; j < SampleGridSide; j++)
            {
                int sz = GameMath.Clamp(z + (j - SampleGridSide / 2) * SampleGridStride, 0, maxZ);

                // Climate only. Season is a live shader clock (seasonRel / seasonTints),
                // the same class of fix as rgbaAmbientIn for night. Baking both maps
                // here froze far land on the last 30s sample, so autumn snapped to
                // grey-green the moment vanilla unloaded.
                int rgba = world.ApplyColorMapOnRgba(
                    block.ClimateColorMap, (string?)null,
                    unchecked((int)0xFFFFFFFF), sx, y, sz);

                // Unpacked by hand rather than through ColorUtil.ToRGBAFloats, which
                // allocates a float[4] per call and this now calls it 64 times per slot
                // per height. The channel order is the one that function uses, and it is
                // the trap here: ApplyColorMapOnRgba flips red and blue by default, so red
                // arrives at bits 16-23. Reading red out of the low byte swapped R and B
                // and turned every grass tint teal.
                r += (rgba >> 16) & 0xFF;
                g += (rgba >> 8) & 0xFF;
                b += rgba & 0xFF;
            }
        }

        const float scale = SampleGridSide * SampleGridSide * 255f;

        float rf = r / scale;
        float gf = g / scale;
        float bf = b / scale;
        ClampTintAwayFromWhite(ref rf, ref gf, ref bf);

        // Dilute by the share the tint must not touch, so a slot registered from
        // grass-covered soil reproduces vanilla's top-soil shader: the bare dirt that
        // shows through the overlay stays the colour it already is.
        into[slot * 4 + 0] = LodTopSoil.Dilute(share.R, rf);
        into[slot * 4 + 1] = LodTopSoil.Dilute(share.G, gf);
        into[slot * 4 + 2] = LodTopSoil.Dilute(share.B, bf);
        into[slot * 4 + 3] = 1f;
    }

    /// <summary>
    /// High climate sample, in blocks above sea. Must stay below the snow band of the
    /// colour maps: those maps are indexed by temperature, and temperature falls with
    /// height, so a sample hundreds of blocks up returns snow-white even in summer.
    /// </summary>
    public const int HighSampleOffsetBlocks = 160;

    /// <summary>
    /// Only pull a climate sample down when it is brighter than this. 0.7.19 used
    /// 0.65 and that crushed greens toward grey. 0.7.18 used 0.78. Never scale
    /// toward grey; this only clamps high-luma (toward white) samples.
    /// Slot 0 is identity and is never sampled. Real snow is a snow block plus overlay.
    /// </summary>
    public const float MaxTintLuminance = 0.78f;

    public static void ClampTintAwayFromWhite(ref float r, ref float g, ref float b)
    {
        float lum = (r + g + b) / 3f;
        if (lum <= MaxTintLuminance || lum <= 0f) return;
        float k = MaxTintLuminance / lum;
        r *= k;
        g *= k;
        b *= k;
    }

    /// <summary>
    /// High climate sample in the snow band of the colour map (low chroma, high
    /// luma). Do not live-tint HIGH grass toward snow white: copy the valley
    /// climate colour instead, which must itself be a real green/brown sample,
    /// not identity or the grey clamp leftover. Real snow is a snow block.
    /// </summary>
    public static bool IsSnowLikeTint(float r, float g, float b)
    {
        float mx = r > g ? r : g;
        if (b > mx) mx = b;
        float mn = r < g ? r : g;
        if (b < mn) mn = b;
        float lum = (r + g + b) / 3f;
        return lum >= 0.62f && (mx - mn) <= 0.12f;
    }

    public static void ProtectHighTintFromSnow(float[] low, float[] high, int slot)
    {
        if (slot <= SlotNone) return;
        int i = slot * 4;
        if (i + 2 >= high.Length || i + 2 >= low.Length) return;
        if (!IsSnowLikeTint(high[i], high[i + 1], high[i + 2])) return;
        // Copying identity or another snow-grey would paint greyscale grass
        // grey at every altitude. Valley has to be an actual climate colour.
        if (IsSnowLikeTint(low[i], low[i + 1], low[i + 2])) return;
        high[i] = low[i];
        high[i + 1] = low[i + 1];
        high[i + 2] = low[i + 2];
    }

    /// <summary>
    /// How much live season to mix onto climate. Water (band 1) is always 0 so
    /// lakes do not pick up autumn. Slot 0 / no season map is 0. Matches the
    /// lodterrain.vsh mix; keep them in lockstep.
    /// </summary>
    public static float LiveSeasonAmount(int band, float seasonAlpha, float seasonWeight)
    {
        if (band == 1) return 0f;
        if (seasonAlpha <= 0f || seasonWeight <= 0f) return 0f;
        float a = seasonAlpha * seasonWeight;
        if (a < 0f) return 0f;
        if (a > 1f) return 1f;
        return a;
    }

    /// <summary>
    /// Vanilla colormap.vsh seasonWeight at sea, from an unscaled 0..255 worldgen
    /// temperature byte. Temperate (~128) is about 0.93, so autumn actually shows.
    /// </summary>
    public static float SeasonWeightFromTempByte(float unscaledTemp)
    {
        float x = unscaledTemp;
        if (x < 0f) x = 0f;
        if (x > 255f) x = 255f;
        float w = 0.5f - MathF.Cos(x / 42f) / 2.3f
            + Math.Max(0f, 128f - x) / 512f
            - Math.Max(0f, x - 130f) / 200f;
        if (w < 0f) return 0f;
        if (w > 1f) return 1f;
        return w;
    }

    /// <summary>
    /// Inverse of Climate.GetScaledAdjustedTemperatureFloat at sea level.
    /// WorldGenTemperature is celsius; the shader formula wants the 0..255 byte.
    /// </summary>
    public static float UnscaledTempByteFromCelsius(float tempC)
    {
        float unscaled = (tempC - 12.5f) * 5.06f + 128f;
        if (unscaled < 0f) return 0f;
        if (unscaled > 255f) return 255f;
        return unscaled;
    }

    /// <summary>
    /// Resample every slot's season map at the current calendar. Cheap enough
    /// for every frame so /time and walking out of vanilla range keep autumn.
    /// Does not recapture terrain; the mesh albedo is unchanged.
    /// </summary>
    public void RefreshSeason(IClientWorldAccessor world, int x, int z)
    {
        for (int slot = 1; slot < representative.Count; slot++)
            RefreshSeasonSlot(world, x, z, slot);
    }

    void RefreshSeasonSlot(IClientWorldAccessor world, int x, int z, int slot)
    {
        int i = slot * 4;
        Block? block = representative[slot];
        if (block == null || block.SeasonColorMapResolved == null)
        {
            seasonTints[i] = seasonTints[i + 1] = seasonTints[i + 2] = 1f;
            seasonTints[i + 3] = 0f;
            return;
        }

        int y = world.SeaLevel;
        SampleSeason(world, block, x, y, z, seasonTints, slot, untintedShare[slot]);
        seasonTints[i + 3] = 1f;
    }

    const int SeasonSampleCount = 8;

    static void SampleSeason(IClientWorldAccessor world, Block block, int x, int y, int z, float[] into,
        int slot, LodUntintedShare share)
    {
        int maxX = world.BlockAccessor.MapSizeX - 1;
        int maxZ = world.BlockAccessor.MapSizeZ - 1;

        int r = 0, g = 0, b = 0;
        for (int n = 0; n < SeasonSampleCount; n++)
        {
            int sx = GameMath.Clamp(x + (n - SeasonSampleCount / 2) * SampleGridStride, 0, maxX);
            int sz = GameMath.Clamp(z, 0, maxZ);
            int rgba = world.ApplyColorMapOnRgba(
                (string?)null, block.SeasonColorMap,
                unchecked((int)0xFFFFFFFF), sx, y, sz);
            r += (rgba >> 16) & 0xFF;
            g += (rgba >> 8) & 0xFF;
            b += rgba & 0xFF;
        }

        const float scale = SeasonSampleCount * 255f;
        float rf = r / scale;
        float gf = g / scale;
        float bf = b / scale;
        into[slot * 4 + 0] = LodTopSoil.Dilute(share.R, rf);
        into[slot * 4 + 1] = LodTopSoil.Dilute(share.G, gf);
        into[slot * 4 + 2] = LodTopSoil.Dilute(share.B, bf);
    }
}
