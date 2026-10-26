using Dcms.Shared.Audit;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Environments;

public sealed record EnvironmentApplyPayload(Guid Source, Guid UpTo, Dictionary<string, string> Resolutions, Guid? AppliedBy, bool IsSuperAdmin = false);

public sealed record EnvironmentRollbackPayload(Guid MigrationId, Dictionary<string, string> Resolutions, Guid? By = null, bool IsSuperAdmin = false);

public sealed record EnvironmentCopyPayload(Guid Source);

/// <summary>Gives a new environment its source's configuration (ADR 0026: "copy configuration from").</summary>
public sealed class EnvironmentCopyJob(EnvironmentMigrator migrator, TenancyDbContext db) : IJobHandler<EnvironmentCopyPayload>
{
    public const string Kind = "environments.copy";

    public async Task<object?> RunAsync(EnvironmentCopyPayload payload, IJobContext context)
    {
        var target = context.TenantId!.Value;
        await EnvironmentLock.RunAsync(db, target, context.Cancellation, async () =>
        {
            await migrator.CopyAsync(target, payload.Source, context.Cancellation);
            return true;
        });
        return null;
    }
}

/// <summary>
/// Applies a reviewed deployment into the job's tenant (ADR 0026). One at a time per environment:
/// two applies racing would each read the target as the other had not changed it.
/// </summary>
public sealed class EnvironmentApplyJob(EnvironmentMigrator migrator, TenancyDbContext db, IAuditRecorder audit)
    : IJobHandler<EnvironmentApplyPayload>
{
    public const string Kind = "environments.apply";

    public async Task<object?> RunAsync(EnvironmentApplyPayload payload, IJobContext context)
    {
        var target = context.TenantId!.Value;
        var applied = await EnvironmentLock.RunAsync(db, target, context.Cancellation, () =>
            migrator.ApplyAsync(target, payload.Source, payload.UpTo, payload.Resolutions,
                new EnvironmentApplier(payload.AppliedBy, payload.IsSuperAdmin), context.Cancellation));
        audit.Record(AuditActions.EnvironmentMigrationApplied)
            .InTenant(target)
            .For("environment_migration", payload.UpTo, null)
            .With("source", payload.Source)
            .With("migrations", applied)
            .With("resolutions", payload.Resolutions);
        await audit.FlushAsync(context.Cancellation);
        return new { applied };
    }
}

/// <summary>Reverts the environment's last migration, as reviewed.</summary>
public sealed class EnvironmentRollbackJob(EnvironmentMigrator migrator, TenancyDbContext db, IAuditRecorder audit)
    : IJobHandler<EnvironmentRollbackPayload>
{
    public const string Kind = "environments.rollback";

    public async Task<object?> RunAsync(EnvironmentRollbackPayload payload, IJobContext context)
    {
        var target = context.TenantId!.Value;
        var row = await EnvironmentLock.RunAsync(db, target, context.Cancellation, () =>
            migrator.RollbackAsync(target, payload.MigrationId, payload.Resolutions,
                new EnvironmentApplier(payload.By, payload.IsSuperAdmin), context.Cancellation));
        audit.Record(AuditActions.EnvironmentMigrationRolledBack)
            .InTenant(target)
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
    public static async Task<T> RunAsync<T>(TenancyDbContext db, Guid tenantId, CancellationToken ct, Func<Task<T>> work)
    {
        var key = $"environment-migrations:{tenantId:N}";
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
