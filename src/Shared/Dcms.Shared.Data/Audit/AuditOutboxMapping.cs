using Microsoft.EntityFrameworkCore;

namespace Dcms.Shared.Data.Audit;

public static class AuditOutboxMapping
{
    /// <summary>
    /// Maps <see cref="AuditOutboxMessage"/> into a business DbContext.
    ///
    /// <para><b>Why every context maps the same table.</b> The guarantee this design rests on
    /// is that an audit record cannot be lost while the change it describes commits. Two
    /// DbContexts do not share a transaction — writing the outbox row through
    /// <see cref="AuditDbContext"/> while the business change goes through, say,
    /// <c>CmsDbContext</c> would be two separate commits with a window between them.
    /// Mapping the outbox into the *saving* context closes that window: the row is written by
    /// the same <c>SaveChangesAsync</c>, in the same transaction, and a rollback takes the
    /// audit row with it — which is correct, because a change that did not happen must not be
    /// recorded as though it had.</para>
    ///
    /// <para>The table itself is created once, by <see cref="AuditDbContext"/>'s migration;
    /// every other context maps it <c>ExcludeFromMigrations</c> so only one owner emits DDL.</para>
    /// </summary>
    public static ModelBuilder MapAuditOutbox(this ModelBuilder builder)
    {
        builder.Entity<AuditOutboxMessage>(e =>
        {
            e.ToTable("audit_outbox", AuditDbContext.Schema, t => t.ExcludeFromMigrations());
            e.HasKey(o => o.Id);
            e.Property(o => o.Id).ValueGeneratedOnAdd();
            e.Property(o => o.PayloadJson).HasColumnType("jsonb").IsRequired();
            // Not tenant-filtered: the drain is a cross-tenant scan, exactly like content_outbox.
        });
        return builder;
    }
}
