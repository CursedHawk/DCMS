using System.Text.Json;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Caching;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Messaging;
using Dcms.Shared.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Plugins.Analytics;

/// <summary>
/// The anonymous analytics beacon. Events are fire-and-forget onto the ANALYTICS stream; the
/// admin-plane <see cref="AnalyticsConsumer"/> persists them and updates the rollups.
/// </summary>
internal static class AnalyticsIngestEndpoints
{
    /// <summary>
    /// Any origin may POST events (no credentials), so externally hosted sites can use the
    /// documented collect API. Registered by the plugin on the site plane.
    /// </summary>
    public const string CollectCorsPolicy = "analytics-collect";

    private const string NotAnAction =
        "Visitor telemetry, not an action on tenant state. Recorded in analytics.events, which has its own retention and is tenant-purgeable — audit is neither.";

    private const string Anonymous = "Anonymous by design: the site's own visitors send page views.";

    /// <summary><c>POST /api/{slug}/collect</c> — slug-addressed, for an explicit analytics instance.</summary>
    public static void MapInstanceRoutes(IPluginEndpointBuilder endpoints)
    {
        endpoints.MapPost("/collect", async (
            CollectRequest body, HttpContext http, IPluginContext context, ISandboxContext sandbox,
            IEventPublisher events, IGeoIpResolver geo, CancellationToken ct) =>
        {
            // Preview sandbox: swallow the beacon so real analytics stay clean.
            if (!sandbox.IsSandbox)
            {
                await PublishAsync(events, context.TenantId, body, http, geo, ct);
            }
            return Results.Accepted();
        }).RequireCors(CollectCorsPolicy).AuditExempt(NotAnAction).PermissionExempt(Anonymous);
    }

    /// <summary>The slug-less beacon and the status probe, for sites that do not know the slug.</summary>
    public static void MapHostRoutes(IEndpointRouteBuilder app)
    {
        // Fired by the published-site runtime (hydrate.js), which doesn't know the analytics
        // instance slug. Returns 202 even when analytics is disabled so the beacon never logs a
        // client-side error.
        app.MapPost("/api/collect", async (
            CollectRequest body, HttpContext http, ITenantContext tenant, ISandboxContext sandbox,
            CmsDbContext cms, IEventPublisher events, IGeoIpResolver geo, CancellationToken ct) =>
        {
            if (tenant.TenantId is not { } tenantId)
            {
                return Results.NotFound();
            }
            if (await EnabledAsync(cms, ct) && !sandbox.IsSandbox)
            {
                await PublishAsync(events, tenantId, body, http, geo, ct);
            }
            return Results.Accepted();
        }).RequireCors(CollectCorsPolicy).AuditExempt(NotAnAction).PermissionExempt(Anonymous);

        // Whether this tenant records anything, for a site that has to decide whether to *ask*.
        //
        // A prerendered Mode A page is told at publish time (the assembler stamps it into the
        // document), but a Mode B React app is built once and served from static files — it has
        // no publish-time hook and no way to know. Left guessing it would show a cookie banner on
        // a tenant that stores nothing, which is the exact theatre the consent work set out to
        // avoid. Cached briefly: it changes only when a plugin is toggled.
        app.MapGet("/api/analytics/status", async (
            ITenantContext tenant, CmsDbContext cms, ICacheService cache, CancellationToken ct) =>
        {
            if (tenant.TenantId is not { } tenantId)
            {
                return Results.NotFound();
            }
            var key = $"t:{tenantId}:analytics:enabled";
            var cached = await cache.GetAsync<bool?>(key, ct);
            if (cached is not { } enabled)
            {
                enabled = await EnabledAsync(cms, ct);
                await cache.SetAsync(key, (bool?)enabled, TimeSpan.FromMinutes(5), ct);
            }
            return Results.Ok(new { enabled });
        }).RequireCors(CollectCorsPolicy).AuditExempt(NotAnAction).PermissionExempt(Anonymous);
    }

    private static Task<bool> EnabledAsync(CmsDbContext cms, CancellationToken ct) =>
        cms.PluginInstances.AsNoTracking().AnyAsync(p => p.PluginId == AnalyticsPlugin.PluginId && p.Enabled, ct);

    private static ValueTask PublishAsync(
        IEventPublisher events, Guid tenantId, CollectRequest body, HttpContext http, IGeoIpResolver geo, CancellationToken ct)
    {
        // Device/browser/OS and country are derived from the request, not read from the
        // payload: a page can claim to be anything, and it cannot see its own IP.
        var facts = UserAgentFacts.Parse(http.Request.Headers.UserAgent.ToString(), geo.ResolveCountry(http));
        return Publish(events, tenantId, body.Type, body.Path, body.Referrer, body.SessionId, body.Props, facts, ct);
    }

    /// <summary>Queues one event; shared by the beacon and the <c>analytics.tracking@1</c> contract.</summary>
    public static ValueTask Publish(
        IEventPublisher events, Guid tenantId, string? type, string? path, string? referrer, string? sessionId,
        JsonElement? props, RequestFacts facts, CancellationToken ct)
    {
        var utm = UtmFacts.FromPath(path);
        var evt = new AnalyticsEvent(
            DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(type) ? "pageview" : type,
            path ?? "/",
            referrer,
            sessionId,
            Hash(sessionId),
            props,
            facts.Device,
            facts.Browser,
            facts.Os,
            facts.Country,
            utm.Source,
            utm.Medium,
            utm.Campaign);

        return events.PublishAsync(Subjects.AnalyticsEvents, new AnalyticsEventBatch(
            Guid.NewGuid(), DateTimeOffset.UtcNow, tenantId, [evt]), ct);
    }

    private static string? Hash(string? sessionId)
        => string.IsNullOrEmpty(sessionId)
            ? null
            : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(sessionId)))[..16];

    private sealed record CollectRequest(string? Type, string? Path, string? Referrer, string? SessionId, JsonElement? Props);
}
