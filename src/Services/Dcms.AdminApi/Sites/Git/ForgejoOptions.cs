namespace Dcms.AdminApi.Sites.Git;

/// <summary>
/// Connection + provisioning settings for the Forgejo git server that backs
/// Mode B (ReactApp) site source. The machine <see cref="Token"/> is provisioned
/// out-of-band via Vault (KV secret/dcms/admin-api, key "Forgejo__Token") so it
/// is never committed; everything else has a safe dev default.
/// </summary>
public sealed class ForgejoOptions
{
    public const string SectionName = "Forgejo";

    /// <summary>Internal base URL admin-api uses to reach Forgejo over the docker network.</summary>
    public string BaseUrl { get; set; } = "http://forgejo:3000";

    /// <summary>Machine access token (Forgejo personal/bot token) with repo + org admin scope.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Public HTTPS base shown to users for HTTP clone URLs (e.g. https://git.highgeek.eu).</summary>
    public string PublicUrl { get; set; } = "http://localhost:3000";

    /// <summary>Public host used in SSH clone URLs (git@&lt;host&gt;:&lt;port&gt;/…).</summary>
    public string SshHost { get; set; } = "localhost";

    /// <summary>Public SSH port for git clone over SSH.</summary>
    public int SshPort { get; set; } = 2222;

    /// <summary>Org name prefix; one Forgejo org per tenant (e.g. "tenant-&lt;slug&gt;").</summary>
    public string OrgPrefix { get; set; } = "tenant-";

    /// <summary>Git identity stamped on commits admin-api makes on a user's behalf.</summary>
    public string CommitterName { get; set; } = "DCMS";
    public string CommitterEmail { get; set; } = "noreply@highgeek.eu";

    /// <summary>Shared secret for push-webhook HMAC (admin-api both registers and verifies it).</summary>
    public string WebhookSecret { get; set; } = "dcms-dev-webhook-secret";

    /// <summary>Internal URL Forgejo calls on push (docker network), targeting admin-api's webhook.</summary>
    public string WebhookUrl { get; set; } = "http://admin-api:8080/api/internal/git/webhook";

    /// <summary>Git integration is active only once a machine token has been provisioned.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(Token);
}
