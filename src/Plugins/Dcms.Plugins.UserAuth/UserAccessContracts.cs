using Dcms.Plugins.UserAuth.Api;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Contracts.Realms;
using Dcms.Shared.Security.Realms;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.UserAuth;

/// <summary>
/// <c>users.roles@1</c>: the console's roles and grants, through the same <see cref="UserAccessAdmin"/>
/// the routes use. Admin plane only (admin and AI), like the routes.
/// </summary>
public sealed class UserRoles(IPluginContext context, IServiceProvider services) : IUserRoles
{
    private UserAccessAdmin Admin => new(context, services);

    public async Task<UserResourceCatalogList> ListResourcesAsync(CancellationToken ct) => new(await Admin.ResourcesAsync(ct));

    public async Task<SiteRoleList> ListRolesAsync(CancellationToken ct)
    {
        var (roles, grants) = await Admin.RolesAsync(ct);
        return new SiteRoleList(roles.Select(r => ToRole(r, grants)).ToList());
    }

    public async Task<SiteRole> CreateRoleAsync(RoleCreate input, CancellationToken ct) =>
        ToRole(await Admin.CreateRoleAsync(new RoleWrite(input.Key, input.Name, input.Description, input.Permissions, input.BypassRowAccess), ct), []);

    public async Task<SiteRole> UpdateRoleAsync(RoleUpdate input, CancellationToken ct)
    {
        var admin = Admin;
        var role = await admin.UpdateRoleAsync(input.RoleId, new RoleWrite(input.Key, input.Name, input.Description, input.Permissions, input.BypassRowAccess), ct)
                   ?? throw NoRole(input.RoleId);
        return ToRole(role, (await admin.RolesAsync(ct)).Grants);
    }

    public async Task DeleteRoleAsync(RoleRef input, CancellationToken ct)
    {
        if (!await Admin.DeleteRoleAsync(input.RoleId, ct)) throw NoRole(input.RoleId);
    }

    public async Task<GrantResult> GrantAsync(RoleGrant input, CancellationToken ct) =>
        new((await Admin.GrantAsync(input.RoleId, new GrantWrite(input.SubjectType, input.SubjectId), ct) ?? throw NoRole(input.RoleId)).Grant.Id);

    public async Task RevokeAsync(RoleRevoke input, CancellationToken ct)
    {
        if (!await Admin.RevokeAsync(input.RoleId, input.GrantId, ct))
        {
            throw new ContractValidationException($"Role {input.RoleId} has no grant {input.GrantId}.");
        }
    }

    private static ContractValidationException NoRole(Guid id) => new($"There is no role {id}; list the roles first.");

    private static SiteRole ToRole(Dcms.Shared.Data.UserAuth.UserRole r, IEnumerable<Dcms.Shared.Data.UserAuth.UserGrant> grants) =>
        new(r.Id, r.Key, r.Name, r.Description, r.Permissions, r.BypassRowAccess,
            grants.Where(g => g.RoleId == r.Id).Select(g => new RoleGrantInfo(g.Id, UserAccessAdmin.Camel(g.SubjectType), g.SubjectId)).ToList());
}

/// <summary>
/// <c>users.realm@1</c>: how people sign in and what needs signing in. Provider client secrets go
/// to identity and never come back (identity answers <c>hasSecret</c> only). Admin plane only.
/// </summary>
public sealed class UserRealm(IPluginContext context, IServiceProvider services) : IUserRealm
{
    private RealmAdminClient Realms => services.GetService<RealmAdminClient>()
        ?? throw new InvalidOperationException("The realm is available on the admin plane only.");

    private SiteGatePublisher Publisher => services.GetRequiredService<SiteGatePublisher>();

    public async Task<RealmState> GetRealmAsync(CancellationToken ct) =>
        ToState(await Publisher.SyncRealmAsync(await RealmAsync(ct), null, ct));

    public async Task<RealmState> SetPasswordSignInAsync(PasswordSignIn input, CancellationToken ct)
    {
        var realm = await OwnRealmAsync(ct);
        return ToState(await UserDirectory.Call(() => Publisher.SyncRealmAsync(realm, input.Enabled, ct)));
    }

