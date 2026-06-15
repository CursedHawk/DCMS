using Dcms.Shared.Kernel.Abstractions;
using Microsoft.AspNetCore.Authorization;

namespace Dcms.Shared.Security.Authorization;

/// <summary>
/// Grants access when the caller holds the required permission in the current
/// tenant. Platform SuperAdmins bypass tenant permission checks.
/// </summary>
public sealed class PermissionAuthorizationHandler(
    ITenantContext tenantContext,
    IPermissionResolver resolver)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.FindAll("role").Any(c => c.Value == "SuperAdmin"))
        {
            context.Succeed(requirement);
            return;
        }

        var tenantId = tenantContext.TenantId;
        if (tenantId is null)
        {
            return; // no tenant context → cannot evaluate a tenant permission
        }

        var sub = context.User.FindFirst("sub")?.Value;
        if (!Guid.TryParse(sub, out var userId))
        {
            return;
        }

        var permissions = await resolver.GetPermissionsAsync(tenantId.Value, userId);
        if (permissions.Contains(requirement.Permission))
        {
            context.Succeed(requirement);
        }
    }
}
