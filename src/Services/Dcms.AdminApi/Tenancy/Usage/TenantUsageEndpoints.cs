using System.Text.Json;
using Dcms.AdminApi.Media;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
using Dcms.Shared.Security.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Tenancy.Usage;

/// <summary>
/// The tenant's usage snapshot (Settings → Usage), the Recalculate buttons, and the tenant
/// directory the platform console's tenant page lists. The console reads the snapshot itself
/// from <c>obs.v_tenant_usage</c>; it comes here only to recalculate.
/// </summary>
public static class TenantUsageEndpoints
{
    /// <summary>A measurement scans every table of the tenant; this is how often a button may ask for one.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(2);

    private const string RecalculateExempt =
        "Recalculating reads the tenant's data and replaces a derived snapshot; it changes nothing "
        + "anybody owns.";

    public static IEndpointRouteBuilder MapTenantUsageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/usage", async (
            ITenantContext tenant, TenancyDbContext db, MediaDbContext media, CancellationToken ct) =>
                tenant.TenantId is { } id ? Results.Ok(await SnapshotAsync(id, db, media, ct)) : Results.NotFound())
            .RequirePermission(PlatformPermissions.TenantSettings);

        app.MapPost("/api/admin/usage/recalculate", async (
            ITenantContext tenant, TenancyDbContext db, MediaDbContext media, TenantUsageMeasurer measurer,
            HttpContext http, CancellationToken ct) =>
                tenant.TenantId is { } id ? await RecalculateAsync(id, db, media, measurer, http, ct) : Results.NotFound())
            .RequirePermission(PlatformPermissions.TenantSettings)
            .AuditExempt(RecalculateExempt);

        // Console only, like the storage limit: the tenant is named in the route, not the header.
        app.MapPost("/api/admin/tenants/{tenantId:guid}/usage/recalculate", async (
            Guid tenantId, ConsoleCaller console, TenancyDbContext db, MediaDbContext media,
            TenantUsageMeasurer measurer, HttpContext http, CancellationToken ct) =>
        {
            if (!console.Allowed) return Results.Forbid();
            if (!await db.Tenants.AnyAsync(t => t.Id == tenantId.ToString(), ct)) return Results.NotFound();
            return await RecalculateAsync(tenantId, db, media, measurer, http, ct);
        })
            .RequireAuthorization()
            .AuditExempt(RecalculateExempt)
            .AllowConsoleService(
                "the platform console's tenant page recalculates usage; its API holds dcms.console and "
                + "has already checked the operator holds platform:tenants:write.");

        // Who and what a tenant has, for the console's tenant page. Here rather than an obs view
        // because the views deliberately carry no email address.
        app.MapGet("/api/admin/tenants/{tenantId:guid}/directory", async (
            Guid tenantId, ConsoleCaller console, TenancyDbContext db, CmsDbContext cms, CancellationToken ct) =>
        {
            if (!console.Allowed) return Results.Forbid();
            using var rls = RlsScope.Tenant(tenantId);

            var domains = await db.Domains.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.TenantId == tenantId)
                .OrderByDescending(d => d.IsPrimary).ThenBy(d => d.Hostname)
                .Select(d => new { hostname = d.Hostname, verified = d.VerifiedAt != null, primary = d.IsPrimary, createdAt = d.CreatedAt })
                .ToListAsync(ct);
            var members = await db.Memberships.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.TenantId == tenantId)
                .OrderBy(m => m.Email)
                .Select(m => new { userId = m.UserId, email = m.Email, createdAt = m.CreatedAt, roles = m.Roles.Select(r => r.Role!.Name).ToList() })
                .ToListAsync(ct);
            var plugins = await cms.PluginInstances.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.TenantId == tenantId)
                .OrderBy(p => p.Name)
                .Select(p => new { id = p.Id, pluginId = p.PluginId, name = p.Name, slug = p.Slug, enabled = p.Enabled, createdAt = p.CreatedAt })
                .ToListAsync(ct);
            return Results.Ok(new { domains, members, plugins });
        })
            .RequireAuthorization()
            .AllowConsoleService(
                "the platform console's tenant page lists the tenant's domains, members and plugins; its "
                + "API holds dcms.console and has already checked the operator holds platform:tenants:read.");

        return app;
    }

    private static async Task<IResult> RecalculateAsync(
        Guid tenantId, TenancyDbContext db, MediaDbContext media, TenantUsageMeasurer measurer,
        HttpContext http, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(tenantId);
        var last = await db.Usage.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.TenantId == tenantId).Select(u => (DateTimeOffset?)u.MeasuredAt).FirstOrDefaultAsync(ct);
        var wait = last + MinInterval - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.Value.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests,
                detail: "Usage was measured moments ago. Try again in a couple of minutes.");
        }

        // ponytail: two clicks racing past the check both measure; the later write wins and both
        // are correct. A per-tenant advisory lock if measurements ever get expensive enough to matter.
        await measurer.MeasureAsync(tenantId, ct);
        return Results.Ok(await SnapshotAsync(tenantId, db, media, ct));
    }

    private static async Task<long> MediaBytesAsync(MediaDbContext media, Guid tenantId, CancellationToken ct)
    {
        var (originals, renditions) = await MediaFootprint.OfAsync(media, tenantId, ct);
        return originals + renditions;
    }

    private static async Task<object> SnapshotAsync(Guid tenantId, TenancyDbContext db, MediaDbContext media, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(tenantId);
        var usage = await db.Usage.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(u => u.TenantId == tenantId, ct);
        return new
        {
            measuredAt = usage?.MeasuredAt,
            totalBytes = usage?.TotalBytes ?? 0,
            // The storage limit and what it counts (media files only), for the meter beside the breakdown.
            quotaBytes = await MediaFootprint.QuotaAsync(db, tenantId, ct),
            mediaBytes = await MediaBytesAsync(media, tenantId, ct),
            categories = JsonDocument.Parse(usage?.CategoriesJson ?? "[]").RootElement,
        };
    }
}
