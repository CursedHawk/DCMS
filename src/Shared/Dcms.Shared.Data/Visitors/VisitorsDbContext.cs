using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dcms.Shared.Data.Visitors;

/// <summary>
/// A website visitor account — a separate identity pool per tenant, distinct
/// from the platform (admin) users in the identity service.
/// </summary>
public sealed class VisitorAccount : TenantEntity, ISandboxScoped
{
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public DateTimeOffset? EmailVerifiedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>True for accounts created from a site preview's sandbox.</summary>
    public bool IsSandbox { get; set; }
}

public sealed class VisitorRefreshToken : TenantEntity, ISandboxScoped
{
    public Guid VisitorId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public bool IsSandbox { get; set; }
}

/// <summary>Owns the "visitors" schema. Used by content-api's VisitorAuth endpoints.</summary>
public class VisitorsDbContext(
    DbContextOptions<VisitorsDbContext> options, ITenantContext tenantContext, ISandboxContext sandboxContext)
    : DbContext(options)
{
    public const string Schema = "visitors";

    private Guid CurrentTenantId => tenantContext.TenantId ?? Guid.Empty;
    private bool CurrentSandbox => sandboxContext.IsSandbox;

    public DbSet<VisitorAccount> Accounts => Set<VisitorAccount>();
    public DbSet<VisitorRefreshToken> RefreshTokens => Set<VisitorRefreshToken>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);

        builder.Entity<VisitorAccount>(e =>
        {
            e.ToTable("visitor_accounts");
            e.HasKey(a => a.Id);
            e.Property(a => a.Email).HasMaxLength(256).IsRequired();
            e.Property(a => a.DisplayName).HasMaxLength(256);
            e.HasIndex(a => new { a.TenantId, a.IsSandbox, a.Email }).IsUnique();
            e.HasQueryFilter(a => a.TenantId == CurrentTenantId && a.IsSandbox == CurrentSandbox);
        });

        builder.Entity<VisitorRefreshToken>(e =>
        {
            e.ToTable("visitor_refresh_tokens");
            e.HasKey(t => t.Id);
            e.Property(t => t.TokenHash).HasMaxLength(128).IsRequired();
            e.HasIndex(t => t.TokenHash);
            e.HasQueryFilter(t => t.TenantId == CurrentTenantId && t.IsSandbox == CurrentSandbox);
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        Stamp();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        Stamp();
        return base.SaveChanges();
    }

    private void Stamp()
    {
        var tenantId = CurrentTenantId;
        if (tenantId == Guid.Empty)
        {
            return;
        }
        var sandbox = CurrentSandbox;
        foreach (var entry in ChangeTracker.Entries<TenantEntity>())
        {
            if (entry.State != EntityState.Added)
            {
                continue;
            }
            if (entry.Entity.TenantId == Guid.Empty)
            {
                entry.Entity.TenantId = tenantId;
            }
            if (entry.Entity is ISandboxScoped sandboxed)
            {
                sandboxed.IsSandbox = sandbox;
            }
        }
    }
}

public sealed class VisitorsDbContextFactory : IDesignTimeDbContextFactory<VisitorsDbContext>
{
    public VisitorsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<VisitorsDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", VisitorsDbContext.Schema))
            .Options;
        return new VisitorsDbContext(options, new NullTenantContext(), Sandbox.DisabledSandboxContext.Instance);
    }

    private sealed class NullTenantContext : ITenantContext
    {
        public Guid? TenantId => null;
        public string? TenantSlug => null;
    }
}

public static class VisitorsServiceCollectionExtensions
{
    public static IServiceCollection AddDcmsVisitorsData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
                               ?? "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev";

        services.AddDbContext<VisitorsDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__ef_migrations_history", VisitorsDbContext.Schema)));

        services.TryAddScoped<ISandboxContext>(_ => Sandbox.DisabledSandboxContext.Instance);
        return services;
    }
}
