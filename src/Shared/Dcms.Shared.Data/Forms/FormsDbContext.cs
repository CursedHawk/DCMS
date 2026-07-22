using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Shared.Data.Forms;

/// <summary>
/// One visitor submission of a form declared on a Forms plugin instance. The
/// payload is stored as jsonb exactly as submitted (after per-field validation),
/// so changing a form's fields never invalidates historical submissions.
/// </summary>
public sealed class FormSubmission : TenantEntity
{
    public Guid PluginInstanceId { get; set; }

    /// <summary>The form's config name, e.g. "booking".</summary>
    public string FormName { get; set; } = string.Empty;

    public string DataJson { get; set; } = "{}";

    /// <summary>Truncated request origin/user agent, kept for abuse triage only.</summary>
    public string? UserAgent { get; set; }

    public DateTimeOffset SubmittedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Set when an operator has dealt with the submission in the admin.</summary>
    public DateTimeOffset? HandledAt { get; set; }
}

/// <summary>Owns the "forms" schema. Written by content-api, read by admin-api.</summary>
public class FormsDbContext(DbContextOptions<FormsDbContext> options, ITenantContext tenantContext)
    : DbContext(options)
{
    public const string Schema = "forms";

    private Guid CurrentTenantId => tenantContext.TenantId ?? Guid.Empty;

    public DbSet<FormSubmission> Submissions => Set<FormSubmission>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);

        builder.Entity<FormSubmission>(e =>
        {
            e.ToTable("form_submissions");
            e.HasKey(s => s.Id);
            e.Property(s => s.FormName).HasMaxLength(64).IsRequired();
            e.Property(s => s.DataJson).HasColumnType("jsonb");
            e.Property(s => s.UserAgent).HasMaxLength(512);
            e.HasIndex(s => new { s.TenantId, s.PluginInstanceId, s.FormName, s.SubmittedAt });
            e.HasQueryFilter(s => s.TenantId == CurrentTenantId);
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

public sealed class FormsDbContextFactory : IDesignTimeDbContextFactory<FormsDbContext>
{
    public FormsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<FormsDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", FormsDbContext.Schema))
            .Options;
        return new FormsDbContext(options, new NullTenantContext());
    }

    private sealed class NullTenantContext : ITenantContext
    {
        public Guid? TenantId => null;
        public string? TenantSlug => null;
    }
}

public static class FormsServiceCollectionExtensions
{
    public static IServiceCollection AddDcmsFormsData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
                               ?? "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev";

        services.AddDbContext<FormsDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__ef_migrations_history", FormsDbContext.Schema)));

        return services;
    }
}
