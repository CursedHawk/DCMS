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

    /// <summary>The build refused it; <see cref="FunctionDeployment.Error"/> says why.</summary>
    Failed,

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
/// deployed it held (<see cref="VouchedPermissions"/>). Frozen once it leaves
/// <see cref="FunctionDeploymentStatus.Building"/>; a rollback activates an older row, never
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

    /// <summary>Why the build failed, for the console.</summary>
    public string? Error { get; set; }

    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ActivatedAt { get; set; }
}

/// <summary>Owns the "functions" schema: the Functions plugin's deployments and runs (ADR 0029).</summary>
public class FunctionsDbContext(DbContextOptions<FunctionsDbContext> options) : ScopedDbContext(options)
{
    public const string Schema = "functions";

    // An enclosing RlsScope.Tenant wins: runs and builds happen with no request.
    private Guid CurrentTenantId => RlsScope.TenantOverride ?? TenantContext?.TenantId ?? Guid.Empty;

    public DbSet<FunctionDeployment> Deployments => Set<FunctionDeployment>();

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
            e.Property(d => d.CreatedBy).HasMaxLength(128);
            e.HasIndex(d => new { d.TenantId, d.EnvironmentId, d.InstanceId, d.Number }).IsUnique();
            // One live deployment per instance and environment.
            e.HasIndex(d => new { d.TenantId, d.EnvironmentId, d.InstanceId })
                .IsUnique()
                .HasFilter("\"Status\" = 'Active'")
                .HasDatabaseName("IX_deployments_active");
            e.HasQueryFilter(QueryFilters.Tenant, d => d.TenantId == CurrentTenantId)
                .HasQueryFilter(QueryFilters.Environment, d => CurrentEnvironmentId == null || d.EnvironmentId == CurrentEnvironmentId);
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
