using System.Security.Claims;
using Dcms.Identity.Data;
using Dcms.Identity.Domain;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Identity.Endpoints;

/// <summary>
/// The platform console's user directory, served by the service that owns users.
///
/// <para><b>Why here and not in platform-api.</b> identity owns the <c>identity</c> schema and
/// holds <c>UserManager&lt;DcmsUser&gt;</c>, which is what knows how to lock an account, mint a
/// password-reset token and keep the security stamp consistent. Reimplementing any of that
/// against raw rows from another service would be a bug farm, and giving another service write
/// access to <c>AspNetUsers</c> would make "who can change a password" a question with two
/// answers. The console reaches these endpoints same-origin through Caddy, carrying the
/// operator's own token — so every row written here is attributable to a person, not to a
/// service principal.</para>
///
/// <para>Mounted under <c>/api/identity/*</c> rather than <c>/account/api/*</c> because the
/// edge routes <c>/api/*</c> by prefix and this has to be distinguishable from admin-api's
/// share of that space.</para>
/// </summary>
public static class PlatformUserEndpoints
{
    /// <summary>Bearer token + the SuperAdmin global role.</summary>
    public const string PolicyName = "PlatformAdminApi";

    public sealed record UserRow(
        Guid Id,
        string? Email,
        string? DisplayName,
        bool EmailConfirmed,
        DateTimeOffset CreatedAt,
        bool LockedOut,
        DateTimeOffset? LockoutEnd,
        int AccessFailedCount,
        string? ForgejoUsername,
        bool HasGitPassword,
        IReadOnlyList<string> Roles);

    public sealed record RoleRequest(string Role);

    public static IEndpointRouteBuilder MapPlatformUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/identity").RequireAuthorization(PolicyName);

        group.MapGet("/users", async (
            IdentityDbContext db, string? search, bool? lockedOut, int? page, int? pageSize,
            CancellationToken ct) =>
        {
            var take = Math.Clamp(pageSize ?? 50, 1, 200);
            var skip = Math.Max(0, (Math.Max(page ?? 1, 1) - 1) * take);

            var query = db.Users.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(search))
            {
                // NormalizedEmail is indexed and already upper-cased by Identity; matching on
                // it rather than Email is what keeps this from scanning on every keystroke.
                var term = search.Trim().ToUpperInvariant();
                query = query.Where(u =>
                    (u.NormalizedEmail != null && u.NormalizedEmail.Contains(term))
                    || (u.DisplayName != null && u.DisplayName.ToUpper().Contains(term)));
            }

            if (lockedOut == true)
            {
                query = query.Where(u => u.LockoutEnd != null && u.LockoutEnd > DateTimeOffset.UtcNow);
            }

            var total = await query.CountAsync(ct);

            var users = await query
                .OrderByDescending(u => u.CreatedAt)
                .Skip(skip).Take(take)
                .ToListAsync(ct);

            // One join for every role on the page rather than a round trip per user: a
            // directory is exactly where N+1 becomes visible.
            var ids = users.Select(u => u.Id).ToList();
            var roles = await (from ur in db.UserRoles
                               join r in db.Roles on ur.RoleId equals r.Id
                               where ids.Contains(ur.UserId)
                               select new { ur.UserId, r.Name })
                .ToListAsync(ct);
            var byUser = roles
                .GroupBy(x => x.UserId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(x => x.Name ?? string.Empty)]);

            var rows = users.Select(u => new UserRow(
                u.Id, u.Email, u.DisplayName, u.EmailConfirmed, u.CreatedAt,
                u.LockoutEnd is not null && u.LockoutEnd > DateTimeOffset.UtcNow,
                u.LockoutEnd, u.AccessFailedCount, u.ForgejoUsername, u.HasGitPassword,
                byUser.TryGetValue(u.Id, out var r) ? r : []));

