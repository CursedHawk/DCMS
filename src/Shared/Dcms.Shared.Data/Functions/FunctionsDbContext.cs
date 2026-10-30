using Dcms.Shared.Audit.Redaction;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Data.Audit;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Shared.Data.Functions;

public enum FunctionDeploymentStatus
{
    /// <summary>Its commit is being checked and bundled; nothing runs it yet.</summary>
    Building,

    /// <summary>The build refused it; <see cref="FunctionDeployment.Diagnostics"/> say why.</summary>
    Failed,

    /// <summary>
    /// Built and waiting for someone to deploy it: a push proves nothing about who pushed, so a
    /// person holding what the functions use makes it live (definer's rights).
    /// </summary>
    Ready,

    /// <summary>What runs. At most one per instance and environment.</summary>
    Active,

    /// <summary>A later deployment took over.</summary>
    Superseded,

    /// <summary>An active deployment a rollback moved away from.</summary>
    RolledBack,
}

/// <summary>
/// One build of a Functions instance's repository at one commit (ADR 0029): what the build made
/// of it (<see cref="Manifest"/>, <see cref="Bundles"/>) and the permissions the person who
/// deployed it held (<see cref="VouchedPermissions"/>). The build is frozen once it leaves
/// <see cref="FunctionDeploymentStatus.Building"/>, the vouched permissions once it leaves
/// <see cref="FunctionDeploymentStatus.Ready"/>; a rollback activates an older row, never
/// rewrites one.
/// </summary>
public sealed class FunctionDeployment : EnvironmentEntity
{
    public Guid InstanceId { get; set; }

    /// <summary>Per instance and environment, never reused.</summary>
    public int Number { get; set; }

    /// <summary>The commit on the repository's release branch this was built from.</summary>
    public string CommitSha { get; set; } = string.Empty;

    public FunctionDeploymentStatus Status { get; set; } = FunctionDeploymentStatus.Building;

    /// <summary>Every function's name, runtime, triggers, access, uses and limits, as the build described them.</summary>
    [AuditIgnore]
    public string Manifest { get; set; } = "{}";

    /// <summary>One bundled module per function, keyed by function name (JSON object of strings).</summary>
    [AuditIgnore]
    public string Bundles { get; set; } = "{}";

    /// <summary>
    /// The permissions everything in <see cref="Manifest"/> needs that the deployer held when it
    /// went live. A function's call proceeds only if its permission is here (definer's rights,
    /// as Dynamic Apps revisions did for flows).
    /// </summary>
    public List<string> VouchedPermissions { get; set; } = [];

    /// <summary>Why the build failed, in a sentence, for lists.</summary>
    public string? Error { get; set; }

    /// <summary>The build's problems, each with its file and line (JSON array), for the editor.</summary>
    [AuditIgnore]
    public string? Diagnostics { get; set; }

    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ActivatedAt { get; set; }

    /// <summary>Who last made it live: who vouched, or who rolled back to it.</summary>
    public string? ActivatedBy { get; set; }
}

public enum FunctionRunStatus
{
    /// <summary>Waiting for a worker, or for its next attempt.</summary>
    Pending,

    /// <summary>A worker holds it until its lease ends.</summary>
    Running,

    Succeeded,

    /// <summary>Failed on its last attempt; <see cref="FunctionRun.Error"/> says why.</summary>
    Failed,

    /// <summary>Never ran: a cascade limit, or its instance was switched off.</summary>
    Terminated,
}

/// <summary>
/// One run of one function (ADR 0029): the queue for runs a trigger started (rows, messages,
/// schedules, platform events) and the history of every run, however started. Unique per
/// (instance, trigger event, function): a redelivered event or a second replica's schedule tick
/// finds its run already there.
/// </summary>
// The queue's mechanics: each run is audited once, as plugin.functions.run.executed, not per state change.
[AuditIgnore]
public sealed class FunctionRun : EnvironmentEntity
{
    public Guid InstanceId { get; set; }
    public string Function { get; set; } = string.Empty;

