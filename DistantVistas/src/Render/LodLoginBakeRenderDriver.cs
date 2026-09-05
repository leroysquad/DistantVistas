using Vintagestory.API.Client;

namespace DistantVistas;

/// <summary>
/// Drives login visit sweep logic from the render loop. Vintage Story often stalls
/// <see cref="ICoreClientAPI.Event.RegisterGameTickListener"/> callbacks while
/// <see cref="GuiScreenLoadingGame"/> is held on screen, which freezes warmup,
/// teleports, and Esc handling.
/// </summary>
public sealed class LodLoginBakeRenderDriver : IRenderer, IDisposable
{
    const double TickStepSec = 0.05;

    readonly ICoreClientAPI capi;
    LodLoginBake? bake;
    Action? pump;
    double accum;
    bool registered;

    public double RenderOrder => 1.4;
    public int RenderRange => 9997;

    public LodLoginBakeRenderDriver(ICoreClientAPI capi) => this.capi = capi;

    public void Bind(LodLoginBake? bake, Action pump)
    {
        this.bake = bake;
        this.pump = pump;
        accum = 0;
    }

    public void EnsureRegistered()
    {
        if (registered) return;
        registered = true;
        capi.Event.RegisterRenderer(this, EnumRenderStage.AfterFinalComposition,
            "distantvistas-login-bake-drive");
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (stage != EnumRenderStage.AfterFinalComposition) return;
        if (bake?.Active != true) return;

        bake.PollCancelFromRender();

        accum += deltaTime;
        while (accum >= TickStepSec)
        {
            accum -= TickStepSec;
            bake.Tick((float)TickStepSec);
            pump?.Invoke();
        }
    }

    public void Dispose()
    {
        if (!registered) return;
        capi.Event.UnregisterRenderer(this, EnumRenderStage.AfterFinalComposition);
        registered = false;
    }
}
