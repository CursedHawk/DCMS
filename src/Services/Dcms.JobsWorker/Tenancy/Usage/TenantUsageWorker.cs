using Dcms.Shared.Jobs;
using Dcms.Shared.Data;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Tenancy.Usage;

/// <summary>
/// Keeps every tenant's usage snapshot no older than <c>Usage:IntervalHours</c> (default 24), as
/// jobs (ADR 0036): an hourly platform job finds the tenants that are due and queues one
/// <see cref="TenantUsageJob"/> each, which shows in that tenant's own jobs list. A snapshot
/// refreshed by a Recalculate button is simply not due yet.
///
/// <para>Not audited: it reads, and writes only the snapshot.</para>
/// </summary>
public sealed class TenantUsageWorker(
    TenancyDbContext db, IJobQueue jobs, IConfiguration configuration, ILogger<TenantUsageWorker> logger)
    : IJobHandler<RecurringTick>
{
    public const string Kind = "tenancy.usage-sweep";

    private readonly TimeSpan _interval =
        TimeSpan.FromHours(configuration.GetValue("Usage:IntervalHours", 24d));

    public async Task<object?> RunAsync(RecurringTick tick, IJobContext context)
    {
        var ct = context.Cancellation;
        List<Guid> due;
        using (RlsScope.Platform())
        {
            var cutoff = DateTimeOffset.UtcNow - _interval;
            var ids = await db.Tenants.Select(t => t.Id).ToListAsync(ct);
            var measured = await db.Usage.IgnoreTenantFilter()
                .ToDictionaryAsync(u => u.TenantId, u => u.MeasuredAt, ct);
            due = ids.Select(Guid.Parse)
                .Where(id => measured.GetValueOrDefault(id) <= cutoff)
                .OrderBy(id => measured.GetValueOrDefault(id))
                .ToList();
        }

        // One per tenant per day: a sweep that runs again before the job finished queues nothing.
        var day = DateOnly.FromDateTime(DateTime.UtcNow).DayNumber;
        foreach (var tenantId in due)
        {
            await jobs.EnqueueAsync(new JobSpec(
                TenantUsageJob.Kind, tenantId, new TenantUsagePayload(), "jobs.kinds.tenantUsage.title",
                RequiredPermission: TenantUsageJob.Permission, DedupeKey: $"{TenantUsageJob.Kind}:{day}",
                Cancelable: false, Routine: true), ct);
        }
        if (due.Count > 0)
        {
            logger.LogInformation("Tenant usage: queued {Count} measurement(s).", due.Count);
        }
        return new { queued = due.Count };
    }
}

public sealed record TenantUsagePayload;

/// <summary>Measures one tenant's usage: the tenant's own job, so its owners see it run.</summary>
public sealed class TenantUsageJob(TenantUsageMeasurer measurer) : IJobHandler<TenantUsagePayload>
{
    public const string Kind = "tenancy.usage";

    /// <summary>Who sees the job: whoever sees the usage page (Settings, Usage).</summary>
    public const string Permission = Dcms.Shared.Security.PlatformPermissions.TenantSettings;

    public async Task<object?> RunAsync(TenantUsagePayload payload, IJobContext context)
    {
        await measurer.MeasureAsync(context.TenantId!.Value, context.Cancellation);
        return null;
    }
}
