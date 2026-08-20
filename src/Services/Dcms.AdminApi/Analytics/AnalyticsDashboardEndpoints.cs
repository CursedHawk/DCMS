using Dcms.Shared.Data.Analytics;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Analytics;

/// <summary>
/// Analytics dashboard and maintenance for the current tenant.
///
/// Two sources, chosen per question. Totals and the time series come from the daily
/// rollups, which are small and pre-aggregated. Anything that needs a dimension the
/// rollups do not carry — unique visitors, country, device, referrer, campaign —
/// reads the raw event table, which is indexed on (TenantId, OccurredAt).
///
/// Unique visitors specifically cannot come from a rollup: distinct counts do not
/// sum, so per-day visitor columns could not be added up into a period total
/// without over-counting everyone who came back the next day.
/// </summary>
public static class AnalyticsDashboardEndpoints
{
    /// <summary>How many rows each breakdown returns. Enough to be useful, bounded.</summary>
    private const int TopN = 20;

    public static IEndpointRouteBuilder MapAnalyticsDashboard(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/analytics", async (
            int? days, DateTimeOffset? from, DateTimeOffset? to, string? type, string? path,
            string? country, string? device, AnalyticsDbContext db, ITenantContext tenant,
            CancellationToken ct) =>
        {
            var tenantId = tenant.TenantId!.Value;
            var (start, end) = ResolveRange(days, from, to);

            // Filters apply to the raw events. When any is set the summary has to come
            // from there too, or the headline numbers would describe a different
            // population than the breakdowns below them.
            var filtered = !string.IsNullOrWhiteSpace(type)
                           || !string.IsNullOrWhiteSpace(path)
                           || !string.IsNullOrWhiteSpace(country)
                           || !string.IsNullOrWhiteSpace(device);

            var events = db.Events.AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.OccurredAt >= start && e.OccurredAt < end);
            if (!string.IsNullOrWhiteSpace(type)) events = events.Where(e => e.Type == type);
            if (!string.IsNullOrWhiteSpace(country)) events = events.Where(e => e.Country == country);
            if (!string.IsNullOrWhiteSpace(device)) events = events.Where(e => e.Device == device);
            if (!string.IsNullOrWhiteSpace(path)) events = events.Where(e => e.Path.StartsWith(path));

            var totalEvents = await events.LongCountAsync(ct);
            var visitors = await events.Where(e => e.VisitorHash != null)
                .Select(e => e.VisitorHash).Distinct().LongCountAsync(ct);
            var sessions = await events.Where(e => e.SessionId != null)
                .Select(e => e.SessionId).Distinct().LongCountAsync(ct);
            var pageviews = await events.CountAsync(e => e.Type == "pageview", ct);

            // Time series: rollups when unfiltered (cheap), raw events otherwise.
            var startDay = DateOnly.FromDateTime(start.UtcDateTime);
            var endDay = DateOnly.FromDateTime(end.UtcDateTime);
            var series = filtered
                ? (await events
                    .GroupBy(e => new { e.OccurredAt.Year, e.OccurredAt.Month, e.OccurredAt.Day })
                    .Select(g => new
                    {
                        g.Key.Year,
                        g.Key.Month,
                        g.Key.Day,
                        Events = g.LongCount(),
                        Visitors = g.Select(e => e.VisitorHash).Distinct().LongCount(),
                    })
                    .ToListAsync(ct))
                    .Select(x => new DayPoint(
                        new DateOnly(x.Year, x.Month, x.Day).ToString("yyyy-MM-dd"), x.Events, x.Visitors))
                    .OrderBy(x => x.Day)
                    .ToList()
                : await UnfilteredSeriesAsync(db, tenantId, startDay, endDay, events, ct);

            var topPaths = await events
                .GroupBy(e => e.Path)
                .Select(g => new { key = g.Key, count = g.LongCount() })
                .OrderByDescending(x => x.count).Take(TopN).ToListAsync(ct);

