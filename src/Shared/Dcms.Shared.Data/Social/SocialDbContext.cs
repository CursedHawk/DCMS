using Dcms.Shared.Data.Audit;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Shared.Data.Social;

/// <summary>
/// Owns the "social" schema: connected Meta accounts, per-instance sync state, the
/// mirrored-media map and pending OAuth states. Written and read by admin-api only —
/// content-api never touches it, because it holds the tenants' Meta tokens and the
/// public service is deliberately kept unable to decrypt them.
/// </summary>
public class SocialDbContext(DbContextOptions<SocialDbContext> options, ITenantContext tenantContext)
    : DbContext(options)
{
    public const string Schema = "social";

    private Guid CurrentTenantId => tenantContext.TenantId ?? Guid.Empty;

    public DbSet<MetaConnection> Connections => Set<MetaConnection>();
    public DbSet<MetaSyncState> SyncStates => Set<MetaSyncState>();
    public DbSet<MetaMediaMap> MediaMap => Set<MetaMediaMap>();
    public DbSet<MetaOAuthState> OAuthStates => Set<MetaOAuthState>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Audit records are written by the same SaveChanges as the change they describe.
        builder.MapAuditOutbox();
        builder.HasDefaultSchema(Schema);

        builder.Entity<MetaConnection>(e =>
        {
            e.ToTable("meta_connections");
            e.HasKey(c => c.Id);
            e.Property(c => c.Provider).HasConversion<string>().HasMaxLength(32);
            e.Property(c => c.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(c => c.ExternalAccountId).HasMaxLength(64).IsRequired();
            e.Property(c => c.ExternalPageId).HasMaxLength(64);
            e.Property(c => c.AccountName).HasMaxLength(256);
            e.Property(c => c.AccountUsername).HasMaxLength(256);
            e.Property(c => c.AvatarUrl).HasMaxLength(1024);
            e.Property(c => c.ScopesGranted).HasMaxLength(1024);
            e.Property(c => c.LastError).HasMaxLength(2048);
            // Reconnecting the same account updates the existing row rather than
            // accumulating one per consent, which would leave stale tokens behind.
            e.HasIndex(c => new { c.TenantId, c.Provider, c.ExternalAccountId }).IsUnique();
            e.HasQueryFilter(c => c.TenantId == CurrentTenantId);
        });

        builder.Entity<MetaSyncState>(e =>
        {
            e.ToTable("meta_sync_states");
            e.HasKey(s => s.Id);
            e.Property(s => s.ContentType).HasMaxLength(64).IsRequired();
            e.Property(s => s.LastCursor).HasMaxLength(1024);
            e.Property(s => s.LastError).HasMaxLength(2048);
            e.HasIndex(s => new { s.TenantId, s.PluginInstanceId, s.ContentType }).IsUnique();
            e.HasIndex(s => s.NextAttemptAt);
            e.HasQueryFilter(s => s.TenantId == CurrentTenantId);
        });

        builder.Entity<MetaMediaMap>(e =>
        {
            e.ToTable("meta_media_map");
            e.HasKey(m => m.Id);
            e.Property(m => m.ExternalMediaId).HasMaxLength(256).IsRequired();
            e.HasIndex(m => new { m.TenantId, m.ConnectionId, m.ExternalMediaId }).IsUnique();
            e.HasIndex(m => new { m.TenantId, m.MediaAssetId });
            e.HasQueryFilter(m => m.TenantId == CurrentTenantId);
        });

        builder.Entity<MetaOAuthState>(e =>
        {
            e.ToTable("meta_oauth_states");
            e.HasKey(s => s.Id);
            e.Property(s => s.StateHash).HasMaxLength(64).IsRequired();
            e.Property(s => s.Provider).HasConversion<string>().HasMaxLength(32);
            e.Property(s => s.ReturnPath).HasMaxLength(512);
            e.HasIndex(s => s.StateHash).IsUnique();
            e.HasIndex(s => s.ExpiresAt);
            // Tenant-filtered like every other table here, even though the callback has
            // to read it with IgnoreQueryFilters: the filter is the right default, and
            // the one caller that cannot satisfy it says so explicitly at the call site.
            e.HasQueryFilter(s => s.TenantId == CurrentTenantId);
        });
    }
}

public sealed class SocialDbContextFactory : IDesignTimeDbContextFactory<SocialDbContext>
{
    public SocialDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SocialDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", SocialDbContext.Schema))
            .Options;
        return new SocialDbContext(options, new DesignTimeTenantContext());
    }

    /// <summary>Design-time only: migrations are generated, not executed, so no tenant exists.</summary>
    private sealed class DesignTimeTenantContext : ITenantContext
    {
        public Guid? TenantId => null;
        public string? TenantSlug => null;
        public bool HasTenant => false;
    }
}

public static class SocialServiceCollectionExtensions
{
    public static IServiceCollection AddDcmsSocialData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
                               ?? "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev";

        services.AddDbContext<SocialDbContext>((sp, options) =>
            options.UseNpgsql(connectionString, npgsql =>
                    npgsql.MigrationsHistoryTable("__ef_migrations_history", SocialDbContext.Schema))
                .UseDcmsAuditInterceptors(sp));

        return services;
    }
}
