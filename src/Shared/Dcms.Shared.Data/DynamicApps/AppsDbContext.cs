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

/// <summary>
/// One record of a tenant-defined table. Values live in <see cref="Data"/> keyed by field id
/// (lookups by relationship id), never by api name: a deleted field's values can then never
/// resurface under a new field that reuses its name. Read through the published model only.
/// </summary>
public sealed class AppRecord : TenantEntity
{
    public Guid InstanceId { get; set; }
    public Guid TableId { get; set; }

    /// <summary>Bumped on every write; the optimistic-concurrency token.</summary>
    public int Version { get; set; } = 1;

    // Field names only would say "Data changed" on every edit; the record's own audit entry
    // names the record, and its values are the tenant's business data.
    [AuditIgnore]
    public string Data { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }

    /// <summary>The site visitor who created it from the public site, for "own records" access.</summary>
    public Guid? OwnerVisitorId { get; set; }
}

/// <summary>One link of a many-to-many relationship.</summary>
public sealed class AppRelationLink : TenantEntity
{
    public Guid InstanceId { get; set; }
    public Guid RelationshipId { get; set; }
    public Guid SourceId { get; set; }
    public Guid TargetId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A value that must be unique, claimed by a record: a unique field's value, a unique index's
/// tuple, a one-to-one lookup's target. The database's unique index is what refuses a second
/// claim, so two concurrent writers cannot both win.
/// </summary>
[AuditIgnore]
public sealed class AppUniqueKey : TenantEntity
{
    public Guid InstanceId { get; set; }

    /// <summary>The field, index or relationship the constraint belongs to.</summary>
    public Guid ConstraintId { get; set; }

    public Guid RecordId { get; set; }

    /// <summary>The normalized value, or a SHA-256 of it when long.</summary>
    public string Key { get; set; } = string.Empty;
}

/// <summary>
/// A runtime event (row created, record linked, revision published…) waiting to be routed to
/// the application's automations. Written in the transaction of the change it describes, so an
/// event exists exactly when its change does; drained by the plugin's dispatcher.
/// </summary>
// Transport for changes the same transaction already audits.
[AuditIgnore]
public sealed class AppOutboxMessage : TenantEntity
{
    public Guid InstanceId { get; set; }
    public string EventName { get; set; } = string.Empty;

    /// <summary>The whole <c>AppEvent</c> envelope.</summary>
    public string Envelope { get; set; } = "{}";

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }

    /// <summary>The audit context of the request that wrote it, so what it sets off still names that person.</summary>
    public string? ContextJson { get; set; }
}

public enum FlowRunStatus
{
    /// <summary>Waiting for a worker, or for its next retry.</summary>
    Pending,
    Running,
    Succeeded,

    /// <summary>The condition was false; nothing ran.</summary>
    Skipped,

    /// <summary>A step failed and its retries are spent.</summary>
    Failed,

    /// <summary>Stopped by a limit (depth, steps, writes, duration, cascade) or by hand.</summary>
    Terminated,
}

/// <summary>
/// One execution of a flow (ADR 0021), the source of truth for its state: the database is the
/// queue — a worker claims a pending run with a lease — and the history. Pinned to the revision
/// and flow hash it was created for, so a publish never changes what a queued run does.
/// </summary>
public sealed class FlowRun : TenantEntity
{
    public Guid InstanceId { get; set; }
    public Guid FlowId { get; set; }
    public string FlowApiName { get; set; } = string.Empty;
    public int Revision { get; set; }
    public string FlowHash { get; set; } = string.Empty;

    /// <summary>The event that started it; with the flow and its hash, what makes a delivery idempotent.</summary>
    public Guid TriggerEventId { get; set; }

    // The trigger and run data hold tenant records; the run's own audit entry names it.
    [AuditIgnore]
    public string TriggerJson { get; set; } = "{}";

