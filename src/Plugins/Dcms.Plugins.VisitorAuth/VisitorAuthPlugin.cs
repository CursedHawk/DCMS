using System.Text.Json.Nodes;
using Dcms.Plugins.VisitorAuth.Api;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Data;
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
        clientBindings:
        [
            new ClientBinding(["auth"], "http.visitorAuth({slugLiteral})",
                "api.{member}.auth.register(body) / .login(body) / .refresh(token) / .me() / .getProfile() / .updateProfile(update)",
                "VisitorTokens / VisitorProfile / VisitorOwnProfile",
                "POST /api/{slug}/register|login|refresh, GET /api/{slug}/me, GET|PUT /api/{slug}/me/profile",
                "Visitor accounts and profiles. Pass `visitorToken` to createTenantClient for `me()` and the profile calls."),
        ],
        category: "Engagement",
        summary: "Accounts, profiles and gated content for site visitors.",
        iconName: "KeyRound",
        provides:
        [
            ContractProvision.Of<IVisitorIdentity, VisitorIdentity>(),
            ContractProvision.Of<IVisitorProfiles, VisitorProfiles>(),
        ],
        consumes: [ContractRequirement.Of<IPluginEvents>()],
        dataSets:
        [
            DataSetDeclaration.Of<VisitorsDataSet>("visitors", "Visitors",
                "Accounts registered on the site, with their profiles.",
                readPermission: ReadPermission, writePermission: ManagePermission, iconName: "Users"),
        ]);

    public void ConfigureServices(IServiceCollection services, PluginHost host)
    {
        // Prod safety on the plane that mints visitor tokens. Visitor:SigningKey is a manual
        // `vault kv put` in the deploy guide — infra/vault/init.sh only writes a placeholder to
        // secret/dcms/content-api — so the way this goes wrong is a step being skipped, not a bad
        // value being chosen. The whole tenant binding in a visitor token is its audience, and the
        // tenant id is not a secret, so an unconfigured key means every visitor session on every
        // tenant is forgeable by anyone who has read this repository. Fail loudly instead.
        if (host.IsSite && host.IsProduction)
        {
            var key = host.Configuration[$"{VisitorTokenOptions.SectionName}:SigningKey"]?.Trim() ?? string.Empty;
            if (key.Length == 0
                || key == VisitorTokenOptions.DevelopmentSigningKey
                || System.Text.Encoding.UTF8.GetByteCount(key) < VisitorTokenOptions.MinimumKeyBytes)
            {
                throw new InvalidOperationException(
                    "Refusing to start: Visitor__SigningKey is unset, the development default, or shorter "
                    + $"than {VisitorTokenOptions.MinimumKeyBytes} bytes in Production. Set a strong, unique "
                    + "value (openssl rand -base64 32) at secret/dcms/content-api.");
            }
        }

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
