using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Chat;
using Dcms.Shared.Data.Forms;
using Dcms.Shared.Data.Sites;
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
/// tenant from the site id and forces the per-tenant sandbox (X-Dcms-Sandbox)
/// so preview writes never touch live data.
///
/// It only ever proxies as the caller's own tenant: the site is resolved under
/// the tenant query filter, so a siteId owned by another tenant is a 404.
/// (SEC-14 — it was previously anonymous and cross-tenant.) A companion
/// endpoint resets the tenant's sandbox.
/// </summary>
public static class SitePreviewEndpoints
{
    private static readonly string[] ProxyMethods = ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE"];

    public static IEndpointRouteBuilder MapSitePreview(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/api/admin/sites/{siteId:guid}/preview/api/{**path}", ProxyMethods, ProxyAsync)
            // SEC-14: this was unauthenticated and resolved the target tenant from the siteId
            // with IgnoreQueryFilters(), so an anonymous caller could proxy to content-api as any
            // tenant's sandbox. It now requires site:edit and resolves the site under the tenant
            // filter, so a foreign or unknown siteId is a 404 and the proxy only ever acts as the
            // caller's own tenant.
            .RequirePermission(PlatformPermissions.SiteEdit)
            .AuditExempt("Transparent proxy to content-api. The real action is recorded there, "
                       + "against the sandbox tenant; recording it here too would double every "
                       + "preview interaction.");

        // Wipe the tenant's preview sandbox (forms / visitors / chat). Destructive, so it
        // takes site:edit like every other site endpoint — a bare RequireAuthorization() here
        // let any authenticated account, member or not, delete another tenant's sandbox rows.
        app.MapPost("/api/admin/sites/{siteId:guid}/preview/sandbox/reset", async (
            Guid siteId, SitesDbContext sites, FormsDbContext forms,
            VisitorsDbContext visitors, ChatDbContext chat,
            ITenantContext tenant, IAuditRecorder audit, AuditScope scope, CancellationToken ct) =>
        {
            if (tenant.TenantId is not { } tenantId)
            {
                return Results.Unauthorized();
            }
            // The tenant query filter confirms the site belongs to the caller's tenant.
            if (!await sites.Sites.AsNoTracking().AnyAsync(s => s.Id == siteId, ct))
            {
                return Results.NotFound();
            }

            // Five statements, one act. The per-table breakdown is what a tenant asking
            // "what did reset actually remove?" wants, so it is recorded here rather than
            // left to five records that never mention the sandbox.
            using var _ = scope.SuppressBulkCapture();

            var submissions = await forms.Submissions.IgnoreQueryFilters()
                .Where(x => x.TenantId == tenantId && x.IsSandbox).ExecuteDeleteAsync(ct);
            var messages = await chat.Messages.IgnoreQueryFilters()
                .Where(x => x.TenantId == tenantId && x.IsSandbox).ExecuteDeleteAsync(ct);
            var conversations = await chat.Conversations.IgnoreQueryFilters()
                .Where(x => x.TenantId == tenantId && x.IsSandbox).ExecuteDeleteAsync(ct);
            var tokens = await visitors.RefreshTokens.IgnoreQueryFilters()
                .Where(x => x.TenantId == tenantId && x.IsSandbox).ExecuteDeleteAsync(ct);
            var accounts = await visitors.Accounts.IgnoreQueryFilters()
                .Where(x => x.TenantId == tenantId && x.IsSandbox).ExecuteDeleteAsync(ct);

            var deleted = submissions + messages + conversations + tokens + accounts;

            audit.Declared?
                .With("deleted", deleted)
                .With("rows", new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["forms.submissions"] = submissions,
                    ["chat.messages"] = messages,
                    ["chat.conversations"] = conversations,
                    ["visitors.refresh_tokens"] = tokens,
                    ["visitors.accounts"] = accounts,
                });

            return Results.Ok(new { deleted });
        }).RequirePermission(PlatformPermissions.SiteEdit)
          .WithAudit(AuditActions.SitePreviewReset, "site");

        return app;
    }

    private static async Task ProxyAsync(
        Guid siteId, string? path, HttpContext http,
        SitesDbContext sites, ITenantContext tenant,
        IHttpClientFactory httpClientFactory, CancellationToken ct)
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
        request.Headers.TryAddWithoutValidation("X-Dcms-Sandbox", "1");
        if (http.Request.Headers.Accept.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("Accept", http.Request.Headers.Accept.ToArray());
        }

        var client = httpClientFactory.CreateClient("content-api");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        http.Response.StatusCode = (int)response.StatusCode;
        if (response.Content.Headers.ContentType is { } responseType)
        {
            http.Response.ContentType = responseType.ToString();
        }
        await response.Content.CopyToAsync(http.Response.Body, ct);
    }

}
