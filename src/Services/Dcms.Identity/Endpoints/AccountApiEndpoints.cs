using System.Security.Claims;
using Dcms.Identity.Data;
using Dcms.Identity.Domain;
using Dcms.Identity.Forgejo;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;

namespace Dcms.Identity.Endpoints;

/// <summary>
/// Bearer-authenticated JSON API for the admin SPA's account-settings page
/// (same-origin under /account/api/*): read the profile + git status, set/change
/// the git password (kept in sync with Forgejo), and manage SSH keys on the user's
/// Forgejo account. These are the self-service controls that let every user — including
/// Google-only accounts with no password — establish git credentials.
/// </summary>
public static class AccountApiEndpoints
{
    /// <summary>Authorization policy name requiring an OpenIddict-validated access token.</summary>
    public const string PolicyName = "AccountApi";

    public static IEndpointRouteBuilder MapAccountApiEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/account/api").RequireAuthorization(PolicyName);

        // Profile + git status for the account page.
        group.MapGet("/me", async (ClaimsPrincipal principal, UserManager<DcmsUser> users) =>
        {
            var user = await FindUserAsync(principal, users);
            if (user is null) return Results.Unauthorized();
            return Results.Ok(new
            {
                email = user.Email,
                displayName = user.DisplayName,
                forgejoUsername = user.ForgejoUsername,
                hasGitPassword = await users.HasPasswordAsync(user),
            });
        });

        // Set (Google-only accounts) or change the git password. It is the DCMS
        // password, which propagates to Forgejo so it works for git immediately.
        group.MapPost("/password", async (
            ClaimsPrincipal principal, UserManager<DcmsUser> users, ForgejoUserSync forgejo,
            SetPasswordRequest body, CancellationToken ct) =>
        {
            var user = await FindUserAsync(principal, users);
            if (user is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(body.NewPassword) || body.NewPassword.Length < 10)
                return Results.BadRequest(new { error = "Password must be at least 10 characters." });

            IdentityResult result;
            if (await users.HasPasswordAsync(user))
            {
                if (string.IsNullOrEmpty(body.CurrentPassword))
                    return Results.BadRequest(new { error = "Enter your current password." });
                result = await users.ChangePasswordAsync(user, body.CurrentPassword, body.NewPassword);
            }
            else
            {
                result = await users.AddPasswordAsync(user, body.NewPassword);
            }
            if (!result.Succeeded)
                return Results.BadRequest(new { error = result.Errors.FirstOrDefault()?.Description ?? "Could not update password." });

            // Propagate to Forgejo (also flips HasGitPassword).
            await forgejo.EnsureAsync(user, body.NewPassword, ct);
            return Results.NoContent();
        });

        // ---- SSH keys (on the user's Forgejo account) ----

        group.MapGet("/ssh-keys", async (
            ClaimsPrincipal principal, UserManager<DcmsUser> users,
            ForgejoUserSync forgejo, ForgejoAdminClient admin, CancellationToken ct) =>
        {
            var user = await FindUserAsync(principal, users);
            if (user is null) return Results.Unauthorized();
            var username = await forgejo.EnsureAccountAsync(user, ct);
            if (username is null) return Results.Ok(Array.Empty<object>());
            var keys = await admin.ListPublicKeysAsync(username, ct);
            return Results.Ok(keys.Select(k => new { id = k.Id, title = k.Title, fingerprint = k.Fingerprint, createdAt = k.CreatedAt }));
        });

        group.MapPost("/ssh-keys", async (
            ClaimsPrincipal principal, UserManager<DcmsUser> users,
            ForgejoUserSync forgejo, ForgejoAdminClient admin, AddKeyRequest body, CancellationToken ct) =>
        {
            var user = await FindUserAsync(principal, users);
            if (user is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(body.Key))
                return Results.BadRequest(new { error = "Paste an SSH public key." });

            var username = await forgejo.EnsureAccountAsync(user, ct);
            if (username is null) return Results.Problem("Git server is unavailable; try again shortly.", statusCode: 503);
            try
            {
                var title = string.IsNullOrWhiteSpace(body.Title) ? "key" : body.Title.Trim();
                var key = await admin.AddPublicKeyAsync(username, title, body.Key.Trim(), ct);
                return Results.Ok(new { id = key.Id, title = key.Title, fingerprint = key.Fingerprint, createdAt = key.CreatedAt });
            }
            catch (ForgejoApiException)
            {
                return Results.BadRequest(new { error = "That key is invalid or already registered." });
            }
        });

