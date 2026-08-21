using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Data.Audit;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Shared.Data.Media;

/// <summary>
/// Owns the "media" schema. Written by admin-api (upload) and media-worker
/// (variants); read by content-api (serving). Same tenant isolation pattern as
/// the other contexts.
/// </summary>
public class MediaDbContext(DbContextOptions<MediaDbContext> options, ITenantContext tenantContext)
    : DbContext(options)
{
    public const string Schema = "media";

    private Guid CurrentTenantId => tenantContext.TenantId ?? Guid.Empty;

    public DbSet<MediaAsset> Assets => Set<MediaAsset>();
    public DbSet<MediaVariant> Variants => Set<MediaVariant>();
    public DbSet<MediaFolder> Folders => Set<MediaFolder>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Audit records are written by the same SaveChanges as the change they describe.
        builder.MapAuditOutbox();
        builder.HasDefaultSchema(Schema);

        builder.Entity<MediaAsset>(e =>
        {
            e.ToTable("media_assets");
            e.HasKey(a => a.Id);
            e.Property(a => a.Category).HasConversion<string>().HasMaxLength(16);
            e.Property(a => a.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(a => a.FileName).HasMaxLength(512);
            e.Property(a => a.ContentType).HasMaxLength(128);
            e.Property(a => a.Sha256).HasMaxLength(64);
            e.Property(a => a.OriginalKey).HasMaxLength(512);
            e.Property(a => a.MetadataJson).HasColumnType("jsonb");
            e.HasIndex(a => new { a.TenantId, a.Category });
            e.HasIndex(a => new { a.TenantId, a.FolderId });
            e.HasMany(a => a.Variants).WithOne().HasForeignKey(v => v.AssetId);
            e.HasQueryFilter(a => a.TenantId == CurrentTenantId);
        });

        builder.Entity<MediaFolder>(e =>
        {
            e.ToTable("media_folders");
            e.HasKey(f => f.Id);
            e.Property(f => f.Name).HasMaxLength(200);
            e.HasIndex(f => new { f.TenantId, f.ParentId });
            e.HasQueryFilter(f => f.TenantId == CurrentTenantId);
        });

        builder.Entity<MediaVariant>(e =>
        {
            e.ToTable("media_variants");
            e.HasKey(v => v.Id);
            e.Property(v => v.Kind).HasMaxLength(32);
            e.Property(v => v.ObjectKey).HasMaxLength(512);
            e.Property(v => v.ContentType).HasMaxLength(128);
            e.HasIndex(v => new { v.AssetId, v.Kind }).IsUnique();
            e.HasQueryFilter(v => v.TenantId == CurrentTenantId);
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
