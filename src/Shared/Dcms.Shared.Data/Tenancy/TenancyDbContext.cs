using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Shared.Data.Tenancy;

/// <summary>
/// Owns the "tenancy" schema. Tenant isolation is enforced here (not via
/// Finbuckle's string-keyed filters) with EF global query filters on the Guid
/// TenantId plus automatic stamping on insert, keyed off <see cref="ITenantContext"/>.
/// Finbuckle is used only for resolving the ambient tenant. Cross-tenant reads
/// (SuperAdmin, "my tenants") must call <c>IgnoreQueryFilters()</c>.
/// </summary>
public class TenancyDbContext(DbContextOptions<TenancyDbContext> options, ITenantContext tenantContext)
    : DbContext(options)
{
    public const string Schema = "tenancy";

    // Read live (not captured) because the same scoped context may be used by
    // Finbuckle's store during resolution (no tenant yet) and then by request
    // handlers (tenant set). EF re-evaluates instance members in query filters
    // per query, so isolation tracks the resolved tenant.
    private Guid CurrentTenantId => tenantContext.TenantId ?? Guid.Empty;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Domain> Domains => Set<Domain>();
    public DbSet<TenantMembership> Memberships => Set<TenantMembership>();
    public DbSet<TenantRole> TenantRoles => Set<TenantRole>();
    public DbSet<TenantRolePermission> TenantRolePermissions => Set<TenantRolePermission>();
    public DbSet<MemberRole> MemberRoles => Set<MemberRole>();
    public DbSet<Invitation> Invitations => Set<Invitation>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);

        builder.Entity<Tenant>(e =>
        {
            e.ToTable("tenants");
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).HasMaxLength(36);
            e.Property(t => t.Identifier).HasMaxLength(64).IsRequired();
            e.HasIndex(t => t.Identifier).IsUnique();
            e.Property(t => t.Name).HasMaxLength(256);
            e.Property(t => t.Status).HasConversion<string>().HasMaxLength(32);
        });

        builder.Entity<Domain>(e =>
        {
            e.ToTable("domains");
            e.HasKey(d => d.Id);
            e.Property(d => d.Hostname).HasMaxLength(253).IsRequired();
            e.HasIndex(d => d.Hostname).IsUnique();
            e.HasIndex(d => new { d.TenantId, d.IsPrimary });
            e.HasQueryFilter(d => d.TenantId == CurrentTenantId);
        });

        builder.Entity<TenantMembership>(e =>
        {
            e.ToTable("tenant_memberships");
            e.HasKey(m => m.Id);
            e.Property(m => m.Email).HasMaxLength(256).IsRequired();
            e.HasIndex(m => new { m.TenantId, m.UserId }).IsUnique();
            e.HasMany(m => m.Roles).WithOne(r => r.Membership!).HasForeignKey(r => r.MembershipId);
            e.HasQueryFilter(m => m.TenantId == CurrentTenantId);
        });

        builder.Entity<TenantRole>(e =>
        {
            e.ToTable("tenant_roles");
            e.HasKey(r => r.Id);
            e.Property(r => r.Name).HasMaxLength(128).IsRequired();
            e.HasIndex(r => new { r.TenantId, r.Name }).IsUnique();
            e.HasMany(r => r.Permissions).WithOne().HasForeignKey(p => p.TenantRoleId);
            e.HasMany(r => r.Members).WithOne(m => m.Role!).HasForeignKey(m => m.TenantRoleId);
            e.HasQueryFilter(r => r.TenantId == CurrentTenantId);
        });

        builder.Entity<TenantRolePermission>(e =>
        {
            e.ToTable("tenant_role_permissions");
            e.HasKey(p => p.Id);
            e.Property(p => p.Permission).HasMaxLength(128).IsRequired();
            e.HasIndex(p => new { p.TenantRoleId, p.Permission }).IsUnique();
            e.HasQueryFilter(p => p.TenantId == CurrentTenantId);
        });

        builder.Entity<MemberRole>(e =>
        {
            e.ToTable("member_roles");
            e.HasKey(mr => mr.Id);
            e.HasIndex(mr => new { mr.MembershipId, mr.TenantRoleId }).IsUnique();
            e.HasQueryFilter(mr => mr.TenantId == CurrentTenantId);
        });

        builder.Entity<Invitation>(e =>
        {
            e.ToTable("invitations");
            e.HasKey(i => i.Id);
            e.Property(i => i.Email).HasMaxLength(256).IsRequired();
            e.Property(i => i.TokenHash).HasMaxLength(128).IsRequired();
            e.HasIndex(i => i.TokenHash);
            e.HasIndex(i => new { i.TenantId, i.Email });
            e.HasQueryFilter(i => i.TenantId == CurrentTenantId);
        });
    }

    public override int SaveChanges()
    {
        StampTenant();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampTenant();
        return base.SaveChangesAsync(cancellationToken);
    }

    // Stamp the ambient tenant on newly-added tenant-scoped entities that didn't
    // set it explicitly (e.g. accepting an invitation under the invited tenant).
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
