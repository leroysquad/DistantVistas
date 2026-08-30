namespace DistantVistas.Net;

/// <summary>
/// What a server tells a client about its assist, at the moment that client joins.
///
/// One rule carries the whole thing: <em>enabled</em> means this server will serve, and
/// never that it is holding something right now. Those two came apart in 0.2.0, where the
/// answer was taken from the key count, so a server that was capturing and serving
/// perfectly well reported "off" whenever its cache happened to be empty as somebody
/// joined. An empty cache is the ordinary state of a fresh server, and of any server an
/// admin is about to run /dvgen on. The client treats "off" as final - it stops reading
/// manifests and sections for the rest of the session - so the terrain the admin then
/// built reached nobody, and relogging was the only cure.
///
/// Kept apart from the mod system so the rule can be checked without a server: the case
/// that matters is a timing coincidence, and a live run only meets it by luck.
/// </summary>
public static class AssistGreeting
{
    public static (bool Enabled, string Status) Describe(
        bool capturing, bool serving, int keyCount, int serveRadiusBlocks)
    {
        if (!serving)
        {
            return (false, capturing
                ? "this server has a LOD cache but does not share it"
                : "this server does not build a LOD cache");
        }

        // keyCount reaches the wording and stops there. It must never reach the answer.
        return (true, keyCount > 0
            ? $"serving from {keyCount} cached sections"
              + (serveRadiusBlocks > 0 ? $" within {serveRadiusBlocks} blocks" : "")
            : "serving, though its cache is empty so far");
    }
}

