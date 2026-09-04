using Dcms.Shared.Data.Platform;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Dcms.PlatformApi.Authz;

/// <summary>
/// Gives the two global roles their opening set of console permissions, once.
///
/// <para><b>Seed-if-empty, per role, and never a reconcile.</b> The obvious implementation
/// converges each role onto the set below on every start, and it would quietly undo the
/// feature this table exists for: the plan is that gating an area behind a role is a row
/// insert, not a deploy. A reconciling seeder would delete that row on the next restart, and
/// the symptom — a permission that works until something restarts — is about as unpleasant as
/// authorization bugs get. So a role that already has any grant is left exactly as it is.</para>
///
/// <para>SuperAdmin is seeded for completeness rather than necessity: the authorization
/// handler short-circuits on the role, so it would hold everything with no rows at all. The
/// rows exist so the console's own role editor shows the truth rather than an empty list, and
/// so a future decision to drop the short-circuit does not silently lock out every operator.</para>
///
/// <para>Runs in every replica at startup. The unique index on (RoleName, Permission) is what
/// makes that safe: the loser of a race gets a constraint violation, which is logged and
/// ignored, rather than a duplicate grant.</para>
/// </summary>
public sealed class PlatformRoleSeeder(
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<PlatformRoleSeeder> logger)
    : IHostedService
{
    /// <summary>Mirrors <c>GlobalRoles</c> in the identity service, which owns the roles themselves.</summary>
    private const string SuperAdmin = "SuperAdmin";
    private const string Support = "Support";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("Platform:SeedRolePermissions", true))
        {
            return;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        try
        {
            await SeedRoleAsync(db, SuperAdmin, PlatformConsolePermissions.All, cancellationToken);
            await SeedRoleAsync(db, Support, PlatformConsolePermissions.ReadOnly, cancellationToken);
        }
        catch (Exception ex)
        {
            // A seeding failure must not stop the service from starting. SuperAdmin bypasses
            // the permission check outright, so the console still works for the operator who
            // would be fixing this; refusing to boot would take away the tool for diagnosing it.
            logger.LogError(ex, "Platform role permission seeding failed. The console may show an empty role editor.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task SeedRoleAsync(
        PlatformDbContext db, string role, IReadOnlyList<string> permissions, CancellationToken ct)
    {
        if (await db.RolePermissions.AnyAsync(rp => rp.RoleName == role, ct))
        {
            return;
        }

        db.RolePermissions.AddRange(permissions.Select(p => new PlatformRolePermission
        {
            RoleName = role,
            Permission = p,
        }));

        try
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Seeded {Count} platform permissions for global role {Role}.", permissions.Count, role);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Another replica won the race between the AnyAsync above and this insert.
            db.ChangeTracker.Clear();
            logger.LogInformation("Platform permissions for {Role} were seeded by another instance.", role);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };
}
