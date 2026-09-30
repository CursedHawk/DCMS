using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.Shared.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.Search;

/// <summary>
/// Sitewide search for the public site: GET /api/{slug}/search?q=... over the platform's
/// search index (<c>dcms.search@1</c>). The index itself is maintained for every tenant by the
/// platform; this plugin is what publishes it on a site.
/// </summary>
public sealed class SearchPlugin : IPlugin
{
    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: "search",
        name: "Sitewide Search",
        description: "Full-text search across all searchable plugin content of the tenant.",
        allowMultipleInstances: false,
        clientBindings:
        [
            new ClientBinding(["search"],
                "(params: SearchParams): Promise<SearchResult> => http.search({slugLiteral}, params)",
                "api.{member}.search({ q, limit? })", "SearchResult", "GET /api/{slug}/search",
                "Full-text search across searchable content.", ["SearchParams", "SearchResult"]),
        ],
        category: "Engagement",
        summary: "Full-text search across published content.",
        iconName: "Search",
        consumes: [ContractRequirement.Of<IPluginSearch>()]);

    public void ConfigureServices(IServiceCollection services, PluginHost host)
    {
    }

    public void MapEndpoints(IPluginEndpointBuilder endpoints)
    {
        endpoints.MapGet("/search", async (string? q, int? limit, IPluginContext context, DcmsMetrics metrics, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                return Results.Ok(new { items = Array.Empty<object>(), total = 0 });
            }
            var results = await context.Contracts.Get<IPluginSearch>().SearchAsync(
                new SearchRequest(q, Math.Clamp(limit ?? 20, 1, 50), IncludeTotal: true), ct);

            // Counted here, so the number means "a search that ran", not "a URL was hit": the
            // blank-query exit above and a disabled instance (404 from the runtime) are not searches.
            metrics.SearchQuery(context.TenantId);
            return Results.Ok(new
            {
                items = results.Items.Select(i => new { i.Title, i.Url, i.ContentType }),
                total = results.Total ?? 0,
            });
        });
    }
}
