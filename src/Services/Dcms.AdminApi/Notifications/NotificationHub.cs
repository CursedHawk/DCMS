using System.Security.Claims;
using Dcms.Shared.Data.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Notifications;

/// <summary>
/// Push-only transport for admin notifications and for the resource-change hints that keep
/// open consoles current. There are no client-to-server methods:
/// marking read and dismissing go over REST, where they are ordinary authorised requests
/// with the usual audit and tenant middleware around them. A hub method doing the same work
/// would be a second, differently-guarded write path onto the same rows.
///
/// <para>Cross-replica fan-out is the Redis backplane's job, so a consumer running on one
/// admin-api replica can push to a browser connected to any other. Delivery is partitioned
/// by one group per (tenant, user) — recipients are already resolved when a notification is
/// raised, so the hub never evaluates permissions.</para>
///
/// <para><b>Tenant comes from the query string</b>, not the <c>X-Dcms-Tenant</c> header the
/// rest of admin-api uses: the WebSocket transport cannot set custom headers. Every database
/// read here therefore names its tenant explicitly with <see cref="EntityFrameworkQueryableExtensions.IgnoreQueryFilters{TEntity}"/>
/// rather than leaning on the ambient filter, matching <c>ChatHub</c>.</para>
/// </summary>
[Authorize]
public sealed class NotificationHub(IServiceProvider services, ILogger<NotificationHub> logger) : Hub
{
    /// <summary>
    /// Public so out-of-band callers (the consumers, via <c>IHubContext</c>) can fan into the
    /// same group this hub subscribes connections to.
    /// </summary>
    public static string UserGroup(Guid tenantId, Guid userId) => $"notify:{tenantId}:{userId}";

    /// <summary>
    /// Every console open on this tenant, whoever is looking at it.
    ///
    /// <para>Used only for <c>ResourceChanged</c>, which carries a tag naming a class of data
    /// and nothing else — see <see cref="ResourceTags"/>. A notification stays per-user,
    /// because its audience was decided when it was raised and its body is real content.</para>
    /// </summary>
    public static string TenantGroup(Guid tenantId) => $"tenant:{tenantId}";

    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        var slug = http?.Request.Query["tenant"].ToString();
        if (string.IsNullOrWhiteSpace(slug))
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
        // access token carries no tenant claim at all, so this is the only thing standing
        // between an authenticated user and another tenant's notification stream.
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var isMember = await tenancy.Memberships.IgnoreQueryFilters()
            .AnyAsync(m => m.TenantId == tenantId && m.UserId == userId);
        if (!isMember)
        {
            logger.LogWarning(
                "Notification hub connection refused: user {User} is not a member of tenant {Tenant}.",
                userId, tenantId);
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(tenantId, userId));
        await Groups.AddToGroupAsync(Context.ConnectionId, TenantGroup(tenantId));
        await base.OnConnectedAsync();
    }
}
