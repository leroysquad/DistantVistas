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
    float pendingSampleYLow;
    float pendingSampleYHigh;

    /// <summary>Bumped by Refresh; lets the renderer skip re-uploading unchanged tints.</summary>
    public int Version { get; private set; }
    public float[] TintsLow => tintsLow;
    public float[] TintsHigh => tintsHigh;
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
        // Span the height range terrain actually occupies around the viewer, so the
        // interpolation covers valley floor to peak rather than extrapolating.
        pendingSampleYLow = world.SeaLevel;
        pendingSampleYHigh = world.SeaLevel + 320;
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

                int rgba = world.ApplyColorMapOnRgba(
                    block.ClimateColorMapResolved, block.SeasonColorMapResolved,
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

        // Dilute by the share the tint must not touch, so a slot registered from
        // grass-covered soil reproduces vanilla's top-soil shader: the bare dirt that
        // shows through the overlay stays the colour it already is.
        into[slot * 4 + 0] = LodTopSoil.Dilute(share.R, r / scale);
        into[slot * 4 + 1] = LodTopSoil.Dilute(share.G, g / scale);
        into[slot * 4 + 2] = LodTopSoil.Dilute(share.B, b / scale);
        into[slot * 4 + 3] = 1f;
    }
}
