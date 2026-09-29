using Dcms.Plugins.Branding.Api;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;

namespace Dcms.Plugins.Branding;

/// <summary><see cref="IBranding"/> for the providing instance; logo and favicon go through dcms.media@1.</summary>
internal sealed class BrandingIdentity(IPluginContext context) : IBranding
{
    private BrandingInfo Info => BrandingPlugin.ReadBranding(
        (context.Instance ?? throw new ContractValidationException("Branding is served by a Branding instance.")).Config);

    public async Task<PublicBranding> GetAsync(CancellationToken ct)
    {
        // Public section only: PrivateItems must never reach this shape.
        var b = Info;
        return new PublicBranding(
            b.Name,
            b.Tagline,
            await UrlAsync(b.LogoAssetId, ct),
            await UrlAsync(b.LogoDarkAssetId, ct),
            await UrlAsync(b.FaviconAssetId, ct),
            b.PrimaryColor,
            b.SecondaryColor,
            b.Items);
    }

    public Task<PrivateBrandingItems> GetPrivateItemsAsync(CancellationToken ct) =>
        Task.FromResult(new PrivateBrandingItems(Info.PrivateItems));

    /// <summary>
    /// The original rendition's URL; null when unset, malformed, or the asset is gone — an
    /// unconfigured or deleted logo yields no URL rather than an error.
    /// </summary>
    private async Task<string?> UrlAsync(string? assetId, CancellationToken ct)
    {
        if (!Guid.TryParse(assetId, out var id)
            || await context.Contracts.Get<IPluginMedia>().ResolveAsync(new MediaLookup(id), ct) is not { } asset)
        {
            return null;
        }
        return asset.VariantUrls.TryGetValue("original", out var url) ? url : asset.VariantUrls.Values.FirstOrDefault();
    }
}
