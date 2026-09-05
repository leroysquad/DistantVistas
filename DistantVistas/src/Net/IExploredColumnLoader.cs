namespace DistantVistas.Net;

/// <summary>
/// Server-only way to bring an already-explored column into RAM without raising
/// viewDistance and without generating new land. Singleplayer (integrated server)
/// and a dedicated server that has Distant Vistas installed implement this.
/// A multiplayer client talking to a server without the mod has no loader.
/// </summary>
public interface IExploredColumnLoader
{
    bool IsVanillaBusy { get; }

    int InFlight { get; }

    void Tick();

    /// <summary>
    /// Probe existence / neighbourhood, then <c>LoadChunkColumn(..., keepLoaded: false)</c>
    /// only when <see cref="LodColumnMap.Classify"/> says Load and <paramref name="stillIdle"/>
    /// is still true at the load instant. Never Peek. Never LoadChunkColumnPriority.
    /// </summary>
    ExploredLoadAttempt TryRequest(int cx, int cz, Func<bool> stillIdle);
}

public enum ExploredLoadAttempt
{
    None,
    Probing,
    Loading,
    Refused,
}
