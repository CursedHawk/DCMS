using Microsoft.EntityFrameworkCore;

namespace Dcms.Shared.Data.Audit;

/// <summary>
/// Owns the "audit" schema. Unlike every other context here it applies <b>no tenant query
/// filter and no tenant stamping</b>: the chain writer and the verifier are background
/// processes with no ambient tenant, and a filter would hide from them exactly the rows they
/// exist to read. The read plane scopes to a tenant explicitly, in the query, where the
/// scoping is visible to a reviewer.
///
/// Migrated by admin-api along with every other context (ADR 0003). Written by the outbox
/// sink from any service that owns the schema, and drained by AuditChainWriter in admin-api.
/// </summary>
public class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options)
{
    public const string Schema = "audit";

    /// <summary>
    /// Created by raw SQL in the initial migration rather than by EF: the table is
    /// <c>PARTITION BY RANGE ("OccurredAt")</c>, which MigrationBuilder cannot express.
    /// </summary>
    public DbSet<AuditEventRow> Events => Set<AuditEventRow>();

    public DbSet<AuditChainHead> ChainHeads => Set<AuditChainHead>();
    public DbSet<AuditChainAnchor> ChainAnchors => Set<AuditChainAnchor>();
    public DbSet<AuditOutboxMessage> Outbox => Set<AuditOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);

        builder.Entity<AuditEventRow>(e =>
        {
            // Excluded from migrations because the CREATE TABLE is hand-written (partitioning);
            // the model still has to describe it so the writer and read plane can use EF.
            e.ToTable("audit_events", Schema, t => t.ExcludeFromMigrations());

            // Composite key: a partitioned table's constraints must include the partition key.
            e.HasKey(a => new { a.OccurredAt, a.Id });

            e.Property(a => a.Action).HasMaxLength(96).IsRequired();
            e.Property(a => a.Category).HasMaxLength(24).IsRequired();
            e.Property(a => a.Outcome).HasMaxLength(16).IsRequired();
            e.Property(a => a.ActorKind).HasMaxLength(24).IsRequired();
            e.Property(a => a.ActorRef).HasMaxLength(200);
            e.Property(a => a.ActorDisplay).HasMaxLength(200);
            e.Property(a => a.ActorAttribution).HasMaxLength(16).IsRequired();
            e.Property(a => a.ResourceType).HasMaxLength(64);
            e.Property(a => a.ResourceId).HasMaxLength(128);
            e.Property(a => a.ResourceLabel).HasMaxLength(256);
            e.Property(a => a.ServiceName).HasMaxLength(32).IsRequired();
            e.Property(a => a.ServiceInstance).HasMaxLength(64).IsRequired();
            e.Property(a => a.CorrelationId).HasMaxLength(64);
            e.Property(a => a.TraceId).HasMaxLength(32);
            e.Property(a => a.SpanId).HasMaxLength(16);
            e.Property(a => a.HttpMethod).HasMaxLength(8);
            e.Property(a => a.RoutePattern).HasMaxLength(256);
            e.Property(a => a.UserAgent).HasMaxLength(512);
            // varchar rather than inet: Npgsql maps inet to IPAddress, which would push a
            // parsed type through the contract, the canonicaliser and the JSON payload for a
            // subnet query nobody has yet. 45 chars covers IPv6 with a zone id.
            e.Property(a => a.IpAddress).HasMaxLength(45);
            // text, deliberately, and NOT jsonb — these two columns are covered by the chain
            // hash. jsonb is a parsed representation: Postgres reorders object keys by length,
            // rewrites separators and normalises numbers, so what comes back out is never the
            // byte string that went in. Hashing the text we wrote and then verifying against
            // Postgres's reformatting of it makes every record fail verification, reported as
            // "this row was altered after it was written" — an accusation of tampering raised
            // by a storage detail.
            //
            // Nothing queries inside these payloads (no GIN index, by design), and the read
            // plane hands the stored text to the browser verbatim, so jsonb bought nothing
            // here and cost the entire integrity guarantee.
            e.Property(a => a.MetadataJson).HasColumnType("text").IsRequired();
            e.Property(a => a.ChangesJson).HasColumnType("text");
            e.Property(a => a.Period).HasColumnType("date");
        });

        builder.Entity<AuditChainHead>(e =>
        {
            e.ToTable("chain_heads", Schema);
            e.HasKey(c => new { c.ChainKey, c.Period });
            e.Property(c => c.Period).HasColumnType("date");
            // Not tenant-filtered: the writer walks every tenant's chain.
        });

        builder.Entity<AuditChainAnchor>(e =>
        {
            e.ToTable("chain_anchors", Schema);
            e.HasKey(c => new { c.ChainKey, c.Period });
            e.Property(c => c.Period).HasColumnType("date");
        });

        builder.Entity<AuditOutboxMessage>(e =>
        {
            e.ToTable("audit_outbox", Schema);
            e.HasKey(o => o.Id);
            e.Property(o => o.Id).ValueGeneratedOnAdd();
            e.Property(o => o.PayloadJson).HasColumnType("jsonb").IsRequired();
            e.HasIndex(o => o.EventId).IsUnique();
            e.HasIndex(o => o.Id)
                .HasFilter("\"AppliedAt\" IS NULL")
                .HasDatabaseName("IX_audit_outbox_pending");
            // Not tenant-filtered: the drain is a cross-tenant scan, exactly like content_outbox.
        });
    }
}
