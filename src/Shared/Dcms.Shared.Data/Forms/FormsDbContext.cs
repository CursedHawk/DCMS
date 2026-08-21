using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Data.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dcms.Shared.Data.Forms;

/// <summary>
/// One visitor submission of a form declared on a Forms plugin instance. The
/// payload is stored as jsonb exactly as submitted (after per-field validation),
/// so changing a form's fields never invalidates historical submissions.
/// </summary>
public sealed class FormSubmission : TenantEntity, ISandboxScoped
{
    public Guid PluginInstanceId { get; set; }

    /// <summary>True for submissions made from a site preview's sandbox.</summary>
    public bool IsSandbox { get; set; }

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
public class FormsDbContext(
    DbContextOptions<FormsDbContext> options, ITenantContext tenantContext, ISandboxContext sandboxContext)
    : DbContext(options)
{
    public const string Schema = "forms";

    private Guid CurrentTenantId => tenantContext.TenantId ?? Guid.Empty;
    private bool CurrentSandbox => sandboxContext.IsSandbox;

    public DbSet<FormSubmission> Submissions => Set<FormSubmission>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Audit records are written by the same SaveChanges as the change they describe.
        builder.MapAuditOutbox();
        builder.HasDefaultSchema(Schema);

        builder.Entity<FormSubmission>(e =>
        {
            e.ToTable("form_submissions");
            e.HasKey(s => s.Id);
            e.Property(s => s.FormName).HasMaxLength(64).IsRequired();
            e.Property(s => s.DataJson).HasColumnType("jsonb");
            e.Property(s => s.UserAgent).HasMaxLength(512);
            e.HasIndex(s => new { s.TenantId, s.IsSandbox, s.PluginInstanceId, s.FormName, s.SubmittedAt });
            e.HasQueryFilter(s => s.TenantId == CurrentTenantId && s.IsSandbox == CurrentSandbox);
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

public sealed class FormsDbContextFactory : IDesignTimeDbContextFactory<FormsDbContext>
{
    public FormsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<FormsDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", FormsDbContext.Schema))
            .Options;
        return new FormsDbContext(options, new NullTenantContext(), Sandbox.DisabledSandboxContext.Instance);
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

        services.AddDbContext<FormsDbContext>((sp, options) =>
            options.UseNpgsql(connectionString, npgsql =>
                    npgsql.MigrationsHistoryTable("__ef_migrations_history", FormsDbContext.Schema))
                .UseDcmsAuditInterceptors(sp));

        services.TryAddScoped<ISandboxContext>(_ => Sandbox.DisabledSandboxContext.Instance);
        return services;
    }
}
