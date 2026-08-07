using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Shared.Data.Ai;

/// <summary>
/// Owns the "ai" schema (tenant_ai_settings). Written by admin-api (settings UI),
/// read by ai-gateway (provider resolution). Keyed by tenant; isolation is by
/// primary key rather than a query filter since each row is the tenant itself.
/// </summary>
public class AiDbContext(DbContextOptions<AiDbContext> options) : DbContext(options)
{
    public const string Schema = "ai";

    public DbSet<TenantAiSettings> Settings => Set<TenantAiSettings>();
    public DbSet<UserAiSettings> UserSettings => Set<UserAiSettings>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);

        builder.Entity<TenantAiSettings>(e =>
        {
            e.ToTable("tenant_ai_settings");
            e.HasKey(s => s.TenantId);
            e.Property(s => s.Provider).HasConversion<string>().HasMaxLength(16);
            e.Property(s => s.Model).HasMaxLength(128);
            e.Property(s => s.BaseUrl).HasMaxLength(512);
        });

        builder.Entity<UserAiSettings>(e =>
        {
            e.ToTable("user_ai_settings");
            e.HasKey(s => new { s.TenantId, s.UserId });
            e.Property(s => s.Provider).HasConversion<string>().HasMaxLength(16);
            e.Property(s => s.Model).HasMaxLength(128);
            e.Property(s => s.BaseUrl).HasMaxLength(512);
        });
    }
}

public sealed class AiDbContextFactory : IDesignTimeDbContextFactory<AiDbContext>
{
    public AiDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AiDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", AiDbContext.Schema))
            .Options;
        return new AiDbContext(options);
    }
}

public static class AiServiceCollectionExtensions
{
    public static IServiceCollection AddDcmsAiData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
                               ?? "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev";

        services.AddDbContext<AiDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__ef_migrations_history", AiDbContext.Schema)));

        return services;
    }
}
