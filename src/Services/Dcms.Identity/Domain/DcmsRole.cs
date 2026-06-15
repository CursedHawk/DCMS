using Microsoft.AspNetCore.Identity;

namespace Dcms.Identity.Domain;

/// <summary>
/// Global (platform-wide) role, e.g. SuperAdmin, Support. Tenant-scoped roles
/// live in the tenancy model (Phase 3), not here.
/// </summary>
public sealed class DcmsRole : IdentityRole<Guid>
{
    public DcmsRole() { }

    public DcmsRole(string roleName)
        : base(roleName)
    {
    }
}

public static class GlobalRoles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string Support = "Support";

    public static readonly IReadOnlyList<string> All = [SuperAdmin, Support];
}
