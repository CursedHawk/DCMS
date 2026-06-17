using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.Analytics;

public sealed class AnalyticsPlugin : IPlugin
{
    // Tenant-identifying header. Hardcoded here so the plugin stays free of a
    // dependency on the shared tenancy layer; mirrors X-Dcms-Tenant.
    private const string TenantHeader = "X-Dcms-Tenant";

    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: "analytics",
        name: "Analytics",
        description: "Website analytics collection, rollups and dashboards.",
        allowMultipleInstances: false);

    public void ConfigureServices(IServiceCollection services)
    {
        // Plugin services arrive in later phases.
    }

    public void MapEndpoints(IPluginEndpointBuilder endpoints)
    {
        // Delivery endpoints arrive in later phases.
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