            var byType = await events
                .GroupBy(e => e.Type)
                .Select(g => new { key = g.Key, count = g.LongCount() })
                .OrderByDescending(x => x.count).ToListAsync(ct);

            // Referrers are grouped by host: one row per site that links here, rather
            // than one per individual linking page.
            var topSources = await events
                .Where(e => e.Referrer != null && e.Referrer != "")
                .GroupBy(e => e.Referrer!)
                .Select(g => new { key = g.Key, count = g.LongCount() })
                .OrderByDescending(x => x.count).Take(100).ToListAsync(ct);

            var byCountry = await events
                .Where(e => e.Country != null)
                .GroupBy(e => e.Country!)
                .Select(g => new { key = g.Key, count = g.LongCount() })
                .OrderByDescending(x => x.count).Take(TopN).ToListAsync(ct);

            var byDevice = await events
                .Where(e => e.Device != null)
                .GroupBy(e => e.Device!)
                .Select(g => new { key = g.Key, count = g.LongCount() })
                .OrderByDescending(x => x.count).ToListAsync(ct);

            var byBrowser = await events
                .Where(e => e.Browser != null)
                .GroupBy(e => e.Browser!)
                .Select(g => new { key = g.Key, count = g.LongCount() })
                .OrderByDescending(x => x.count).Take(TopN).ToListAsync(ct);

            var byCampaign = await events
                .Where(e => e.UtmSource != null)
                .GroupBy(e => new { e.UtmSource, e.UtmMedium, e.UtmCampaign })
                .Select(g => new
                {
                    source = g.Key.UtmSource!,
                    medium = g.Key.UtmMedium,
                    campaign = g.Key.UtmCampaign,
                    count = g.LongCount(),
                })
                .OrderByDescending(x => x.count).Take(TopN).ToListAsync(ct);

