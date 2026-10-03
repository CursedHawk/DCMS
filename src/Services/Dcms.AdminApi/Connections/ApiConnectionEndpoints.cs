using System.Text;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Security;
using Dcms.Shared.Vault;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Connections;

/// <summary>
/// The console's management of external API connections (Mode D backlog #124). The credential is
/// write-only: it is encrypted on the way in and no response ever carries it, or its ciphertext.
/// </summary>
public static class ApiConnectionEndpoints
{
    public sealed record SaveConnection(
        string Name,
        string BaseUrl,
        string AuthKind,
        string? AuthName,
        /// <summary>Null keeps the stored credential; a value replaces it. Ignored for authKind none.</summary>
        string? Secret,
        List<string> Operations,
        int RefreshMinutes = 60);

    public static IEndpointRouteBuilder MapApiConnectionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/connections", async (CmsDbContext db, CancellationToken ct) =>
        {
            var rows = await db.ApiConnections.OrderBy(c => c.Slug).ToListAsync(ct);
            // The shapes the builder offers, without the bodies.
            var shapes = await db.ApiSnapshots
                .Select(s => new { s.ConnectionId, s.Operation, s.ItemsPath, s.Fields, s.FetchedAt })
                .ToListAsync(ct);
            return Results.Ok(rows.Select(c => View(c, shapes
                .Where(s => s.ConnectionId == c.Id)
                .Select(s => new OperationShape(s.Operation, s.ItemsPath, s.Fields, s.FetchedAt)))));
        }).RequirePermission(PlatformPermissions.PluginsManage);

        app.MapPut("/api/admin/connections/{slug}", async (
            string slug, SaveConnection body, CmsDbContext db, ITransitEncryptor transit, ApiConnectionFetcher fetcher,
            IConfiguration config, CancellationToken ct) =>
        {
            var allowLocal = config.GetValue("Connections:AllowLocal", false);
            var operations = (body.Operations ?? []).Select(o => o.Trim()).Where(o => o.Length > 0).ToList();
            var authName = body.AuthKind is "header" or "query" ? body.AuthName?.Trim() : null;
            if (ApiConnectionRules.Problem(slug, body.Name?.Trim() ?? "", body.BaseUrl?.Trim() ?? "", body.AuthKind ?? "", authName,
                    operations, body.RefreshMinutes, allowLocal) is { } problem)
            {
                return Results.BadRequest(new { error = problem });
            }

            var row = await db.ApiConnections.FirstOrDefaultAsync(c => c.Slug == slug, ct);
            if (row is null)
            {
                row = new ApiConnection { Id = Guid.CreateVersion7(), Slug = slug };
                db.ApiConnections.Add(row);
            }
            row.Name = body.Name!.Trim();
            row.BaseUrl = body.BaseUrl!.Trim().TrimEnd('/');
            row.AuthKind = body.AuthKind!;
            row.AuthName = authName;
            row.Operations = operations;
            row.RefreshMinutes = body.RefreshMinutes;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            if (row.AuthKind == "none")
            {
                row.SecretCiphertext = null;
            }
            else if (!string.IsNullOrEmpty(body.Secret))
            {
                if (Encoding.UTF8.GetByteCount(body.Secret) > 4096)
                {
                    return Results.BadRequest(new { error = "the key is longer than 4 KB." });
                }
                row.SecretCiphertext = await transit.EncryptAsync(ApiConnectionRules.TransitKey, Encoding.UTF8.GetBytes(body.Secret), ct);
            }
            if (row.AuthKind != "none" && row.SecretCiphertext is null)
            {
                return Results.BadRequest(new { error = "this auth kind needs a key." });
            }
            await db.SaveChangesAsync(ct);

            // Fetched now, so the author sees at once whether the settings work.
            await fetcher.RefreshAsync(row, ct);
            return Results.Ok(View(row));
        }).RequirePermission(PlatformPermissions.PluginsManage).WithAudit(AuditActions.ApiConnectionSaved, "api_connection");

        app.MapPost("/api/admin/connections/{slug}/refresh", async (
            string slug, CmsDbContext db, ApiConnectionFetcher fetcher, CancellationToken ct) =>
        {
            var row = await db.ApiConnections.FirstOrDefaultAsync(c => c.Slug == slug, ct);
            if (row is null)
            {
                return Results.NotFound();
            }
            await fetcher.RefreshAsync(row, ct);
            return Results.Ok(View(row));
        }).RequirePermission(PlatformPermissions.PluginsManage).WithAudit(AuditActions.ApiConnectionRefreshed, "api_connection");

        app.MapDelete("/api/admin/connections/{slug}", async (string slug, CmsDbContext db, CancellationToken ct) =>
        {
            var row = await db.ApiConnections.FirstOrDefaultAsync(c => c.Slug == slug, ct);
            if (row is null)
            {
                return Results.NotFound();
            }
            db.ApiConnections.Remove(row);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.PluginsManage).WithAudit(AuditActions.ApiConnectionDeleted, "api_connection");

        return app;
    }

    public sealed record OperationShape(string Operation, string? Items, List<string> Fields, DateTimeOffset FetchedAt);

    /// <summary>What the console sees: everything but the credential.</summary>
    private static object View(ApiConnection c, IEnumerable<OperationShape>? shapes = null) => new
    {
        slug = c.Slug,
        name = c.Name,
        baseUrl = c.BaseUrl,
        authKind = c.AuthKind,
        authName = c.AuthName,
        hasSecret = c.SecretCiphertext is not null,
        operations = c.Operations,
        refreshMinutes = c.RefreshMinutes,
        refreshedAt = c.RefreshedAt,
        lastError = c.LastError,
        shapes = shapes?.ToList(),
    };
}
