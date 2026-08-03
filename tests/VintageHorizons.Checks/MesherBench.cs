using System.Diagnostics;

namespace VintageHorizons.Checks;

/// <summary>
/// Quad counts and build time for the mesher, on terrain shapes chosen to tell different
/// merge behaviours apart. Not part of the check tier: it asserts nothing, because a
/// timing assertion on shared CI hardware fails for reasons that have nothing to do with
/// this code. Run it with `dotnet run --project tests/VintageHorizons.Checks -- bench`.
///
/// Quad count is the number that matters. It is deterministic, it is what the vertex
/// buffer costs, and it is what the fill rate and the upload budget both scale with. The
/// millisecond column is context, not a claim.
///
/// The rolling-hills case exists to be UNMOVED by merge changes. A benchmark where every
/// row improves cannot tell you whether it measured anything.
/// </summary>
public static class MesherBench
{
    const int Gs = LodSection.GridSize;
    const int Iterations = 40;

    public static void Run()
    {
        Console.WriteLine();
        Console.WriteLine("  mesher benchmark");
        Console.WriteLine();
        Console.WriteLine("  case                          opaque    water    verts     ms");
        Console.WriteLine("  " + new string('-', 62));

        Report("flat plain", FlatPlain());
        Report("ocean over sloping seabed", OceanOverSlope());
        Report("ocean over stepped seabed", OceanOverSteps());
        Report("plateau on uneven bedrock", PlateauOnUnevenBase());
        Report("rolling hills (control)", RollingHills());

        Console.WriteLine();
    }

    static void Report(string name, LodSection section)
    {
        MeshJob job = Fixtures.Job(section);

        // One untimed build first: the first call through BuildMesh pays JIT and the
        // thread-static list growth, which is not what any of these rows is about.
        MeshResult warm = LodMesher.BuildMesh(job);

        var watch = Stopwatch.StartNew();
        for (int i = 0; i < Iterations; i++) LodMesher.BuildMesh(job);
        watch.Stop();

        double ms = watch.Elapsed.TotalMilliseconds / Iterations;
        Console.WriteLine($"  {name,-28}{warm.VertexCount / 4,7}{warm.WaterVertexCount / 4,9}"
            + $"{warm.VertexCount + warm.WaterVertexCount,9}{ms,7:0.00}");
    }

    // ---- terrain shapes ----

    /// <summary>The best case, and the one the class comment has always claimed.</summary>
    static LodSection FlatPlain()
    {
        var s = Palette();
        Fill(s, (cx, cz) => new[] { LodSection.PackRun(Stone, 60, 1) });
        return s;
    }

    /// <summary>
    /// A flat sea over a seabed that falls away diagonally. The surface is one plane; the
    /// water runs beneath it all end somewhere different. This is the shape that made the
    /// merge fall apart, and it is ordinary coastline.
    /// </summary>
    static LodSection OceanOverSlope()
    {
        var s = Palette();
        Fill(s, (cx, cz) =>
        {
            // Kept clear of SeaLevel: a seabed above it would pack an inverted run and
            // the row would be measuring nonsense.
            int seabed = 20 + (cx + cz) / 8;
            return new[]
            {
                LodSection.PackRun(Water, SeaLevel, seabed),
                LodSection.PackRun(Stone, seabed, 1),
            };
        });
        return s;
    }

    /// <summary>
    /// The same sea over a seabed of wide flat terraces. Fewer distinct depths than the
    /// slope, so a bottom-keyed merge does better here than on the slope and still far
    /// worse than a surface-keyed one.
    /// </summary>
    static LodSection OceanOverSteps()
    {
        var s = Palette();
        Fill(s, (cx, cz) =>
        {
            int seabed = 20 + cx / 16 * 5;
            return new[]
            {
                LodSection.PackRun(Water, SeaLevel, seabed),
                LodSection.PackRun(Stone, seabed, 1),
            };
        });
        return s;
    }

    /// <summary>Dry land with the same shape: one flat top, many different depths.</summary>
    static LodSection PlateauOnUnevenBase()
    {
        var s = Palette();
        Fill(s, (cx, cz) => new[] { LodSection.PackRun(Stone, 80, 10 + (cx * 7 + cz * 3) % 23) });
        return s;
    }

    /// <summary>
    /// The control. The surface itself varies per column, so there is very little for any
    /// merge to join, and the sort key cannot change that. A row that moves here means the
    /// benchmark is measuring something other than what it claims to.
    /// </summary>
    static LodSection RollingHills()
    {
        var s = Palette();
        Fill(s, (cx, cz) =>
        {
            int top = 50 + (cx * 13 + cz * 29) % 17;
            return new[] { LodSection.PackRun(Stone, top, 1) };
        });
        return s;
    }

    // ---- helpers ----

    const int Stone = 0;
    const int Water = 1;
    const int SeaLevel = 45;

    static LodSection Palette()
    {
        var s = new LodSection();
        s.FindOrAddPaletteEntry(blockId: 1, color: 0x00607080, flags: 0);
        s.FindOrAddPaletteEntry(blockId: 2, color: 0x00204880, flags: LodPaletteEntry.FlagWater);
        return s;
    }

    static void Fill(LodSection s, Func<int, int, ulong[]> column)
    {
        for (int cz = 0; cz < Gs; cz++)
        {
            for (int cx = 0; cx < Gs; cx++) s.SetColumn(LodSection.ColumnIndex(cx, cz), column(cx, cz));
        }
    }
}
