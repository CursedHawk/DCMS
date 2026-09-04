using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Shared.Data.Platform;

/// <summary>
/// Maps one global role name to one platform-console permission key.
///
/// <para><b>Why a role name and not a role id.</b> The roles themselves live in
/// <c>identity."AspNetRoles"</c>, owned by the identity service and on the other side of a
/// service boundary — this row cannot carry a foreign key to them, and a copied GUID would be
/// a second source of truth that drifts silently. The name is what the access token actually
/// carries (one <c>role</c> claim per global role), so keying on it means authorization is
/// answerable from the token plus this table and nothing else. That is what lets platform-api
/// run with no grant whatsoever on the identity schema.</para>
///
/// <para>The cost is that renaming a global role orphans its grants. Global roles are a
/// closed set defined in code (<c>GlobalRoles</c>), so a rename is a code change that can
/// carry the data change with it.</para>
/// </summary>
public sealed class PlatformRolePermission
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Matches <c>GlobalRoles</c> — "SuperAdmin", "Support".</summary>
    public string RoleName { get; set; } = string.Empty;

    /// <summary>A key from <c>PlatformConsolePermissions</c>.</summary>
    public string Permission { get; set; } = string.Empty;
}

/// <summary>
/// Owns the "platform" schema: which global role carries which platform-console permission.
///
/// <para><b>Deliberately not tenant-scoped, and that is load-bearing.</b> No entity here has a
/// <c>TenantId</c>, so <c>RlsConfigurator.AssertCoverage</c> — which fails startup for any EF
/// entity that has one and is registered in neither <c>TenantTables</c> nor
/// <c>ExemptTables</c> — does not consider these tables at all. That is the correct outcome
/// and it is written down here because the assertion's silence is otherwise indistinguishable
/// from an omission. A platform permission is a property of a role across the whole platform;
/// scoping it to a tenant would be a category error, not a missing feature.</para>
///
/// <para>Migrated by the <c>migrate</c> job, which runs the admin-api image as the schema
/// owner. platform-api reads and writes the rows through the least-privilege
/// <c>dcms_platform</c> role and never runs DDL.</para>
/// </summary>
public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public const string Schema = "platform";

    public DbSet<PlatformRolePermission> RolePermissions => Set<PlatformRolePermission>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);

        builder.Entity<PlatformRolePermission>(e =>
        {
            e.ToTable("role_permissions");
            e.HasKey(x => x.Id);
            e.Property(x => x.RoleName).HasMaxLength(64).IsRequired();
            e.Property(x => x.Permission).HasMaxLength(128).IsRequired();

            // The unique index is the whole concurrency story for the seeder: two instances
            // starting together both try to insert the same grants, and the loser gets a
            // constraint violation rather than a duplicate row.
            e.HasIndex(x => new { x.RoleName, x.Permission }).IsUnique();

            // Every read is "what does this set of roles hold", so the role name leads.
            e.HasIndex(x => x.RoleName);
        });
    }
}

public static class PlatformDataServiceCollectionExtensions
{
    public static IServiceCollection AddDcmsPlatformData(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<PlatformDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("Postgres"),
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history", PlatformDbContext.Schema)));
        return services;
    }
}

public sealed class PlatformDbContextFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev",
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history", PlatformDbContext.Schema))
            .Options;
        return new PlatformDbContext(options);
    }
}
