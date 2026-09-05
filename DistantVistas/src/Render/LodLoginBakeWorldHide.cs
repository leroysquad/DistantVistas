using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace DistantVistas;

/// <summary>
/// Hides vanilla chunk meshes during the login visit sweep so teleports cannot flash
/// sky/terrain between stops. Only the active L0 target columns are revealed for capture.
/// </summary>
public sealed class LodLoginBakeWorldHide
{
    readonly ICoreClientAPI capi;
    readonly HashSet<long> hiddenIndices = new();

    public LodLoginBakeWorldHide(ICoreClientAPI capi) => this.capi = capi;

    public void HideAllLoaded()
    {
        IList<long> loaded = capi.World.LoadedChunkIndices;
        for (int i = 0; i < loaded.Count; i++)
            HideIndex(loaded[i]);
    }

    public void RevealL0(long l0Key)
    {
        int dim = capi.World.Player.Entity.Pos.Dimension;
        foreach ((int cx, int cz) in LodLoginSweep.ChunkColumnsForL0(l0Key))
            capi.World.SetChunkColumnVisible(cx, cz, dim);

        int footprint = LodWorld.KeyFootprintBlocks(l0Key);
        int minX = LodWorld.KeySx(l0Key) * footprint;
        int minZ = LodWorld.KeySz(l0Key) * footprint;
        int maxX = minX + footprint - 1;
        int maxZ = minZ + footprint - 1;
        int cs = GlobalConstants.ChunkSize;
        int minCx = minX / cs;
        int maxCx = maxX / cs;
        int minCz = minZ / cs;
        int maxCz = maxZ / cs;
        int mapY = capi.World.BlockAccessor.MapSizeY;

        for (int cx = minCx; cx <= maxCx; cx++)
        {
            for (int cz = minCz; cz <= maxCz; cz++)
            {
                for (int cy = 0; cy < mapY; cy += cs)
                {
                    IWorldChunk? chunk = capi.World.BlockAccessor.GetChunk(cx, cy, cz);
                    if (chunk is IClientChunk client)
                        client.SetVisibility(true);
                }
            }
        }
    }

    public void Restore()
    {
        foreach (long idx in hiddenIndices)
        {
            IWorldChunk? chunk = capi.World.BlockAccessor.GetChunk(idx);
            if (chunk is IClientChunk client)
                client.SetVisibility(true);
        }
        hiddenIndices.Clear();
    }

    void HideIndex(long idx)
    {
        IWorldChunk? chunk = capi.World.BlockAccessor.GetChunk(idx);
        if (chunk is not IClientChunk client) return;
        client.SetVisibility(false);
        hiddenIndices.Add(idx);
    }
}
