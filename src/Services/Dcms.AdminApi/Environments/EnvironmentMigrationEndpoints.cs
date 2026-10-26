using System.Text.Json;
using Dcms.AdminApi.Tenancy;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Jobs;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
using Dcms.Shared.Security.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Environments;

/// <summary>
/// Deployments between a workspace's environments (ADR 0026): an environment's unpublished
/// configuration changes, cutting them into a migration, its history, and — from the receiving
/// side — every pending migration and change reviewed before anything is applied.
///
/// <para>Applying and rolling back are jobs: a site's git push or many plugin changes do not
/// belong on a request, and the job ledger shows the run to everyone working there.</para>
/// </summary>
public static class EnvironmentMigrationEndpoints
{
    public static IEndpointRouteBuilder MapEnvironmentMigrationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/environments/changes", async (
            ITenantContext tenant, EnvironmentMigrator migrator, CancellationToken ct) =>
        {
            var changes = await migrator.UnpublishedAsync(tenant.TenantId!.Value, ct);
            return Results.Ok(changes.Select(c => View(c)));
        }).RequirePermission(PlatformPermissions.EnvironmentsManage);

        app.MapPost("/api/admin/environments/migrations", async (
            CutRequest body, ITenantContext tenant, CurrentUser me, EnvironmentMigrator migrator,
            IAuditRecorder audit, CancellationToken ct) =>
        {
            var name = body.Name?.Trim() ?? "";
            if (name.Length is 0 or > 200) return Results.BadRequest(new { error = "Name the migration (at most 200 characters)." });
            var migration = await migrator.CutAsync(tenant.TenantId!.Value, name, body.Description?.Trim(), me.UserId, me.Email, ct);
            if (migration is null)
            {
                audit.Discard(audit.Declared!);
                return Results.Conflict(new { error = "Nothing has changed since the last migration." });
            }
            audit.Declared?.For("environment_migration", migration.MigrationId, migration.Name)
                .With("changes", EnvironmentMigrator.Changes(migration).Count);
            return Results.Created($"/api/admin/environments/migrations/{migration.MigrationId}", Row(migration, null));
        }).RequirePermission(PlatformPermissions.EnvironmentsManage)
          .WithAudit(AuditActions.EnvironmentMigrationCreated, "environment_migration");

        app.MapGet("/api/admin/environments/migrations", async (
            ITenantContext tenant, CurrentUser me, IPermissionResolver permissions, EnvironmentMigrator migrator,
            TenancyDbContext db, CancellationToken ct) =>
        {
            // Whoever cuts migrations here or deploys into here, like the screens around it.
            var held = me.IsSuperAdmin ? null : await permissions.GetPermissionsAsync(tenant.TenantId!.Value, me.RequireUserId(), ct);
            if (held is not null && !held.Contains(PlatformPermissions.EnvironmentsManage) && !held.Contains(PlatformPermissions.EnvironmentsPromote))
            {
                return Results.Forbid();
            }
            var history = await migrator.HistoryAsync(tenant.TenantId!.Value, ct);
            var names = await EnvironmentNamesAsync(db, tenant.TenantId!.Value, ct);
            return Results.Ok(history.Select(m => Row(m, names)));
        }).RequireAuthorization();

        // What the source has that this environment has not, change by change, against what
        // this environment holds now. Nothing is applied by reading it.
        app.MapGet("/api/admin/environments/pending", async (
            Guid from, ITenantContext tenant, TenancyDbContext db, EnvironmentMigrator migrator, CancellationToken ct) =>
        {
            var targetId = tenant.TenantId!.Value;
            if (await RefusedSourceAsync(db, targetId, from, ct) is { } refused) return refused;
            var pending = await migrator.PendingAsync(targetId, from, ct);
            return Results.Ok(pending.Select(p => new
            {
                migration = Row(p.Migration, null),
                changes = p.Changes.Select(r => View(r.Change, r)),
            }));
        }).RequirePermission(PlatformPermissions.EnvironmentsPromote);

        app.MapPost("/api/admin/environments/pending/apply", async (
            ApplyRequest body, ITenantContext tenant, TenancyDbContext db, CurrentUser me, IJobQueue jobs,
            CancellationToken ct) =>
        {
            var targetId = tenant.TenantId!.Value;
            if (await RefusedSourceAsync(db, targetId, body.From, ct) is { } refused) return refused;
            var job = await jobs.EnqueueAsync(new JobSpec(
                EnvironmentApplyJob.Kind, targetId,
                new EnvironmentApplyPayload(body.From, body.UpTo, body.Resolutions ?? [], me.UserId, me.IsSuperAdmin),
                "jobs.kinds.environmentApply.title",
                RequiredPermission: PlatformPermissions.EnvironmentsPromote,
                DedupeKey: $"{EnvironmentApplyJob.Kind}:{body.UpTo:N}",
                MaxAttempts: 1,
                LinkPath: "/deployments"), ct);
            return Results.Accepted(value: new { jobId = job.Id });
        }).RequirePermission(PlatformPermissions.EnvironmentsPromote)
          .AuditExempt(JobAudits);

        app.MapGet("/api/admin/environments/migrations/{migrationId:guid}/rollback", async (
            Guid migrationId, ITenantContext tenant, EnvironmentMigrator migrator, CancellationToken ct) =>
        {
            try
            {
                var (row, changes) = await migrator.RollbackPlanAsync(tenant.TenantId!.Value, migrationId, ct);
                return Results.Ok(new { migration = Row(row, null), changes = changes.Select(r => View(r.Change, r)) });
            }
            catch (EnvironmentMigrationException ex)
            {
                return Results.Conflict(new { error = ex.Key });
            }
        }).RequirePermission(PlatformPermissions.EnvironmentsPromote);

        app.MapPost("/api/admin/environments/migrations/{migrationId:guid}/rollback", async (
            Guid migrationId, RollbackRequest body, ITenantContext tenant, CurrentUser me, IJobQueue jobs, CancellationToken ct) =>
        {
            var job = await jobs.EnqueueAsync(new JobSpec(
                EnvironmentRollbackJob.Kind, tenant.TenantId!.Value,
                new EnvironmentRollbackPayload(migrationId, body.Resolutions ?? [], me.UserId, me.IsSuperAdmin),
                "jobs.kinds.environmentRollback.title",
                RequiredPermission: PlatformPermissions.EnvironmentsPromote,
                DedupeKey: $"{EnvironmentRollbackJob.Kind}:{migrationId:N}",
                MaxAttempts: 1,
                LinkPath: "/deployments"), ct);
            return Results.Accepted(value: new { jobId = job.Id });
        }).RequirePermission(PlatformPermissions.EnvironmentsPromote)
          .AuditExempt(JobAudits);

        return app;
    }

    /// <summary>
    /// Deployments come only from an environment of the same workspace that this one lists as a
    /// source — the pipeline the workspace configured, not any tenant a caller can name.
    /// </summary>
    private static async Task<IResult?> RefusedSourceAsync(TenancyDbContext db, Guid targetId, Guid sourceId, CancellationToken ct)
    {
        var target = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == targetId.ToString(), ct);
        if (target?.WorkspaceId is null || !target.PromotesFrom.Contains(sourceId))
        {
            return Results.BadRequest(new { error = "This environment does not receive deployments from that one." });
        }
        return null;
    }

    private static async Task<Dictionary<Guid, string>> EnvironmentNamesAsync(TenancyDbContext db, Guid tenantId, CancellationToken ct)
    {
        var workspaceId = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId.ToString()).Select(t => t.WorkspaceId).FirstOrDefaultAsync(ct);
        if (workspaceId is null) return [];
        return (await db.Tenants.AsNoTracking().Where(t => t.WorkspaceId == workspaceId)
                .Select(t => new { t.Id, t.EnvironmentName }).ToListAsync(ct))
            .ToDictionary(t => Guid.Parse(t.Id), t => t.EnvironmentName ?? Tenant.ProductionEnvironment);
    }

    private static object Row(EnvironmentMigration m, Dictionary<Guid, string>? names) => new
    {
        migrationId = m.MigrationId,
        name = m.Name,
        description = m.Description,
        origin = m.OriginTenantId,
        originName = names?.GetValueOrDefault(m.OriginTenantId),
        authorEmail = m.AuthorEmail,
        createdAt = m.CreatedAt,
        appliedAt = m.AppliedAt,
        rolledBackAt = m.RolledBackAt,
        resolutions = JsonDocument.Parse(m.ResolutionsJson).RootElement,
        changes = EnvironmentMigrator.Changes(m).Select(c => View(c)),
    };

    private static object View(ConfigChange c, ReviewedChange? reviewed = null) => new
    {
        id = c.Id,
        kind = c.Kind,
        key = c.Key,
        label = c.Label ?? c.Key,
        op = c.Op,
        before = c.Before,
        after = c.After,
        current = reviewed?.Current,
        outcome = reviewed?.Outcome.ToString().ToLowerInvariant(),
    };

    private const string JobAudits = "Only queues the job, which records what it applied or rolled back "
                                   + "(environment.migration.applied / rolled_back) with the resolutions once it runs.";

    private sealed record CutRequest(string? Name, string? Description);
    private sealed record ApplyRequest(Guid From, Guid UpTo, Dictionary<string, string>? Resolutions);
    private sealed record RollbackRequest(Dictionary<string, string>? Resolutions);
}
