namespace DistantVistas;

/// <summary>
/// One staggered coverage scout. A real <see cref="LodScoutViewerEntity"/> sits on the
/// visit cell so Vintage Story has a player-style stream/render center there. The
/// human player never moves to that cell. After stream → capture → paint → mesh,
/// the viewer despawns.
/// </summary>
public sealed class LodScoutEntity
{
    public enum Phase : byte { WaitChunks, Capture, Mesh, Done }

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

    public LodScoutEntity(long key)
    {
        Key = key;
        Current = Phase.WaitChunks;
        RevealRadius = LodLoginBakePlayerMove.ChunkVisibleRadius;
        Live = true;
    }
}
