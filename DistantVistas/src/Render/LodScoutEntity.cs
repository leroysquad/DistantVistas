namespace DistantVistas;

/// <summary>
/// One staggered coverage scout. Not a Vintage Story <c>Entity</c>: Distant Vistas is
/// client-only, so a custom entity class cannot spawn on a vanilla server and would
/// not load chunks there anyway. Each scout force-loads one L0 via
/// <see cref="LodLoginBakePlayerMove.RequestChunkColumnsVisible"/>, then despawns.
/// </summary>
public sealed class LodScoutEntity
{
    public enum Phase : byte { WaitChunks, Capture, Bake }

    public long Key { get; }
    public Phase Current { get; set; }
    public int Ticks { get; set; }
    public int RevealRadius { get; set; }
    public bool Live { get; set; }

    public LodScoutEntity(long key)
    {
        Key = key;
        Current = Phase.WaitChunks;
        RevealRadius = LodLoginBakePlayerMove.ChunkVisibleRadius;
        Live = true;
    }
}
