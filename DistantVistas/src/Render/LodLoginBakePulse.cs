namespace DistantVistas;

/// <summary>
/// Advances login visit sweep logic on the game tick while the HUD overlay is up.
/// Vanilla game ticks run once the world is in RunningGame; this does not hook
/// ScreenManager present paths.
/// </summary>
public sealed class LodLoginBakePulse
{
    const double TickStepSec = 0.05;

    LodLoginBake? bake;
    Action? pump;
    double accum;

    public void Bind(LodLoginBake? bake, Action pump)
    {
        this.bake = bake;
        this.pump = pump;
        accum = 0;
    }

    public void Pulse(float deltaTime)
    {
        if (bake?.Active != true) return;

        bake.PollCancelFromRender();

        if (deltaTime <= 0f) deltaTime = 1f / 60f;

        accum += deltaTime;
        // One Tick max. Catch-up after a hitch used to dump tens of hops onto
        // one frame and Windows marked the client not responding.
        if (accum < TickStepSec) return;
        accum = 0;
        bake.Tick((float)TickStepSec);
        pump?.Invoke();
    }
}
