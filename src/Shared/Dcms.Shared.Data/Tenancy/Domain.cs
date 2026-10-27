namespace Dcms.Shared.Data.Tenancy;

/// <summary>
/// A custom domain a tenant wants to host its website on. Routed by site-host
/// (Phase 8) only once <see cref="VerifiedAt"/> is set via the TXT challenge.
/// </summary>
public sealed class Domain : EnvironmentEntity
{
    public string Hostname { get; set; } = string.Empty;
    public string VerificationToken { get; set; } = string.Empty;
    public DateTimeOffset? VerifiedAt { get; set; }
    public bool IsPrimary { get; set; }

    /// <summary>The site served on this domain (set by the editor / domain admin).</summary>
    public Guid? SiteId { get; set; }

    /// <summary>
    /// The site's publishing branch this domain serves (#277); null for <c>release</c>. Only a
    /// release domain can be the site's primary one.
    /// </summary>
    public string? Branch { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsVerified => VerifiedAt is not null;
}