        // ---- Account deletion ----
        //
        // Split across two services deliberately (see Dcms.AdminApi's
        // MyAccountEndpoints). admin-api owns the tenancy rules and detaches the user
        // from every workspace; identity owns the login and refuses to destroy it
        // while any membership remains. So the irreversible half cannot run before the
        // safe half, and neither service writes the other's tables — identity only
        // *counts* memberships.
        group.MapDelete("/me", async (
            ClaimsPrincipal principal, UserManager<DcmsUser> users, IdentityDbContext db,
            ForgejoAdminClient admin, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var user = await FindUserAsync(principal, users);
            if (user is null) return Results.Unauthorized();

            var userId = user.Id;
            var memberships = await CountMembershipsAsync(db, userId, ct);
            if (memberships > 0)
            {
                return Results.Conflict(new
                {
                    error = "Leave your workspaces before deleting your account.",
                    memberships,
                });
            }

            var logger = loggerFactory.CreateLogger("AccountDeletion");

            // Forgejo first: once the identity row is gone we no longer know which
            // mirrored account belonged to this user, and it would be orphaned for good.
            if (user.ForgejoUsername is { Length: > 0 } username)
            {
                try
                {
                    await admin.DeleteUserAsync(username, ct);
                }
                catch (Exception ex)
                {
                    // Not fatal: an orphaned git account is tidy-up, whereas refusing
                    // to delete the login because a side system is down is not a
                    // defensible answer to "delete my account".
                    logger.LogError(ex, "Failed deleting Forgejo account {Username}; it is now orphaned.", username);
                }
            }

            // Queued credential syncs would otherwise keep re-creating the account.
            await db.ForgejoSyncOutbox.Where(o => o.UserId == userId).ExecuteDeleteAsync(ct);

            var result = await users.DeleteAsync(user);
            if (!result.Succeeded)
            {
                return Results.Problem(
                    result.Errors.FirstOrDefault()?.Description ?? "Could not delete the account.",
                    statusCode: 500);
            }

            logger.LogWarning("Account {UserId} deleted at the user's request.", userId);
            return Results.NoContent();
        });

        group.MapDelete("/ssh-keys/{id:long}", async (
            long id, ClaimsPrincipal principal, UserManager<DcmsUser> users,
            ForgejoUserSync forgejo, ForgejoAdminClient admin, CancellationToken ct) =>
        {
            var user = await FindUserAsync(principal, users);
            if (user is null) return Results.Unauthorized();
            var username = await forgejo.EnsureAccountAsync(user, ct);
            if (username is null) return Results.NoContent();
            await admin.DeletePublicKeyAsync(username, id, ct);
            return Results.NoContent();
        });

        return app;
    }

    /// <summary>
    /// How many tenant memberships the user still has. This is the one piece of
    /// tenancy state identity looks at, and it only reads: admin-api owns those rows
    /// and the rules about when they may go (see its MyAccountEndpoints). Read with
    /// raw SQL over the Identity connection rather than by taking a dependency on
    /// TenancyDbContext, which would drag Finbuckle's ambient-tenant resolution into
    /// a service that has no tenant. Both schemas live in the same database.
    /// </summary>
    private static async Task<int> CountMembershipsAsync(IdentityDbContext db, Guid userId, CancellationToken ct)
    {
        var counts = await db.Database
            .SqlQueryRaw<int>(
                """SELECT count(*)::int AS "Value" FROM tenancy.tenant_memberships WHERE "UserId" = {0}""",
                userId)
            .ToListAsync(ct);
        return counts.FirstOrDefault();
    }

    private static Task<DcmsUser?> FindUserAsync(ClaimsPrincipal principal, UserManager<DcmsUser> users)
    {
        var sub = principal.FindFirstValue(OpenIddictConstants.Claims.Subject)
                  ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return sub is null ? Task.FromResult<DcmsUser?>(null) : users.FindByIdAsync(sub)!;
    }

    private sealed record SetPasswordRequest(string? CurrentPassword, string NewPassword);
    private sealed record AddKeyRequest(string? Title, string Key);
}