            return Results.Ok(new
            {
                range = new { from = start, to = end },
                summary = new { events = totalEvents, pageviews, visitors, sessions },
                series,
                topPaths = topPaths.Select(x => new { path = x.key, count = x.count }),
                byType = byType.Select(x => new { type = x.key, count = x.count }),
                topSources = GroupByHost(topSources.Select(x => (x.key, x.count))),
                byCountry = byCountry.Select(x => new { country = x.key, count = x.count }),
                byDevice = byDevice.Select(x => new { device = x.key, count = x.count }),
                byBrowser = byBrowser.Select(x => new { browser = x.key, count = x.count }),
                byCampaign,
            });
        }).RequirePermission(PlatformPermissions.AnalyticsRead);

        // The dimensions the SPA's filter dropdowns offer, so they list what this
        // tenant actually has rather than a hard-coded guess.
        app.MapGet("/api/admin/analytics/dimensions", async (
            int? days, AnalyticsDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            var tenantId = tenant.TenantId!.Value;
            var (start, end) = ResolveRange(days, null, null);
            var events = db.Events.AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.OccurredAt >= start && e.OccurredAt < end);

            return Results.Ok(new
            {
                types = await events.Select(e => e.Type).Distinct().OrderBy(x => x).ToListAsync(ct),
                countries = await events.Where(e => e.Country != null)
                    .Select(e => e.Country!).Distinct().OrderBy(x => x).ToListAsync(ct),
                devices = await events.Where(e => e.Device != null)
                    .Select(e => e.Device!).Distinct().OrderBy(x => x).ToListAsync(ct),
            });
        }).RequirePermission(PlatformPermissions.AnalyticsRead);

        // Clearing statistics. `before` deletes only what is older than that instant,
        // which is the routine use (retention); omitting it wipes the tenant's history.
        //
        // Rollups are deleted alongside the raw events, and only for whole days that
        // fall entirely inside the window — a rollup row is a whole day's total, so
        // deleting it for a partially-cleared day would throw away counts for events
        // that are still there.
        app.MapDelete("/api/admin/analytics", async (
            DateTimeOffset? before, AnalyticsDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            var tenantId = tenant.TenantId!.Value;

            var events = db.Events.Where(e => e.TenantId == tenantId);
            var rollups = db.DailyRollups.Where(r => r.TenantId == tenantId);
            if (before is { } cutoff)
            {
                events = events.Where(e => e.OccurredAt < cutoff);
                var lastWholeDay = DateOnly.FromDateTime(cutoff.UtcDateTime);
                rollups = rollups.Where(r => r.Day < lastWholeDay);
            }

            var deletedEvents = await events.ExecuteDeleteAsync(ct);
            var deletedRollups = await rollups.ExecuteDeleteAsync(ct);
            return Results.Ok(new { deletedEvents, deletedRollups });
        }).RequirePermission(PlatformPermissions.TenantSettings);

        return app;
    }

    private sealed record DayPoint(string Day, long Events, long Visitors);

    /// <summary>
    /// The unfiltered time series: event counts from the rollups (cheap), unique
    /// visitors per day from the raw events (the rollups cannot carry them).
    /// </summary>
    private static async Task<List<DayPoint>> UnfilteredSeriesAsync(
        AnalyticsDbContext db, Guid tenantId, DateOnly startDay, DateOnly endDay,
        IQueryable<Shared.Data.Analytics.AnalyticsEvent> events, CancellationToken ct)
    {
        var counts = await db.DailyRollups.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Day >= startDay && r.Day <= endDay)
            .GroupBy(r => r.Day)
            .Select(g => new { Day = g.Key, Count = g.Sum(r => r.Count) })
            .ToListAsync(ct);

        var visitors = (await events
                .Where(e => e.VisitorHash != null)
                .GroupBy(e => new { e.OccurredAt.Year, e.OccurredAt.Month, e.OccurredAt.Day })
                .Select(g => new
                {
                    g.Key.Year,
                    g.Key.Month,
                    g.Key.Day,
                    Visitors = g.Select(e => e.VisitorHash).Distinct().LongCount(),
                })
                .ToListAsync(ct))
            .ToDictionary(x => new DateOnly(x.Year, x.Month, x.Day), x => x.Visitors);

        return counts
            .OrderBy(x => x.Day)
            .Select(x => new DayPoint(
                x.Day.ToString("yyyy-MM-dd"), x.Count, visitors.GetValueOrDefault(x.Day)))
            .ToList();
    }

    /// <summary>
    /// Collapses referrer URLs to their host, so the sources table has one row per
    /// site linking here instead of one per linking page. Anything unparseable keeps
    /// its raw value rather than being dropped.
    /// </summary>
    private static object GroupByHost(IEnumerable<(string Referrer, long Count)> rows)
    {
        var byHost = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (referrer, count) in rows)
        {
            var host = Uri.TryCreate(referrer, UriKind.Absolute, out var uri) ? uri.Host : referrer;
            byHost[host] = byHost.GetValueOrDefault(host) + count;
        }
        return byHost
            .OrderByDescending(kv => kv.Value)
            .Take(TopN)
            .Select(kv => new { source = kv.Key, count = kv.Value })
            .ToList();
    }

    /// <summary>
    /// Resolves the requested window. An explicit from/to wins; otherwise it is the
    /// last <paramref name="days"/> days (default 30, clamped to a year). The end is
    /// exclusive and rounded up to the next day so "today" is always included whole.
    /// </summary>
    private static (DateTimeOffset Start, DateTimeOffset End) ResolveRange(
        int? days, DateTimeOffset? from, DateTimeOffset? to)
    {
        // Explicitly UTC: DateTimeOffset.UtcNow.Date yields an Unspecified DateTime,
        // which converts back to a DateTimeOffset at the *server's* local offset.
        var end = to ?? new DateTimeOffset(DateTime.UtcNow.Date.AddDays(1), TimeSpan.Zero);
        var start = from ?? end.AddDays(-Math.Clamp(days ?? 30, 1, 365));
        return start <= end ? (start, end) : (end, start);
    }
}
