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

            var evt = new AnalyticsEvent(
                DateTimeOffset.UtcNow,
                string.IsNullOrWhiteSpace(body.Type) ? "pageview" : body.Type,
                body.Path ?? "/",
                body.Referrer,
                body.SessionId,
                Hash(body.SessionId),
                body.Props);

            await events.PublishAsync(Subjects.AnalyticsEvents, new AnalyticsEventBatch(
                Guid.NewGuid(), DateTimeOffset.UtcNow, tenantId, [evt]), ct);

            return Results.Accepted();
        });

        return app;
    }

    private static string? Hash(string? sessionId)
        => string.IsNullOrEmpty(sessionId)
            ? null
            : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(sessionId)))[..16];

    private sealed record CollectRequest(string? Type, string? Path, string? Referrer, string? SessionId, JsonElement? Props);
}
