using System.Text.Json;
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

    public static IEndpointRouteBuilder MapAnalyticsIngest(this IEndpointRouteBuilder app)
    {
        // Slug-addressed beacon (explicit analytics instance).
        app.MapPost("/api/{slug}/collect", async (
            string slug, CollectRequest body, ITenantContext tenant, CmsDbContext cms,
            IEventPublisher events, CancellationToken ct) =>
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

            await PublishAsync(events, tenantId, body, ct);
            return Results.Accepted();
        });

        // Slug-less beacon fired by the published-site runtime (hydrate.js). The
        // runtime doesn't know the analytics instance slug, so we resolve the
        // tenant's single enabled analytics instance here. Returns 202 even when
        // analytics is disabled so the beacon never logs a client-side error.
        app.MapPost("/api/collect", async (
            CollectRequest body, ITenantContext tenant, CmsDbContext cms,
            IEventPublisher events, CancellationToken ct) =>
        {
            if (tenant.TenantId is not { } tenantId)
            {
                return Results.NotFound();
            }
            var enabled = await cms.PluginInstances.AsNoTracking()
                .AnyAsync(p => p.PluginId == AnalyticsPluginId && p.Enabled, ct);
            if (enabled)
            {
                await PublishAsync(events, tenantId, body, ct);
            }
            return Results.Accepted();
        });

        return app;
    }

    private static ValueTask PublishAsync(IEventPublisher events, Guid tenantId, CollectRequest body, CancellationToken ct)
    {
        var evt = new AnalyticsEvent(
            DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(body.Type) ? "pageview" : body.Type,
            body.Path ?? "/",
            body.Referrer,
            body.SessionId,
            Hash(body.SessionId),
            body.Props);

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
