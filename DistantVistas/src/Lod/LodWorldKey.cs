using System.Text.RegularExpressions;
using Vintagestory.API.Common;

namespace DistantVistas;

/// <summary>
/// Stable per-world key for ModData files. Matches the LOD cache <c>{worldKey}.db</c>
/// naming in <see cref="LodPipeline.Open"/> so login-sweep markers stay world-scoped.
/// </summary>
public static class LodWorldKey
{
    public static string For(IWorldAccessor world)
    {
        string worldKey = world.SavegameIdentifier;
        if (string.IsNullOrEmpty(worldKey)) worldKey = "seed-" + world.Seed;
        return Regex.Replace(worldKey, "[^A-Za-z0-9_-]", "_");
    }
}
