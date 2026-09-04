using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Platform;
using Dcms.Shared.Security;
using Dcms.Shared.Security.Authorization;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Dcms.PlatformApi.Authz;

/// <summary>
/// Who the caller is, and which global role holds which console permission.
/// </summary>
public static class PlatformAuthzEndpoints
{
    public sealed record MeResponse(
        string? UserId,
        string? Name,
        string? Email,
        bool IsSuperAdmin,
        IReadOnlyList<string> Roles,
        IReadOnlyList<string> Permissions);

    public sealed record RolePermissionsResponse(string RoleName, IReadOnlyList<string> Permissions);

    public sealed record UpdateRolePermissionsRequest(IReadOnlyList<string> Permissions);

    public static IEndpointRouteBuilder MapPlatformAuthzEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/platform");

        // Deliberately only RequireAuthorization(): this is the endpoint the SPA calls to find
        // out whether it may show anything at all, so gating it on a platform permission would
        // make a legitimate refusal indistinguishable from a broken deployment. It answers with
        // an empty permission list for a caller who holds nothing, and the shell renders a
        // refusal from that.
        group.MapGet("/me", async (
            ClaimsPrincipal user,
            IPlatformPermissionResolver resolver,
            CancellationToken ct) =>
        {
            var roles = user.FindAll("role").Select(c => c.Value).Distinct().ToArray();
            var isSuperAdmin = roles.Contains("SuperAdmin", StringComparer.Ordinal);

            // A SuperAdmin holds everything by the handler's short-circuit, whether or not the
            // rows exist. Reporting the resolved set instead would let a half-seeded table hide
            // the console from the one person who can repair it.
            var permissions = isSuperAdmin
                ? PlatformConsolePermissions.All
                : [.. (await resolver.GetPermissionsAsync(roles, ct)).Order(StringComparer.Ordinal)];

            return Results.Ok(new MeResponse(
                user.FindFirst("sub")?.Value,
                user.FindFirst("name")?.Value,
                user.FindFirst("email")?.Value,
                isSuperAdmin,
                roles,
                permissions));
        })
        .RequireAuthorization()
        .WithName("PlatformMe");

        // The catalog is a constant, but it is served rather than duplicated in the SPA so the
        // role editor cannot offer a key the server does not know.
        group.MapGet("/permissions/catalog", () => Results.Ok(new
        {
            permissions = PlatformConsolePermissions.All,
            readOnly = PlatformConsolePermissions.ReadOnly,
        }))
        .RequirePlatformPermission(PlatformConsolePermissions.RolesManage)
        .WithName("PlatformPermissionCatalog");

        group.MapGet("/roles", async (PlatformDbContext db, CancellationToken ct) =>
        {
            var rows = await db.RolePermissions
                .AsNoTracking()
                .OrderBy(rp => rp.RoleName).ThenBy(rp => rp.Permission)
                .ToListAsync(ct);

            var grouped = rows
                .GroupBy(rp => rp.RoleName, StringComparer.Ordinal)
                .Select(g => new RolePermissionsResponse(g.Key, [.. g.Select(rp => rp.Permission)]))
                .ToList();

            return Results.Ok(grouped);
        })
        .RequirePlatformPermission(PlatformConsolePermissions.RolesManage)
        .WithName("PlatformRoles");

        group.MapPut("/roles/{roleName}/permissions", async (
            string roleName,
            UpdateRolePermissionsRequest request,
            PlatformDbContext db,
            PlatformPermissionResolver resolver,
            IAuditRecorder audit,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var unknown = request.Permissions
                .Where(p => !PlatformConsolePermissions.All.Contains(p))
                .ToArray();
            if (unknown.Length > 0)
            {
                return Results.Problem(
                    title: "Unknown permission key",
                    detail: $"Not a platform console permission: {string.Join(", ", unknown)}.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var desired = request.Permissions.Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);

            // Read the previous set BEFORE destroying it. ExecuteDeleteAsync never touches the
            // change tracker, so nothing downstream can reconstruct a before-image — a record
            // saying only "the grants changed" would leave the one question anyone asks about
            // a permission change ("what did it used to be?") permanently unanswerable.
            var previous = await db.RolePermissions
                .AsNoTracking()
                .Where(rp => rp.RoleName == roleName)
                .Select(rp => rp.Permission)
                .ToListAsync(ct);

            var granted = desired.Except(previous, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var revoked = previous.Except(desired, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

            // Enriched before the write, and the diff spelled out rather than left implicit:
            // this is the endpoint that can hand `platform:logs:purge` to another role, so it
            // is the record somebody reads when asking how an account got that reach.
            audit.Declared?
                .Platform()
                .As(AuditCategory.Security, AuditSeverity.Warning)
                .For("platform-role", roleName, roleName)
                .With("granted", granted)
                .With("revoked", revoked)
                .With("permissions", desired.Order(StringComparer.Ordinal).ToArray());

            // Delete-then-insert in one transaction rather than RemoveRange + re-Add in one
            // SaveChanges: EF matches the re-added rows to the removed ones by key, turns the
            // pair into an UPDATE against ids that no longer exist, and fails with "affected 0
            // rows". That exact shape has broken a role PUT on this codebase before.
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            await db.RolePermissions.Where(rp => rp.RoleName == roleName).ExecuteDeleteAsync(ct);
            db.RolePermissions.AddRange(desired.Select(p => new PlatformRolePermission
            {
                RoleName = roleName,
                Permission = p,
            }));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            await resolver.InvalidateAsync(ct);

            loggerFactory.CreateLogger(typeof(PlatformAuthzEndpoints)).LogInformation(
                "Platform permissions for global role {Role} set to {Count} keys.", roleName, desired.Count);

            return Results.Ok(new RolePermissionsResponse(roleName, [.. desired.Order(StringComparer.Ordinal)]));
        })
        .RequirePlatformPermission(PlatformConsolePermissions.RolesManage)
        .WithName("PlatformSetRolePermissions")
        .WithAudit(AuditActions.PlatformPermissionsChanged, "platform-role");

        return app;
    }
}
