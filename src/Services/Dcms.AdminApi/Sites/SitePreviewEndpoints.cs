using Dcms.Shared.Data.Cms;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Chat;
using Dcms.Shared.Data.Forms;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Data.Visitors;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Sites;

/// <summary>
/// Backs the site IDE's live preview against real tenant content without a
/// public domain. The preview iframe runs the site's own code, which fetches
/// <c>/api/...</c> with no admin token — so this is a site:edit-gated,
/// same-origin reverse proxy to content-api's delivery API that resolves the
/// tenant from the site id. A preview acts on its environment's live data, as the
/// site does (ADR 0026); it is marked only so analytics can leave it out.
///
/// It only ever proxies as the caller's own tenant: the site is resolved under
/// the tenant query filter, so a siteId owned by another tenant is a 404.
/// (SEC-14 — it was previously anonymous and cross-tenant.)
/// </summary>
public static class SitePreviewEndpoints
{
    private static readonly string[] ProxyMethods = ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE"];

    /// <summary>A site user's realm access token (ADR 0022); the same name the edge and content-api use.</summary>
    private const string RealmTokenHeader = "X-Dcms-Realm-Token";
    private const string VisitorTokenHeader = "X-Dcms-Visitor-Token";

    public static IEndpointRouteBuilder MapSitePreview(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/api/admin/sites/{siteId:guid}/preview/api/{**path}", ProxyMethods, ProxyAsync)
            // SEC-14: this was unauthenticated and resolved the target tenant from the siteId
            // with IgnoreQueryFilters(), so an anonymous caller could proxy to content-api as any
            // tenant. It now requires site:edit and resolves the site under the tenant
            // filter, so a foreign or unknown siteId is a 404 and the proxy only ever acts as the
            // caller's own tenant.
            .RequirePermission(PlatformPermissions.SiteEdit)
            .AuditExempt("Transparent proxy to content-api. The real action is recorded there, "
                       + "against the tenant; recording it here too would double every "
                       + "preview interaction.");

        /*
         * Which tenant a preview's site belongs to, and the domain whose site access rules it
         * follows: its primary verified one, so a preview shows what that site shows (#269). Asked
         * by the edge, with the member's own session, before it signs a preview in or answers what
         * a preview may see — so site:edit on this site is the gate for all of that.
         */
        app.MapGet("/api/admin/sites/{siteId:guid}/preview/realm", async (
            Guid siteId, SitesDbContext sites, TenancyDbContext tenancy, ITenantContext tenant, CancellationToken ct) =>
        {
            // The tenant query filter: a site in another tenant is not found.
            if (tenant.TenantId is not { } tenantId || !await sites.Sites.AsNoTracking().AnyAsync(s => s.Id == siteId, ct))
            {
                return Results.NotFound();
            }
            var host = await tenancy.Domains.AsNoTracking()
                .Where(d => d.SiteId == siteId && d.VerifiedAt != null && d.Branch == null)
                .OrderByDescending(d => d.IsPrimary)
                .ThenBy(d => d.Hostname)
                .Select(d => d.Hostname)
                .FirstOrDefaultAsync(ct);
            // The realm the preview signs in through: another environment's, when this one takes its users from it (ADR 0026).
            return Results.Ok(new { tenantId = await EnvironmentRealm.OfAsync(tenancy, tenantId, ct), host });
        }).RequirePermission(PlatformPermissions.SiteEdit);


        return app;
    }

    private static async Task ProxyAsync(
        Guid siteId, string? path, HttpContext http,
        SitesDbContext sites, ITenantContext tenant,
        IHttpClientFactory httpClientFactory, AuditScope audit, CancellationToken ct)
    {
        // The caller's own tenant, and the site must belong to it. The tenant query filter (no
        // IgnoreQueryFilters here) is what enforces that: a site in another tenant is invisible,
        // so a foreign siteId is a 404 rather than a cross-tenant proxy. (SEC-14)
        var slug = tenant.TenantSlug;
        if (slug is null || tenant.TenantId is null
            || !await sites.Sites.AsNoTracking().AnyAsync(s => s.Id == siteId, ct))
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var target = $"api/{path}{http.Request.QueryString.Value}";
        using var request = new HttpRequestMessage(new HttpMethod(http.Request.Method), target);

        if (http.Request.ContentLength is > 0 ||
            http.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            request.Content = new StreamContent(http.Request.Body);
            if (http.Request.ContentType is { } contentType)
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
        }
        request.Headers.TryAddWithoutValidation("X-Dcms-Tenant", slug);
        request.Headers.TryAddWithoutValidation(PreviewRequest.Header, "1");
        // The site user the preview is signed in as (#269). Only the edge sets this header — it
        // strips any copy a browser sends — from the preview session it holds for this browser;
        // content-api accepts it only for this tenant's realm.
        if (http.Request.Headers.TryGetValue(RealmTokenHeader, out var realmToken))
        {
            request.Headers.TryAddWithoutValidation(RealmTokenHeader, realmToken.ToArray());
        }
        // The visitor the preview signed in (Visitor Authentication): their own bearer, which
        // cannot travel as Authorization here — that header is the console's. It grants nothing
        // the caller could not get by calling the site's API directly.
        if (http.Request.Headers.TryGetValue(VisitorTokenHeader, out var visitorToken)
            && visitorToken.ToString() is { Length: > 0 and < 4096 } token
            && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
        if (http.Request.Headers.Accept.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("Accept", http.Request.Headers.Accept.ToArray());
        }

        var client = httpClientFactory.CreateClient("content-api");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        // content-api recorded any refusal itself, against the tenant.
        audit.StatusRelayed = true;
        http.Response.StatusCode = (int)response.StatusCode;
        if (response.Content.Headers.ContentType is { } responseType)
        {
            http.Response.ContentType = responseType.ToString();
        }
        await response.Content.CopyToAsync(http.Response.Body, ct);
    }

}
