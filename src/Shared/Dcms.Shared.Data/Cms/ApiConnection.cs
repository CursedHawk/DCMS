using Dcms.Shared.Audit.Redaction;

namespace Dcms.Shared.Data.Cms;

/// <summary>
/// An external HTTP API a tenant's sites read from (Mode D backlog #124): a base URL, a
/// credential, and the GET operations a site may use.
///
/// <para><b>Synced, never proxied.</b> admin-api — the only service holding the
/// <c>dcms-api-connections</c> Transit key — calls the API for each allowed operation on a
/// schedule and stores the response as an <see cref="ApiSnapshot"/>; content-api serves the
/// snapshot. So the internet-facing plane never decrypts the credential and never makes an
/// outbound call, and visitor traffic cannot spend the tenant's quota with the provider.</para>
/// </summary>
[AuditIgnore]
public sealed class ApiConnection : TenantEntity
{
    /// <summary>Kebab-case; the site reads <c>/api/connections/{Slug}{operation}</c>.</summary>
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>https only, no credentials or query in it.</summary>
    public string BaseUrl { get; set; } = string.Empty;
    /// <summary><c>none</c>, <c>bearer</c>, <c>header</c> (secret in header <see cref="AuthName"/>) or <c>query</c>.</summary>
    public string AuthKind { get; set; } = "none";
    public string? AuthName { get; set; }
    /// <summary>The credential, Transit-encrypted. Never returned by any endpoint.</summary>
    public string? SecretCiphertext { get; set; }
    /// <summary>Allowed GET operations: paths under the base URL, with an optional query (<c>/events?limit=20</c>).</summary>
    public List<string> Operations { get; set; } = [];
    public int RefreshMinutes { get; set; } = 60;
    public DateTimeOffset? RefreshedAt { get; set; }
    /// <summary>The last refresh's failure, per operation, for the console. Null when it went through.</summary>
    public string? LastError { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One operation's last good response, as content-api serves it.</summary>
[AuditIgnore]
public sealed class ApiSnapshot : TenantEntity
{
    public Guid ConnectionId { get; set; }
    public string Operation { get; set; } = string.Empty;
    /// <summary>The response body: JSON, at most <c>ApiConnections.MaxBodyBytes</c>.</summary>
    public string Body { get; set; } = "null";
    /// <summary>Where the list sits in <see cref="Body"/> (<c>data.items</c>); null for the body itself, or none found.</summary>
    public string? ItemsPath { get; set; }
    /// <summary>The fields an item has, as paths (<c>name</c>, <c>venue.city</c>) — what the builder offers to bind.</summary>
    public List<string> Fields { get; set; } = [];
    public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.UtcNow;
}
