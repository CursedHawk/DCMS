using Dcms.Plugins.Analytics.Api;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Data.Analytics;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.Analytics;

/// <summary><see cref="IAnalytics"/>: other plugins' events join the page views on the same bus and dashboard.</summary>
internal sealed class AnalyticsTracking(IPluginContext context, IServiceProvider services) : IAnalytics
{
    public async Task TrackAsync(TrackEvent input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Type) || input.Type.Length > 64)
        {
            throw new ContractValidationException("Type is required, at most 64 characters.");
        }
        if (services.GetService<ISandboxContext>() is { IsSandbox: true })
        {
            return;
        }
        await AnalyticsIngestEndpoints.Publish(
            services.GetRequiredService<IEventPublisher>(), context.TenantId, input.Type, input.Path, referrer: null,
            input.SessionId, input.Props, new RequestFacts(null, null, null, null), ct);
    }

    public async Task<AnalyticsSummary> SummaryAsync(AnalyticsSummaryRequest input, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var db = services.GetRequiredService<AnalyticsDbContext>();
        var to = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(1), TimeSpan.Zero);
        var from = to.AddDays(-Math.Clamp(input.Days, 1, 365));

        var events = db.Events.AsNoTracking()
            .Where(e => e.TenantId == context.TenantId && e.OccurredAt >= from && e.OccurredAt < to);
        var top = (await events.Where(e => e.Type == "pageview")
                .GroupBy(e => e.Path).Select(g => new { Path = g.Key, Count = g.LongCount() })
                .OrderByDescending(p => p.Count).Take(10).ToListAsync(ct))
            .Select(p => new PageCount(p.Path, p.Count)).ToList();

        return new AnalyticsSummary(
            from, to,
            await events.LongCountAsync(ct),
            await events.LongCountAsync(e => e.Type == "pageview", ct),
            await events.Where(e => e.VisitorHash != null).Select(e => e.VisitorHash).Distinct().LongCountAsync(ct),
            await events.Where(e => e.SessionId != null).Select(e => e.SessionId).Distinct().LongCountAsync(ct),
            top);
    }
}
