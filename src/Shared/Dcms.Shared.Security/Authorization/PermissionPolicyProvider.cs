using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Dcms.Shared.Security.Authorization;

/// <summary>
/// Materializes a policy on demand for each permission key.
///
/// <para>Two prefixes, one provider, on purpose. ASP.NET Core resolves exactly one
/// <see cref="IAuthorizationPolicyProvider"/> from the container, so a second provider
/// registered for the platform prefix would not sit beside this one — it would replace it,
/// and every <c>dcms.perm:</c> policy would silently fall through to the default provider,
/// which does not know the name and returns null. A null policy is an <i>allow</i> for an
/// endpoint whose only guard was that policy. Handling both prefixes here means that failure
/// cannot be introduced by a registration order.</para>
///
/// <para>Requirements are the part that is safely additive: a service registers whichever
/// handlers it needs and unhandled requirement types simply never succeed.</para>
/// </summary>
public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : IAuthorizationPolicyProvider
{
    /// <summary>Tenant-scoped permission keys — produced by <c>RequirePermission</c>.</summary>
    public const string Prefix = "dcms.perm:";

    /// <summary>Platform-console permission keys — produced by <c>RequirePlatformPermission</c>.</summary>
    public const string PlatformPrefix = "dcms.pperm:";

    private readonly DefaultAuthorizationPolicyProvider _fallback = new(options);

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        // The platform prefix is tested first: "dcms.perm:" is not a prefix of "dcms.pperm:",
        // so the order is not load-bearing, but reading it in the same order as the constants
        // above is one less thing to check when someone adds a third space.
        if (policyName.StartsWith(PlatformPrefix, StringComparison.Ordinal))
        {
            return Task.FromResult<AuthorizationPolicy?>(Build(
                new PlatformPermissionRequirement(policyName[PlatformPrefix.Length..])));
        }

        if (policyName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return Task.FromResult<AuthorizationPolicy?>(Build(
                new PermissionRequirement(policyName[Prefix.Length..])));
        }

        return _fallback.GetPolicyAsync(policyName);
    }

    private static AuthorizationPolicy Build(IAuthorizationRequirement requirement) =>
        new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(requirement)
            .Build();

    public static string PolicyName(string permission) => Prefix + permission;

    public static string PlatformPolicyName(string permission) => PlatformPrefix + permission;
}
