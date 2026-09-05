using Vintagestory.API.Client;

namespace DistantVistas;

/// <summary>
/// Advances login visit sweep logic on the render thread. Must run before any blocking
/// vanilla loading draw — <see cref="GuiScreenLoadingGame.RenderToDefaultFramebuffer"/>
/// can stall waiting for async sound and prevent AfterFinalComposition callbacks.
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
        while (accum >= TickStepSec)
        {
            accum -= TickStepSec;
            bake.Tick((float)TickStepSec);
            pump?.Invoke();
        }
    }
}
