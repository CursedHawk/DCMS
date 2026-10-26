using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Data.Audit;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Shared.Data.Sites;

/// <summary>
/// Owns the "sites" schema. Written by admin-api (editor) and site-builder
/// (builds); read by site-host (serving). Same tenant isolation pattern.
/// </summary>
public class SitesDbContext(DbContextOptions<SitesDbContext> options)
    : ScopedDbContext(options)
{

    public const string Schema = "sites";

    private Guid CurrentTenantId => TenantContext?.TenantId ?? Guid.Empty;

    public DbSet<Site> Sites => Set<Site>();
    public DbSet<SiteBuild> Builds => Set<SiteBuild>();
    public DbSet<SiteDraft> Drafts => Set<SiteDraft>();
    public DbSet<SitePublishBranch> PublishBranches => Set<SitePublishBranch>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Audit records are written by the same SaveChanges as the change they describe.
        builder.MapAuditOutbox();
        builder.HasDefaultSchema(Schema);

        builder.Entity<Site>(e =>
        {
            e.ToTable("sites");
            e.HasKey(s => s.Id);
            e.Property(s => s.Name).HasMaxLength(256);
            e.Property(s => s.RenderMode).HasConversion<string>().HasMaxLength(32);
            e.Property(s => s.DraftDefinitionJson).HasColumnType("jsonb");
            e.Property(s => s.StaticBundleKey).HasMaxLength(512);
            e.Property(s => s.StaticBundleName).HasMaxLength(512);
            e.Property(s => s.GitRepoFullName).HasMaxLength(512);
            e.Property(s => s.GitDefaultBranch).HasMaxLength(256);
            e.Property(s => s.EnvironmentKey).HasMaxLength(64);
            e.HasIndex(s => new { s.TenantId, s.EnvironmentKey });
            e.Ignore(s => s.KeyAcrossEnvironments);
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
            e.Property(b => b.GitCommitSha).HasMaxLength(64);
            e.Property(b => b.Branch).HasMaxLength(256);
            e.HasIndex(b => new { b.SiteId, b.CreatedAt });
            e.HasQueryFilter(b => b.TenantId == CurrentTenantId);
        });

        builder.Entity<SiteDraft>(e =>
        {
            e.ToTable("site_drafts");
            e.HasKey(d => d.Id);
            e.Property(d => d.Branch).HasMaxLength(256);
            e.Property(d => d.DefinitionJson).HasColumnType("jsonb");
            e.Property(d => d.BaseSha).HasMaxLength(64);
            // One working draft per (site, user, branch); the IDE loads/saves by this key.
            e.HasIndex(d => new { d.TenantId, d.SiteId, d.UserId, d.Branch }).IsUnique();
            e.HasQueryFilter(d => d.TenantId == CurrentTenantId);
        });

        builder.Entity<SitePublishBranch>(e =>
        {
            e.ToTable("site_publish_branches");
            e.HasKey(b => b.Id);
            e.Property(b => b.Branch).HasMaxLength(256);
            e.HasIndex(b => new { b.SiteId, b.Branch }).IsUnique();
            e.HasOne<Site>().WithMany().HasForeignKey(b => b.SiteId).OnDelete(DeleteBehavior.Cascade);
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
