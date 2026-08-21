using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Redaction;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dcms.Shared.Data.Audit;

public static class AuditDataServiceCollectionExtensions
{
    /// <summary>
    /// Registers AuditDbContext and switches the audit write path from the logging floor to the
    /// transactional outbox. Called by every service that can reach the audit schema; the two
    /// that cannot — email-worker, which has no database, and site-builder, which is confined to
    /// the <c>sites</c> schema — deliberately do not call this.
    ///
    /// <para>Needs no ITenantContext: the audit tables are deliberately not tenant-filtered.</para>
    /// </summary>
    public static IServiceCollection AddDcmsAuditData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
                               ?? "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev";

        services.AddDbContext<AuditDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__ef_migrations_history", AuditDbContext.Schema)));

        var options = configuration.GetSection(AuditOptions.SectionName).Get<AuditOptions>() ?? new AuditOptions();
        services.TryAddSingleton(options);

        services.TryAddSingleton<AuditEventSerializer>();
        services.TryAddSingleton<IAuditChainKeyProvider>(sp => new AuditChainKeyProvider(
            sp.GetRequiredService<AuditOptions>(),
            sp.GetRequiredService<IHostEnvironment>().IsDevelopment(),
            sp.GetRequiredService<ILogger<AuditChainKeyProvider>>()));

        services.AddScoped<AuditChainAppender>();
        services.AddScoped<AuditChainVerifier>();
        services.AddScoped<AuditChainSealer>();
        services.AddScoped<AuditRetention>();
        services.AddScoped<AuditGapDetector>();

        // Replace the logging floor registered by AddDcmsAudit: from here the durable outbox
        // is the destination, and the log is only reached if that fails.
        services.RemoveAll<IAuditSink>();
        services.AddScoped<IAuditSink, OutboxAuditSink>();

        // One redactor for the process. It is a registry, so building it per request would
        // rebuild the same allowlist thousands of times a second — and, worse, would make a
        // plugin's registration disappear at the end of the request that made it.
        services.TryAddSingleton(sp => new AuditRedactor().Apply());
        services.AddScoped<AuditChangeCapture>();

        // Registering these is necessary but NOT sufficient. EF Core does not discover
        // DI-registered IInterceptors on its own: each context must also call
        // .UseDcmsAuditInterceptors(sp) in its AddDbContext, and every one of them does.
        // Omitting that line costs no error and no warning — the interceptor simply never
        // runs and the atomicity guarantee is quietly inert, which is exactly how it was
        // missed the first time. AuditInterceptorDiscoveryTests exists to catch a recurrence.
        services.AddScoped<IInterceptor, AuditOutboxInterceptor>();
        services.AddScoped<IInterceptor, AuditBulkCommandInterceptor>();

        return services;
    }
}

public sealed class AuditDbContextFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", AuditDbContext.Schema))
            .Options;

        return new AuditDbContext(options);
    }
}
