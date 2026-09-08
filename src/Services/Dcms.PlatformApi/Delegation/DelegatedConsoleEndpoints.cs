using Dcms.Shared.Audit.Http;
using Dcms.Shared.Security;
using Dcms.Shared.Security.Authorization;

namespace Dcms.PlatformApi.Delegation;

/// <summary>
/// The console pages admin-api performs for us: certificates, the platform bell, tenant
/// lifecycle and the analytics prune.
///
/// <para><b>What this layer adds is the permission check.</b> On admin-api these endpoints are
/// gated on the SuperAdmin role, because the tenant permission model does not reach
/// platform-wide state and there was nothing finer to ask. Here each one names a
/// <see cref="PlatformConsolePermissions"/> key, which is what makes those keys mean something:
/// <c>CertificatesManage</c>, <c>NotificationsRead</c>, <c>TenantsLifecycle</c> and
/// <c>OpsAct</c> enforced nothing anywhere before this. A support operator can now be given the
/// bell without being given the tenant suspend button.</para>
///
/// <para><b>And what it deliberately does not add is a second copy of the contract.</b> Bodies
/// and responses pass through untouched — see <see cref="AdminApiProxy"/>.</para>
/// </summary>
public static class DelegatedConsoleEndpoints
{
    public static IEndpointRouteBuilder MapDelegatedConsoleEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- The platform's own TLS certificates (edge schema, ADR 0011) ----

        var certificates = app.MapGroup("/api/platform/certificates");

        Delegate(certificates, HttpMethod.Get, "", "/api/admin/platform/certificates",
            PlatformConsolePermissions.CertificatesManage);
        Delegate(certificates, HttpMethod.Get, "/{id:guid}/attempts",
            "/api/admin/platform/certificates/{0}/attempts",
            PlatformConsolePermissions.CertificatesManage);
        Delegate(certificates, HttpMethod.Post, "", "/api/admin/platform/certificates",
            PlatformConsolePermissions.CertificatesManage);
        Delegate(certificates, HttpMethod.Put, "/{id:guid}", "/api/admin/platform/certificates/{0}",
            PlatformConsolePermissions.CertificatesManage);
        Delegate(certificates, HttpMethod.Delete, "/{id:guid}", "/api/admin/platform/certificates/{0}",
            PlatformConsolePermissions.CertificatesManage);
        Delegate(certificates, HttpMethod.Post, "/{id:guid}/reissue",
            "/api/admin/platform/certificates/{0}/reissue",
            PlatformConsolePermissions.CertificatesManage);

        // ---- The console's bell (notifications schema) ----

        var notifications = app.MapGroup("/api/platform/notifications");

        Delegate(notifications, HttpMethod.Get, "", "/api/admin/platform/notifications",
            PlatformConsolePermissions.NotificationsRead, forwardQuery: true);
        Delegate(notifications, HttpMethod.Get, "/unread-count",
            "/api/admin/platform/notifications/unread-count",
            PlatformConsolePermissions.NotificationsRead);
        Delegate(notifications, HttpMethod.Post, "/{id:guid}/read",
            "/api/admin/platform/notifications/{0}/read",
            PlatformConsolePermissions.NotificationsRead);
        Delegate(notifications, HttpMethod.Post, "/read-all",
            "/api/admin/platform/notifications/read-all",
            PlatformConsolePermissions.NotificationsRead);
        Delegate(notifications, HttpMethod.Post, "/{id:guid}/dismiss",
            "/api/admin/platform/notifications/{0}/dismiss",
            PlatformConsolePermissions.NotificationsRead);

        // ---- Tenant lifecycle (tenancy schema + an event site-host depends on) ----

        var tenants = app.MapGroup("/api/platform/tenants");

        Delegate(tenants, HttpMethod.Post, "/{id:guid}/suspend", "/api/admin/tenants/{0}/suspend",
            PlatformConsolePermissions.TenantsLifecycle);
        Delegate(tenants, HttpMethod.Post, "/{id:guid}/resume", "/api/admin/tenants/{0}/resume",
            PlatformConsolePermissions.TenantsLifecycle);

        // ---- Retention (a batched delete on analytics.events) ----

        Delegate(app.MapGroup("/api/platform/ops"), HttpMethod.Post, "/analytics/prune",
            "/api/admin/analytics/prune", PlatformConsolePermissions.OpsAct);

        return app;
    }

    /// <summary>
    /// One delegated route: authorize the operator here, perform it there.
    /// </summary>
    /// <param name="upstream">
    /// The admin-api path, with <c>{0}</c> where the route's <c>id</c> goes. A format slot
    /// rather than string concatenation so the id is always the route-bound Guid — a caller
    /// cannot put a path into it, because the route constraint has already refused anything
    /// that is not a Guid.
    /// </param>
    /// <param name="forwardQuery">
    /// Carry the query string across. Only the notification list has one (cursor, limit,
    /// unreadOnly); everywhere else forwarding it would pass parameters this console never
    /// documented straight into another service.
    /// </param>
    private static void Delegate(
        RouteGroupBuilder group,
        HttpMethod method,
        string pattern,
        string upstream,
        string permission,
        bool forwardQuery = false)
    {
        var builder = group.MapMethods(
            pattern.Length == 0 ? "/" : pattern,
            [method.Method],
            async (HttpContext context, AdminApiProxy proxy, Guid? id, CancellationToken ct) =>
            {
                var path = upstream.Contains("{0}", StringComparison.Ordinal)
                    ? string.Format(System.Globalization.CultureInfo.InvariantCulture, upstream, id)
                    : upstream;

                if (forwardQuery && context.Request.QueryString.HasValue)
                {
                    path += context.Request.QueryString.Value;
                }

                await proxy.ForwardAsync(method, path, context, ct);
            });

        builder.RequirePlatformPermission(permission);

        // Audited where it happens, not here. admin-api records the action against this
        // operator -- the propagation headers are what make that true -- so a record written
        // here as well would double every certificate edit and every suspension in the log.
        builder.AuditExempt(
            "Delegated to admin-api, which records the action against the operator named in the "
            + "propagated actor headers. Recording here too would duplicate every entry.");
    }
}
