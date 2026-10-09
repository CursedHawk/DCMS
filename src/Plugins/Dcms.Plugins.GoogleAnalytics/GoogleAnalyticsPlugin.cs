using System.Text.Json;
using System.Text.RegularExpressions;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Caching;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Plugins.GoogleAnalytics;

/// <summary>
/// Google Analytics 4 on the tenant's sites, each with its own Measurement ID (docs/adr/0023).
///
/// <para>The IDs live in this plugin's single instance config, keyed by site id, and are edited on
/// its "settings" screen. The published-site runtimes (hydrate.js for Mode A, src/dcms/analytics.ts
/// for Modes B and D) ask <c>GET /api/ga/config</c> which ID — if any — belongs to the site they
/// are on, and load Google's tag only after the visitor accepts the consent banner. Looked up at
/// runtime rather than stamped at publish, so changing an ID or disabling the plugin reaches every
/// live site within the cache window, without republishing any of them.</para>
///
/// <para>Mode C (an uploaded bundle) runs no DCMS runtime; its owner adds the tag themselves.</para>
/// </summary>
public sealed partial class GoogleAnalyticsPlugin : IPlugin
{
    public const string PluginId = "google-analytics";

    private const string ConfigSchema = """
        {
          "type": "object",
          "properties": {
            "sites": {
              "type": "object",
              "title": "Measurement IDs",
              "description": "One GA4 Measurement ID (G-…) per site, keyed by site id. Edit them on the plugin's Sites screen.",
              "additionalProperties": { "type": "string", "pattern": "^G-[A-Z0-9]{4,20}$" }
            }
          }
        }
        """;

    private const string SiteHeader = "X-Dcms-Site";

    /// <summary>How long a site's answer is reused — by this service and by the visitor's browser.</summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: PluginId,
        name: "Google Analytics",
        description: "Google Analytics 4 on your sites, a Measurement ID per site, loaded only after consent.",
        allowMultipleInstances: false,
        configJsonSchema: ConfigSchema,
        // Its host route (/api/ga/config) would shadow an instance with this slug.
        reservedSlugs: ["ga"],
        // Deliberately false. The flag stamps Mode A's consent block with "the DCMS collector is on",
        // which also makes hydrate.js send DCMS beacons; GA's own need for a banner is answered by
        // /api/ga/config at runtime instead.
        tracksVisitors: false,
        category: "Insight",
        summary: "GA4 per site, behind the consent banner.",
        iconName: "ChartColumn",
        adminScreens:
        [
            new AdminScreen("sites", "Sites", AdminScreenScope.Instance,
                IconName: "Globe", Titles: new Dictionary<string, string> { ["cs"] = "Weby" },
                Description: "The Measurement ID each site sends to."),
        ]);

    public void MapHostEndpoints(IEndpointRouteBuilder app, PluginHost host)
    {
        if (!host.IsSite)
        {
            return;
        }

        // Which Measurement ID the site asking has. `{}` for none, never 404: the runtime has one
        // branch ("is there an id"), and a site without GA must not log a failed request on every
        // page. X-Dcms-Site is set by site-host from the Host it resolved and scrubbed by the edge,
        // so a page cannot ask on another site's behalf.
        app.MapGet("/api/ga/config", async (
            HttpContext http, ITenantContext tenant, CmsDbContext cms, ICacheService cache, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = $"public, max-age={(int)CacheFor.TotalSeconds}";
            if (tenant.TenantId is not { } tenantId
                || !Guid.TryParse(http.Request.Headers[SiteHeader], out var siteId))
            {
                return Results.Ok(new { });
            }

            var key = $"t:{tenantId}:ga:sites";
            var sites = await cache.GetAsync<Dictionary<string, string>>(key, ct);
            if (sites is null)
            {
                sites = await LoadAsync(cms, ct);
                await cache.SetAsync(key, sites, CacheFor, ct);
            }

            return sites.TryGetValue(siteId.ToString(), out var id)
                ? Results.Ok(new { measurementId = id })
                : Results.Ok(new { });
        }).AuditExempt("Reads public configuration; changes nothing.")
          .PermissionExempt("Anonymous by design: every visitor's browser asks before loading the tag.");
    }

    /// <summary>
    /// The enabled instance's site → ID map, empty when the plugin is off. Each ID is checked again
    /// here although the config validator already did: it ends up in a script URL on the tenant's
    /// pages, and config written before the schema tightened would otherwise go straight there.
    /// </summary>
    private static async Task<Dictionary<string, string>> LoadAsync(CmsDbContext cms, CancellationToken ct)
    {
        var config = await cms.PluginInstances.AsNoTracking()
            .Where(p => p.PluginId == PluginId && p.Enabled)
            .Select(p => p.ConfigJson)
            .FirstOrDefaultAsync(ct);
        return MeasurementIds(config);
    }

    /// <summary>Parses the instance config's <c>sites</c> map, keeping only well-formed IDs.</summary>
    public static Dictionary<string, string> MeasurementIds(string? configJson)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return result;
        }
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            if (doc.RootElement.TryGetProperty("sites", out var sites) && sites.ValueKind == JsonValueKind.Object)
            {
                foreach (var site in sites.EnumerateObject())
                {
                    if (site.Value.ValueKind == JsonValueKind.String && site.Value.GetString() is { } id
                        && MeasurementId().IsMatch(id) && Guid.TryParse(site.Name, out var siteId))
                    {
                        result[siteId.ToString()] = id;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // An unreadable config serves no tag rather than failing every page.
        }
        return result;
    }

    [GeneratedRegex("^G-[A-Z0-9]{4,20}$")]
    private static partial Regex MeasurementId();
}
