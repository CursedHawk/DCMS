using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using System.Text.Json;
using Dcms.Shared.Caching;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Dcms.ContentApi.Delivery;

/// <summary>
/// Anonymous analytics beacon: POST /api/{slug}/collect from the tenant website.
/// Resolves the Analytics plugin instance and fire-and-forgets the event to NATS;
/// the admin-api consumer persists it and updates rollups.
/// </summary>
public static class AnalyticsIngestEndpoints
{
    private const string AnalyticsPluginId = "analytics";

    /// <summary>
    /// CORS policy for the anonymous collect beacon: any origin may POST events
    /// (no credentials), so externally hosted sites can use the documented API.
    /// Registered in content-api's Program.cs.
    /// </summary>
    public const string CollectCorsPolicy = "analytics-collect";

    public static IEndpointRouteBuilder MapAnalyticsIngest(this IEndpointRouteBuilder app)
    {
        // Slug-addressed beacon (explicit analytics instance).
        app.MapPost("/api/{slug}/collect", async (
            string slug, CollectRequest body, HttpContext http, ITenantContext tenant, ISandboxContext sandbox,
            CmsDbContext cms, IEventPublisher events, IGeoIpResolver geo, CancellationToken ct) =>
        {
            if (tenant.TenantId is not { } tenantId)
            {
                return Results.NotFound();
            }
            var instance = await cms.PluginInstances.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Slug == slug && p.PluginId == AnalyticsPluginId && p.Enabled, ct);
            if (instance is null)
            {
                return Results.NotFound();
            }

            // Preview sandbox: swallow the beacon so real analytics stay clean.
            if (!sandbox.IsSandbox)
            {
                await PublishAsync(events, tenantId, body, http, geo, ct);
            }
            return Results.Accepted();
        }).RequireCors(CollectCorsPolicy).AuditExempt("Visitor telemetry, not an action on tenant state. Recorded in analytics.events, which has its own retention and is tenant-purgeable — audit is neither.");

        // Slug-less beacon fired by the published-site runtime (hydrate.js). The
        // runtime doesn't know the analytics instance slug, so we resolve the
        // tenant's single enabled analytics instance here. Returns 202 even when
        // analytics is disabled so the beacon never logs a client-side error.
        app.MapPost("/api/collect", async (
            CollectRequest body, HttpContext http, ITenantContext tenant, ISandboxContext sandbox,
            CmsDbContext cms, IEventPublisher events, IGeoIpResolver geo, CancellationToken ct) =>
        {
            if (tenant.TenantId is not { } tenantId)
            {
                return Results.NotFound();
            }
            var enabled = await cms.PluginInstances.AsNoTracking()
                .AnyAsync(p => p.PluginId == AnalyticsPluginId && p.Enabled, ct);
            if (enabled && !sandbox.IsSandbox)
            {
                await PublishAsync(events, tenantId, body, http, geo, ct);
            }
            return Results.Accepted();
        }).RequireCors(CollectCorsPolicy).AuditExempt("Visitor telemetry, not an action on tenant state. Recorded in analytics.events, which has its own retention and is tenant-purgeable — audit is neither.");

        // Whether this tenant records anything, for a site that has to decide
        // whether to *ask*.
        //
        // A prerendered Mode A page is told at publish time (the assembler stamps
        // it into the document), but a Mode B React app is built once and served
        // from static files — it has no publish-time hook and no way to know. Left
        // guessing it would show a cookie banner on a tenant that stores nothing,
        // which is the exact theatre the consent work set out to avoid.
        //
        // The beacon itself does not depend on this — it already no-ops server-side
        // when analytics is off — so this exists purely so the banner can be
        // suppressed. Cached briefly: it changes only when a plugin is toggled.
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
                enabled = await cms.PluginInstances.AsNoTracking()
                    .AnyAsync(p => p.PluginId == AnalyticsPluginId && p.Enabled, ct);
                await cache.SetAsync(key, (bool?)enabled, TimeSpan.FromMinutes(5), ct);
            }
            return Results.Ok(new { enabled });
        }).RequireCors(CollectCorsPolicy).AuditExempt("Visitor telemetry, not an action on tenant state. Recorded in analytics.events, which has its own retention and is tenant-purgeable — audit is neither.");

        return app;
    }

    private static ValueTask PublishAsync(
        IEventPublisher events, Guid tenantId, CollectRequest body,
        HttpContext http, IGeoIpResolver geo, CancellationToken ct)
    {
        // Device/browser/OS and country are derived from the request, not read from
        // the payload: a page can claim to be anything, and it cannot see its own IP.
        var facts = UserAgentFacts.Parse(http.Request.Headers.UserAgent.ToString(), geo.ResolveCountry(http));
        var utm = UtmFacts.FromPath(body.Path);

        var evt = new AnalyticsEvent(
            DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(body.Type) ? "pageview" : body.Type,
            body.Path ?? "/",
            body.Referrer,
            body.SessionId,
            Hash(body.SessionId),
            body.Props,
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
