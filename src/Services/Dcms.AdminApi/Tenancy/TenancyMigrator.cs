using Dcms.Shared.Data.Ai;
using Dcms.Shared.Data.Analytics;
using Dcms.Shared.Data.Chat;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Forms;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Search;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Data.Visitors;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Tenancy;

/// <summary>Applies the tenancy + CMS migrations on startup (gated by Tenancy:Migrate).</summary>
public sealed class TenancyMigrator(IServiceProvider services, IConfiguration configuration, ILogger<TenancyMigrator> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("Tenancy:Migrate", true))
        {
            return;
        }
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<TenancyDbContext>().Database.MigrateAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<CmsDbContext>().Database.MigrateAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<MediaDbContext>().Database.MigrateAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<SitesDbContext>().Database.MigrateAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<AiDbContext>().Database.MigrateAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<SearchDbContext>().Database.MigrateAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<AnalyticsDbContext>().Database.MigrateAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<VisitorsDbContext>().Database.MigrateAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<ChatDbContext>().Database.MigrateAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<FormsDbContext>().Database.MigrateAsync(cancellationToken);
        logger.LogInformation("All databases migrated.");

        // Defense-in-depth: apply the RLS backstop once tables exist.
        if (configuration.GetValue("Tenancy:ApplyRls", true))
        {
            var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            await RlsConfigurator.ApplyAsync(tenancy, logger, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
