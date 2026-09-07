namespace DistantVistas;

/// <summary>
/// One staggered coverage scout. A real <see cref="LodScoutViewerEntity"/> sits on the
/// visit cell so Vintage Story has a player-style stream/render center there. The
/// human player never moves to that cell. Stream → capture → release slot; GetColor
/// paint runs from the shared scoutReady queue. Spawn-solid mesh is gated at overlay end.
/// </summary>
public sealed class LodScoutEntity
{
    public enum Phase : byte { WaitChunks, Capture, Paint, Mesh, Done }

    public long Key { get; }
    public Phase Current { get; set; }
    public int Ticks { get; set; }
    public int RevealRadius { get; set; }
    public bool Live { get; set; }
    public LodScoutViewerEntity? Viewer { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public int Cx { get; set; }
    public int Cz { get; set; }
    public bool PaintQueued { get; set; }
    public bool Painted { get; set; }
    /// <summary>True inside the spawn-solid disk: wait for a drawable mesh before despawn.</summary>
    public bool WaitForMesh { get; set; }
    /// <summary>KeepLoaded radius last sent to the server (retry RequestUp uses this).</summary>
    public int HoldRadius { get; set; }
    /// <summary>Pinned-scout partitioning cadence (not every overlay tick).</summary>
    public int PartitionTicks { get; set; }

    public LodScoutEntity(long key)
    {
        Key = key;
        Current = Phase.WaitChunks;
        RevealRadius = LodLoginBakePlayerMove.ChunkVisibleRadius;
        Live = true;
    }
}
