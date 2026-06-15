using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Shared.Data.Analytics;

/// <summary>Raw analytics event (page view, custom event). One row per ingested event.</summary>
public sealed class AnalyticsEvent
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string? Referrer { get; set; }
    public string? SessionId { get; set; }
    public string? VisitorHash { get; set; }
    public string PropsJson { get; set; } = "{}";
}

/// <summary>Per-day aggregate maintained by the consumer for fast dashboards.</summary>
public sealed class DailyRollup
{
    public Guid TenantId { get; set; }
    public DateOnly Day { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public long Count { get; set; }
}

/// <summary>
/// Owns the "analytics" schema. Written by the admin-api consumer, read by the
/// dashboard. (Event-table range partitioning is a documented later optimization.)
/// </summary>
public class AnalyticsDbContext(DbContextOptions<AnalyticsDbContext> options) : DbContext(options)
{
    public const string Schema = "analytics";

    public DbSet<AnalyticsEvent> Events => Set<AnalyticsEvent>();
    public DbSet<DailyRollup> DailyRollups => Set<DailyRollup>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);

        builder.Entity<AnalyticsEvent>(e =>
        {
            e.ToTable("events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasMaxLength(64);
            e.Property(x => x.Path).HasMaxLength(1024);
            e.Property(x => x.Referrer).HasMaxLength(1024);
            e.Property(x => x.SessionId).HasMaxLength(128);
            e.Property(x => x.VisitorHash).HasMaxLength(128);
            e.Property(x => x.PropsJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.TenantId, x.OccurredAt });
        });

        builder.Entity<DailyRollup>(e =>
        {
            e.ToTable("daily_rollups");
            e.HasKey(x => new { x.TenantId, x.Day, x.Type, x.Path });
            e.Property(x => x.Type).HasMaxLength(64);
            e.Property(x => x.Path).HasMaxLength(1024);
        });
    }
}

public sealed class AnalyticsDbContextFactory : IDesignTimeDbContextFactory<AnalyticsDbContext>
{
    public AnalyticsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AnalyticsDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", AnalyticsDbContext.Schema))
            .Options;
        return new AnalyticsDbContext(options);
    }
}

public static class AnalyticsServiceCollectionExtensions
{
    public static IServiceCollection AddDcmsAnalyticsData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
                               ?? "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev";

        services.AddDbContext<AnalyticsDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__ef_migrations_history", AnalyticsDbContext.Schema)));

        return services;
    }
}
