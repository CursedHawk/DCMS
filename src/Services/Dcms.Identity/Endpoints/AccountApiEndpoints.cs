using System.Security.Claims;
using Dcms.Identity.Domain;
using Dcms.Identity.Forgejo;
using Microsoft.AspNetCore.Identity;
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

    private static Task<DcmsUser?> FindUserAsync(ClaimsPrincipal principal, UserManager<DcmsUser> users)
    {
        var sub = principal.FindFirstValue(OpenIddictConstants.Claims.Subject)
                  ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return sub is null ? Task.FromResult<DcmsUser?>(null) : users.FindByIdAsync(sub)!;
    }

    private sealed record SetPasswordRequest(string? CurrentPassword, string NewPassword);
    private sealed record AddKeyRequest(string? Title, string Key);
}
