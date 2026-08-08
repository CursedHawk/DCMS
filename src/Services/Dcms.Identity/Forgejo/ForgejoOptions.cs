namespace Dcms.Identity.Forgejo;

/// <summary>
/// Connection settings for the Forgejo git server that Identity provisions user
/// accounts on. Identity mirrors each DCMS login into a Forgejo account and keeps
/// the username/email/password in sync, so users can clone/pull/push the site
/// repos with their own credentials.
///
/// The <see cref="AdminToken"/> is a Forgejo access token with <c>write:admin</c>
/// scope (the site-admin bot's provisioning token) — required for the
/// <c>/api/v1/admin/users</c> endpoints. It is delivered via config/env
/// (<c>Forgejo__AdminToken</c>), the same way every other prod secret is; user
/// provisioning is a no-op until it is set (<see cref="Enabled"/>).
/// </summary>
public sealed class ForgejoOptions
{
    public const string SectionName = "Forgejo";

    /// <summary>Internal base URL Identity uses to reach Forgejo over the docker network.</summary>
    public string BaseUrl { get; set; } = "http://forgejo:3000";

    /// <summary>Admin-scoped access token (write:admin) used for user provisioning.</summary>
    public string AdminToken { get; set; } = string.Empty;

    /// <summary>Public HTTPS base shown to users for HTTP clone URLs (e.g. https://git.highgeek.eu).</summary>
    public string PublicUrl { get; set; } = "http://localhost:3000";

    /// <summary>User provisioning is active only once an admin token has been configured.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(AdminToken);
}
