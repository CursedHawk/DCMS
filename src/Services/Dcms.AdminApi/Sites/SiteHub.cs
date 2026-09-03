using System.Security.Claims;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Sites;

/// <summary>
/// Live state for one site's workspace: its deployments and the commits landing on its
/// branches. Everyone with the site open is in the same group, which is the point —
/// a build somebody else started, or a commit somebody else pushed to the branch you are
/// editing, is news you need and had no way of receiving.
///
/// <para>Push-only, like <c>NotificationHub</c>: there are no client-to-server methods. What
/// arrives here is a hint that something changed, carrying enough to render immediately; the
/// authoritative read is still the REST endpoint, which is where the permission checks, the
/// tenant filter and the audit middleware live. A hub method writing to the same rows would be
/// a second, differently-guarded write path.</para>
///
/// <para><b>Tenant and site come from the query string</b> rather than the
/// <c>X-Dcms-Tenant</c> header, because the WebSocket transport cannot set custom headers.
/// Every read here therefore names its tenant explicitly with <c>IgnoreQueryFilters</c> instead
/// of leaning on the ambient filter, matching <c>NotificationHub</c> and <c>ChatHub</c>.</para>
/// </summary>
[Authorize]
public sealed class SiteHub(IServiceProvider services, ILogger<SiteHub> logger) : Hub
{
    /// <summary>
    /// The group everyone watching one site shares. Public so the endpoints and the event
    /// consumers can fan into it through <c>IHubContext</c> without opening a connection.
    /// </summary>
    public static string SiteGroup(Guid tenantId, Guid siteId) => $"site:{tenantId}:{siteId}";

    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        var slug = http?.Request.Query["tenant"].ToString();
        var siteParam = http?.Request.Query["siteId"].ToString();

        if (string.IsNullOrWhiteSpace(slug) || !Guid.TryParse(siteParam, out var siteId))
        {
            Context.Abort();
            return;
        }

        if (!Guid.TryParse(Context.User?.FindFirstValue("sub"), out var userId))
        {
            Context.Abort();
            return;
        }

        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<TenantStore>();
        var tenant = await store.GetByIdentifierAsync(slug);
        if (tenant is null || !Guid.TryParse(tenant.Id, out var tenantId))
        {
            Context.Abort();
            return;
        }

        // Membership is re-checked here rather than trusted from the token: per ADR 0003 the
        // access token carries no tenant claim at all, so this is the only thing between an
        // authenticated user and another tenant's build stream.
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var isMember = await tenancy.Memberships.IgnoreQueryFilters()
            .AnyAsync(m => m.TenantId == tenantId && m.UserId == userId);
        if (!isMember)
        {
            logger.LogWarning(
                "Site hub connection refused: user {User} is not a member of tenant {Tenant}.",
                userId, tenantId);
            Context.Abort();
            return;
        }

        // And the site must be that tenant's. Without this a member of tenant A could name any
        // site id and join the group another tenant's pushes land in — the group key contains
        // the tenant id, but the caller supplies both halves, so only this check ties them
        // together.
        var sites = scope.ServiceProvider.GetRequiredService<SitesDbContext>();
        var siteBelongs = await sites.Sites.IgnoreQueryFilters()
            .AnyAsync(s => s.Id == siteId && s.TenantId == tenantId);
        if (!siteBelongs)
        {
            logger.LogWarning(
                "Site hub connection refused: site {Site} does not belong to tenant {Tenant}.",
                siteId, tenantId);
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, SiteGroup(tenantId, siteId));
        await base.OnConnectedAsync();
    }
}