    /// <summary>The deployment it ran, or will: the live one when it was queued.</summary>
    public Guid DeploymentId { get; set; }
    public int DeploymentNumber { get; set; }

    /// <summary><c>row</c>, <c>relation</c>, <c>message</c>, <c>schedule</c>, <c>event</c>, <c>job</c>, <c>manual</c>, <c>http</c>.</summary>
    public string TriggerKind { get; set; } = string.Empty;
    public Guid TriggerEventId { get; set; }

    /// <summary>What the function receives as input (JSON).</summary>
    [AuditIgnore]
    public string? Input { get; set; }

    /// <summary>The trigger's own detail, for <c>ctx.trigger</c> (JSON).</summary>
    [AuditIgnore]
    public string? TriggerDetail { get; set; }

    public FunctionRunStatus Status { get; set; } = FunctionRunStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LeaseUntil { get; set; }

    /// <summary>The original action every run it set off shares; the cascade limits count by it.</summary>
    public Guid CorrelationId { get; set; }
    public Guid? CausationId { get; set; }
    public int Depth { get; set; }

    [AuditIgnore]
    public string? Result { get; set; }
    public string? Error { get; set; }

    /// <summary>What the function logged, capped. Tenant data: shown to holders of the logs permission only.</summary>
    [AuditIgnore]
    public string? Logs { get; set; }
    public int Calls { get; set; }
    public long DurationMs { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>
/// A key outside callers present to invoke an instance's HTTP functions (<c>X-Api-Key</c>), in
/// one environment. Only its hash is kept: the key itself is shown once, when it is made.
/// </summary>
[Audited("function_api_key", LabelProperty = nameof(Name))]
public sealed class FunctionApiKey : EnvironmentEntity
{
    public Guid InstanceId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>The key's first characters, to find it by and to recognise it in a list.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>SHA-256 of the whole key, hex.</summary>
    [AuditSensitive]
    public string Hash { get; set; } = string.Empty;

    /// <summary>The functions it may invoke; empty for every HTTP function that accepts keys.</summary>
    public List<string> Functions { get; set; } = [];

    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>Owns the "functions" schema: the Functions plugin's deployments and runs (ADR 0029).</summary>
public class FunctionsDbContext(DbContextOptions<FunctionsDbContext> options) : ScopedDbContext(options)
{
    public const string Schema = "functions";

    // An enclosing RlsScope.Tenant wins: runs and builds happen with no request.
    private Guid CurrentTenantId => RlsScope.TenantOverride ?? TenantContext?.TenantId ?? Guid.Empty;

    public DbSet<FunctionDeployment> Deployments => Set<FunctionDeployment>();
    public DbSet<FunctionRun> Runs => Set<FunctionRun>();
    public DbSet<FunctionApiKey> ApiKeys => Set<FunctionApiKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.MapAuditOutbox();
        builder.HasDefaultSchema(Schema);

        builder.Entity<FunctionDeployment>(e =>
        {
            e.ToTable("deployments");
            e.HasKey(d => d.Id);
            e.Property(d => d.CommitSha).HasMaxLength(64).IsRequired();
            e.Property(d => d.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(d => d.Manifest).HasColumnType("jsonb");
            e.Property(d => d.Bundles).HasColumnType("jsonb");
            e.Property(d => d.Error).HasMaxLength(4000);
            e.Property(d => d.Diagnostics).HasColumnType("jsonb");
            e.Property(d => d.CreatedBy).HasMaxLength(128);
            e.Property(d => d.ActivatedBy).HasMaxLength(128);
            e.HasIndex(d => new { d.TenantId, d.EnvironmentId, d.InstanceId, d.Number }).IsUnique();
            // One live deployment per instance and environment.
            e.HasIndex(d => new { d.TenantId, d.EnvironmentId, d.InstanceId })
                .IsUnique()
                .HasFilter("\"Status\" = 'Active'")
                .HasDatabaseName("IX_deployments_active");
            e.HasQueryFilter(QueryFilters.Tenant, d => d.TenantId == CurrentTenantId)
                .HasQueryFilter(QueryFilters.Environment, d => CurrentEnvironmentId == null || d.EnvironmentId == CurrentEnvironmentId);
        });

        builder.Entity<FunctionApiKey>(e =>
        {
            e.ToTable("api_keys");
            e.HasKey(k => k.Id);
            e.Property(k => k.Name).HasMaxLength(100).IsRequired();
            e.Property(k => k.Prefix).HasMaxLength(16).IsRequired();
            e.Property(k => k.Hash).HasMaxLength(64).IsRequired();
            e.Property(k => k.CreatedBy).HasMaxLength(128);
            e.HasIndex(k => k.Prefix).IsUnique();
            e.HasIndex(k => new { k.TenantId, k.EnvironmentId, k.InstanceId });
            e.HasQueryFilter(QueryFilters.Tenant, k => k.TenantId == CurrentTenantId)
                .HasQueryFilter(QueryFilters.Environment, k => CurrentEnvironmentId == null || k.EnvironmentId == CurrentEnvironmentId);
        });

        builder.Entity<FunctionRun>(e =>
        {
            e.ToTable("runs");
            e.HasKey(r => r.Id);
            e.Property(r => r.Function).HasMaxLength(64).IsRequired();
            e.Property(r => r.TriggerKind).HasMaxLength(16).IsRequired();
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(r => r.Input).HasColumnType("jsonb");
            e.Property(r => r.TriggerDetail).HasColumnType("jsonb");
            e.Property(r => r.Result).HasColumnType("jsonb");
            e.Property(r => r.Error).HasMaxLength(4000);
            e.HasIndex(r => new { r.TenantId, r.EnvironmentId, r.InstanceId, r.TriggerEventId, r.Function }).IsUnique();
            e.HasIndex(r => new { r.TenantId, r.EnvironmentId, r.InstanceId, r.CreatedAt });
            e.HasIndex(r => r.CorrelationId);
            // The worker's claim: what is due, across tenants.
            e.HasIndex(r => new { r.Status, r.NextAttemptAt }).HasFilter("\"Status\" IN ('Pending', 'Running')");
            e.HasQueryFilter(QueryFilters.Tenant, r => r.TenantId == CurrentTenantId)
                .HasQueryFilter(QueryFilters.Environment, r => CurrentEnvironmentId == null || r.EnvironmentId == CurrentEnvironmentId);
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        Stamp();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        Stamp();
        return base.SaveChanges();
    }

    private void Stamp()
    {
        var tenantId = CurrentTenantId;
        if (tenantId == Guid.Empty)
        {
            return;
        }
        foreach (var entry in ChangeTracker.Entries<TenantEntity>())
        {
            if (entry.State == EntityState.Added && entry.Entity.TenantId == Guid.Empty)
            {
                entry.Entity.TenantId = tenantId;
            }
        }
    }
}

public sealed class FunctionsDbContextFactory : IDesignTimeDbContextFactory<FunctionsDbContext>
{
    public FunctionsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<FunctionsDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", FunctionsDbContext.Schema))
            .Options;
        return new FunctionsDbContext(options).WithTenant(new NullTenantContext());
    }

    private sealed class NullTenantContext : ITenantContext
    {
        public Guid? TenantId => null;
        public string? TenantSlug => null;
    }
}

public static class FunctionsServiceCollectionExtensions
{
    public static IServiceCollection AddDcmsFunctionsData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
                               ?? "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev";

        services.AddDcmsDbContext<FunctionsDbContext>(connectionString, FunctionsDbContext.Schema);
        return services;
    }
}
