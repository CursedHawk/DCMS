using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Chat;
using Dcms.Shared.Data.Forms;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Data.Visitors;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Sites;

/// <summary>
/// Backs the site IDE's live preview against real tenant content without a
/// public domain. The preview iframe runs the site's own code, which fetches
/// <c>/api/...</c> with no admin token — so this is an <b>anonymous</b>,
/// same-origin reverse proxy to content-api's delivery API that resolves the
/// tenant from the site id and forces the per-tenant sandbox (X-Dcms-Sandbox)
/// so preview writes never touch live data.
///
/// Exposing this anonymously is safe: content-api's delivery surface is public
/// by design (the live site serves the same to anonymous visitors, and its
/// collect/submit endpoints already allow any origin). A companion,
/// authenticated endpoint resets the tenant's sandbox.
/// </summary>
public static class SitePreviewEndpoints
{
    private static readonly string[] ProxyMethods = ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE"];

    public static IEndpointRouteBuilder MapSitePreview(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/api/admin/sites/{siteId:guid}/preview/api/{**path}", ProxyMethods, ProxyAsync)
            .AuditExempt("Transparent proxy to content-api. The real action is recorded there, "
                       + "against the sandbox tenant; recording it here too would double every "
                       + "preview interaction.");

        // Wipe the tenant's preview sandbox (forms / visitors / chat). Authenticated
        // admin action, scoped to the caller's current tenant.
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
        }).RequireAuthorization().WithAudit(AuditActions.SitePreviewReset, "site");

        return app;
    }

    private static async Task ProxyAsync(
        Guid siteId, string? path, HttpContext http,
        SitesDbContext sites, TenancyDbContext tenancy,
        IHttpClientFactory httpClientFactory, CancellationToken ct)
    {
        var slug = await ResolveTenantSlugAsync(sites, tenancy, siteId, ct);
        if (slug is null)
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

    private static async Task<string?> ResolveTenantSlugAsync(
        SitesDbContext sites, TenancyDbContext tenancy, Guid siteId, CancellationToken ct)
    {
        var site = await sites.Sites.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == siteId, ct);
        if (site is null)
        {
            return null;
        }
        var tenant = await tenancy.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == site.TenantId.ToString(), ct);
        return tenant?.Identifier;
    }
}
