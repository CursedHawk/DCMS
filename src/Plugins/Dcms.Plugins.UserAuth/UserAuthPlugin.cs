using System.Text.Json.Nodes;
using Dcms.Plugins.DynamicApps.Api;
using Dcms.Plugins.UserAuth.Api;
using Dcms.Plugins.VisitorAuth.Api;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.UserAuth;

/// <summary>
/// Enterprise users for a tenant's sites (ADR 0022): the management and policy layer over the
/// tenant's realm in Identity. Accounts, sign-in and sessions live in Identity; who may reach
/// which page, table, flow or API — roles, grants and site access rules — lives here.
/// </summary>
public sealed class UserAuthPlugin : IPlugin
{
    public const string PluginId = UserAuthPermissions.PluginId;

    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: PluginId,
        name: "User Authentication",
        description: "Enterprise sign-in for your sites: tenant users, groups, roles and access to pages, apps and APIs.",
        // One realm per tenant, so one policy over it.
        allowMultipleInstances: false,
        configJsonSchema: new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }.ToJsonString(),
        permissions:
        [
            new PermissionDefinition("users-read", "View site users",
                "See the people who can sign in to your sites, their groups and their sessions."),
            new PermissionDefinition("users-manage", "Manage site users",
                "Invite, disable and delete site users, reset their sign-in and change their groups."),
            new PermissionDefinition("access-manage", "Manage site access",
                "Define roles and who holds them, and which pages of each site need signing in."),
            new PermissionDefinition("providers-manage", "Manage sign-in providers",
                "Connect Google, Microsoft or another identity provider, including its client secret."),
        ],
        category: "Engagement",
        summary: "Enterprise sign-in, users, groups and roles that gate your sites, apps and APIs.",
        iconName: "ShieldCheck",
        provides:
        [
            ContractProvision.Of<IUserIdentity, UserIdentity>(),
            ContractProvision.Of<IUserAccess, UserAccess>(),
            ContractProvision.Of<IUserDirectory, UserDirectory>(),
            // The same signed-in user, to plugins that only know visitors (own records, prefilled forms).
            ContractProvision.Of<IVisitorIdentity, UserVisitorIdentity>(),
            // Invite and group membership as steps of Dynamic Apps flows.
            ContractProvision.Of<IAutomationActionProvider, UserAutomationActions>(),
        ],
        consumes:
        [
            // What roles can hold: whatever enabled plugins offer to gate.
            ContractRequirement.Of<IUserResources>(optional: true),
            // user.invited and user.activated.
            ContractRequirement.Of<IPluginEvents>(),
        ],
        // The plugin's page (admin/src): the directory, the policy over it, and how people sign in.
        adminScreens:
        [
            new AdminScreen("users", "Users", AdminScreenScope.Instance, Permission: "users-read", IconName: "Users",
                Titles: new Dictionary<string, string> { ["cs"] = "Uživatelé" },
                Description: "The people who can sign in to your sites, and their groups."),
            new AdminScreen("access", "Access", AdminScreenScope.Instance, Permission: "users-read", IconName: "ShieldCheck",
                Titles: new Dictionary<string, string> { ["cs"] = "Přístup" },
                Description: "Which pages of each site need signing in, and roles over your apps and APIs."),
            new AdminScreen("sign-in", "Sign-in", AdminScreenScope.Instance, Permission: "users-read", IconName: "KeyRound",
                Titles: new Dictionary<string, string> { ["cs"] = "Přihlášení" },
                Description: "Passwords and the identity providers your users sign in with."),
        ]);

    public void ConfigureServices(IServiceCollection services, PluginHost host)
    {
        services.AddHttpContextAccessor();
        // On both planes: the contracts read the request's user wherever they are called. Only
        // the edge ever sets the header, and only on a tenant site's requests.
        services.AddUserAuthentication(host.Configuration);
        if (host.IsSite)
        {
            // API access: every instance's /api/{slug}, before its endpoint runs.
            services.AddSingleton<IPluginRequestGate, UserApiAccess>();
        }
        if (host.IsAdmin)
        {
            // The site rules the edge enforces: published on every change, reconciled every few
            // minutes. RealmAdminClient comes from admin-api, the one service identity answers.
            services.AddScoped<SiteGatePublisher>();
            services.AddScoped<RealmRelay>();
            services.AddHostedService<SiteGateReconciler>();
            services.AddHostedService<RealmEventRelay>();
        }
    }

    /// <summary>Admin plane, <c>/api/admin/plugins/{slug}/…</c>: users, groups, providers, roles and site rules.</summary>
    public void MapAdminEndpoints(IPluginEndpointBuilder endpoints) => UserAuthAdminEndpoints.Map(endpoints);
}
