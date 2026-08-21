using Dcms.Shared.Audit.Redaction;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Data.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Shared.Data.Analytics;

/// <summary>
/// Raw analytics event (page view, custom event). One row per ingested event.
///
/// Everything past <see cref="PropsJson"/> is derived server-side at ingest, from
/// the request rather than from the beacon payload: a browser cannot be trusted to
/// report its own country, and it does not know its own IP at all.
/// </summary>
// Raw visitor telemetry: written on every page view, tenant-purgeable, and already a record of itself. Auditing it would drown the log in traffic.
[AuditIgnore]
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

    // ---- Derived at ingest from the User-Agent header ----
    /// <summary>"desktop", "mobile", "tablet" or "bot".</summary>
    public string? Device { get; set; }
    public string? Browser { get; set; }
    public string? Os { get; set; }

    /// <summary>ISO 3166-1 alpha-2 country code, or null when it could not be resolved.</summary>
    public string? Country { get; set; }

    // ---- Campaign attribution, parsed from the beacon's URL query ----
    public string? UtmSource { get; set; }
    public string? UtmMedium { get; set; }
    public string? UtmCampaign { get; set; }
}

/// <summary>
/// Per-day aggregate maintained by the consumer for fast dashboards.
///
/// Counts only. Unique visitors deliberately live nowhere here: distinct counts do
/// not sum, so a "visitors" column per (day, type, path) could not be rolled up into
/// a period total without over-counting anyone who visited twice. The dashboard
/// computes uniques from the raw events instead.
/// </summary>
// Derived from AnalyticsEvent by a scheduled job; nothing a person did.
[AuditIgnore]
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

        // Audit records are written by the same SaveChanges as the change they describe.
        builder.MapAuditOutbox();
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
            e.Property(x => x.Device).HasMaxLength(16);
            e.Property(x => x.Browser).HasMaxLength(64);
            e.Property(x => x.Os).HasMaxLength(64);
            e.Property(x => x.Country).HasMaxLength(2).IsFixedLength();
            e.Property(x => x.UtmSource).HasMaxLength(128);
            e.Property(x => x.UtmMedium).HasMaxLength(128);
            e.Property(x => x.UtmCampaign).HasMaxLength(128);
            e.HasIndex(x => new { x.TenantId, x.OccurredAt });
            // The dashboard's unique-visitor count is a DISTINCT over this triple for
            // a date range; without the index it is a full scan of the tenant's events.
            e.HasIndex(x => new { x.TenantId, x.OccurredAt, x.VisitorHash });
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

        services.AddDbContext<AnalyticsDbContext>((sp, options) =>
            options.UseNpgsql(connectionString, npgsql =>
                    npgsql.MigrationsHistoryTable("__ef_migrations_history", AnalyticsDbContext.Schema))
                .UseDcmsAuditInterceptors(sp));

        return services;
    }
}
