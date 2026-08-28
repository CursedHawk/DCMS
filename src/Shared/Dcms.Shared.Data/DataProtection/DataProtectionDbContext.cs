using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Dcms.Shared.Data.DataProtection;

/// <summary>
/// Owns the "dataprotection" schema: the shared ASP.NET Core Data Protection key ring.
///
/// <para>Without this the key ring falls back to a per-container directory that no volume
/// is mounted into, so every replica and every restart mints its own keys. Three things
/// break at once, and all three look like unrelated bugs:</para>
///
/// <list type="bullet">
///   <item>Identity's interactive auth cookie is unreadable by a sibling replica, so a user
///   whose next request lands elsewhere is bounced back to the login page — forever, because
///   the replica that issued the cookie is not the one that gets asked to read it.</item>
///   <item>The Google external-login correlation cookie is written during the redirect out and
///   read on the way back, so a scaled Identity fails the OAuth handshake intermittently.</item>
///   <item><c>ForgejoSyncOutbox.EncryptedPassword</c> rows are protected on write and
///   unprotected on drain. A row written by one replica throws in another, retries twelve
///   times and dead-letters.</item>
/// </list>
///
/// <para>This context is deliberately NOT audited. The rows are written by the framework's
/// key-management background work rather than by an application code path, and
/// <c>MapAuditOutbox</c> would put key-ring creation behind the audit outbox interceptor —
/// coupling the ability to mint a key to the availability of the audit schema.</para>
/// </summary>
public class DataProtectionDbContext(DbContextOptions<DataProtectionDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public const string Schema = "dataprotection";

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);

        builder.Entity<DataProtectionKey>(e =>
        {
            e.ToTable("data_protection_keys");
            e.HasKey(k => k.Id);
            e.Property(k => k.FriendlyName).HasMaxLength(256);
        });
    }
}

public sealed class DataProtectionDbContextFactory : IDesignTimeDbContextFactory<DataProtectionDbContext>
{
    public DataProtectionDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<DataProtectionDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", DataProtectionDbContext.Schema))
            .Options;
        return new DataProtectionDbContext(options);
    }
}
