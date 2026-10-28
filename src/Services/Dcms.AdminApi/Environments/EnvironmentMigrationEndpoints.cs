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
            ITenantContext tenant, EnvironmentStore environments, EnvironmentMigrator migrator, CancellationToken ct) =>
        {
            var changes = await migrator.UnpublishedAsync((await environments.CurrentAsync(tenant, ct))!, ct);
            return Results.Ok(changes.Select(c => View(c)));
        }).RequirePermission(PlatformPermissions.EnvironmentsManage);

        app.MapPost("/api/admin/environments/migrations", async (
            CutRequest body, ITenantContext tenant, EnvironmentStore environments, CurrentUser me, EnvironmentMigrator migrator,
            IAuditRecorder audit, CancellationToken ct) =>
        {
            var name = body.Name?.Trim() ?? "";
            if (name.Length is 0 or > 200) return Results.BadRequest(new { error = "Name the migration (at most 200 characters)." });
            if (body.Changes is { Count: > 500 }) return Results.BadRequest(new { error = "Choose at most 500 changes." });
            var migration = await migrator.CutAsync((await environments.CurrentAsync(tenant, ct))!, name, body.Description?.Trim(), me.UserId, me.Email, ct,
                body.Changes?.ToHashSet(StringComparer.Ordinal));
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
            EnvironmentStore environments, CancellationToken ct) =>
        {
            // Whoever cuts migrations here or deploys into here, like the screens around it.
            var held = me.IsSuperAdmin ? null : await permissions.GetPermissionsAsync(tenant.TenantId!.Value, me.RequireUserId(), ct);
            if (held is not null && !held.Contains(PlatformPermissions.EnvironmentsManage) && !held.Contains(PlatformPermissions.EnvironmentsPromote))
            {
                return Results.Forbid();
            }
            var current = (await environments.CurrentAsync(tenant, ct))!;
            var history = await migrator.HistoryAsync(current, ct);
            var names = (await environments.ListAsync(current.TenantId, ct)).ToDictionary(e => e.Id, e => e.Name);
            return Results.Ok(history.Select(m => Row(m, names)));
        }).RequireAuthorization();

        // What the source has that this environment has not, change by change, against what
        // this environment holds now. Nothing is applied by reading it.
        app.MapGet("/api/admin/environments/pending", async (
            Guid from, ITenantContext tenant, EnvironmentStore environments, EnvironmentMigrator migrator, CancellationToken ct) =>
        {
            var (target, source) = await PairAsync(environments, tenant, from, ct);
            if (source is null) return RefusedSource;
            var pending = await migrator.PendingAsync(target, source, ct);
            return Results.Ok(pending.Select(p => new
            {
                migration = Row(p.Migration, null),
                changes = p.Changes.Select(r => View(r.Change, r)),
            }));
        }).RequirePermission(PlatformPermissions.EnvironmentsPromote);

        app.MapPost("/api/admin/environments/pending/apply", async (
            ApplyRequest body, ITenantContext tenant, EnvironmentStore environments, CurrentUser me, IJobQueue jobs,
            EnvironmentMigrator migrator, CancellationToken ct) =>
        {
            var (target, source) = await PairAsync(environments, tenant, body.From, ct);
            if (source is null) return RefusedSource;
            var dedupe = await DedupeKeyAsync(EnvironmentApplyJob.Kind, migrator, target, body.UpTo, body.Resolutions, ct);
            var job = await jobs.EnqueueAsync(new JobSpec(
                EnvironmentApplyJob.Kind, target.TenantId,
                new EnvironmentApplyPayload(body.From, body.UpTo, body.Resolutions ?? [], me.UserId, me.IsSuperAdmin),
                "jobs.kinds.environmentApply.title",
                RequiredPermission: PlatformPermissions.EnvironmentsPromote,
                DedupeKey: dedupe,
                MaxAttempts: 1,
                LinkPath: "/deployments") { EnvironmentId = target.Id }, ct);
            return Results.Accepted(value: new { jobId = job.Id });
        }).RequirePermission(PlatformPermissions.EnvironmentsPromote)
          .AuditExempt(JobAudits);

        app.MapGet("/api/admin/environments/migrations/{migrationId:guid}/rollback", async (
            Guid migrationId, ITenantContext tenant, EnvironmentStore environments, EnvironmentMigrator migrator, CancellationToken ct) =>
        {
            try
            {
                var (row, changes) = await migrator.RollbackPlanAsync((await environments.CurrentAsync(tenant, ct))!, migrationId, ct);
                return Results.Ok(new { migration = Row(row, null), changes = changes.Select(r => View(r.Change, r)) });
            }
            catch (EnvironmentMigrationException ex)
            {
                return Results.Conflict(new { error = ex.Key });
            }
        }).RequirePermission(PlatformPermissions.EnvironmentsPromote);

        app.MapPost("/api/admin/environments/migrations/{migrationId:guid}/rollback", async (
            Guid migrationId, RollbackRequest body, ITenantContext tenant, CurrentUser me, IJobQueue jobs,
            EnvironmentStore environments, EnvironmentMigrator migrator, CancellationToken ct) =>
        {
            var dedupe = await DedupeKeyAsync(EnvironmentRollbackJob.Kind, migrator, (await environments.CurrentAsync(tenant, ct))!,
                migrationId, body.Resolutions, ct);
            var job = await jobs.EnqueueAsync(new JobSpec(
                EnvironmentRollbackJob.Kind, tenant.TenantId!.Value,
                new EnvironmentRollbackPayload(migrationId, body.Resolutions ?? [], me.UserId, me.IsSuperAdmin),
                "jobs.kinds.environmentRollback.title",
                RequiredPermission: PlatformPermissions.EnvironmentsPromote,
                DedupeKey: dedupe,
                MaxAttempts: 1,
                LinkPath: "/deployments") { EnvironmentId = tenant.EnvironmentId }, ct);
            return Results.Accepted(value: new { jobId = job.Id });
        }).RequirePermission(PlatformPermissions.EnvironmentsPromote)
          .AuditExempt(JobAudits);

        return app;
    }

    /// <summary>
    /// The same request twice is one job; the same deployment again once the environment's history
    /// moved (rolled back, then deployed again), or with other resolutions (a retry after a
    /// conflict), is another.
    /// </summary>
    private static async Task<string> DedupeKeyAsync(
        string kind, EnvironmentMigrator migrator, TenantEnvironment target, Guid migrationId,
        Dictionary<string, string>? resolutions, CancellationToken ct)
    {
        var history = await migrator.HistoryAsync(target, ct);
        var state = string.Join(",", history.Select(m => $"{m.MigrationId:N}:{m.RolledBackAt?.UtcTicks}")) + "|"
                    + string.Join(",", (resolutions ?? []).OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => $"{r.Key}={r.Value}"));
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(state)))[..32];
        return $"{kind}:{target.Id:N}:{migrationId:N}:{hash}";
    }

    /// <summary>
    /// The request's environment, and the one deployments are asked from when it lists it as a
    /// source: the pipeline the tenant configured, not any environment a caller can name.
    /// </summary>
    private static async Task<(TenantEnvironment Target, TenantEnvironment? Source)> PairAsync(
        EnvironmentStore environments, ITenantContext tenant, Guid sourceId, CancellationToken ct)
    {
        var target = (await environments.CurrentAsync(tenant, ct))!;
        var source = target.PromotesFrom.Contains(sourceId)
            ? await environments.GetAsync(target.TenantId, sourceId, ct)
            : null;
        return (target, source);
    }

    private static readonly IResult RefusedSource =
        Results.BadRequest(new { error = "This environment does not receive deployments from that one." });

    private static object Row(EnvironmentMigration m, Dictionary<Guid, string>? names) => new
    {
        migrationId = m.MigrationId,
        name = m.Name,
        description = m.Description,
        origin = m.OriginEnvironmentId,
        originName = names?.GetValueOrDefault(m.OriginEnvironmentId),
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
        merged = reviewed?.Outcome is ChangeOutcome.Merge or ChangeOutcome.Conflict ? reviewed.Value : null,
        conflicts = reviewed?.Conflicts.Select(c => new { path = c.Path, ours = c.Ours, theirs = c.Theirs }),
    };

    private const string JobAudits = "Only queues the job, which records what it applied or rolled back "
                                   + "(environment.migration.applied / rolled_back) with the resolutions once it runs.";

    /// <param name="Changes">The unpublished changes to cut, by id; all of them when absent.</param>
    private sealed record CutRequest(string? Name, string? Description, List<string>? Changes);
    private sealed record ApplyRequest(Guid From, Guid UpTo, Dictionary<string, string>? Resolutions);
    private sealed record RollbackRequest(Dictionary<string, string>? Resolutions);
}
