using Dcms.Plugins.DynamicApps.Api;
using Dcms.Plugins.DynamicApps.Data;
using Dcms.Plugins.DynamicApps.Endpoints;
using Dcms.Plugins.DynamicApps.Metadata;
using Dcms.PluginSdk.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dcms.Plugins.DynamicApps;

/// <summary>
/// A Dataverse-style application runtime: one instance is one application whose tables,
/// fields, relationships, views and automations the tenant defines at runtime (ADR 0021).
///
/// <para>The application's configuration is database state — versioned revisions in the
/// <c>apps</c> schema, drafted, validated and published — never instance config, which holds
/// only the bootstrap settings below. Runtime-defined tables are not DCMS contracts: the
/// plugin's contracts, jobs and routes are static, and the tenant's model is data behind them.</para>
/// </summary>
public sealed class DynamicAppsPlugin : IPlugin
{
    public const string PluginId = DynamicAppsPermissions.PluginId;

    private const string ConfigSchema = """
        {
          "type": "object",
          "properties": {
            "displayName": {
              "type": "string",
              "title": "Application name",
              "description": "Shown in the admin and in the generated API description.",
              "maxLength": 120
            },
            "defaultLocale": {
              "type": "string",
              "title": "Default locale",
              "description": "BCP 47 tag, e.g. en or cs.",
              "pattern": "^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$",
              "default": "en"
            },
            "defaultTimeZone": {
              "type": "string",
              "title": "Default time zone",
              "description": "IANA zone used by date functions and schedules, e.g. Europe/Prague.",
              "default": "UTC"
            }
          },
          "additionalProperties": false
        }
        """;

    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: PluginId,
        name: "Dynamic Apps",
        description: "Build your own data application: tables, relationships, views, automations and a generated API, "
            + "versioned and published like a release — and configurable by asking the assistant.",
        allowMultipleInstances: true,
        configJsonSchema: ConfigSchema,
        permissions:
        [
            new PermissionDefinition("model-read", "View application configuration",
                "See the application's tables, automations, security rules and revision history."),
            new PermissionDefinition("model-write", "Edit application configuration",
                "Change the draft configuration. Nothing reaches the live application until it is published."),
            new PermissionDefinition("publish", "Publish application configuration",
                "Make a draft live, or roll back to an earlier published revision."),
            new PermissionDefinition("data-read", "View application records"),
            new PermissionDefinition("data-write", "Create and edit application records"),
            new PermissionDefinition("data-delete", "Delete application records"),
            new PermissionDefinition("flows-run", "Run automations",
                "Start a flow by hand and retry failed runs."),
        ],
        category: "Content",
        summary: "Your own tables, automations and API, versioned and AI-configurable.",
        iconName: "DatabaseZap",
        tags: ["data", "automation", "api", "low-code"]);

    public void ConfigureServices(IServiceCollection services, PluginHost host)
    {
        services.AddMemoryCache();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ConfigurationService>();
        services.AddScoped<RuntimeModelProvider>();
        services.AddScoped<RecordService>();
    }

    /// <summary>Admin plane, <c>/api/admin/plugins/{slug}/…</c>: the configuration and the records.</summary>
    public void MapAdminEndpoints(IPluginEndpointBuilder endpoints)
    {
        ModelEndpoints.Map(endpoints);
        RecordEndpoints.Map(endpoints);
    }
}
