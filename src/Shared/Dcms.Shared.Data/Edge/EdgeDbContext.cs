using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Dcms.Shared.Data.Edge;

/// <summary>
/// Owns the "edge" schema: TLS certificates, the ACME account, the route overlay, and the
/// edge's own Data Protection key ring.
///
/// <para>None of these tables carries a TenantId, so none of them takes part in RLS — see
/// <see cref="EdgeCertificate"/> for why that is a design decision rather than an oversight.
/// The context is still registered in <c>AssertRlsCoverage</c>, because that assertion's job is
/// to notice a tenant column that nobody registered, and a context it never sees cannot fail it.
/// </para>
///
/// <para>Deliberately not audited. The rows are written by ACME issuance and a renewal timer
/// rather than by an operator action, and routing certificate renewal through the audit outbox
/// would couple the ability to renew a certificate to the availability of the audit schema —
/// the same reasoning as <c>DataProtectionDbContext</c>. Operator-initiated changes (uploading a
/// custom certificate, forcing a reissue) are audited where they are made, in admin-api.</para>
/// </summary>
public class EdgeDbContext(DbContextOptions<EdgeDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public const string Schema = "edge";

    public DbSet<EdgeCertificate> Certificates => Set<EdgeCertificate>();
    public DbSet<AcmeAccount> AcmeAccounts => Set<AcmeAccount>();
    public DbSet<EdgeRoute> Routes => Set<EdgeRoute>();

    /// <summary>The domains DCMS keeps renewed on its own behalf. See ADR 0011.</summary>
    public DbSet<EdgeManagedCertificate> ManagedCertificates => Set<EdgeManagedCertificate>();

    /// <summary>
    /// One row per order attempt, which is what stops a retry loop spending Let's Encrypt's
    /// five-per-week duplicate-certificate budget for the whole platform.
    /// </summary>
    public DbSet<EdgeManagedCertificateAttempt> ManagedCertificateAttempts
        => Set<EdgeManagedCertificateAttempt>();

    /// <summary>
    /// The edge's OWN key ring, not the shared <c>dataprotection</c> schema every other service
    /// uses.
    ///
    /// <para>That schema's key ring also protects <c>ForgejoSyncOutbox.EncryptedPassword</c> —
    /// every user's git credential. Handing it to the process that terminates TLS for the whole
    /// internet, so that its session cookie survives a restart, is a bad trade. A separate ring
    /// under a separate application name means the edge can read exactly one thing: its own
    /// cookies.</para>
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);

        builder.Entity<EdgeCertificate>(e =>
        {
            e.ToTable("certificates");
            e.HasKey(c => c.Id);
            // Unique, because the handshake looks a certificate up by SNI name and two rows for
            // one hostname would make which one is served depend on row order.
            e.HasIndex(c => c.Hostname).IsUnique();
            e.Property(c => c.Hostname).HasMaxLength(253).IsRequired();
            e.Property(c => c.Issuer).HasMaxLength(256);
            e.Property(c => c.LastError).HasMaxLength(2000);
            // Indexed because the renewal sweep's only query is "what expires soon".
            e.HasIndex(c => c.NotAfter);

            // One artifact per intent. Filtered, because every per-hostname certificate leaves
            // this null and a plain unique index would allow exactly one of them.
            e.HasIndex(c => c.ManagedCertificateId)
                .IsUnique()
                .HasFilter("\"ManagedCertificateId\" IS NOT NULL");

            // A GIN index, because the handshake's fallback lookup is a containment test
            // (`@hostname = ANY(...)`) and btree cannot answer one. Small table, but this is on
            // the TLS path for every hostname the platform does not hold an exact row for --
            // which, once the wildcard supersedes the per-host certificates, is all of them.
            e.HasIndex(c => c.SubjectAlternativeNames).HasMethod("gin");
        });

        builder.Entity<EdgeManagedCertificate>(e =>
        {
            e.ToTable("managed_certificates");
            e.HasKey(m => m.Id);
            e.Property(m => m.Name).HasMaxLength(128).IsRequired();
            // 253 is the DNS name limit; a wildcard costs two more characters.
            e.Property(m => m.Identifiers).IsRequired();
        });

        builder.Entity<EdgeManagedCertificateAttempt>(e =>
        {
            e.ToTable("managed_certificate_attempts");
            e.HasKey(a => a.Id);
            e.Property(a => a.Identifiers).HasMaxLength(2000).IsRequired();
            e.Property(a => a.Error).HasMaxLength(2000);

            // Defaults to TRUE, and the default is the migration's problem as much as the
            // model's: every row written before this column existed was an order the CA answered,
            // because those were the only attempts recorded at all. Backfilling them as false
            // would take real refusals out of the hourly ceiling.
            e.Property(a => a.ReachedCa).HasDefaultValue(true);

            // The rate-limit guard's only query: "how many attempts for this identifier set
            // since a week ago". Composite so it is answered from the index alone.
            e.HasIndex(a => new { a.ManagedCertificateId, a.AttemptedAt });

            // Cascade, so removing a managed certificate takes its history with it rather than
            // leaving rows that count towards a budget for an identifier set nobody orders.
            e.HasOne<EdgeManagedCertificate>()
                .WithMany()
                .HasForeignKey(a => a.ManagedCertificateId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AcmeAccount>(e =>
        {
            e.ToTable("acme_accounts");
            e.HasKey(a => a.Id);
            e.HasIndex(a => a.DirectoryUrl).IsUnique();
            e.Property(a => a.DirectoryUrl).HasMaxLength(512).IsRequired();
            e.Property(a => a.ContactEmail).HasMaxLength(320).IsRequired();
        });

        builder.Entity<DataProtectionKey>(e =>
        {
            e.ToTable("data_protection_keys");
            e.HasKey(k => k.Id);
            e.Property(k => k.FriendlyName).HasMaxLength(256);
        });

        builder.Entity<EdgeRoute>(e =>
        {
            e.ToTable("routes");
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.RouteId).IsUnique();
            e.Property(r => r.RouteId).HasMaxLength(128).IsRequired();
            e.Property(r => r.Hosts).HasMaxLength(2000);
            e.Property(r => r.PathPattern).HasMaxLength(512).IsRequired();
            e.Property(r => r.ClusterId).HasMaxLength(128).IsRequired();
        });
    }
}

public sealed class EdgeDbContextFactory : IDesignTimeDbContextFactory<EdgeDbContext>
{
    public EdgeDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<EdgeDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", EdgeDbContext.Schema))
            .Options;
        return new EdgeDbContext(options);
    }
}
