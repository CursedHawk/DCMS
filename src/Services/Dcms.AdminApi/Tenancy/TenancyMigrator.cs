using Dcms.Shared.Audit;
using Dcms.Shared.Data.Audit;
using Dcms.Shared.Data.Ai;
using Dcms.Shared.Data.Analytics;
using Dcms.Shared.Data.Chat;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.DataProtection;
using Dcms.Shared.Data.Forms;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Data.Observability;
using Dcms.Shared.Data.Notifications;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Search;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Social;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Data.Visitors;
using Dcms.Shared.Data;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Tenancy;

/// <summary>
/// Applies every schema change the platform owns: EF migrations for the twelve contexts, the
/// hand-written audit partition DDL, the RLS backstop, the reporting views and the owner-permission
/// backfill.
///
/// <para><b>This is intended to run as a one-shot job, not as part of a service.</b> Invoke it with
/// <c>--migrate-only</c>, which builds the host, runs this, and exits — that is what the deploy
/// pipeline does before rolling any service. Services then start with <c>Tenancy:Migrate=false</c>
/// and never touch DDL.</para>
///
/// <para>The advisory lock below is the belt-and-braces for when they do anyway. Until every
/// environment is on the job, <see cref="TenancyMigrator"/> still calls this at startup, and two
/// instances starting together would otherwise race on DDL — concurrent <c>CREATE TABLE</c> and
/// concurrent inserts into the same <c>__ef_migrations_history</c>. Postgres advisory locks are
/// held by the session rather than the transaction, which is why this opens its own connection
/// and holds it for the duration instead of borrowing a pooled one.</para>
/// </summary>
public static class DcmsMigrationRunner
{
    public static async Task RunAsync(
        IServiceProvider scoped,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken ct)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
                               ?? throw new InvalidOperationException(
                                   "ConnectionStrings:Postgres is required to run migrations.");

        await using var migrationLock = await PostgresAdvisoryLock.AcquireAsync(
            connectionString, PostgresAdvisoryLock.MigrationLockKey, logger, ct);

        await MigrateAllAsync(scoped, configuration, logger, ct);
    }

    private static async Task MigrateAllAsync(
        IServiceProvider scoped,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken ct)
    {
        await scoped.GetRequiredService<TenancyDbContext>().Database.MigrateAsync(ct);
        await scoped.GetRequiredService<CmsDbContext>().Database.MigrateAsync(ct);
        await scoped.GetRequiredService<MediaDbContext>().Database.MigrateAsync(ct);
        await scoped.GetRequiredService<SitesDbContext>().Database.MigrateAsync(ct);
        await scoped.GetRequiredService<AiDbContext>().Database.MigrateAsync(ct);
        await scoped.GetRequiredService<SearchDbContext>().Database.MigrateAsync(ct);
        await scoped.GetRequiredService<AnalyticsDbContext>().Database.MigrateAsync(ct);
        await scoped.GetRequiredService<VisitorsDbContext>().Database.MigrateAsync(ct);
        await scoped.GetRequiredService<ChatDbContext>().Database.MigrateAsync(ct);
        await scoped.GetRequiredService<FormsDbContext>().Database.MigrateAsync(ct);
        await scoped.GetRequiredService<SocialDbContext>().Database.MigrateAsync(ct);
        await scoped.GetRequiredService<NotificationsDbContext>().Database.MigrateAsync(ct);

        // The shared Data Protection key ring. admin-api does not use it — it is bearer-token
        // only — but it owns every schema on this database, and identity must not run DDL of
        // its own now that migrations are a job.
        await scoped.GetRequiredService<DataProtectionDbContext>().Database.MigrateAsync(ct);

        var audit = scoped.GetRequiredService<AuditDbContext>();
        await audit.Database.MigrateAsync(ct);

        // The partitioned table's DDL is hand-written in the migration; this adds what has to
        // be re-checked every startup rather than once: the month partitions ahead of now, and
        // the append-only trigger. Idempotent, and it must run after the migration created the
        // table it partitions.
        await AuditSchemaConfigurator.ApplyAsync(audit, logger, ct);

        logger.LogInformation("All databases migrated.");

        // Defense-in-depth: apply the RLS backstop once tables exist.
        if (configuration.GetValue("Tenancy:ApplyRls", true))
        {
            var tenancy = scoped.GetRequiredService<TenancyDbContext>();
            await RlsConfigurator.ApplyAsync(tenancy, logger, ct);
        }

        // Reporting views for Grafana. After the migrations, because every view reads tables
        // they create; before the backfill, because it changes nothing the backfill depends on
        // and a failure here is logged rather than thrown.
        if (configuration.GetValue("Observability:ApplyViews", true))
        {
            await ObservabilityViewConfigurator.ApplyAsync(
                scoped.GetRequiredService<TenancyDbContext>(),
                logger,
                ct);
        }

        // Last: it records what it grants, so the audit outbox has to exist first.
        await OwnerPermissionBackfill.ApplyAsync(
            scoped.GetRequiredService<TenancyDbContext>(),
            scoped.GetRequiredService<IAuditRecorder>(),
            logger,
            ct);
    }

    /// <summary>
    /// Model-only check that the hand-maintained <c>RlsConfigurator.TenantTables</c> list
    /// still covers every tenant-scoped table. Reads no database, so it is cheap enough to run on
    /// every startup of every instance — which is the point. A tenant table added without an entry
    /// keeps working and keeps passing tests; the only thing that changes is that the backstop
    /// stops covering it. Running this only inside the migration job would move the check away
    /// from the moment somebody adds a table.
    /// </summary>
    public static void AssertRlsCoverage(IServiceProvider scoped, ILogger logger) =>
        RlsConfigurator.AssertCoverage(
            [
                scoped.GetRequiredService<TenancyDbContext>(),
                scoped.GetRequiredService<CmsDbContext>(),
                scoped.GetRequiredService<MediaDbContext>(),
                scoped.GetRequiredService<SitesDbContext>(),
                scoped.GetRequiredService<AiDbContext>(),
                scoped.GetRequiredService<SearchDbContext>(),
                scoped.GetRequiredService<AnalyticsDbContext>(),
                scoped.GetRequiredService<VisitorsDbContext>(),
                scoped.GetRequiredService<ChatDbContext>(),
                scoped.GetRequiredService<FormsDbContext>(),
                scoped.GetRequiredService<SocialDbContext>(),
                scoped.GetRequiredService<NotificationsDbContext>(),
                scoped.GetRequiredService<AuditDbContext>(),
            ],
            logger);
}

/// <summary>
/// Startup hook that runs <see cref="DcmsMigrationRunner"/> when <c>Tenancy:Migrate</c> is set.
///
/// <para>Set <c>Tenancy:Migrate=false</c> everywhere the deploy pipeline runs the migration job
/// (which is everywhere, eventually). The RLS coverage assertion runs either way: it touches no
/// database and it is the guard that catches a tenant table added without a policy.</para>
/// </summary>
public sealed class TenancyMigrator(IServiceProvider services, IConfiguration configuration, ILogger<TenancyMigrator> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();

        DcmsMigrationRunner.AssertRlsCoverage(scope.ServiceProvider, logger);

        if (!configuration.GetValue("Tenancy:Migrate", true))
        {
            logger.LogInformation(
                "Tenancy:Migrate is false; skipping migrations. They are expected to have been applied by the migration job.");
            return;
        }

        await DcmsMigrationRunner.RunAsync(scope.ServiceProvider, configuration, logger, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