    public FlowRunStatus Status { get; set; } = FlowRunStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>While running: when the claiming worker's lease ends and another may take the run over.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    public Guid CorrelationId { get; set; }
    public Guid? CausationId { get; set; }
    public int Depth { get; set; }
    public int Writes { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>One attempt at one step of a run: what went in, what came out, how long it took.</summary>
[AuditIgnore]
public sealed class FlowRunStep : TenantEntity
{
    public Guid RunId { get; set; }
    public string StepId { get; set; } = string.Empty;
    public int Attempt { get; set; }
    public string Action { get; set; } = string.Empty;
    public FlowRunStatus Status { get; set; }
    public string? InputJson { get; set; }
    public string? OutputJson { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>A scheduled flow's next due time; rewritten from the configuration at every publish.</summary>
public sealed class FlowSchedule : TenantEntity
{
    public Guid InstanceId { get; set; }
    public Guid FlowId { get; set; }
    public int EveryMinutes { get; set; }
    public DateTimeOffset NextRunAt { get; set; }
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
    public DbSet<AppRecord> Records => Set<AppRecord>();
    public DbSet<AppRelationLink> RelationLinks => Set<AppRelationLink>();
    public DbSet<AppUniqueKey> UniqueKeys => Set<AppUniqueKey>();
    public DbSet<AppOutboxMessage> Outbox => Set<AppOutboxMessage>();
    public DbSet<FlowRun> FlowRuns => Set<FlowRun>();
    public DbSet<FlowRunStep> FlowRunSteps => Set<FlowRunStep>();
    public DbSet<FlowSchedule> FlowSchedules => Set<FlowSchedule>();

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

        builder.Entity<AppRecord>(e =>
        {
            e.ToTable("records");
            e.HasKey(r => r.Id);
            e.Property(r => r.Data).HasColumnType("jsonb");
            e.Property(r => r.Version).IsConcurrencyToken();
            e.Property(r => r.CreatedBy).HasMaxLength(128);
            e.Property(r => r.UpdatedBy).HasMaxLength(128);
            e.HasIndex(r => new { r.TenantId, r.InstanceId, r.TableId, r.CreatedAt });
            // Lookups and filters by containment: Data @> {"<relationshipId>": "<targetId>"}.
            e.HasIndex(r => r.Data).HasMethod("gin").HasOperators("jsonb_path_ops");
            e.HasQueryFilter(r => r.TenantId == CurrentTenantId);
        });

        builder.Entity<AppRelationLink>(e =>
        {
            e.ToTable("relation_links");
            e.HasKey(l => l.Id);
            e.HasIndex(l => new { l.TenantId, l.InstanceId, l.RelationshipId, l.SourceId, l.TargetId }).IsUnique();
            e.HasIndex(l => new { l.TenantId, l.InstanceId, l.RelationshipId, l.TargetId });
            e.HasQueryFilter(l => l.TenantId == CurrentTenantId);
        });

        builder.Entity<AppOutboxMessage>(e =>
        {
            // Not tenant-filtered: the dispatcher drains every tenant's events, and each carries
            // its own tenant. RlsConfigurator.ExemptTables says so too.
            e.ToTable("outbox");
            e.HasKey(m => m.Id);
            e.Property(m => m.EventName).HasMaxLength(128);
            e.Property(m => m.Envelope).HasColumnType("jsonb");
            e.Property(m => m.LastError).HasMaxLength(2000);
            e.HasIndex(m => m.OccurredAt).HasFilter("\"SentAt\" IS NULL");
            e.HasIndex(m => m.SentAt);
        });

        builder.Entity<FlowRun>(e =>
        {
            e.ToTable("flow_runs");
            e.HasKey(r => r.Id);
            e.Property(r => r.FlowApiName).HasMaxLength(64);
            e.Property(r => r.FlowHash).HasMaxLength(64);
            e.Property(r => r.TriggerJson).HasColumnType("jsonb");
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(r => r.Error).HasMaxLength(4000);
            e.HasIndex(r => new { r.TenantId, r.InstanceId, r.TriggerEventId, r.FlowId, r.FlowHash }).IsUnique();
            e.HasIndex(r => new { r.TenantId, r.InstanceId, r.CreatedAt });
            e.HasIndex(r => new { r.TenantId, r.CorrelationId });
            // The worker's claim: runnable runs, oldest due first, across tenants.
            e.HasIndex(r => r.NextAttemptAt).HasFilter("\"Status\" IN ('Pending', 'Running')");
            e.HasQueryFilter(r => r.TenantId == CurrentTenantId);
        });

        builder.Entity<FlowRunStep>(e =>
        {
            e.ToTable("flow_run_steps");
            e.HasKey(s => s.Id);
            e.Property(s => s.StepId).HasMaxLength(64);
            e.Property(s => s.Action).HasMaxLength(128);
            e.Property(s => s.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(s => s.InputJson).HasColumnType("jsonb");
            e.Property(s => s.OutputJson).HasColumnType("jsonb");
            e.Property(s => s.Error).HasMaxLength(4000);
            e.HasIndex(s => new { s.TenantId, s.RunId, s.StepId, s.Attempt }).IsUnique();
            e.HasQueryFilter(s => s.TenantId == CurrentTenantId);
        });

        builder.Entity<FlowSchedule>(e =>
        {
            e.ToTable("flow_schedules");
            e.HasKey(s => s.Id);
            e.HasIndex(s => new { s.TenantId, s.InstanceId, s.FlowId }).IsUnique();
            e.HasIndex(s => s.NextRunAt);
            e.HasQueryFilter(s => s.TenantId == CurrentTenantId);
        });

        builder.Entity<AppUniqueKey>(e =>
        {
            e.ToTable("unique_keys");
            e.HasKey(k => k.Id);
            e.Property(k => k.Key).HasMaxLength(256);
            e.HasIndex(k => new { k.TenantId, k.InstanceId, k.ConstraintId, k.Key }).IsUnique();
            e.HasIndex(k => new { k.TenantId, k.RecordId });
            e.HasQueryFilter(k => k.TenantId == CurrentTenantId);
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
