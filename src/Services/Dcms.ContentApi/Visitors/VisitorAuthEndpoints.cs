using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Visitors;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Dcms.ContentApi.Visitors;

/// <summary>
/// Website visitor authentication (the VisitorAuth plugin). register/login/refresh
/// issue per-tenant-audience JWTs; /me validates one. Separate identity pool per
/// tenant, isolated from platform users and from other tenants.
/// </summary>
public static class VisitorAuthEndpoints
{
    private const string PluginId = "visitor-auth";
    private static readonly PasswordHasher<VisitorAccount> Hasher = new();

    public static IEndpointRouteBuilder MapVisitorAuth(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/{slug}/register", async (
            string slug, AuthRequest body, ITenantContext tenant, CmsDbContext cms,
            VisitorsDbContext db, VisitorTokenService tokens, CancellationToken ct) =>
        {
            if (!await IsEnabled(cms, slug, ct) || tenant.TenantId is not { } tenantId)
            {
                return Results.NotFound();
            }
            var email = body.Email.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(body.Password))
            {
                return Results.BadRequest(new { error = "Email and password are required." });
            }
            if (await db.Accounts.AnyAsync(a => a.Email == email, ct))
            {
                return Results.Conflict(new { error = "An account with that email already exists." });
            }

            var account = new VisitorAccount { Id = Guid.NewGuid(), TenantId = tenantId, Email = email, DisplayName = body.DisplayName };
            account.PasswordHash = Hasher.HashPassword(account, body.Password);
            db.Accounts.Add(account);
            await db.SaveChangesAsync(ct);

            return Results.Ok(await IssueAsync(db, tokens, tenantId, account, ct));
        }).WithAudit(AuditActions.VisitorRegistered, "visitor", AuditCategory.Auth);

        app.MapPost("/api/{slug}/login", async (
            string slug, AuthRequest body, ITenantContext tenant, CmsDbContext cms,
            VisitorsDbContext db, VisitorTokenService tokens, CancellationToken ct) =>
        {
            if (!await IsEnabled(cms, slug, ct) || tenant.TenantId is not { } tenantId)
            {
                return Results.NotFound();
            }
            var email = body.Email.Trim().ToLowerInvariant();
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.Email == email, ct);
            if (account is null ||
                Hasher.VerifyHashedPassword(account, account.PasswordHash, body.Password) == PasswordVerificationResult.Failed)
            {
                return Results.Unauthorized();
            }
            return Results.Ok(await IssueAsync(db, tokens, tenantId, account, ct));
        }).WithAudit(AuditActions.VisitorLoggedIn, "visitor", AuditCategory.Auth);

        app.MapPost("/api/{slug}/refresh", async (
            string slug, RefreshRequest body, ITenantContext tenant, CmsDbContext cms,
            VisitorsDbContext db, VisitorTokenService tokens, CancellationToken ct) =>
        {
            if (!await IsEnabled(cms, slug, ct) || tenant.TenantId is not { } tenantId)
            {
                return Results.NotFound();
            }
            var hash = VisitorTokenService.HashToken(body.RefreshToken);
            var stored = await db.RefreshTokens
                .FirstOrDefaultAsync(t => t.TokenHash == hash && t.RevokedAt == null && t.ExpiresAt > DateTimeOffset.UtcNow, ct);
            if (stored is null)
            {
                return Results.Unauthorized();
            }
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == stored.VisitorId, ct);
            if (account is null)
            {
                return Results.Unauthorized();
            }
            stored.RevokedAt = DateTimeOffset.UtcNow; // rotate
            var result = await IssueAsync(db, tokens, tenantId, account, ct);
            return Results.Ok(result);
        }).AuditExempt("Token refresh. High volume, low signal — the sign-in it renews is already recorded, and the visitor plane refreshes every 15 minutes.");

        app.MapGet("/api/{slug}/me", async (
            string slug, HttpContext http, ITenantContext tenant, CmsDbContext cms,
            VisitorsDbContext db, VisitorTokenService tokens, CancellationToken ct) =>
        {
            if (!await IsEnabled(cms, slug, ct) || tenant.TenantId is not { } tenantId)
            {
                return Results.NotFound();
            }
            var visitorId = await AuthenticateAsync(http, tokens, tenantId);
            if (visitorId is null)
            {
                return Results.Unauthorized();
            }
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == visitorId, ct);
            return account is null
                ? Results.Unauthorized()
                : Results.Ok(new { id = account.Id, email = account.Email, displayName = account.DisplayName });
        });

        return app;
    }

    /// <summary>Validates a visitor bearer token for the current tenant; null if absent/invalid.</summary>
    public static async Task<Guid?> AuthenticateAsync(HttpContext http, VisitorTokenService tokens, Guid tenantId)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return await tokens.ValidateAccessTokenAsync(header["Bearer ".Length..].Trim(), tenantId);
    }

    private static async Task<bool> IsEnabled(CmsDbContext cms, string slug, CancellationToken ct)
        => await cms.PluginInstances.AsNoTracking()
            .AnyAsync(p => p.Slug == slug && p.PluginId == PluginId && p.Enabled, ct);

    private static async Task<object> IssueAsync(
        VisitorsDbContext db, VisitorTokenService tokens, Guid tenantId, VisitorAccount account, CancellationToken ct)
    {
        var access = tokens.IssueAccessToken(tenantId, account.Id, account.Email);
        var refresh = tokens.IssueRefreshToken();
        db.RefreshTokens.Add(new VisitorRefreshToken
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            VisitorId = account.Id,
            TokenHash = refresh.TokenHash,
            ExpiresAt = refresh.ExpiresAt,
        });
        await db.SaveChangesAsync(ct);
        return new { accessToken = access, refreshToken = refresh.Token };
    }

    private sealed record AuthRequest(string Email, string Password, string? DisplayName);
    private sealed record RefreshRequest(string RefreshToken);
}