    public async Task<SignInProviderList> ListProvidersAsync(CancellationToken ct)
    {
        var realm = await RealmAsync(ct);
        var providers = await UserDirectory.Call(() => Realms.CallAsync<List<RealmProviderInfo>>(realm, HttpMethod.Get, "/providers", null, ct));
        return new SignInProviderList(providers.Select(ToProvider).ToList());
    }

    public async Task<SignInProvider> SetProviderAsync(SignInProviderWrite input, CancellationToken ct)
    {
        var realm = await OwnRealmAsync(ct);
        // A stored client secret goes wherever the provider points: moving one that holds a secret
        // takes a new secret in the same call (which only the operator can give), never the old one.
        var existing = (await UserDirectory.Call(() => Realms.CallAsync<List<RealmProviderInfo>>(realm, HttpMethod.Get, "/providers", null, ct)))
            .FirstOrDefault(p => p.Key == input.Key);
        if (existing is { HasSecret: true } && input.ClientSecret is null
            && (existing.Kind != input.Kind || existing.Issuer != input.Issuer || existing.EntraTenant != input.EntraTenant))
        {
            throw new ContractValidationException(
                $"Provider '{input.Key}' holds a client secret: changing where it signs in takes a new clientSecret from the operator in the same call.");
        }
        var write = new RealmProviderWrite(input.Kind, input.DisplayName, input.Enabled, input.ClientId, input.ClientSecret, input.Issuer,
            input.EntraTenant, input.HostedDomain, input.Provisioning, input.AllowedDomains, input.DefaultGroups, input.GroupClaim, input.GroupMappings);
        return ToProvider(await UserDirectory.Call(() =>
            Realms.CallAsync<RealmProviderInfo>(realm, HttpMethod.Put, $"/providers/{Uri.EscapeDataString(input.Key)}", write, ct)));
    }

    public async Task RemoveProviderAsync(ProviderRef input, CancellationToken ct)
    {
        var realm = await OwnRealmAsync(ct);
        await UserDirectory.Call(() => Realms.CallAsync(realm, HttpMethod.Delete, $"/providers/{Uri.EscapeDataString(input.Key)}", null, ct));
    }

    public async Task<SiteRulesList> ListSiteRulesAsync(CancellationToken ct) => new(await new UserAccessAdmin(context, services).SitesAsync(ct));

    public async Task<RulesSaved> SetSiteRulesAsync(SiteRulesWrite input, CancellationToken ct)
    {
        var rules = input.Rules.Select(r => new GateWrite(r.Prefix, r.Access, r.Groups)).ToList();
        try
        {
            return await new UserAccessAdmin(context, services).SetSiteRulesAsync(input.SiteId, rules, ct)
                ? new RulesSaved(true)
                : throw new ContractValidationException($"There is no site {input.SiteId}; list the sites first.");
        }
        catch (Exception e) when (e is RealmAdminException or HttpRequestException)
        {
            // Saved: the reconciler tells the edge within minutes.
            return new RulesSaved(false, "Saved, but not live yet: the edge will have the rules within minutes.");
        }
    }

    public async Task<ApiAccessList> ListApiAccessAsync(CancellationToken ct) => new(await new UserAccessAdmin(context, services).ApiAccessAsync(ct));

    public async Task SetApiAccessAsync(ApiAccessChange input, CancellationToken ct)
    {
        if (!await new UserAccessAdmin(context, services).SetApiAccessAsync(input.InstanceId, new ApiAccessWrite(input.Access), ct))
        {
            throw new ContractValidationException($"No enabled app has the id {input.InstanceId}; list the API access first.");
        }
    }

    private Task<Guid> RealmAsync(CancellationToken ct) => AdminRealm.OfAsync(context, services, ct);

    private Task<Guid> OwnRealmAsync(CancellationToken ct) => AdminRealm.OwnAsync(context, services, ct);

    private static RealmState ToState(RealmInfo? realm) =>
        realm is null
            ? throw new ContractValidationException("The User Authentication plugin is not enabled in this environment.")
            : new RealmState(realm.Slug, realm.Hosts, realm.PasswordEnabled, realm.ClientReady);

    private static SignInProvider ToProvider(RealmProviderInfo p) =>
        new(p.Key, p.Kind, p.DisplayName, p.Enabled, p.ClientId, p.HasSecret, p.Issuer, p.EntraTenant, p.HostedDomain, p.Provisioning,
            p.AllowedDomains, p.DefaultGroups, p.GroupClaim, p.GroupMappings, p.CallbackUrl);
}
