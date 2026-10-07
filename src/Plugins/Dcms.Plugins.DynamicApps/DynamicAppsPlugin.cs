using Dcms.Plugins.DynamicApps.Api;
using Dcms.Plugins.DynamicApps.Automation;
using Dcms.Plugins.DynamicApps.Data;
using Dcms.Plugins.DynamicApps.Endpoints;
using Dcms.Plugins.DynamicApps.Metadata;
using Dcms.Plugins.Forms.Api;
using Dcms.Plugins.UserAuth.Api;
using Dcms.Plugins.VisitorAuth.Api;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
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
        // The control and data planes as contract operations: the admin and AI agents (when the
        // instance opts in) call the same services the admin routes do.
        provides:
        [
            ContractProvision.Of<IDynamicAppsConfig, Ai.DynamicAppsContracts>(),
            ContractProvision.Of<IDynamicAppsRecords, Ai.DynamicAppsContracts>(),
            // Tables and manual flows a tenant can give its site users through roles (ADR 0022).
            ContractProvision.Of<IUserResources, AppUserResources>(),
        ],
        // What flow actions reach, always through these contracts: never another plugin's tables.
        consumes:
        [
            ContractRequirement.Of<IPluginEmail>(),
            ContractRequirement.Of<IPluginNotifications>(),
            ContractRequirement.Of<IPluginContent>(),
            // Throttles the public API's writes.
            ContractRequirement.Of<IPluginCache>(),
            ContractRequirement.Of<IVisitorProfiles>(optional: true),
            // Who the signed-in site visitor is, for tables with "own records" public access.
            ContractRequirement.Of<IVisitorIdentity>(optional: true),
            // What a signed-in enterprise user holds on this app beyond its public access.
            ContractRequirement.Of<IUserAccess>(optional: true),
            // Flows trigger on form.submitted and read what was sent.
            ContractRequirement.Of<IFormSubmissions>(optional: true),
            // Actions other plugins offer to flows.
            ContractRequirement.Of<IAutomationActionProvider>(optional: true),
        ],
        // Other plugins' events, forwarded to the flows that trigger on them.
        subscribes:
        [
            EventSubscription.Of<VisitorRegistered, PlatformEventBridge>(),
            EventSubscription.Of<FormSubmitted, PlatformEventBridge>(),
        ],
        // The control plane and the data, as tabs of each app's page (admin/src).
        adminScreens:
        [
            new AdminScreen("configuration", "Configuration", AdminScreenScope.Instance, Permission: "model-read", IconName: "Blocks",
                Titles: new Dictionary<string, string> { ["cs"] = "Konfigurace" },
                Description: "Tables, relationships, views, automations, public access and revisions."),
            new AdminScreen("records", "Records", AdminScreenScope.Instance, Permission: "data-read", IconName: "Table",
                Titles: new Dictionary<string, string> { ["cs"] = "Záznamy" },
                Description: "Browse and edit the app's records."),
        ],
        category: "Content",
        summary: "Your own tables, automations and API, versioned and AI-configurable.",
        iconName: "DatabaseZap",
        tags: ["data", "automation", "api", "low-code"]);

    public void ConfigureServices(IServiceCollection services, PluginHost host)
    {
        services.AddMemoryCache();
        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<AppEventLog>();
        services.AddScoped<ConfigurationService>();
        services.AddScoped<RuntimeModelProvider>();
        services.AddScoped<RecordService>();
        services.AddScoped<FlowRunQueue>();
        services.AddScoped<FlowExecutor>();
        services.AddScoped<FlowRunService>();
        if (host.IsAdmin)
        {
            services.AddHostedService<AutomationWorker>();
        }
    }

    /// <summary>Site plane, <c>/api/{slug}/…</c>: the published tables, as each one's public access allows.</summary>
    public void MapEndpoints(IPluginEndpointBuilder endpoints) => PublicEndpoints.Map(endpoints);

    /// <summary>The OpenAPI of the published model — never the draft — for the tenant's document and generated client.</summary>
    public async Task<OpenApiFragment> BuildOpenApiFragmentAsync(PluginInstanceContext instance, IServiceProvider services, CancellationToken ct)
    {
        var model = await PublishedModels.LoadAsync(services.GetRequiredService<Dcms.Shared.Data.DynamicApps.AppsDbContext>(),
            services.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(), instance.TenantId, instance.InstanceId, ct);
        return model is null
            ? new OpenApiFragment(instance.Name, instance.Description ?? Manifest.Description, [], new Dictionary<string, System.Text.Json.Nodes.JsonNode>())
            : PublicApi.Fragment(instance, model, Manifest);
    }

    /// <summary>The published revision's hash: a publish changes the API, so cached documents and clients are rebuilt.</summary>
    public Task<string?> ApiVersionAsync(PluginInstanceContext instance, IServiceProvider services, CancellationToken ct) =>
        PublishedModels.HashAsync(services.GetRequiredService<Dcms.Shared.Data.DynamicApps.AppsDbContext>(), instance.TenantId, instance.InstanceId, ct);

    /// <summary>Admin plane, <c>/api/admin/plugins/{slug}/…</c>: the configuration and the records.</summary>
    public void MapAdminEndpoints(IPluginEndpointBuilder endpoints)
    {
        ModelEndpoints.Map(endpoints);
        RecordEndpoints.Map(endpoints);
        AutomationEndpoints.Map(endpoints);
    }
}
