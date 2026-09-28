using System.Security.Claims;
using System.Text.Json;
using Dcms.Plugins.VisitorAuth.Contracts;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Visitors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dcms.Plugins.VisitorAuth;

/// <summary>
/// Website visitor authentication and profiles, mounted by the plugin runtime under
/// <c>/api/{slug}</c> for an enabled VisitorAuth instance only. register/login/refresh issue
/// per-tenant-audience JWTs; a separate identity pool per tenant, isolated from platform users
/// and from other tenants.
/// </summary>
internal static class VisitorAuthEndpoints
{
    private static readonly PasswordHasher<VisitorAccount> Hasher = new();

    public static void Map(IPluginEndpointBuilder endpoints)
    {
        endpoints.MapPost("/register", async (
            AuthRequest body, IPluginContext context, VisitorsDbContext db, VisitorTokenService tokens,
            ILogger<VisitorTokenService> logger, CancellationToken ct) =>
        {
            var email = body.Email?.Trim().ToLowerInvariant() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(body.Password))
            {
                return Results.BadRequest(new { error = "Email and password are required." });
            }
            if (await db.Accounts.AnyAsync(a => a.Email == email, ct))
            {
                return Results.Conflict(new { error = "An account with that email already exists." });
            }

            var account = new VisitorAccount { Id = Guid.NewGuid(), TenantId = context.TenantId, Email = email, DisplayName = body.DisplayName };
            account.PasswordHash = Hasher.HashPassword(account, body.Password);
            db.Accounts.Add(account);
            await db.SaveChangesAsync(ct);

            try
            {
                await context.PublishAsync(new VisitorRegistered(account.Id), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The account exists; a bus hiccup must not turn a successful sign-up into an error.
                logger.LogWarning(ex, "Could not publish visitor.registered for {VisitorId}.", account.Id);
            }

            return Results.Ok(await IssueAsync(db, tokens, context.TenantId, account, ct));
        }).WithAudit(AuditActions.VisitorRegistered, "visitor", AuditCategory.Auth);

        endpoints.MapPost("/login", async (
            AuthRequest body, IPluginContext context, VisitorsDbContext db, VisitorTokenService tokens, CancellationToken ct) =>
        {
            var email = body.Email?.Trim().ToLowerInvariant() ?? string.Empty;
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.Email == email, ct);
            if (account is null ||
                Hasher.VerifyHashedPassword(account, account.PasswordHash, body.Password ?? string.Empty) == PasswordVerificationResult.Failed)
            {
                return Results.Unauthorized();
            }
            return Results.Ok(await IssueAsync(db, tokens, context.TenantId, account, ct));
        }).WithAudit(AuditActions.VisitorLoggedIn, "visitor", AuditCategory.Auth);

        endpoints.MapPost("/refresh", async (
            RefreshRequest body, IPluginContext context, VisitorsDbContext db, VisitorTokenService tokens, CancellationToken ct) =>
        {
            var hash = VisitorTokenService.HashToken(body.RefreshToken ?? string.Empty);
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
            return Results.Ok(await IssueAsync(db, tokens, context.TenantId, account, ct));
        }).AuditExempt("Token refresh. High volume, low signal — the sign-in it renews is already recorded, and the visitor plane refreshes every 15 minutes.");

        endpoints.MapGet("/me", async (ClaimsPrincipal user, VisitorsDbContext db, CancellationToken ct) =>
        {
            var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == user.VisitorId(), ct);
            return account is null
                ? Results.Unauthorized()
                : Results.Ok(new { id = account.Id, email = account.Email, displayName = account.DisplayName });
        }).RequireVisitor();

        // The visitor's own profile: every attribute, private ones included, plus the
        // definitions so a site can render the form without a second request.
        endpoints.MapGet("/me/profile", async (ClaimsPrincipal user, IPluginContext context, VisitorsDbContext db, CancellationToken ct) =>
        {
            var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == user.VisitorId(), ct);
            return account is null ? Results.Unauthorized() : Results.Ok(OwnProfile(account, context));
        }).RequireVisitor();

        endpoints.MapPut("/me/profile", async (
            ProfileUpdate body, ClaimsPrincipal user, IPluginContext context, VisitorsDbContext db,
            ILogger<VisitorTokenService> logger, CancellationToken ct) =>
        {
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == user.VisitorId(), ct);
            if (account is null)
            {
                return Results.Unauthorized();
            }
            var definitions = VisitorProfiles.Definitions(context);
            Dictionary<string, JsonElement> next;
            List<string> changed;
            try
            {
                next = VisitorAttributes.Apply(
                    VisitorAttributes.Parse(account.AttributesJson), body.Attributes ?? new Dictionary<string, JsonElement>(),
                    definitions, d => d.VisitorEditable, out changed);
            }
            catch (ContractValidationException e)
            {
                return Results.BadRequest(new { error = e.Message });
            }
            if (body.DisplayName is { } displayName)
            {
                var trimmed = displayName.Trim();
                if (trimmed.Length > 256)
                {
                    return Results.BadRequest(new { error = "Display name is at most 256 characters." });
                }
                if (trimmed != account.DisplayName)
                {
                    account.DisplayName = trimmed.Length == 0 ? null : trimmed;
                    changed.Add("displayName");
                }
            }
            if (changed.Count > 0)
            {
                account.AttributesJson = VisitorAttributes.Serialize(next);
                await db.SaveChangesAsync(ct);
                await VisitorProfiles.PublishUpdatedAsync(context, account.Id, changed, logger, ct);
            }
            return Results.Ok(OwnProfile(account, context));
        }).RequireVisitor().WithAudit(AuditActions.VisitorProfileUpdated, "visitor", AuditCategory.TenantState);
    }

    private static object OwnProfile(VisitorAccount account, IPluginContext context) => new
    {
        id = account.Id,
        email = account.Email,
        displayName = account.DisplayName,
        attributes = VisitorAttributes.Parse(account.AttributesJson),
        definitions = VisitorProfiles.Definitions(context),
    };

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

    private sealed record AuthRequest(string? Email, string? Password, string? DisplayName);
    private sealed record RefreshRequest(string? RefreshToken);
    private sealed record ProfileUpdate(string? DisplayName, Dictionary<string, JsonElement>? Attributes);
}
