using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Audit;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Environments;

public sealed record EnvironmentApplyPayload(Guid Source, Guid UpTo, Dictionary<string, string> Resolutions, Guid? AppliedBy, bool IsSuperAdmin = false);

public sealed record EnvironmentRollbackPayload(Guid MigrationId, Dictionary<string, string> Resolutions, Guid? By = null, bool IsSuperAdmin = false);

/// <param name="Data">The data scopes to copy as well (<see cref="IEnvironmentDataCopier.Scope"/>); configuration always comes.</param>
public sealed record EnvironmentCopyPayload(Guid Source, List<string>? Data = null);

/// <summary>
/// Gives a new environment its source's configuration (ADR 0026: "copy configuration from"), and
/// the data scopes ticked. All or nothing: a copy that stops partway is no environment to work in,
/// so it is deleted again and the job says on what it stopped.
/// </summary>
public sealed class EnvironmentCopyJob(
    EnvironmentMigrator migrator, EnvironmentDuplicator duplicator, TenancyDbContext db, EnvironmentStore environments,
    Tenancy.TenantDeleter deleter, Dcms.Shared.Audit.AuditScope audit, ILogger<EnvironmentCopyJob> logger)
    : IJobHandler<EnvironmentCopyPayload>
{
    public const string Kind = "environments.copy";

    public async Task<object?> RunAsync(EnvironmentCopyPayload payload, IJobContext context)
    {
        var (target, source) = await environments.JobPairAsync(context, payload.Source);
        var scopes = payload.Data ?? [];
        Dictionary<string, int> copied = [];
        var log = EnvironmentSetupLog.Start(db, audit, target, context, migrator.KindNames, scopes);
        try
        {
            await log.WriteAsync(ready: false);
            await EnvironmentLock.RunAsync(db, target.Id, context.Cancellation, async () =>
            {
                await migrator.CopyAsync(target, source, context.Cancellation, log.KindAsync);
                copied = await duplicator.CopyAsync(target, source, scopes, log.ScopeAsync, context.Cancellation);
                return true;
            });
            // Everything is in: the environment opens.
            await log.WriteAsync(ready: true);
            environments.Evict(target.TenantId);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // The innermost error: a database layer wraps the real one ("No space left on device")
            // in "an exception ... likely due to a transient failure", which says nothing.
            var reason = failure is JobFailedException f ? f.Detail ?? f.ErrorKey : failure.GetBaseException().Message;
            try
            {
                await deleter.DeleteEnvironmentAsync(target.TenantId, target.Id, CancellationToken.None);
                environments.Evict(target.TenantId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Environment {Environment}, whose copy failed, could not be deleted; it stays.", target.Id);
                throw new JobFailedException("environments.errors.copyLeft", reason, failure);
            }
            throw new JobFailedException("environments.errors.copyRemoved", reason, failure);
        }
        return new { copied };
    }
}

/// <summary>
/// Applies a reviewed deployment into the job's environment (ADR 0026). One at a time per environment:
/// two applies racing would each read the target as the other had not changed it.
/// </summary>
public sealed class EnvironmentApplyJob(
    EnvironmentMigrator migrator, TenancyDbContext db, EnvironmentStore environments, IAuditRecorder audit,
    Tenancy.TenantDeleter deleter, ILogger<EnvironmentApplyJob> logger)
    : IJobHandler<EnvironmentApplyPayload>
{
    public const string Kind = "environments.apply";

    public async Task<object?> RunAsync(EnvironmentApplyPayload payload, IJobContext context)
    {
        var (target, source) = await environments.JobPairAsync(context, payload.Source);
        var applied = await EnvironmentLock.RunAsync(db, target.Id, context.Cancellation, () =>
            migrator.ApplyAsync(target, source, payload.UpTo, payload.Resolutions,
                new EnvironmentApplier(payload.AppliedBy, payload.IsSuperAdmin), context.Cancellation));
        audit.Record(AuditActions.EnvironmentMigrationApplied)
            .InTenant(target.TenantId)
            .With("environment", target.Name)
            .For("environment_migration", payload.UpTo, null)
            .With("source", payload.Source)
            .With("migrations", applied)
            .With("resolutions", payload.Resolutions);
        await audit.FlushAsync(context.Cancellation);
        var deleted = await DeleteMergedFeatureAsync(target, source, context.Cancellation);
        return new { applied, deleted };
    }

    /// <summary>
    /// A feature environment set to go once merged, when nothing it cut is left to deploy here.
    /// After the deployment is recorded: a deletion that fails leaves the feature, not the merge, undone.
    /// </summary>
    private async Task<bool> DeleteMergedFeatureAsync(TenantEnvironment target, TenantEnvironment source, CancellationToken ct)
    {
        if (source is not { ForkedFrom: { } forkedFrom, DeleteWhenMerged: true } || forkedFrom != target.Id
            || (await migrator.PendingAsync(target, source, ct)).Count > 0)
        {
            return false;
        }
        try
        {
            await deleter.DeleteEnvironmentAsync(source.TenantId, source.Id, ct);
            environments.Evict(source.TenantId);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Merged feature environment {Source} could not be deleted; it stays.", source.Id);
            return false;
        }

    }
}

/// <summary>Reverts the environment's last migration, as reviewed.</summary>
public sealed class EnvironmentRollbackJob(EnvironmentMigrator migrator, TenancyDbContext db, EnvironmentStore environments, IAuditRecorder audit)
    : IJobHandler<EnvironmentRollbackPayload>
{
    public const string Kind = "environments.rollback";

    public async Task<object?> RunAsync(EnvironmentRollbackPayload payload, IJobContext context)
    {
        var target = await environments.JobTargetAsync(context);
        var row = await EnvironmentLock.RunAsync(db, target.Id, context.Cancellation, () =>
            migrator.RollbackAsync(target, payload.MigrationId, payload.Resolutions,
                new EnvironmentApplier(payload.By, payload.IsSuperAdmin), context.Cancellation));
        audit.Record(AuditActions.EnvironmentMigrationRolledBack)
            .InTenant(target.TenantId)
            .With("environment", target.Name)
            .For("environment_migration", payload.MigrationId, row.Name)
            .With("resolutions", payload.Resolutions);
        await audit.FlushAsync(context.Cancellation);
        return null;
    }
}

internal static class EnvironmentLock
{
    /// <summary>
    /// Runs <paramref name="work"/> holding the environment's migration lock, a session advisory
    /// lock on a connection held open for the duration; a refusal becomes a job failure that is
    /// not retried, because the same input would be refused again.
    /// </summary>
    public static async Task<T> RunAsync<T>(TenancyDbContext db, Guid environmentId, CancellationToken ct, Func<Task<T>> work)
    {
        var key = $"environment-migrations:{environmentId:N}";
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_lock(hashtextextended({key}, 0))", ct);
            try
            {
                return await work();
            }
            catch (EnvironmentMigrationException ex)
            {
                throw new JobFailedException(ex.Key, ex.Detail);
            }
            finally
            {
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock(hashtextextended({key}, 0))", CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}

internal static class EnvironmentJobContext
{
    /// <summary>The environment a job runs in (ADR 0028); a job refused when it has gone.</summary>
    public static async Task<TenantEnvironment> JobTargetAsync(this EnvironmentStore environments, IJobContext context)
    {
        var tenantId = context.TenantId!.Value;
        // A job runs on the worker, whose cached list may predate the change that queued it (a
        // new environment, a flag just set): the job works from what the database says now.
        environments.Evict(tenantId);
        return await environments.GetAsync(tenantId, context.EnvironmentId ?? tenantId, context.Cancellation)
               ?? throw new JobFailedException("environments.errors.environmentGone");
    }

    /// <summary>The job's environment and the one it takes configuration from, both of its tenant.</summary>
    public static async Task<(TenantEnvironment Target, TenantEnvironment Source)> JobPairAsync(
        this EnvironmentStore environments, IJobContext context, Guid sourceId)
    {
        var target = await environments.JobTargetAsync(context);
        var source = await environments.GetAsync(target.TenantId, sourceId, context.Cancellation)
                     ?? throw new JobFailedException("environments.errors.environmentGone");
        return (target, source);
    }
}
