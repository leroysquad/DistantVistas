namespace DistantVistas.Net;

/// <summary>
/// Who has been told about which cached sections.
///
/// A client only ever asks for keys it has been offered, so a server whose cache grows
/// while people are online has to keep offering. Doing that needs one fact held
/// carefully: the set of keys that <em>every</em> connected player has heard about.
/// Anything smaller re-sends what people already have; anything larger strands sections
/// for the rest of their session.
///
/// The rule that keeps it right is that a greeting must never widen that set. A greeting
/// carries the whole cache to one player, so folding it in would claim the others had
/// heard it too. That is the defect this class was extracted to stop: with a shared set
/// widened at every join, a second player arriving stranded, for everyone already on,
/// every section captured in the seconds before they joined.
///
/// Held apart from the mod system because the failure is an ordering, and an ordering can
/// be checked with no server, no session and no players.
/// </summary>
public sealed class ManifestLedger
{
    readonly HashSet<string> greeted = new();
    readonly HashSet<long> announced = new();

    public int GreetedCount => greeted.Count;

    public bool HasGreeted(string playerUid) => greeted.Contains(playerUid);

    /// <summary>
    /// Record a player who has just been sent the full manifest in
    /// <paramref name="cacheAtGreet"/>.
    ///
    /// The first player defines the baseline, because what they were just sent and what
    /// has been announced are then the same set. Later arrivals are ahead of the baseline
    /// and must not move it: whatever they know beyond it is exactly what the players
    /// already on are still owed, and the next delta gives it to them. The newcomer sees
    /// those keys twice, which costs nothing, because a client merges manifests and only
    /// ever adds.
    /// </summary>
    public void Greet(string playerUid, IReadOnlyList<long> cacheAtGreet)
    {
        if (greeted.Count == 0)
        {
            announced.Clear();
            foreach (long key in cacheAtGreet) announced.Add(key);
        }

        greeted.Add(playerUid);
    }

    /// <summary>
    /// Forget a player who has left. The announced set stays as it is: it describes what
    /// has been broadcast to the players still here, and an empty server re-baselines on
    /// the next greeting anyway.
    /// </summary>
    public void Forget(string playerUid) => greeted.Remove(playerUid);

    /// <summary>
    /// The keys to offer every greeted player, given the cache as it stands now. Empty
    /// when nothing is new, and empty when nobody is listening.
    ///
    /// The cache is scanned in full every time. Comparing counts first would be cheaper
    /// and is wrong: it assumes the announced set is always a subset of the snapshot, so
    /// one key leaving the cache would let an equal count hide a new one for good.
    /// </summary>
    public long[] Delta(IReadOnlyList<long> cache)
    {
        if (greeted.Count == 0) return Array.Empty<long>();

        // Allocated on the first miss: the ordinary call finds nothing new and should not
        // pay for a list to say so.
        List<long>? fresh = null;
        foreach (long key in cache)
        {
            if (!announced.Add(key)) continue;
            (fresh ??= new List<long>()).Add(key);
        }

        return fresh?.ToArray() ?? Array.Empty<long>();
    }
}
