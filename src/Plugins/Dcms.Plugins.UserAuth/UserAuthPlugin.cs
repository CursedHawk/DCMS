using System.Text.Json.Nodes;
using Dcms.Plugins.UserAuth.Api;
using Dcms.PluginSdk.Abstractions;
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
        iconName: "ShieldCheck");

    public void ConfigureServices(IServiceCollection services, PluginHost host)
    {
        if (host.IsAdmin)
        {
            // The site rules the edge enforces: published on every change, reconciled every few minutes.
            services.AddScoped<SiteGatePublisher>();
            services.AddHostedService<SiteGateReconciler>();
        }
    }
}
