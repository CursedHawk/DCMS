namespace Dcms.Shared.Data.Social;

/// <summary>
/// Which Meta login path a connection was established through. The two differ in
/// more than branding: they use different hosts, different scopes and different
/// token-refresh calls, and only <see cref="Facebook"/> can read Instagram stories.
/// </summary>
public enum MetaProvider
{
    /// <summary>
    /// Facebook Login for Business — <c>graph.facebook.com</c>. Yields a Page token and,
    /// when the Page has one linked, its Instagram professional account. Required for
    /// stories.
    /// </summary>
    Facebook,

    /// <summary>
    /// Instagram Login — <c>graph.instagram.com</c>. Works for a professional account
    /// with no Facebook Page, but exposes no stories endpoint.
    /// </summary>
    InstagramLogin,
}

public enum MetaConnectionStatus
{
    Active,

    /// <summary>Refresh failed or Meta rejected the token; the admin must reconnect.</summary>
    NeedsReauth,

    /// <summary>Disconnected by an admin. Kept as a row so sync state can explain itself.</summary>
    Revoked,
}

/// <summary>
/// One connected Meta account — a Facebook Page or an Instagram professional account.
///
/// <para>Tokens are Vault Transit ciphertext and never plaintext, the same shape as
/// <see cref="Ai.TenantAiSettings"/>, but on the dedicated <c>dcms-social-tokens</c> key
/// so granting a service the ability to read social tokens does not also grant it the
/// tenant's AI keys.</para>
/// </summary>
public sealed class MetaConnection : TenantEntity
{
    public MetaProvider Provider { get; set; }

    /// <summary>Page id (Facebook) or IG user id (Instagram). Unique per tenant + provider.</summary>
    public string ExternalAccountId { get; set; } = string.Empty;

    /// <summary>
    /// For an Instagram account reached through a Facebook Page, the Page that owns it.
    /// Null on the Instagram-Login path. Stories are read with the Page's token.
    /// </summary>
    public string? ExternalPageId { get; set; }

    public string AccountName { get; set; } = string.Empty;
    public string? AccountUsername { get; set; }
    public string? AvatarUrl { get; set; }

    /// <summary>The long-lived user (Facebook) or Instagram token, as Transit ciphertext.</summary>
    public string AccessTokenCiphertext { get; set; } = string.Empty;

    /// <summary>The Page token, as Transit ciphertext. Facebook path only.</summary>
    public string? PageTokenCiphertext { get; set; }

    /// <summary>
    /// Null means "does not expire on a schedule we were told about" — a Page token
    /// derived from a long-lived user token behaves that way. It is not a licence to
    /// skip the refresh worker: Meta still invalidates on password change or revocation,
    /// which surfaces as a 190 at call time, not as an expiry we can see coming.
    /// </summary>
    public DateTimeOffset? TokenExpiresAt { get; set; }

    public string ScopesGranted { get; set; } = string.Empty;
    public MetaConnectionStatus Status { get; set; } = MetaConnectionStatus.Active;
    public string? LastError { get; set; }

    public Guid? ConnectedBy { get; set; }
    public DateTimeOffset ConnectedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastRefreshedAt { get; set; }
}
