using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Edge;
using Dcms.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Tenancy;

/// <summary>
/// Client addresses and ranges the edge does not rate-limit, managed from the platform console.
///
/// <para>Here rather than in platform-api for the reason <see cref="ManagedCertificateEndpoints"/>
/// gives: admin-api owns and migrates the <c>edge</c> schema, platform-api holds no grant on it.
/// The console reaches these through platform-api, which checks
/// <c>platform:ratelimits:manage</c> first.</para>
///
/// <para>A save takes effect without a restart: the edge reloads the list the moment
/// <see cref="Subjects.RateLimitExemptionsChanged"/> arrives, and re-reads it every few minutes
/// in case a message is lost.</para>
/// </summary>
public static class RateLimitExemptionEndpoints
{
    public sealed record Request(string Address, string Note);

    public static IEndpointRouteBuilder MapRateLimitExemptionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/platform/rate-limit-exemptions", async (
            EdgeDbContext edge, ConsoleCaller console, CancellationToken ct) =>
        {
            if (!console.Allowed)
            {
                return Results.Forbid();
            }

            return Results.Ok(await edge.RateLimitExemptions.AsNoTracking()
                .OrderBy(x => x.Cidr)
                .Select(x => new { id = x.Id, cidr = x.Cidr, note = x.Note, createdAt = x.CreatedAt, createdBy = x.CreatedBy })
                .ToListAsync(ct));
        }).RequireAuthorization()
          .AllowConsoleService(
              "the platform console reads this page; its API holds dcms.console and has already "
              + "checked the operator holds platform:ratelimits:manage.");

        app.MapPost("/api/admin/platform/rate-limit-exemptions", async (
            Request body, EdgeDbContext edge, IEventPublisher events, ConsoleCaller console,
            AuditScope audit, CancellationToken ct) =>
        {
            if (!console.Allowed)
            {
                return Results.Forbid();
            }

            if (!RateLimitExemptionRules.TryNormalize(body.Address, out var cidr, out var error))
            {
                return Results.BadRequest(new { error });
            }

            var note = body.Note?.Trim() ?? string.Empty;
            if (note.Length == 0)
            {
                return Results.BadRequest(new { error = "Say why this address is exempt -- a load generator, a probe, a partner." });
            }
            if (note.Length > 500)
            {
                return Results.BadRequest(new { error = "Keep the note under 500 characters." });
            }

            if (await edge.RateLimitExemptions.AnyAsync(x => x.Cidr == cidr, ct))
            {
                return Results.Conflict(new { error = $"{cidr} is already exempt." });
            }

            var actor = audit.ResolveActor();
            var row = new EdgeRateLimitExemption
            {
                Cidr = cidr,
                Note = note,
                CreatedBy = actor.Display ?? actor.Id?.ToString(),
            };
            edge.RateLimitExemptions.Add(row);
            await edge.SaveChangesAsync(ct);
            await PublishAsync(events, ct);

            return Results.Created(
                $"/api/admin/platform/rate-limit-exemptions/{row.Id}",
                new { id = row.Id, cidr = row.Cidr, note = row.Note, createdAt = row.CreatedAt, createdBy = row.CreatedBy });
        }).RequireAuthorization()
          .WithAudit(AuditActions.RateLimitExemptionAdded, "rate-limit-exemption")
          .AllowConsoleService(
              "the platform console owns this button; its API holds dcms.console and has already "
              + "checked the operator holds platform:ratelimits:manage.");

        app.MapDelete("/api/admin/platform/rate-limit-exemptions/{id:guid}", async (
            Guid id, EdgeDbContext edge, IEventPublisher events, ConsoleCaller console, CancellationToken ct) =>
        {
            if (!console.Allowed)
            {
                return Results.Forbid();
            }

            if (await edge.RateLimitExemptions.FirstOrDefaultAsync(x => x.Id == id, ct) is not { } row)
            {
                return Results.NotFound();
            }

            edge.RateLimitExemptions.Remove(row);
            await edge.SaveChangesAsync(ct);
            await PublishAsync(events, ct);
            return Results.NoContent();
        }).RequireAuthorization()
          .WithAudit(AuditActions.RateLimitExemptionRemoved, "rate-limit-exemption")
          .AllowConsoleService(
              "the platform console owns this button; its API holds dcms.console and has already "
              + "checked the operator holds platform:ratelimits:manage.");

        return app;
    }

    // After the save, never instead of it: the row is what the edge reads, and the event only
    // says "read it now". A lost event costs the edge's five-minute sweep, not the change.
    private static ValueTask PublishAsync(IEventPublisher events, CancellationToken ct) =>
        events.PublishAsync(
            Subjects.RateLimitExemptionsChanged,
            new RateLimitExemptionsChanged(Guid.NewGuid(), DateTimeOffset.UtcNow),
            ct);
}
