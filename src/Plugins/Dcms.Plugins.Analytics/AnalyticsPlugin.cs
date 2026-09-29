using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions;
using Dcms.Plugins.Analytics.Api;
using Dcms.PluginSdk.Abstractions.Contracts;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dcms.Plugins.Analytics;

/// <summary>
/// Website analytics. Owns the whole path: the public beacon (site plane), the ingest consumer
/// that writes events and rollups, and the dashboard (admin plane). Other plugins record their
/// own events through <see cref="IAnalytics"/>.
/// </summary>
public sealed class AnalyticsPlugin : IPlugin
{
    public const string PluginId = "analytics";

    // Tenant-identifying header, documented for externally hosted sites; mirrors X-Dcms-Tenant.
    private const string TenantHeader = "X-Dcms-Tenant";

    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: PluginId,
        name: "Analytics",
        description: "Website analytics collection, rollups and dashboards.",
        allowMultipleInstances: false,
        provides: [ContractProvision.Of<IAnalytics, AnalyticsTracking>()],
        category: "Insight",
        summary: "Page views, sessions and campaign attribution.",
        iconName: "TrendingUp");

    public void ConfigureServices(IServiceCollection services, PluginHost host)
    {
        if (host.IsSite)
        {
            // Country comes from the edge; register a GeoIP-database implementation in its
            // place if the deployment has no country-stamping proxy.
            services.TryAddSingleton<IGeoIpResolver, HeaderGeoIpResolver>();
            services.AddCors(o => o.AddPolicy(AnalyticsIngestEndpoints.CollectCorsPolicy, policy => policy
                .AllowAnyOrigin()
                .AllowAnyHeader()
                .WithMethods("GET", "POST")));
        }
        else
        {
            services.AddHostedService<AnalyticsConsumer>();
        }
    }

    public void MapEndpoints(IPluginEndpointBuilder endpoints) => AnalyticsIngestEndpoints.MapInstanceRoutes(endpoints);

    // Tenant-wide rather than per instance (analytics is single-instance and its data is the
    // tenant's), so these keep their established addresses.
    public void MapHostEndpoints(IEndpointRouteBuilder app, PluginHost host)
    {
        if (host.IsSite)
        {
            AnalyticsIngestEndpoints.MapHostRoutes(app);
        }
        else
        {
            AnalyticsDashboardEndpoints.MapHostRoutes(app);
        }
    }

    // Document the anonymous ingest beacon so externally hosted sites (not served
    // on a DCMS domain) can record analytics and have it in the OpenAPI spec.
    // The endpoint itself lives in content-api (AnalyticsIngestEndpoints); the
    // assembler mounts this fragment under /api/{instanceSlug}/collect.
    public OpenApiFragment BuildOpenApiFragment(PluginInstanceContext instance)
    {
        var eventSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["type"] = new JsonObject
                {
                    ["type"] = "string",
                    ["default"] = "pageview",
                    ["description"] = "Event type, e.g. \"pageview\" or a custom event name.",
                },
                ["path"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Path the event occurred on (e.g. \"/pricing\"). Defaults to \"/\".",
                },
                ["referrer"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Referring URL, if any.",
                },
                ["sessionId"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Opaque per-visit id; hashed server-side into an anonymous visitor.",
                },
                ["props"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = true,
                    ["description"] = "Arbitrary custom properties for the event.",
                },
            },
        };

        var tenantHeader = new JsonObject
        {
            ["name"] = TenantHeader,
            ["in"] = "header",
            ["required"] = true,
            ["schema"] = new JsonObject { ["type"] = "string" },
            ["example"] = instance.Slug,
            ["description"] =
                "Your tenant slug. Required when posting directly to the content API from an " +
                "externally hosted site. Sites served on a DCMS domain may omit it — the platform " +
                "injects it automatically.",
        };

        var description =
            $"{instance.Description}\n\n{Manifest.Description}\n\n" +
            "Records an anonymous analytics event (page view or custom event). No authentication " +
            "required. Externally hosted sites set the " + TenantHeader + " header to their tenant " +
            "slug; sites served on a DCMS domain can omit it.";

        return new OpenApiFragment(
            TagName: instance.Name,
            TagDescription: $"{instance.Description}\n\n{Manifest.Description}",
            Paths:
            [
                new OpenApiPathFragment(
                    RelativePath: "/collect",
                    Method: "post",
                    OperationId: $"{instance.Slug}_collect",
                    Summary: "Record an analytics event",
                    Description: description,
                    ResponseSchema: null,
                    RequestBodySchema: eventSchema,
                    Parameters: [tenantHeader],
                    SuccessStatus: "202"),
            ],
            Schemas: new Dictionary<string, JsonNode> { [$"{instance.Slug}_event"] = eventSchema });
    }
}
