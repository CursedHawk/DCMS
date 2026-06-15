using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Shared.Data.Sites;

/// <summary>
/// Owns the "sites" schema. Written by admin-api (editor) and site-builder
/// (builds); read by site-host (serving). Same tenant isolation pattern.
/// </summary>
public class SitesDbContext(DbContextOptions<SitesDbContext> options, ITenantContext tenantContext)
    : DbContext(options)
{
    public const string Schema = "sites";

    private Guid CurrentTenantId => tenantContext.TenantId ?? Guid.Empty;

    public DbSet<Site> Sites => Set<Site>();
    public DbSet<SiteBuild> Builds => Set<SiteBuild>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);

        builder.Entity<Site>(e =>
        {
            e.ToTable("sites");
            e.HasKey(s => s.Id);
            e.Property(s => s.Name).HasMaxLength(256);
            e.Property(s => s.RenderMode).HasConversion<string>().HasMaxLength(32);
            e.Property(s => s.DraftDefinitionJson).HasColumnType("jsonb");
            e.HasMany(s => s.Builds).WithOne().HasForeignKey(b => b.SiteId);
            e.HasQueryFilter(s => s.TenantId == CurrentTenantId);
        });

        builder.Entity<SiteBuild>(e =>
        {
            e.ToTable("site_builds");
            e.HasKey(b => b.Id);
            e.Property(b => b.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(b => b.DefinitionSnapshotJson).HasColumnType("jsonb");
            e.Property(b => b.ArtifactPrefix).HasMaxLength(256);
            e.Property(b => b.LogObjectKey).HasMaxLength(256);
            e.HasIndex(b => new { b.SiteId, b.CreatedAt });
            e.HasQueryFilter(b => b.TenantId == CurrentTenantId);
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
