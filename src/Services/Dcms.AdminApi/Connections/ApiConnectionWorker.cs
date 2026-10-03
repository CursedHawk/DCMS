using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Connections;

/// <summary>
/// Refreshes every connection whose snapshots are older than its interval. Finds them across
/// tenants (a platform-scoped read), then refreshes each as its own tenant, one at a time — a
/// slow provider delays the next refresh, never anyone's site, which serves snapshots.
/// </summary>
public sealed class ApiConnectionWorker(IServiceProvider services, IConfiguration configuration, ILogger<ApiConnectionWorker> logger)
    : BackgroundService
{
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(configuration.GetValue("Connections:PollSeconds", 60));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "API connection refresh sweep failed; retrying next poll.");
            }
            await Task.Delay(_pollInterval, stoppingToken);
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        List<(Guid Tenant, Guid Id)> due;
        using (RlsScope.Platform())
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
            var now = DateTimeOffset.UtcNow;
            var rows = await db.ApiConnections.IgnoreQueryFilters()
                .Select(c => new { c.TenantId, c.Id, c.RefreshedAt, c.RefreshMinutes })
                .ToListAsync(ct);
            due = rows
                .Where(c => c.RefreshedAt is null || c.RefreshedAt.Value.AddMinutes(c.RefreshMinutes) <= now)
                .OrderBy(c => c.RefreshedAt)
                .Take(50)
                .Select(c => (c.TenantId, c.Id))
                .ToList();
        }

        foreach (var (tenant, id) in due)
        {
            using var rls = RlsScope.Tenant(tenant);
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
            var row = await db.ApiConnections.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (row is not null)
            {
                await scope.ServiceProvider.GetRequiredService<ApiConnectionFetcher>().RefreshAsync(row, ct);
            }
        }
    }
}
