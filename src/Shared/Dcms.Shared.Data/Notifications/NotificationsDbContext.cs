using Dcms.Shared.Data.Audit;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Shared.Data.Notifications;

/// <summary>How loudly a notification asks to be noticed. Drives the SPA's toast rule.</summary>
public enum NotificationSeverity
{
    Info = 0,
    Success = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>
/// One thing that happened in a tenant, worth telling its admins about.
///
/// <para><b>Why the text is stored as keys, not prose.</b> <see cref="TitleKey"/> and
/// <see cref="BodyKey"/> are i18n keys and <see cref="ParamsJson"/> holds the interpolation
/// values. The admin SPA ships English and Czech; a rendered English sentence written into
/// this table at raise time would be untranslatable forever after, and re-rendering it later
/// is impossible once the originating entity has been renamed or deleted.</para>
/// </summary>
public sealed class Notification : TenantEntity
{
    /// <summary>Stable discriminator, e.g. <c>site.published</c>. Chooses the i18n keys and the icon.</summary>
    public string Kind { get; set; } = string.Empty;

    public NotificationSeverity Severity { get; set; } = NotificationSeverity.Info;

    public string TitleKey { get; set; } = string.Empty;
    public string BodyKey { get; set; } = string.Empty;

    /// <summary>JSON object of interpolation values for the two keys above.</summary>
    public string ParamsJson { get; set; } = "{}";

    /// <summary>SPA route this notification links to, e.g. <c>/sites/{id}</c>. Null for items with no destination.</summary>
    public string? LinkPath { get; set; }

    public string? ResourceType { get; set; }
    public Guid? ResourceId { get; set; }

    /// <summary>
    /// Who caused this, when it was a person. The SPA suppresses the toast (but not the bell
    /// entry) when the actor is the viewer — publishing ten pages should not toast ten times
    /// at the person who clicked publish.
    /// </summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>
    /// Idempotency key, unique per tenant. JetStream delivery is at-least-once, so a
    /// redelivered event must not notify twice; consumers pass the source event's id.
    /// </summary>
    public string DedupeKey { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One (notification, user) pair, carrying that user's own read state.
///
/// <para>Recipients are resolved and materialised when the notification is raised rather than
/// filtered by permission at query time. That keeps the unread count an indexed count instead
/// of a permission-filtered anti-join, and it fixes the audience at the moment the thing
/// happened — a permission granted next week should not retroactively reveal last week's
/// notifications.</para>
/// </summary>
public sealed class NotificationRecipient : TenantEntity
{
    public Guid NotificationId { get; set; }
    public Guid UserId { get; set; }

    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset? DismissedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Notification? Notification { get; set; }
}

/// <summary>
/// Owns the "notifications" schema. Written only by admin-api: services that cannot reach
/// this schema publish <c>notify.raise</c> instead, the way audit's <c>audit.submitted</c>
/// works, so the table keeps a single writer.
/// </summary>
public class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options, ITenantContext tenantContext)
    : DbContext(options)
{
    public const string Schema = "notifications";

    // Read live rather than captured: the same scoped context is resolved before Finbuckle
    // has settled a tenant and then used by handlers once it has.
    private Guid CurrentTenantId => tenantContext.TenantId ?? Guid.Empty;

    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationRecipient> Recipients => Set<NotificationRecipient>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.MapAuditOutbox();
        builder.HasDefaultSchema(Schema);

        builder.Entity<Notification>(e =>
        {
            e.ToTable("notifications");
            e.HasKey(n => n.Id);
            e.Property(n => n.Kind).HasMaxLength(128).IsRequired();
            e.Property(n => n.Severity).HasConversion<int>();
            e.Property(n => n.TitleKey).HasMaxLength(256).IsRequired();
            e.Property(n => n.BodyKey).HasMaxLength(256).IsRequired();
            e.Property(n => n.ParamsJson).HasColumnType("jsonb").IsRequired();
            e.Property(n => n.LinkPath).HasMaxLength(512);
            e.Property(n => n.ResourceType).HasMaxLength(64);
            e.Property(n => n.DedupeKey).HasMaxLength(256).IsRequired();

            // The idempotency guard. Consumers insert optimistically and treat a 23505 as
            // "already handled", so this index is load-bearing, not just an optimisation.
            e.HasIndex(n => new { n.TenantId, n.DedupeKey }).IsUnique();
            e.HasIndex(n => new { n.TenantId, n.CreatedAt });

            e.HasQueryFilter(n => n.TenantId == CurrentTenantId);
        });

        builder.Entity<NotificationRecipient>(e =>
        {
            e.ToTable("recipients");
            e.HasKey(r => r.Id);

            e.HasOne(r => r.Notification)
                .WithMany()
                .HasForeignKey(r => r.NotificationId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(r => new { r.NotificationId, r.UserId }).IsUnique();

            // The bell's two queries: unread count, and the newest-first list for one user.
            e.HasIndex(r => new { r.TenantId, r.UserId, r.ReadAt, r.CreatedAt });

            e.HasQueryFilter(r => r.TenantId == CurrentTenantId);
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

public sealed class NotificationsDbContextFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", NotificationsDbContext.Schema))
            .Options;
        return new NotificationsDbContext(options, new NullTenantContext());
    }

    private sealed class NullTenantContext : ITenantContext
    {
        public Guid? TenantId => null;
        public string? TenantSlug => null;
    }
}

public static class NotificationsServiceCollectionExtensions
{
    public static IServiceCollection AddDcmsNotificationsData(
        this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
                               ?? "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev";

        services.AddDbContext<NotificationsDbContext>((sp, options) =>
            options.UseNpgsql(connectionString, npgsql =>
                    npgsql.MigrationsHistoryTable("__ef_migrations_history", NotificationsDbContext.Schema))
                .UseDcmsAuditInterceptors(sp));

        return services;
    }
}
