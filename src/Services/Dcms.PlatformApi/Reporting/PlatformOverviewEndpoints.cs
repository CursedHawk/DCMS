using Dcms.Shared.Security;

namespace Dcms.PlatformApi.Reporting;

/// <summary>
/// The console's landing page and tenant directory, read from the <c>obs.*</c> views.
///
/// <para>These are the native tiles that sit above the embedded Grafana dashboards. They
/// exist rather than deferring everything to an iframe for one reason: Grafana is a separate
/// container with its own session, and the first question an operator asks — is anything
/// wrong — should still have an answer on a morning when Grafana is the thing that is
/// broken.</para>
/// </summary>
public static class PlatformOverviewEndpoints
{
    public sealed record OverviewTotals(
        int Tenants,
        int ActiveTenants,
        int SuspendedTenants,
        int Sites,
        int ContentItems,
        int PublishedItems,
        int MediaAssets,
        long StorageBytes,
        int Members);

    public sealed record GrowthPoint(
        DateTimeOffset Day, int NewTenants, int NewUsers, int NewSites, int NewContent);

    public sealed record TenantRow(
        Guid TenantId,
        string Slug,
        string? Name,
        string Status,
        DateTimeOffset? CreatedAt,
        int Members,
        int Domains,
        int VerifiedDomains,
        int Sites,
        int ContentItems,
        int PublishedItems,
        int MediaAssets,
        int EnabledPlugins,
        int VisitorAccounts,
        int FormSubmissions,
        long StorageBytes);

    public static IEndpointRouteBuilder MapPlatformOverviewEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/platform");

        group.MapGet("/overview", async (ObservabilityQuery q, CancellationToken ct) =>
        {
            // One round trip rather than eight counts. v_tenant_inventory is already one row
            // per tenant with every count on it, so the totals are a fold over that view.
            var totals = await q.SingleAsync(
                """
                SELECT
                    count(*)                                             AS tenants,
                    count(*) FILTER (WHERE status = 'Active')            AS active_tenants,
                    count(*) FILTER (WHERE status = 'Suspended')         AS suspended_tenants,
                    COALESCE(sum(sites), 0)                              AS sites,
                    COALESCE(sum(content_items), 0)                      AS content_items,
                    COALESCE(sum(published_items), 0)                    AS published_items,
                    COALESCE(sum(media_assets), 0)                       AS media_assets,
                    COALESCE(sum(members), 0)                            AS members
                FROM obs.v_tenant_inventory
                """,
                r => new
                {
                    Tenants = r.GetInt32OrZero("tenants"),
                    Active = r.GetInt32OrZero("active_tenants"),
                    Suspended = r.GetInt32OrZero("suspended_tenants"),
                    Sites = r.GetInt32OrZero("sites"),
                    Content = r.GetInt32OrZero("content_items"),
                    Published = r.GetInt32OrZero("published_items"),
                    Media = r.GetInt32OrZero("media_assets"),
                    Members = r.GetInt32OrZero("members"),
                },
                ct: ct);

            var storage = await q.SingleAsync(
                "SELECT COALESCE(sum(total_bytes), 0) AS bytes FROM obs.v_storage_usage",
                r => r.GetInt64OrZero("bytes"),
                ct: ct);

            return Results.Ok(new OverviewTotals(
                totals?.Tenants ?? 0,
                totals?.Active ?? 0,
                totals?.Suspended ?? 0,
                totals?.Sites ?? 0,
                totals?.Content ?? 0,
                totals?.Published ?? 0,
                totals?.Media ?? 0,
                storage,
                totals?.Members ?? 0));
        })
        .RequirePlatformPermission(PlatformConsolePermissions.OverviewRead)
        .WithName("PlatformOverview");

        group.MapGet("/growth", async (ObservabilityQuery q, int? days, CancellationToken ct) =>
        {
            // v_growth_daily generates a dense 365-day series, so a missing day is a zero
            // rather than a gap — which is what a chart needs and what a GROUP BY would not give.
            var window = Math.Clamp(days ?? 90, 1, 365);
            var rows = await q.QueryAsync(
                """
                SELECT day, new_tenants, new_users, new_sites, new_content
                FROM obs.v_growth_daily
                WHERE day >= date_trunc('day', now()) - make_interval(days => @days)
                ORDER BY day
                """,
                r => new GrowthPoint(
                    r.GetDateTimeOrNull("day") ?? default,
                    r.GetInt32OrZero("new_tenants"),
                    r.GetInt32OrZero("new_users"),
                    r.GetInt32OrZero("new_sites"),
                    r.GetInt32OrZero("new_content")),
                new Dictionary<string, object?> { ["days"] = window },
                ct);

            return Results.Ok(rows);
        })
        .RequirePlatformPermission(PlatformConsolePermissions.OverviewRead)
        .WithName("PlatformGrowth");

        group.MapGet("/tenants", async (
            ObservabilityQuery q, string? search, CancellationToken ct) =>
        {
            // Storage is joined in rather than fetched per row: the tenant list is the page
            // where "which tenant is using the disk" gets asked, and N+1 on a directory is how
            // a console becomes slow at exactly the scale where it starts mattering.
            var rows = await q.QueryAsync(
                """
                SELECT
                    i.tenant_id, i.tenant_slug, i.tenant_name, i.status, i.created_at,
                    i.members, i.domains, i.verified_domains, i.sites, i.content_items,
                    i.published_items, i.media_assets, i.enabled_plugins, i.visitor_accounts,
                    i.form_submissions,
                    COALESCE(s.total_bytes, 0) AS storage_bytes
                FROM obs.v_tenant_inventory i
                LEFT JOIN obs.v_storage_usage s ON s.tenant_id = i.tenant_id
                -- The ::text casts are load-bearing. A parameter whose only appearance is
                -- `@p IS NULL` gives Postgres nothing to infer a type from, and it refuses to
                -- plan the statement at all: 42P08, "could not determine data type of
                -- parameter $1". The cast is what makes the optional-filter idiom work.
                WHERE @search::text IS NULL
                   OR i.tenant_slug ILIKE '%' || @search::text || '%'
                   OR COALESCE(i.tenant_name, '') ILIKE '%' || @search::text || '%'
                ORDER BY i.created_at DESC NULLS LAST
                """,
                r => new TenantRow(
                    r.GetGuidOrNull("tenant_id") ?? Guid.Empty,
                    r.GetStringOrNull("tenant_slug") ?? string.Empty,
                    r.GetStringOrNull("tenant_name"),
                    r.GetStringOrNull("status") ?? "Unknown",
                    r.GetDateTimeOrNull("created_at"),
                    r.GetInt32OrZero("members"),
                    r.GetInt32OrZero("domains"),
                    r.GetInt32OrZero("verified_domains"),
                    r.GetInt32OrZero("sites"),
                    r.GetInt32OrZero("content_items"),
                    r.GetInt32OrZero("published_items"),
                    r.GetInt32OrZero("media_assets"),
                    r.GetInt32OrZero("enabled_plugins"),
                    r.GetInt32OrZero("visitor_accounts"),
                    r.GetInt32OrZero("form_submissions"),
                    r.GetInt64OrZero("storage_bytes")),
                new Dictionary<string, object?>
                {
                    ["search"] = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
                },
                ct);

            return Results.Ok(rows);
        })
        .RequirePlatformPermission(PlatformConsolePermissions.TenantsRead)
        .WithName("PlatformTenantDirectory");

        return app;
    }
}
