using Dcms.Shared.Audit.Redaction;
using Dcms.Shared.Data.Audit;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Shared.Data.Jobs;

public enum JobStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Canceled = 4,
}

/// <summary>
/// One unit of background work, tenant or platform, and how far along it is (ADR 0024).
///
/// <para><b>The ledger, not the queue.</b> Where the work actually waits depends on the kind:
/// <c>JOBS</c> for the generic workers, <c>MEDIA</c>/<c>SITES</c> for theirs, <c>apps.flow_runs</c>
/// for Dynamic Apps. This row is what every one of them reports into, so a person can see all of
/// it in one place.</para>
///
/// <para><b>A null <see cref="TenantId"/> is a platform job.</b> Both RLS policies already do the
/// right thing with it: <c>tenant_isolation</c> compares for equality, which NULL never satisfies,
/// and <c>platform_scope</c> admits it. The EF filter says the same explicitly, because a
/// tenant-less context comparing <c>TenantId == null</c> would otherwise match exactly the platform
/// rows. A sentinel <c>Guid.Empty</c> would have made that very mistake the default.</para>
///
/// <para>Not audited row by row: progress rewrites the row every second while work runs. The
/// actions behind a job — the import, the publish, a cancel — are audited where they happen.</para>
/// </summary>
[AuditIgnore]
public sealed class Job
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }

    /// <summary>A job made of jobs (a Drive import of twelve files). Progress rolls up at read time.</summary>
    public Guid? ParentId { get; set; }

    /// <summary>Stable discriminator, e.g. <c>media.drive-import</c>. Chooses the handler, the i18n keys and the icon.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary><c>tenant</c> or <c>platform</c>: which worker pool's queue the run message went to.</summary>
    public string Lane { get; set; } = JobLanes.Tenant;

    /// <summary>Who enqueued it: <c>admin-api</c>, <c>plugin:{id}</c>, …</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>i18n key; never prose, for the reason notifications give (ADR 0009).</summary>
    public string TitleKey { get; set; } = string.Empty;

    public string ParamsJson { get; set; } = "{}";

    /// <summary>
    /// The permission that gates the feature this job belongs to. A member who holds it sees the
    /// job; so does whoever started it. Null: every member of the tenant may see it.
    /// </summary>
    public string? RequiredPermission { get; set; }

    /// <summary>SPA route to the thing the job produces or changes, when there is one.</summary>
    public string? LinkPath { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Queued;

    public long Done { get; set; }

    /// <summary>Null while the amount of work is unknown; the UI draws an indeterminate ring.</summary>
    public long? Total { get; set; }

    public string? StageKey { get; set; }

    public string? ErrorKey { get; set; }
    public string? ErrorDetail { get; set; }

    /// <summary>The handler's input, kept so a failed job can be retried as it was. Secrets inside are encrypted.</summary>
    public string? PayloadJson { get; set; }

    public string? ResultJson { get; set; }

    /// <summary>The audit/actor context of the request that enqueued it, restored when the job runs.</summary>
    public string? ContextJson { get; set; }

    public bool Cancelable { get; set; } = true;
    public DateTimeOffset? CancelRequestedAt { get; set; }

    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 3;

    /// <summary>While Running: the worker holding it must renew before this, or another may reclaim it.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    /// <summary>
    /// Bumped on every write. Reports that arrive over the bus carry the sequence they were sent
    /// at, so a late progress message can never overwrite a later state.
    /// </summary>
    public long Seq { get; set; }

    /// <summary>Identifies the occurrence: a second enqueue with the same key returns the first job.</summary>
    public string? DedupeKey { get; set; }

    public Guid? ActorUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsFinished => Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Canceled;
}

public static class JobLanes
{
    /// <summary>Tenant work: jobs-worker.</summary>
    public const string Tenant = "tenant";

    /// <summary>Work with no tenant: platform-worker.</summary>
    public const string Platform = "platform";
}

/// <summary>Owns the "jobs" schema (ADR 0024).</summary>
public class JobsDbContext(DbContextOptions<JobsDbContext> options, ITenantContext tenantContext)
    : DbContext(options)
{
    public const string Schema = "jobs";

    private Guid? CurrentTenantId => tenantContext.TenantId;

    public DbSet<Job> Jobs => Set<Job>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.MapAuditOutbox();
        builder.HasDefaultSchema(Schema);

        builder.Entity<Job>(e =>
        {
            e.ToTable("jobs");
            e.HasKey(j => j.Id);
            e.Property(j => j.Kind).HasMaxLength(128).IsRequired();
            e.Property(j => j.Lane).HasMaxLength(32).IsRequired();
            e.Property(j => j.Source).HasMaxLength(128).IsRequired();
            e.Property(j => j.TitleKey).HasMaxLength(256).IsRequired();
            e.Property(j => j.ParamsJson).HasColumnType("jsonb").IsRequired();
            e.Property(j => j.RequiredPermission).HasMaxLength(256);
            e.Property(j => j.LinkPath).HasMaxLength(512);
            e.Property(j => j.Status).HasConversion<int>();
            e.Property(j => j.StageKey).HasMaxLength(128);
            e.Property(j => j.ErrorKey).HasMaxLength(256);
            e.Property(j => j.ErrorDetail).HasMaxLength(4000);
            e.Property(j => j.PayloadJson).HasColumnType("jsonb");
            e.Property(j => j.ResultJson).HasColumnType("jsonb");
            e.Property(j => j.ContextJson).HasColumnType("jsonb");
            e.Property(j => j.DedupeKey).HasMaxLength(256);
            e.Ignore(j => j.IsFinished);

            // The enqueue idempotency guard: insert optimistically, treat 23505 as "this is the
            // job you already asked for". NULLS NOT DISTINCT so platform jobs (no tenant) collide
            // with each other; filtered so the many jobs without a key never collide at all.
            e.HasIndex(j => new { j.TenantId, j.DedupeKey })
                .IsUnique()
                .AreNullsDistinct(false)
                .HasFilter("\"DedupeKey\" IS NOT NULL");

            // The list: a tenant's jobs, active first, newest first.
            e.HasIndex(j => new { j.TenantId, j.Status, j.CreatedAt });
            e.HasIndex(j => j.ParentId);

            // The sweeps: stale Queued to redispatch, lapsed leases, finished to retire.
            e.HasIndex(j => new { j.Status, j.UpdatedAt });

            // Tenant-less contexts see nothing, rather than the platform rows; see Job.
            e.HasQueryFilter(j => j.TenantId != null && j.TenantId == CurrentTenantId);
        });
    }
}

public sealed class JobsDbContextFactory : IDesignTimeDbContextFactory<JobsDbContext>
{
    public JobsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<JobsDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", JobsDbContext.Schema))
            .Options;
        return new JobsDbContext(options, new NullTenantContext());
    }

    private sealed class NullTenantContext : ITenantContext
    {
        public Guid? TenantId => null;
        public string? TenantSlug => null;
    }
}

public static class JobsServiceCollectionExtensions
{
    public static IServiceCollection AddDcmsJobsData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
                               ?? "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev";

        services.AddDbContext<JobsDbContext>((sp, options) =>
            options.UseNpgsql(connectionString, npgsql =>
                    npgsql.MigrationsHistoryTable("__ef_migrations_history", JobsDbContext.Schema))
                .UseDcmsAuditInterceptors(sp));

        return services;
    }
}
