using Dcms.Shared.Audit.Redaction;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Data.Audit;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Shared.Data.DynamicApps;

/// <summary>
/// One Dynamic Apps plugin instance's application: the pointers to its open draft and its
/// published configuration revision (ADR 0021). Exactly one per instance.
/// </summary>
public sealed class DynamicApp : TenantEntity
{
    public Guid InstanceId { get; set; }

    /// <summary>The single shared draft, when one is open.</summary>
    public Guid? DraftRevisionId { get; set; }

    /// <summary>What the runtime, the public API and OpenAPI serve. Null until the first publish.</summary>
    public Guid? PublishedRevisionId { get; set; }

    /// <summary>The last revision number handed out; numbers are per app and never reused.</summary>
    public int LastRevisionNumber { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum AppRevisionStatus
{
    Draft,
    Published,
    Superseded,

    /// <summary>A published revision a rollback moved away from.</summary>
    RolledBack,

    /// <summary>A draft thrown away unpublished.</summary>
    Discarded,
}

public enum AppChangeSource
{
    Human,
    Ai,
    System,
    Import,
}

/// <summary>
/// The whole application configuration at one point in time, as one canonical JSON document
/// (<see cref="Snapshot"/>) and its SHA-256 (<see cref="Hash"/>). A draft is edited in place
/// under an expected-hash check; once published it never changes again. A rollback is a new
/// revision copied from an older snapshot, never a step backwards.
/// </summary>
public sealed class AppRevision : TenantEntity
{
    public Guid AppId { get; set; }
    public int Number { get; set; }

    /// <summary>The revision this one was copied from (the published one, or the rollback target).</summary>
    public Guid? ParentId { get; set; }

    /// <summary>The published revision current when this draft was opened; publishing requires it still is.</summary>
    public Guid? BasePublishedId { get; set; }

    public AppRevisionStatus Status { get; set; } = AppRevisionStatus.Draft;
    public AppChangeSource Source { get; set; } = AppChangeSource.Human;

    // Field names only would say nothing; the whole document would bury the record. What
    // changed is in the change rows and the revision.* audit records.
    [AuditIgnore]
    public string Snapshot { get; set; } = "{}";

    /// <summary>Lowercase hex SHA-256 of the canonical snapshot; the optimistic-concurrency token.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>Version of the snapshot's shape, for reading old revisions after it evolves.</summary>
    public int SchemaVersion { get; set; } = 1;

    public string? Description { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the snapshot with <see cref="ValidatedHash"/> last passed validation.</summary>
    public DateTimeOffset? ValidatedAt { get; set; }
    public string? ValidatedHash { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }
    public string? PublishedBy { get; set; }

    /// <summary>Opaque links to the assistant conversation and run that opened this revision.</summary>
    public Guid? SourceConversationId { get; set; }
    public Guid? SourceAiRunId { get; set; }
}

/// <summary>
/// One logical edit applied to a revision — "+ field deals.amount" — in order. Append-only.
/// What the revisions screen and the assistant show as the change set; the revision itself
/// stays the source of truth.
/// </summary>
// The log is a record of audited actions (configuration.change.applied); auditing its rows
// as well would record every edit twice.
[AuditIgnore]
public sealed class AppChange : TenantEntity
{
    public Guid RevisionId { get; set; }
    public int Seq { get; set; }

    /// <summary>create, update or delete.</summary>
    public string Op { get; set; } = string.Empty;

    /// <summary>table, field, relationship, index, view, choiceSet, flow, security, settings.</summary>
    public string ResourceType { get; set; } = string.Empty;
    public Guid? ResourceId { get; set; }

    /// <summary>Human-readable address, e.g. <c>deals.amount</c>.</summary>
    public string Path { get; set; } = string.Empty;

    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }

    public AppChangeSource ActorType { get; set; } = AppChangeSource.Human;
    public string? ActorId { get; set; }
    public Guid? AiConversationId { get; set; }
    public Guid? AiRunId { get; set; }
    public string? ToolCallId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Owns the "apps" schema: the Dynamic Apps plugin's applications (ADR 0021).</summary>
public class AppsDbContext(DbContextOptions<AppsDbContext> options, ITenantContext tenantContext) : DbContext(options)
{
    public const string Schema = "apps";

    // An enclosing RlsScope.Tenant wins: flows and the outbox dispatcher run with no request.
    private Guid CurrentTenantId => RlsScope.TenantOverride ?? tenantContext.TenantId ?? Guid.Empty;

    public DbSet<DynamicApp> Apps => Set<DynamicApp>();
    public DbSet<AppRevision> Revisions => Set<AppRevision>();
    public DbSet<AppChange> Changes => Set<AppChange>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.MapAuditOutbox();
        builder.HasDefaultSchema(Schema);

        builder.Entity<DynamicApp>(e =>
        {
            e.ToTable("apps");
            e.HasKey(a => a.Id);
            e.HasIndex(a => new { a.TenantId, a.InstanceId }).IsUnique();
            e.HasQueryFilter(a => a.TenantId == CurrentTenantId);
        });

        builder.Entity<AppRevision>(e =>
        {
            e.ToTable("revisions");
            e.HasKey(r => r.Id);
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(r => r.Source).HasConversion<string>().HasMaxLength(16);
            e.Property(r => r.Snapshot).HasColumnType("jsonb");
            e.Property(r => r.Hash).HasMaxLength(64).IsRequired();
            e.Property(r => r.ValidatedHash).HasMaxLength(64);
            e.Property(r => r.Description).HasMaxLength(2000);
            e.Property(r => r.CreatedBy).HasMaxLength(128);
            e.Property(r => r.PublishedBy).HasMaxLength(128);
            e.HasIndex(r => new { r.TenantId, r.AppId, r.Number }).IsUnique();
            e.HasQueryFilter(r => r.TenantId == CurrentTenantId);
        });

        builder.Entity<AppChange>(e =>
        {
            e.ToTable("changes");
            e.HasKey(c => c.Id);
            e.Property(c => c.Op).HasMaxLength(16);
            e.Property(c => c.ResourceType).HasMaxLength(32);
            e.Property(c => c.Path).HasMaxLength(256);
            e.Property(c => c.BeforeJson).HasColumnType("jsonb");
            e.Property(c => c.AfterJson).HasColumnType("jsonb");
            e.Property(c => c.ActorType).HasConversion<string>().HasMaxLength(16);
            e.Property(c => c.ActorId).HasMaxLength(128);
            e.Property(c => c.ToolCallId).HasMaxLength(128);
            e.HasIndex(c => new { c.TenantId, c.RevisionId, c.Seq }).IsUnique();
            e.HasQueryFilter(c => c.TenantId == CurrentTenantId);
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

public sealed class AppsDbContextFactory : IDesignTimeDbContextFactory<AppsDbContext>
{
    public AppsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppsDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", AppsDbContext.Schema))
            .Options;
        return new AppsDbContext(options, new NullTenantContext());
    }

    private sealed class NullTenantContext : ITenantContext
    {
        public Guid? TenantId => null;
        public string? TenantSlug => null;
    }
}

public static class AppsServiceCollectionExtensions
{
    public static IServiceCollection AddDcmsAppsData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
                               ?? "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev";

        services.AddDbContext<AppsDbContext>((sp, options) =>
            options.UseNpgsql(connectionString, npgsql =>
                    npgsql.MigrationsHistoryTable("__ef_migrations_history", AppsDbContext.Schema))
                .UseDcmsAuditInterceptors(sp));
        return services;
    }
}
