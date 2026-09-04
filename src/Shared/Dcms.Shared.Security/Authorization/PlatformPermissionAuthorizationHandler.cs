using Microsoft.AspNetCore.Authorization;

namespace Dcms.Shared.Security.Authorization;

/// <summary>
/// Grants access when the caller's global roles carry the required platform-console
/// permission.
///
/// <para>The SuperAdmin short-circuit is the same rule, spelled the same way, as
/// <see cref="PermissionAuthorizationHandler"/>'s. That is deliberate duplication of one
/// line: the two handlers govern different permission spaces, and a shared helper would
/// invite someone to "improve" the bypass in one place and change both.</para>
/// </summary>
public sealed class PlatformPermissionAuthorizationHandler(IPlatformPermissionResolver resolver)
    : AuthorizationHandler<PlatformPermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PlatformPermissionRequirement requirement)
    {
        if (context.User.FindAll("role").Any(c => c.Value == "SuperAdmin"))
        {
            context.Succeed(requirement);
            return;
        }

        var roles = context.User.FindAll("role").Select(c => c.Value).ToArray();
        if (roles.Length == 0)
        {
            // No global role at all. Fail rather than resolve: an empty role set resolves to an
            // empty permission set, which is the same refusal by a longer route, and this is the
            // common case for an ordinary tenant user who found the console's URL.
            return;
        }

        var permissions = await resolver.GetPermissionsAsync(roles);
        if (permissions.Contains(requirement.Permission))
        {
            context.Succeed(requirement);
        }
    }
}
