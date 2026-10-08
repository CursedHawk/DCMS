using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Data.Audit;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Shared.Data.UserAuth;

/// <summary>
/// A named bundle of site permissions (<c>{plugin}:{instance}:{resource}:{action}</c>) that a
/// tenant gives to groups or users of its realm (ADR 0022). The users and groups themselves live
/// in Identity; this is the policy over them.
/// </summary>
public sealed class UserRole : TenantEntity
{
    /// <summary>snake_case, unique per tenant: how flows and the assistant name it.</summary>
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string> Permissions { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum GrantSubject
{
    Group,
    User,
}

/// <summary>A role given to a realm group (everyone in it) or to one realm user.</summary>
public sealed class UserGrant : TenantEntity
{
    public Guid RoleId { get; set; }
    public GrantSubject SubjectType { get; set; }

    /// <summary>The realm group's or user's id in Identity.</summary>
    public Guid SubjectId { get; set; }

    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum GateAccess
{
    /// <summary>Anyone, signed in or not.</summary>
    Public,

    /// <summary>Any signed-in user of the tenant's realm.</summary>
    SignedIn,

    /// <summary>Signed-in users in at least one of <see cref="SiteGate.Groups"/>.</summary>
    Groups,
}

/// <summary>
/// One access rule of a hosted site: requests whose path starts with <see cref="PathPrefix"/>
/// need <see cref="Access"/>. The edge applies a site's rules in <see cref="Position"/> order;
/// the first that matches decides, and a path no rule matches is public.
/// </summary>
public sealed class SiteGate : TenantEntity
{
    public Guid SiteId { get; set; }
    public int Position { get; set; }

    /// <summary>Starts with "/"; "/" itself covers the whole site.</summary>
    public string PathPrefix { get; set; } = "/";

    public GateAccess Access { get; set; } = GateAccess.SignedIn;

    /// <summary>Realm group ids, for <see cref="GateAccess.Groups"/>.</summary>
    public List<Guid> Groups { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum ApiAccess
{
    /// <summary>Any signed-in user of the tenant's realm.</summary>
    SignedIn,

    /// <summary>
    /// Reads need <c>{plugin}:{instance}:api:read</c>, every other method <c>…:api:write</c>,
    /// held through a role.
    /// </summary>
    Permission,
}

/// <summary>
/// Who may call one plugin instance's site API, <c>/api/{slug}/…</c> — its own routes, its
/// content delivery and its site contracts (ADR 0022). An instance with no rule is public. This
/// is what protects a single-page app's data: its routes never reach the edge's path rules.
/// </summary>
public sealed class ApiRule : TenantEntity
{
    public Guid InstanceId { get; set; }
    public ApiAccess Access { get; set; } = ApiAccess.SignedIn;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Owns the "userauth" schema: the User Authentication plugin's access policy (ADR 0022).</summary>
public class UserAuthDbContext(DbContextOptions<UserAuthDbContext> options, ITenantContext tenantContext) : DbContext(options)
{
    public const string Schema = "userauth";

    // An enclosing RlsScope.Tenant wins: the edge's gate lookups run with no tenant request.
    private Guid CurrentTenantId => RlsScope.TenantOverride ?? tenantContext.TenantId ?? Guid.Empty;

    public DbSet<UserRole> Roles => Set<UserRole>();
    public DbSet<UserGrant> Grants => Set<UserGrant>();
    public DbSet<SiteGate> Gates => Set<SiteGate>();
    public DbSet<ApiRule> ApiRules => Set<ApiRule>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.MapAuditOutbox();
        builder.HasDefaultSchema(Schema);

        builder.Entity<UserRole>(e =>
        {
            e.ToTable("roles");
            e.HasKey(r => r.Id);
            e.Property(r => r.Key).HasMaxLength(64);
            e.Property(r => r.Name).HasMaxLength(120);
            e.Property(r => r.Description).HasMaxLength(1000);
            e.HasIndex(r => new { r.TenantId, r.Key }).IsUnique();
            e.HasQueryFilter(r => r.TenantId == CurrentTenantId);
        });

        builder.Entity<UserGrant>(e =>
        {
            e.ToTable("grants");
            e.HasKey(g => g.Id);
            e.Property(g => g.SubjectType).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(g => new { g.TenantId, g.RoleId, g.SubjectType, g.SubjectId }).IsUnique();
            // What a sign-in needs: every grant of one user, or of their groups.
            e.HasIndex(g => new { g.TenantId, g.SubjectType, g.SubjectId });
            e.HasOne<UserRole>().WithMany().HasForeignKey(g => g.RoleId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(g => g.TenantId == CurrentTenantId);
        });

        builder.Entity<SiteGate>(e =>
        {
            e.ToTable("gates");
            e.HasKey(g => g.Id);
            e.Property(g => g.PathPrefix).HasMaxLength(512);
            e.Property(g => g.Access).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(g => new { g.TenantId, g.SiteId, g.PathPrefix }).IsUnique();
            e.HasIndex(g => new { g.TenantId, g.SiteId, g.Position });
            e.HasQueryFilter(g => g.TenantId == CurrentTenantId);
        });

        builder.Entity<ApiRule>(e =>
        {
            e.ToTable("api_rules");
            e.HasKey(r => r.Id);
            e.Property(r => r.Access).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(r => new { r.TenantId, r.InstanceId }).IsUnique();
            e.HasQueryFilter(r => r.TenantId == CurrentTenantId);
        });
    }
}

public sealed class UserAuthDbContextFactory : IDesignTimeDbContextFactory<UserAuthDbContext>
{
    public UserAuthDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<UserAuthDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", UserAuthDbContext.Schema))
            .Options;
        return new UserAuthDbContext(options, new NullTenantContext());
    }

    private sealed class NullTenantContext : ITenantContext
    {
        public Guid? TenantId => null;
        public string? TenantSlug => null;
    }
}

public static class UserAuthServiceCollectionExtensions
{
    public static IServiceCollection AddDcmsUserAuthData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
                               ?? "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev";

        services.AddDbContext<UserAuthDbContext>((sp, options) =>
            options.UseNpgsql(connectionString, npgsql =>
                    npgsql.MigrationsHistoryTable("__ef_migrations_history", UserAuthDbContext.Schema))
                .UseDcmsAuditInterceptors(sp));
        return services;
    }
}
