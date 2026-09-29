using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.Plugins.Branding.Api;

/// <summary>The site's public identity, with media resolved to servable URLs.</summary>
public sealed record PublicBranding(
    string? Name,
    string? Tagline,
    string? LogoUrl,
    string? LogoDarkUrl,
    string? FaviconUrl,
    string? PrimaryColor,
    string? SecondaryColor,
    IReadOnlyDictionary<string, string> Items);

/// <summary>The tenant-private key:value section — integration ids, notes.</summary>
public sealed record PrivateBrandingItems(IReadOnlyDictionary<string, string> Items);

/// <summary>
/// The site's identity. Every plugin that renders anything branded — an email template, an
/// invoice, a social card — reads it here rather than asking the tenant to configure it twice.
/// </summary>
[DcmsContract("branding.identity", 1, Description = "The site's name, logo, colours and custom branding values.")]
public interface IBranding
{
    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai,
        Description = "The public branding: name, tagline, logo, favicon, colours and public items.")]
    Task<PublicBranding> GetAsync(CancellationToken ct);

    /// <summary>Plugin-to-plugin only: never exposed to a site, the admin API or an AI.</summary>
    [Operation(OpRisk.Read, Description = "The tenant-private branding items.")]
    Task<PrivateBrandingItems> GetPrivateItemsAsync(CancellationToken ct);
}
