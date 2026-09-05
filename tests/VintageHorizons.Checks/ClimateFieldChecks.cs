namespace DistantVistas.Checks;

/// <summary>
/// Spatial climate: two hills differ, grass and leaves on one hill share the
/// sample, and walking does not rewrite a far cell.
/// </summary>
public static class ClimateFieldChecks
{
    public static void Run(Check c)
    {
        TwoClimatesDiffer(c);
        GrassAndLeafShareClimate(c);
        WalkDoesNotRecolorFar(c);
        UnfilledFallsBackToKeep(c);
        SeasonWeightUsesLocalTemp(c);
        CellBlocksAvoidSectionSnap(c);
        AdjacentSectionsShareWorldSamples(c);
    }

    static void CellBlocksAvoidSectionSnap(Check c)
    {
        c.Eq(40, LodClimateField.CellBlocks,
            "climate cells are 40 blocks so they straddle 64-tile seams");
        c.True((LodClimateField.CellBlocks & (LodClimateField.CellBlocks - 1)) != 0,
            "climate cells are not a power of two (32/64 snap to section edges)");
    }

    static void AdjacentSectionsShareWorldSamples(Check c)
    {
        const int footprint = 64;
        int step = LodClimateField.GridStep(footprint);
        c.Eq(40, step, "L0 climate grid step is 40, not 64");
        int gxL = LodClimateField.GridOrigin(0, step);
        int gxR = LodClimateField.GridOrigin(footprint, step);
        int gz = LodClimateField.GridOrigin(0, step);
        LodClimateField.WorldToGrid(footprint, 0, gxL, gz, step, out int ixL, out _, out _, out _);
        LodClimateField.WorldToGrid(footprint, 0, gxR, gz, step, out int ixR, out _, out _, out _);
        int worldL = LodClimateField.SampleWorld(gxL, ixL, step);
        int worldR = LodClimateField.SampleWorld(gxR, ixR, step);
        c.Eq(worldL, worldR,
            "L0 seam at 64 uses the same world-space climate cell from both tiles");
    }

    static LodClimateField.Sample Valley => new()
    {
        LowR = 0.55f, LowG = 0.72f, LowB = 0.18f, LowTemp = 140f,
        HighR = 0.50f, HighG = 0.68f, HighB = 0.16f, HighTemp = 128f,
        Filled = true
    };

    static LodClimateField.Sample Mountain => new()
    {
        LowR = 0.38f, LowG = 0.42f, LowB = 0.14f, LowTemp = 96f,
        HighR = 0.34f, HighG = 0.38f, HighB = 0.12f, HighTemp = 80f,
        Filled = true
    };

    static void TwoClimatesDiffer(Check c)
    {
        var keep = LodClimateField.Identity;
        LodClimateField.ApplyLocalClimate(
            0.70f, 0.80f, 0.20f,
            keep.LowR, keep.LowG, keep.LowB,
            Valley.LowR, Valley.LowG, Valley.LowB,
            out float vR, out float vG, out float vB);
        LodClimateField.ApplyLocalClimate(
            0.70f, 0.80f, 0.20f,
            keep.LowR, keep.LowG, keep.LowB,
            Mountain.LowR, Mountain.LowG, Mountain.LowB,
            out float mR, out float mG, out float mB);
        c.True(Math.Abs(vR - mR) > 0.05f || Math.Abs(vG - mG) > 0.05f,
            "valley and mountain climate shift a slot to different colours");
        c.True(mG < vG, "mountain plant tint is the colder, less-lime sample");
    }

    static void GrassAndLeafShareClimate(Check c)
    {
        var keep = LodClimateField.Identity;
        // Grass slot is dirt-diluted; leaf slot is a fuller climate colour.
        LodClimateField.ApplyLocalClimate(
            0.62f, 0.58f, 0.28f,
            keep.LowR, keep.LowG, keep.LowB,
            Mountain.LowR, Mountain.LowG, Mountain.LowB,
            out float gR, out float gG, out float gB);
        LodClimateField.ApplyLocalClimate(
            0.48f, 0.70f, 0.16f,
            keep.LowR, keep.LowG, keep.LowB,
            Mountain.LowR, Mountain.LowG, Mountain.LowB,
            out float lR, out float lG, out float lB);
        c.Near(gR / 0.62f, lR / 0.48f, 0.0001, "grass and leaf share the same red climate ratio");
        c.Near(gG / 0.58f, lG / 0.70f, 0.0001, "grass and leaf share the same green climate ratio");
        c.Near(gB / 0.28f, lB / 0.16f, 0.0001, "grass and leaf share the same blue climate ratio");
    }

    static void WalkDoesNotRecolorFar(Check c)
    {
        var field = new LodClimateField();
        field.Put(10000, 8000, Mountain);
        c.True(field.TryGet(10000, 8000, out LodClimateField.Sample before),
            "far hill is in the field");
        field.Put(64, 64, Valley);
        field.Put(128, 64, Valley);
        c.True(field.TryGet(10000, 8000, out LodClimateField.Sample after),
            "far hill is still in the field after a walk");
        c.Eq(before.LowR, after.LowR, "walk does not rewrite far red");
        c.Eq(before.LowG, after.LowG, "walk does not rewrite far green");
        c.Eq(before.LowB, after.LowB, "walk does not rewrite far blue");
        c.Eq(before.LowTemp, after.LowTemp, "walk does not rewrite far temperature");
        c.True(field.TryGet(10000 + 10, 8000 + 10, out _),
            "a point inside the same climate cell hits the same sample");
        c.False(field.TryGet(10000 + LodClimateField.CellBlocks, 8000, out _),
            "the next cell is not filled by a neighbour write");
    }

    static void UnfilledFallsBackToKeep(Check c)
    {
        var field = new LodClimateField();
        var keep = Valley;
        LodClimateField.Sample got = field.GetOrKeep(0, 0, keep);
        c.Eq(keep.LowG, got.LowG, "an unfilled cell uses the keep-origin climate");
        field.Put(0, 0, Mountain);
        got = field.GetOrKeep(0, 0, keep);
        c.Eq(Mountain.LowG, got.LowG, "a filled cell wins over keep");
    }

    static void SeasonWeightUsesLocalTemp(Check c)
    {
        float warm = LodTintRegistry.SeasonWeightFromTempByte(Valley.LowTemp);
        float cold = LodTintRegistry.SeasonWeightFromTempByte(Mountain.LowTemp);
        c.True(Math.Abs(warm - cold) > 0.01f,
            "local temperature is what seasonWeight reads, not a global sea-level byte");
        c.Near(
            LodTintRegistry.SeasonWeightFromTempByte(128f),
            LodTintRegistry.SeasonWeightFromTempByte(LodClimateField.Identity.LowTemp),
            0.0001,
            "identity keep climate is temperate 128");
    }
}
