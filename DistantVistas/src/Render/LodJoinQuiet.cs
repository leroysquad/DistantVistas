namespace DistantVistas;

/// <summary>
/// Join/char-create/login-sweep window where disposing MeshRef VAOs mid-present
/// can leave SwapBuffers with no bound buffer. vsvaogc reads
/// <see cref="SuppressVaoDrain"/> by type name. No project reference.
/// </summary>
public static class LodJoinQuiet
{
    /// <summary>
    /// True while terrain GL is blocked or the login sweep has not finished (or
    /// failed into play). Starts true so the first RunningGame present is covered
    /// before the renderer exists.
    /// </summary>
    public static bool SuppressVaoDrain { get; private set; } = true;

    const int PlayQuietMs = 4000;
    static long playQuietUntilMs;

    internal static void Sync(bool loginBakeBlocked, bool loginBakeComplete)
    {
        bool next = loginBakeBlocked || !loginBakeComplete;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (next)
        {
            playQuietUntilMs = 0;
        }
        else
        {
            // First play frames after overlay/skip still present vanilla chunk
            // meshes queued during the sweep. Drain on that same flip hung
            // both the skip path and the finished-sweep path (no play-settle).
            if (playQuietUntilMs == 0)
                playQuietUntilMs = now + PlayQuietMs;
            if (now < playQuietUntilMs)
                next = true;
        }
        if (loggedQuiet != next)
        {
            loggedQuiet = next;
            // #region agent log
            try
            {
                System.IO.File.AppendAllText(
                    @"C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\debug-40cccb.log",
                    "{\"sessionId\":\"40cccb\",\"hypothesisId\":\"H6\",\"location\":\"LodJoinQuiet.Sync\",\"message\":\"join-quiet-flip\",\"data\":{\"suppressDrain\":" + (next ? "true" : "false") + ",\"complete\":" + (loginBakeComplete ? "true" : "false") + ",\"blocked\":" + (loginBakeBlocked ? "true" : "false") + "},\"timestamp\":" + now + "}\n");
            }
            catch { }
            // #endregion
        }
        SuppressVaoDrain = next;
    }

    static bool loggedQuiet;
}
