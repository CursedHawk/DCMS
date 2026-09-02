using Dcms.Shared.Data.Social;

namespace Dcms.AdminApi.Social;

/// <summary>
/// Platform-level Meta app configuration, bound from the <c>Social</c> section
/// (Vault: <c>Social__Meta__AppId</c>, <c>Social__Meta__AppSecret</c>, …).
///
/// <para>Following the Google SSO convention in Dcms.Identity: leave the credentials out and
/// the feature simply does not offer itself — <see cref="IsConfigured"/> is false, the connect
/// endpoint returns 501 and the admin UI hides the button. Dev works with no Meta app at all.</para>
/// </summary>
public sealed class MetaSocialOptions
{
    public const string SectionName = "Social";

    /// <summary>Facebook Login for Business app — Pages, and Instagram accounts linked to them.</summary>
    public MetaAppOptions Meta { get; set; } = new();

    /// <summary>Instagram Login app — professional accounts with no Facebook Page.</summary>
    public MetaAppOptions Instagram { get; set; } = new();

    /// <summary>
    /// Absolute callback URL, registered verbatim in the Meta app. One URI serves every
    /// tenant; which tenant a callback belongs to comes from the state row, not the URL.
    /// </summary>
    public string? RedirectUri { get; set; }

    /// <summary>
    /// Graph API version. Configurable because Meta ships a new one roughly quarterly and
    /// retires each after about two years — pinning it here means an upgrade is a config
    /// change rather than a redeploy, and that we never silently follow an unversioned host.
    /// </summary>
    public string GraphVersion { get; set; } = "v25.0";

    /// <summary>
    /// Replaces every Meta host — the OAuth dialog, both graph hosts and the Instagram token
    /// endpoint — with this base URL.
    ///
    /// <para><b>For tests only.</b> It exists because the alternative was leaving the hosts as
    /// string literals inside the client, which would have meant the OAuth exchange and the
    /// account discovery could only ever be exercised against Meta itself — i.e. never. Leave
    /// it unset and the real hosts are used; there is no partial mode.</para>
    /// </summary>
    public string? OverrideBaseUrl { get; set; }

    /// <summary>Where to send the browser after the callback completes. Relative to the admin SPA.</summary>
    public string ReturnPath { get; set; } = "/plugins";

    public MetaAppOptions App(MetaProvider provider) =>
        provider == MetaProvider.Facebook ? Meta : Instagram;

    public bool IsConfigured(MetaProvider provider)
    {
        var app = App(provider);
        return !string.IsNullOrWhiteSpace(app.AppId)
               && !string.IsNullOrWhiteSpace(app.AppSecret)
               && !string.IsNullOrWhiteSpace(RedirectUri);
    }
}

public sealed class MetaAppOptions
{
    public string? AppId { get; set; }
    public string? AppSecret { get; set; }
}
