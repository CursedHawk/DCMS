using Dcms.Shared.Data;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Tenancy.Usage;

/// <summary>
/// Keeps every tenant's usage snapshot no older than <c>Usage:IntervalHours</c> (default 24).
///
/// <para>Tenants are measured one at a time, oldest snapshot first, and only by the replica
/// holding the lock: each measurement scans the tenant's rows in every table, and vps1 has four
/// cores. A snapshot refreshed by a Recalculate button is simply not due yet.</para>
///
/// <para>Not audited: it reads, and writes only the snapshot.</para>
/// </summary>
public sealed class TenantUsageWorker(
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<TenantUsageWorker> logger) : BackgroundService
{
    private readonly TimeSpan _interval =
        TimeSpan.FromHours(configuration.GetValue("Usage:IntervalHours", 24d));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Nothing here is urgent, and a scan on the startup path would compete with migrations.
        await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PassAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Tenant usage pass failed; retrying next cycle.");
            }
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    private async Task PassAsync(CancellationToken ct)
    {
        var connectionString = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        await using var leadership = await PostgresAdvisoryLock.TryAcquireAsync(
            connectionString, PostgresAdvisoryLock.TenantUsageLockKey, logger, ct);
        if (leadership is null)
        {
            return;
        }

        List<Guid> due;
        using (RlsScope.Platform())
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            var cutoff = DateTimeOffset.UtcNow - _interval;
            var ids = await db.Tenants.Select(t => t.Id).ToListAsync(ct);
            var measured = await db.Usage.IgnoreTenantFilter()
                .ToDictionaryAsync(u => u.TenantId, u => u.MeasuredAt, ct);
            due = ids.Select(Guid.Parse)
                .Where(id => measured.GetValueOrDefault(id) <= cutoff)
                .OrderBy(id => measured.GetValueOrDefault(id))
                .ToList();
        }

        foreach (var tenantId in due)
        {
            // A fresh scope per tenant: one change tracker and one failure each.
            using var scope = services.CreateScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<TenantUsageMeasurer>().MeasureAsync(tenantId, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Tenant usage: measuring {TenantId} failed; it is retried next pass.", tenantId);
            }
        }

        if (due.Count > 0)
        {
            logger.LogInformation("Tenant usage: measured {Count} tenant(s).", due.Count);
        }
    }
}
