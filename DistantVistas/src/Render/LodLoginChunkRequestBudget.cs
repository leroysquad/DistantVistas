using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DistantVistas;

/// <summary>
/// Caps overlay <see cref="IClientWorldAccessor.SetChunkColumnVisible"/> so login
/// stream growth and scout rings cannot flood the server RequestChunkColumns FIFO
/// (1.0.44 autosave death: chunkdbthread never paused).
/// </summary>
public static class LodLoginChunkRequestBudget
{
    /// <summary>Chebyshev shells at spawn-solid are ~256 cells; spread across ticks.</summary>
    public const int MaxVisiblePerTick = 96;

    static int issuedThisTick;
    static bool overlayTick;

    public static int IssuedThisTick => issuedThisTick;

    public static bool Exhausted => overlayTick && issuedThisTick >= MaxVisiblePerTick;

    public static void BeginOverlayTick()
    {
        overlayTick = true;
        issuedThisTick = 0;
    }

    public static void EndOverlayTick() => overlayTick = false;

    public static bool TrySetVisible(IClientWorldAccessor world, int cx, int cz, int dimension)
    {
        if (cx < 0 || cz < 0) return false;
        if (overlayTick && issuedThisTick >= MaxVisiblePerTick)
            return false;
        world.SetChunkColumnVisible(cx, cz, dimension);
        if (overlayTick)
            issuedThisTick++;
        return true;
    }
}
