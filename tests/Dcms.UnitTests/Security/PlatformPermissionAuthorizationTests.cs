using System.Security.Claims;
using Dcms.Shared.Security;
using Dcms.Shared.Security.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Dcms.UnitTests.Security;

/// <summary>
/// The platform console is SuperAdmin-only today and gated by role later, which means these
/// tests are guarding a transition: the day someone grants <c>Support</c> a key, the handler
/// has to already be the thing deciding, and the SuperAdmin bypass has to still work when the
/// grant table is empty or unreachable.
/// </summary>
public class PlatformPermissionAuthorizationTests
{
    private static ClaimsPrincipal User(params string[] roles) =>
        new(new ClaimsIdentity(
            [new Claim("sub", Guid.NewGuid().ToString()), .. roles.Select(r => new Claim("role", r))],
            authenticationType: "test"));

    private static AuthorizationHandlerContext Context(ClaimsPrincipal user, string permission)
    {
        var requirement = new PlatformPermissionRequirement(permission);
        return new AuthorizationHandlerContext([requirement], user, resource: null);
    }

    [Fact]
    public async Task SuperAdmin_succeeds_without_consulting_the_grant_table()
    {
        var resolver = Substitute.For<IPlatformPermissionResolver>();
        var context = Context(User("SuperAdmin"), PlatformConsolePermissions.LogsPurge);

        await new PlatformPermissionAuthorizationHandler(resolver).HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
        // The bypass must not depend on the table: a half-seeded or unreachable `platform`
        // schema would otherwise lock out the only operator who can repair it.
        await resolver.DidNotReceiveWithAnyArgs()
            .GetPermissionsAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Role_holding_the_key_succeeds()
    {
        var resolver = Substitute.For<IPlatformPermissionResolver>();
        resolver.GetPermissionsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>(PlatformConsolePermissions.ReadOnly, StringComparer.Ordinal));

        var context = Context(User("Support"), PlatformConsolePermissions.OpsRead);

        await new PlatformPermissionAuthorizationHandler(resolver).HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task Role_without_the_key_does_not_succeed()
    {
        var resolver = Substitute.For<IPlatformPermissionResolver>();
        resolver.GetPermissionsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>(PlatformConsolePermissions.ReadOnly, StringComparer.Ordinal));

        // LogsPurge is the one that deletes from a telemetry store; a read-only role must not
        // reach it by holding every other key.
        var context = Context(User("Support"), PlatformConsolePermissions.LogsPurge);

        await new PlatformPermissionAuthorizationHandler(resolver).HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task Caller_with_no_global_role_is_refused_without_a_lookup()
    {
        var resolver = Substitute.For<IPlatformPermissionResolver>();
        var context = Context(User(), PlatformConsolePermissions.OverviewRead);

        await new PlatformPermissionAuthorizationHandler(resolver).HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
        // An ordinary tenant user who found the console's URL is the common case here; it
        // should cost a claim check, not a database round trip.
        await resolver.DidNotReceiveWithAnyArgs()
            .GetPermissionsAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_empty_grant_set_is_a_refusal_rather_than_an_allow()
    {
        var resolver = Substitute.For<IPlatformPermissionResolver>();
        resolver.GetPermissionsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>(StringComparer.Ordinal));

        var context = Context(User("Support"), PlatformConsolePermissions.OverviewRead);

        await new PlatformPermissionAuthorizationHandler(resolver).HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    // ---- the policy provider ----

    private static PermissionPolicyProvider Provider() =>
        new(Options.Create(new AuthorizationOptions()));

    [Fact]
    public async Task Provider_materializes_a_platform_requirement_for_the_platform_prefix()
    {
        var name = PermissionPolicyProvider.PlatformPolicyName(PlatformConsolePermissions.UsersRead);

        var policy = await Provider().GetPolicyAsync(name);

        policy.Should().NotBeNull();
        policy!.Requirements.OfType<PlatformPermissionRequirement>().Should()
            .ContainSingle(r => r.Permission == PlatformConsolePermissions.UsersRead);
    }

    [Fact]
    public async Task Provider_still_materializes_the_tenant_requirement()
    {
        // The regression this guards: one IAuthorizationPolicyProvider is resolved from the
        // container, so a separate provider for the platform prefix would REPLACE this one and
        // every tenant policy would resolve to null — which an endpoint guarded only by that
        // policy treats as allow. Both prefixes live in one provider to make that impossible.
        var name = PermissionPolicyProvider.PolicyName(PlatformPermissions.MediaRead);

        var policy = await Provider().GetPolicyAsync(name);

        policy.Should().NotBeNull();
        policy!.Requirements.OfType<PermissionRequirement>().Should()
            .ContainSingle(r => r.Permission == PlatformPermissions.MediaRead);
    }

    [Fact]
    public async Task Provider_falls_through_for_an_unrelated_policy_name()
    {
        (await Provider().GetPolicyAsync("SomeOtherPolicy")).Should().BeNull();
    }

    // ---- the key catalogue ----

    [Fact]
    public void Read_only_subset_contains_no_key_that_changes_state()
    {
        PlatformConsolePermissions.ReadOnly.Should().NotBeEmpty();
        PlatformConsolePermissions.ReadOnly.Should().OnlyContain(k => k.EndsWith(":read"));
        PlatformConsolePermissions.ReadOnly.Should().NotContain(PlatformConsolePermissions.LogsPurge);
        PlatformConsolePermissions.ReadOnly.Should().NotContain(PlatformConsolePermissions.UsersRoles);
        PlatformConsolePermissions.ReadOnly.Should().NotContain(PlatformConsolePermissions.TenantsLifecycle);
    }

    [Fact]
    public void Platform_keys_cannot_collide_with_the_tenant_key_space()
    {
        // Both sets are compared as strings and nothing else, so this is the whole guarantee.
        PlatformConsolePermissions.All.Should().OnlyContain(k => PlatformConsolePermissions.IsPlatformKey(k));
        PlatformPermissions.All.Should().NotContain(k => PlatformConsolePermissions.IsPlatformKey(k));
        PlatformConsolePermissions.All.Should().NotIntersectWith(PlatformPermissions.All);
    }
}
