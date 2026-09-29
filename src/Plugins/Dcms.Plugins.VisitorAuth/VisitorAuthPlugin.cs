using System.Text.Json.Nodes;
using Dcms.Plugins.VisitorAuth.Api;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dcms.Plugins.VisitorAuth;

/// <summary>
/// Visitor accounts for a tenant's website, and the reference contract provider: other plugins
/// learn who the visitor is (<c>visitors.identity@1</c>) and read the profile attributes the
/// tenant chose to share with them (<c>visitors.profiles@1</c>).
/// </summary>
public sealed class VisitorAuthPlugin : IPlugin
{
    public const string PluginId = "visitor-auth";
    public const string ReadPermission = VisitorPermissions.Read;
    public const string ManagePermission = VisitorPermissions.Manage;

    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: PluginId,
        name: "Visitor Authentication",
        description: "Visitor accounts (register, login, refresh) and profiles for the tenant website.",
        allowMultipleInstances: false,
        configJsonSchema: new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { [VisitorAttributes.ConfigKey] = VisitorAttributes.ConfigSchema() },
        }.ToJsonString(),
        permissions:
        [
            new PermissionDefinition("read", "View visitor profiles"),
            new PermissionDefinition("manage", "Edit visitor profiles"),
        ],
        // The site renders the profile form from the definitions; values are never in config.
        publicConfigKeys: [VisitorAttributes.ConfigKey],
        category: "Engagement",
        summary: "Accounts, profiles and gated content for site visitors.",
        iconName: "KeyRound",
        provides:
        [
            ContractProvision.Of<IVisitorIdentity, VisitorIdentity>(),
            ContractProvision.Of<IVisitorProfiles, VisitorProfiles>(),
        ],
        consumes: [ContractRequirement.Of<IPluginEvents>()]);

    public void ConfigureServices(IServiceCollection services, PluginHost host)
    {
        services.AddHttpContextAccessor();
        services.AddOptions<VisitorTokenOptions>().BindConfiguration(VisitorTokenOptions.SectionName);
        services.AddSingleton(sp => new VisitorTokenService(sp.GetRequiredService<IOptions<VisitorTokenOptions>>().Value));

        // Never the default scheme: platform users and visitors both send bearer tokens, and
        // only an endpoint that asks for a visitor should read one as a visitor's.
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, VisitorAuthenticationHandler>(VisitorAuthenticationHandler.SchemeName, null);
        services.AddAuthorizationBuilder()
            .AddPolicy(VisitorAuthorization.Policy, policy => policy
                .AddAuthenticationSchemes(VisitorAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser());
    }

    public void MapEndpoints(IPluginEndpointBuilder endpoints) => VisitorAuthEndpoints.Map(endpoints);
}
