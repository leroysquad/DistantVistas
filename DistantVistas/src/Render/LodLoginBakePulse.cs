using Vintagestory.API.Client;

namespace DistantVistas;

/// <summary>
/// Advances login visit sweep logic on the render thread. Pulses from
/// <see cref="LodLoginBakeHarmony.RenderPulse"/> (ScreenManager.OnNewFrame prefix, before
/// any screen draw) and from the splash overlay renderer. Vanilla
/// <see cref="GuiScreenLoadingGame.RenderToDefaultFramebuffer"/> can stall waiting for
/// async sound and prevent later render-stage callbacks from firing.
/// </summary>
public sealed class LodLoginBakePulse
{
    const double TickStepSec = 0.05;

    LodLoginBake? bake;
    Action? pump;
    double accum;
    bool pulsedThisFrame;

    public void Bind(LodLoginBake? bake, Action pump)
    {
        this.bake = bake;
        this.pump = pump;
        accum = 0;
        pulsedThisFrame = false;
    }

    public void Pulse(float deltaTime)
    {
        if (bake?.Active != true) return;

        bake.PollCancelFromRender();

        if (deltaTime <= 0f) deltaTime = 1f / 60f;

        // OnNewFrame and the overlay renderer both call Pulse; coalesce to one tick batch.
        if (pulsedThisFrame) return;
        pulsedThisFrame = true;

        accum += deltaTime;
        while (accum >= TickStepSec)
        {
            accum -= TickStepSec;
            bake.Tick((float)TickStepSec);
            pump?.Invoke();
        }
    }

    /// <summary>Called from <see cref="LodLoginBakeHarmony.RenderPulse"/> at frame start.</summary>
    public void BeginFrame() => pulsedThisFrame = false;
}
