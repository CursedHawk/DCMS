using Microsoft.AspNetCore.Identity;

namespace Dcms.Identity.Domain;

/// <summary>
/// Global user account. One identity spans all tenants; per-tenant access is
/// granted through tenant memberships (Phase 3). Visitor accounts on tenant
/// websites are a separate pool (visitors schema, VisitorAuth plugin).
/// </summary>
public sealed class DcmsUser : IdentityUser<Guid>
{
    public string? DisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
