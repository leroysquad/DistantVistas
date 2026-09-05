using Vintagestory.API.Server;

namespace DistantVistas.Net;

/// <summary>
/// Probes the savegame, then loads only columns whose 4-chunk neighbourhood already
/// exists. That is the measured "generates nothing" rule from <see cref="LodColumnMap"/>.
/// keepLoaded is false so vanilla can drop the column when the player walks on.
/// </summary>
public sealed class LodExploredColumnLoader : IExploredColumnLoader
{
    readonly ICoreServerAPI sapi;
    readonly LodExploredColumnBook book = new();

    public LodExploredColumnLoader(ICoreServerAPI sapi) => this.sapi = sapi;

    public bool IsVanillaBusy =>
        sapi.WorldManager.CurrentGeneratingChunkCount > 0;

    public int InFlight => book.InFlight;

    public void Tick() => book.Tick();

    public ExploredLoadAttempt TryRequest(int cx, int cz, Func<bool> stillIdle) =>
        book.TryRequest(cx, cz, stillIdle, IsVanillaBusy, StartProbe, LoadColumn);

    void StartProbe(int cx, int cz)
    {
        sapi.WorldManager.TestMapChunkExists(cx, cz, hit => book.CompleteProbe(cx, cz, hit));
    }

    void LoadColumn(int cx, int cz) =>
        sapi.WorldManager.LoadChunkColumn(cx, cz, keepLoaded: false);
}