            return Results.Ok(new { total, page = Math.Max(page ?? 1, 1), pageSize = take, items = rows });
        });

        group.MapGet("/users/{id:guid}", async (Guid id, IdentityDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
            if (user is null) return Results.NotFound();

            var roles = await (from ur in db.UserRoles
                               join r in db.Roles on ur.RoleId equals r.Id
                               where ur.UserId == id
                               select r.Name ?? string.Empty).ToListAsync(ct);

            return Results.Ok(new UserRow(
                user.Id, user.Email, user.DisplayName, user.EmailConfirmed, user.CreatedAt,
                user.LockoutEnd is not null && user.LockoutEnd > DateTimeOffset.UtcNow,
                user.LockoutEnd, user.AccessFailedCount, user.ForgejoUsername,
                user.HasGitPassword, roles));
        });

        group.MapPost("/users/{id:guid}/lock", async (
            Guid id, ClaimsPrincipal principal, UserManager<DcmsUser> users, IdentityDbContext db,
            IAuditRecorder audit, CancellationToken ct) =>
        {
            var user = await users.FindByIdAsync(id.ToString());
            if (user is null) return Results.NotFound();

            if (SelfId(principal) == id)
            {
                return await RefuseAsync(audit, AuditActions.PlatformUserLocked, id, user.Email,
                    "self", "You cannot lock your own account.",
                    "Locking yourself out of the console that unlocks accounts is a one-way door.", ct);
            }

            if (await IsLastSuperAdminAsync(db, id, ct))
            {
                return await RefuseAsync(audit, AuditActions.PlatformUserLocked, id, user.Email,
                    "last_superadmin", "This is the last SuperAdmin.",
                    "Locking it would leave the platform with no operator who can unlock anything.", ct);
            }

            // Identity refuses SetLockoutEndDate on a user whose lockout is not enabled, and
            // says so only through a failed IdentityResult — which is easy to ignore and
            // produces a lock button that reports success and does nothing.
            if (!await users.GetLockoutEnabledAsync(user))
            {
                var enable = await users.SetLockoutEnabledAsync(user, true);
                if (!enable.Succeeded) return IdentityProblem(enable);
            }

            var result = await users.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
            if (!result.Succeeded) return IdentityProblem(result);

            audit.Declared?.Platform().About(id).With("email", user.Email);
            return Results.NoContent();
        }).WithAudit(AuditActions.PlatformUserLocked, category: AuditCategory.Auth);

        group.MapPost("/users/{id:guid}/unlock", async (
            Guid id, UserManager<DcmsUser> users, IAuditRecorder audit) =>
        {
            var user = await users.FindByIdAsync(id.ToString());
            if (user is null) return Results.NotFound();

            var result = await users.SetLockoutEndDateAsync(user, null);
            if (!result.Succeeded) return IdentityProblem(result);
            // Failed attempts survive a lockout end date, so an account unlocked without this
            // re-locks itself on the next wrong password.
            await users.ResetAccessFailedCountAsync(user);

            audit.Declared?.Platform().About(id).With("email", user.Email);
            return Results.NoContent();
        }).WithAudit(AuditActions.PlatformUserUnlocked, category: AuditCategory.Auth);

        group.MapPost("/users/{id:guid}/confirm-email", async (
            Guid id, UserManager<DcmsUser> users, IAuditRecorder audit) =>
        {
            var user = await users.FindByIdAsync(id.ToString());
            if (user is null) return Results.NotFound();
            if (user.EmailConfirmed) return Results.NoContent();

            // The support case this exists for: mail that never arrived. Confirming by hand is
            // a deliberate override of a check, so it is recorded as one.
            var token = await users.GenerateEmailConfirmationTokenAsync(user);
            var result = await users.ConfirmEmailAsync(user, token);
            if (!result.Succeeded) return IdentityProblem(result);

            audit.Declared?.Platform().About(id).With("email", user.Email);
            return Results.NoContent();
        }).WithAudit(AuditActions.PlatformUserEmailConfirmed, category: AuditCategory.Auth);

        group.MapPost("/users/{id:guid}/roles", async (
            Guid id, RoleRequest body, ClaimsPrincipal principal,
            UserManager<DcmsUser> users, IAuditRecorder audit) =>
        {
            if (!GlobalRoles.All.Contains(body.Role, StringComparer.Ordinal))
            {
                return Problem("Unknown global role.",
                    $"Valid roles are: {string.Join(", ", GlobalRoles.All)}.");
            }

            var user = await users.FindByIdAsync(id.ToString());
            if (user is null) return Results.NotFound();
            if (await users.IsInRoleAsync(user, body.Role)) return Results.NoContent();

            var result = await users.AddToRoleAsync(user, body.Role);
            if (!result.Succeeded) return IdentityProblem(result);

            audit.Declared?.Platform().About(id)
                .With("email", user.Email)
                .With("role", body.Role)
                .With("granted_by", SelfId(principal));
            return Results.NoContent();
        }).WithAudit(AuditActions.PlatformRoleGranted, category: AuditCategory.Auth);

        group.MapDelete("/users/{id:guid}/roles/{role}", async (
            Guid id, string role, ClaimsPrincipal principal,
            UserManager<DcmsUser> users, IdentityDbContext db,
            IAuditRecorder audit, CancellationToken ct) =>
        {
            var user = await users.FindByIdAsync(id.ToString());
            if (user is null) return Results.NotFound();

            // Two guards, and both are about the same failure: a platform with no operator.
            if (string.Equals(role, GlobalRoles.SuperAdmin, StringComparison.Ordinal))
            {
                if (SelfId(principal) == id)
                {
                    return await RefuseAsync(audit, AuditActions.PlatformRoleRevoked, id, user.Email,
                        "self", "You cannot remove your own SuperAdmin role.",
                        "Ask another SuperAdmin to do it, so there is always someone who can undo it.", ct);
                }

                if (await IsLastSuperAdminAsync(db, id, ct))
                {
                    return await RefuseAsync(audit, AuditActions.PlatformRoleRevoked, id, user.Email,
                        "last_superadmin", "This is the last SuperAdmin.",
                        "Grant the role to someone else before removing it here.", ct);
                }
            }

            if (!await users.IsInRoleAsync(user, role)) return Results.NoContent();

            var result = await users.RemoveFromRoleAsync(user, role);
            if (!result.Succeeded) return IdentityProblem(result);

            audit.Declared?.Platform().About(id)
                .With("email", user.Email)
                .With("role", role)
                .With("revoked_by", SelfId(principal));
            return Results.NoContent();
        }).WithAudit(AuditActions.PlatformRoleRevoked, category: AuditCategory.Auth);

        return app;
    }

    private static Guid? SelfId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirst("sub")?.Value, out var id) ? id : null;

    /// <summary>
    /// True when <paramref name="id"/> holds SuperAdmin and nobody else does.
    ///
    /// <para>Counted rather than assumed, and counted at the moment of the change. Two
    /// concurrent revocations could still race past this — the honest fix is a constraint the
    /// database cannot express, so the compensating control is that both are audited and
    /// either can be undone by the break-glass path (a direct role insert), which is the same
    /// path that seeded the first SuperAdmin.</para>
    /// </summary>
    private static async Task<bool> IsLastSuperAdminAsync(IdentityDbContext db, Guid id, CancellationToken ct)
    {
        var roleId = await db.Roles
            .Where(r => r.Name == GlobalRoles.SuperAdmin)
            .Select(r => r.Id)
            .FirstOrDefaultAsync(ct);
        if (roleId == Guid.Empty) return false;

        var holders = await db.UserRoles.Where(ur => ur.RoleId == roleId).Select(ur => ur.UserId).ToListAsync(ct);
        return holders.Contains(id) && holders.Count <= 1;
    }

    /// <summary>
    /// Refuses an identity-preserving guard AND records the attempt.
    ///
    /// <para>The record is written with <c>RecordNowAsync</c> rather than by enriching the
    /// declared entry, because the middleware withdraws a declared action whose response is
    /// 4xx — correctly, since the action did not happen. But "somebody tried to remove the
    /// last SuperAdmin" is not nothing: it is either an operator about to be surprised or an
    /// attempt to decapitate the platform, and neither should be inferable only from an
    /// absence. A denial record is written automatically for 401 and 403; these refusals are
    /// neither, so they write their own.</para>
    /// </summary>
    private static async Task<IResult> RefuseAsync(
        IAuditRecorder audit, string action, Guid subjectId, string? email,
        string reason, string title, string detail, CancellationToken ct)
    {
        await audit.RecordNowAsync(
            new AuditEntry { Action = action }
                .Platform()
                .About(subjectId)
                .With("email", email)
                .Failed(reason),
            ct);

        return Problem(title, detail);
    }

    private static IResult Problem(string title, string detail) =>
        Results.Problem(title: title, detail: detail, statusCode: StatusCodes.Status400BadRequest);

    private static IResult IdentityProblem(IdentityResult result) =>
        Results.Problem(
            title: "The change was refused.",
            detail: result.Errors.FirstOrDefault()?.Description ?? "Identity refused the operation.",
            statusCode: StatusCodes.Status400BadRequest);
}
