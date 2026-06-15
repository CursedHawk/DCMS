using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Shared.Data.Cms;

/// <summary>
/// Owns the "plugins" and "cms" schemas. Tenant isolation uses the same Guid
/// query-filter + insert-stamping pattern as <see cref="Tenancy.TenancyDbContext"/>,
/// keyed off <see cref="ITenantContext"/>. Owned/migrated by admin-api; read by
/// content-api for delivery.
/// </summary>
public class CmsDbContext(DbContextOptions<CmsDbContext> options, ITenantContext tenantContext)
    : DbContext(options)
{
    public const string PluginsSchema = "plugins";
    public const string CmsSchema = "cms";

    private Guid CurrentTenantId => tenantContext.TenantId ?? Guid.Empty;

    public DbSet<PluginInstance> PluginInstances => Set<PluginInstance>();
    public DbSet<ContentItem> ContentItems => Set<ContentItem>();
    public DbSet<ContentVersion> ContentVersions => Set<ContentVersion>();
    public DbSet<ContentOutboxMessage> Outbox => Set<ContentOutboxMessage>();
    public DbSet<ScheduledPublish> ScheduledPublishes => Set<ScheduledPublish>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<PluginInstance>(e =>
        {
            e.ToTable("plugin_instances", PluginsSchema);
            e.HasKey(p => p.Id);
            e.Property(p => p.PluginId).HasMaxLength(64).IsRequired();
            e.Property(p => p.Slug).HasMaxLength(64).IsRequired();
            e.Property(p => p.Name).HasMaxLength(256);
            e.Property(p => p.ConfigJson).HasColumnType("jsonb");
            e.HasIndex(p => new { p.TenantId, p.Slug }).IsUnique();
            e.HasIndex(p => new { p.TenantId, p.PluginId });
            e.HasQueryFilter(p => p.TenantId == CurrentTenantId);
        });

        builder.Entity<ContentItem>(e =>
        {
            e.ToTable("content_items", CmsSchema);
            e.HasKey(c => c.Id);
            e.Property(c => c.ContentType).HasMaxLength(64).IsRequired();
            e.Property(c => c.Slug).HasMaxLength(256).IsRequired();
            e.Property(c => c.Status).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(c => new { c.TenantId, c.PluginInstanceId, c.ContentType, c.Slug }).IsUnique();
            e.HasMany(c => c.Versions).WithOne().HasForeignKey(v => v.ItemId);
            e.HasQueryFilter(c => c.TenantId == CurrentTenantId);
        });

        builder.Entity<ContentVersion>(e =>
        {
            e.ToTable("content_versions", CmsSchema);
            e.HasKey(v => v.Id);
            e.Property(v => v.DataJson).HasColumnType("jsonb");
            e.HasIndex(v => new { v.ItemId, v.VersionNo }).IsUnique();
            e.HasQueryFilter(v => v.TenantId == CurrentTenantId);
        });

        builder.Entity<ContentOutboxMessage>(e =>
        {
            e.ToTable("content_outbox", CmsSchema);
            e.HasKey(o => o.Id);
            e.Property(o => o.Subject).HasMaxLength(128).IsRequired();
            e.Property(o => o.PayloadJson).HasColumnType("jsonb");
            e.HasIndex(o => o.SentAt);
            // Not tenant-filtered: the dispatcher scans across tenants.
        });

        builder.Entity<ScheduledPublish>(e =>
        {
            e.ToTable("scheduled_publishes", CmsSchema);
            e.HasKey(s => s.Id);
            e.Property(s => s.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(s => new { s.Status, s.PublishAt });
            // Not tenant-filtered: the scheduler claims due rows across tenants.
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampTenant();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        StampTenant();
        return base.SaveChanges();
    }

    private void StampTenant()
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
