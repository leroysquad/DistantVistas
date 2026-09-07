using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DistantVistas;

/// <summary>
/// Farseer's zip loads after ours, so their stock region shaders overwrite
/// the copies in assets/farseer/. optionaldependencies is not a VS field.
/// We keep a second copy under the distantvistas domain and write it into
/// farseer:shaders/region.* after assets are loaded, before they compile.
///
/// Do not call ReloadShaders() after this. That reloads from Farseer's zip
/// and wipes the overlay (0.7.60: marker=present then marker=MISSING).
/// Do not RegisterFileShaderProgram/Compile their live region program
/// (0.7.61: compile logged true, Farseer still drew nothing in the fog gap).
/// </summary>
public static class FarseerShaderOverlay
{
    /// <summary>
    /// Overlay inject is off. Stock Farseer with SkyTint 4.3 paints a sky-coloured
    /// disc, not hills. Yielding for that disc punched holes in us. Last shot failed.
    /// </summary>
    public const bool OverlayActive = false;

    public const string Marker = "DV_FARSEER_OVERLAY";

    static readonly AssetLocation SrcVsh = new("distantvistas", "shaders/farseer-region.vsh");
    static readonly AssetLocation SrcFsh = new("distantvistas", "shaders/farseer-region.fsh");
    static readonly AssetLocation DstVsh = new("farseer", "shaders/region.vsh");
    static readonly AssetLocation DstFsh = new("farseer", "shaders/region.fsh");

    /// <summary>
    /// Copy overlay bytes onto Farseer's region assets. Call from AssetsLoaded
    /// so the first compile of pass "region" already has our GLSL. Safe if
    /// Farseer is missing. Does not ReloadShaders.
    /// </summary>
    public static bool ApplyBytes(ICoreAPI api, ILogger log)
    {
        if (!OverlayActive) return false;
        if (!api.ModLoader.IsModEnabled("farseer")) return false;

        IAsset? srcVsh = api.Assets.TryGet(SrcVsh);
        IAsset? srcFsh = api.Assets.TryGet(SrcFsh);
        IAsset? dstVsh = api.Assets.TryGet(DstVsh);
        IAsset? dstFsh = api.Assets.TryGet(DstFsh);
        if (srcVsh == null || srcFsh == null)
        {
            log.Warning("Farseer overlay source missing ({0} / {1})", SrcVsh, SrcFsh);
            return false;
        }
        if (dstVsh == null || dstFsh == null)
        {
            log.Warning("Farseer region shaders missing ({0} / {1})", DstVsh, DstFsh);
            return false;
        }

        dstVsh.Data = (byte[])srcVsh.Data.Clone();
        dstFsh.Data = (byte[])srcFsh.Data.Clone();
        dstVsh.IsPatched = true;
        dstFsh.IsPatched = true;
        return MarkerPresent(api);
    }

    public static bool MarkerPresent(ICoreAPI api)
    {
        IAsset? vsh = api.Assets.TryGet(DstVsh);
        return vsh != null && vsh.ToText().Contains(Marker);
    }

    public static string Describe(ICoreAPI api)
    {
        var sb = new StringBuilder();
        bool loaded = api.ModLoader.IsModEnabled("farseer");
        sb.Append("farseer loaded=").Append(loaded ? "yes" : "no");
        sb.Append(" marker=").Append(MarkerPresent(api) ? "present" : "MISSING");

        int i = 0;
        int dv = -1, fs = -1;
        foreach (IAssetOrigin origin in api.Assets.Origins)
        {
            string label = origin.ToString() ?? "";
            if (dv < 0 && label.IndexOf("distantvistas", StringComparison.OrdinalIgnoreCase) >= 0)
                dv = i;
            if (fs < 0 && ContainsFarseerOrigin(label))
                fs = i;
            i++;
        }
        sb.Append(" originIndex distantvistas=").Append(dv);
        sb.Append(" farseer=").Append(fs);
        sb.Append(" (first=highest)");

        if (api is ICoreClientAPI capi)
        {
            try
            {
                FarseerClientDump? cfg = capi.LoadModConfig<FarseerClientDump>("farseer-client.json");
                if (cfg != null)
                {
                    sb.Append(" Enabled=").Append(cfg.Enabled);
                    sb.Append(" SkyTint=").Append(cfg.SkyTint.ToString("0.###"));
                    sb.Append(" FarViewDistance=").Append(cfg.FarViewDistance.ToString("0"));
                }
                else sb.Append(" config=missing");
            }
            catch (Exception e)
            {
                sb.Append(" config=unreadable ").Append(e.Message);
            }
        }
        return sb.ToString();
    }

    static bool ContainsFarseerOrigin(string label) =>
        label.IndexOf("farseer", StringComparison.OrdinalIgnoreCase) >= 0
        && label.IndexOf("distantvistas", StringComparison.OrdinalIgnoreCase) < 0;
}

/// <summary>
/// Subscribe ReloadShader before Farseer's default 0.1 so ApplyBytes runs
/// first, then their LoadShader reads overlay assets.
/// </summary>
public class FarseerOverlayEarlyHook : ModSystem
{
    ICoreClientAPI? capi;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override double ExecuteOrder() => 0.05;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        api.Event.ReloadShader += OnReload;
    }

    bool OnReload()
    {
        if (capi == null || !FarseerShaderOverlay.OverlayActive) return true;
        FarseerShaderOverlay.ApplyBytes(capi, capi.Logger);
        return true;
    }

    public override void Dispose()
    {
        if (capi == null) return;
        capi.Event.ReloadShader -= OnReload;
        capi = null;
    }
}

/// <summary>Enough of farseer-client.json for the overlay diagnostic. Extra fields ignored.</summary>
public class FarseerClientDump
{
    public bool Enabled = true;
    public float FarViewDistance;
    public float SkyTint;
}
