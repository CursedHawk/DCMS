using Dcms.Shared.Data.Analytics;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Analytics;

/// <summary>Analytics dashboard for the current tenant, served from the daily rollups.</summary>
public static class AnalyticsDashboardEndpoints
{
    public static IEndpointRouteBuilder MapAnalyticsDashboard(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/analytics", async (
            int? days, AnalyticsDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            var tenantId = tenant.TenantId!.Value;
            var from = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-Math.Clamp(days ?? 30, 1, 365)));

            var rollups = await db.DailyRollups.AsNoTracking()
                .Where(r => r.TenantId == tenantId && r.Day >= from)
                .ToListAsync(ct);

            var total = rollups.Sum(r => r.Count);
            var byDay = rollups.GroupBy(r => r.Day)
                .OrderBy(g => g.Key)
                .Select(g => new { day = g.Key.ToString("yyyy-MM-dd"), count = g.Sum(r => r.Count) });
            var topPaths = rollups.GroupBy(r => r.Path)
                .Select(g => new { path = g.Key, count = g.Sum(r => r.Count) })
                .OrderByDescending(x => x.count)
                .Take(20);
            var byType = rollups.GroupBy(r => r.Type)
                .Select(g => new { type = g.Key, count = g.Sum(r => r.Count) })
                .OrderByDescending(x => x.count);

            return Results.Ok(new { total, byDay, topPaths, byType });
        }).RequirePermission(PlatformPermissions.AnalyticsRead);

        return app;
    }
}
