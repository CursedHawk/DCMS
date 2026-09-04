using Dcms.Shared.Audit;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Tenancy;

/// <summary>
/// Marks an endpoint that may run with a tenant the caller does not belong to.
///
/// <para>Rare and deliberate: the only legitimate case is a request whose whole purpose is to
/// establish the membership that is missing. Everything else wants the check.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class AllowNonMemberTenantAttribute(string reason) : Attribute
{
    /// <summary>Why this endpoint is exempt. Recorded here so the exemption has to be argued for.</summary>
    public string Reason { get; } = reason;

    /// <summary>
    /// The <c>/api/admin/me/*</c> case. These endpoints scope everything to the caller's own
    /// user id and never read the tenant header, so they disclose nothing cross-tenant — and
    /// they are also how a client recovers. The SPA keeps the selected slug in localStorage,
    /// which goes stale when a user is removed from a workspace or a second account signs in
    /// on the same browser; 403-ing the calls that list the caller's workspaces and effective
    /// permissions would leave them stuck holding a tenant they cannot deselect.
    /// </summary>
    public const string SelfScoped =
        "Scoped to the caller's own user id and independent of the tenant header. Also the "
        + "path a client uses to recover from a stale tenant selection.";
}

/// <summary>
/// Requires the authenticated caller to be a member of the tenant they named.
///
/// <para><b>Why this exists.</b> On the admin plane the ambient tenant comes from the
/// <c>X-Dcms-Tenant</c> header and <see cref="TenantStore"/> resolves it by identifier — it
/// does not, and cannot, know who is asking. Membership was therefore established in exactly
/// one place: <c>PermissionAuthorizationHandler</c>, which asks the resolver for the caller's
/// permissions <i>in that tenant</i> and gets an empty set for a non-member. That is correct,
/// but it only protects endpoints that ask for a permission. An endpoint guarded by a bare
/// <c>RequireAuthorization()</c> got the tenant context with no membership check at all, which
/// made every one of them readable — and in the case of the preview sandbox reset, writable —
/// against any tenant by any authenticated account.</para>
///
/// <para>Guarding the tenant context itself, rather than auditing endpoints one at a time,
/// is what makes the next <c>RequireAuthorization()</c> endpoint safe by default.</para>
///
/// <para>Runs after authentication and tenant resolution and before authorization, so the
/// permission handler still sees a tenant context for the requests that survive.</para>
/// </summary>
public sealed class TenantMembershipMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ITenantContext tenant, CurrentUser me)
    {
        // No tenant named, or a name that resolved to nothing: there is no tenant context to
        // protect, and the endpoints already treat a null tenant as "select a tenant first".
        if (tenant.TenantId is not { } tenantId)
        {
            await next(context);
            return;
        }

        // Unauthenticated requests are the authorization stack's business, not this one's —
        // failing here would turn a 401 into a 403 on every anonymous route.
        if (me.UserId is not { } userId)
        {
            await next(context);
            return;
        }

        if (me.IsSuperAdmin)
        {
            // Before the suspension gate, deliberately. A suspended tenant is exactly the one a
            // platform operator needs to reach — to inspect it, and above all to resume it. A
            // gate that locked SuperAdmins out too would make suspension a one-way door.
            await next(context);
            return;
        }

        // Suspension is enforced here rather than endpoint by endpoint, for the same reason the
        // membership check is: this is the one place every tenant-scoped admin request passes
        // through, so a new endpoint is covered by default instead of by remembering.
        //
        // The delivery plane is enforced separately, in site-host — suspending a tenant has to
        // stop the public site, not just the admin UI, and neither half implies the other.
        if (tenant.IsSuspended)
        {
            var suspendAudit = context.RequestServices.GetRequiredService<IAuditRecorder>();
            suspendAudit.Record(AuditActions.PermissionDenied)
                .InTenant(tenantId)
                .As(AuditCategory.Security, AuditSeverity.Warning)
                .For("tenant", tenantId, tenant.TenantSlug)
                .With("path", context.Request.Path.Value)
                .Denied("tenant is suspended");

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var exemption = context.GetEndpoint()?.Metadata.GetMetadata<AllowNonMemberTenantAttribute>();
        if (exemption is not null)
        {
            await next(context);
            return;
        }

        // Both columns named explicitly, and query filters ignored, so this probe depends on
        // nothing ambient.
        //
        // TenancyDbContext does put a `TenantId == CurrentTenantId` filter on Memberships, and
        // relying on it would give the same result today. But this is the authorization gate
        // for every bare RequireAuthorization() endpoint on the admin plane, and its
        // correctness would then rest on a filter defined in another file, evaluated against a
        // tenant this method never compares to the one it resolved. If those two ever diverge
        // -- a host wiring a different ITenantContext, an upstream IgnoreQueryFilters, a
        // refactor of CurrentTenantId -- the failure is silent cross-tenant access rather than
        // an error. Naming both columns costs nothing: (TenantId, UserId) is the unique index,
        // so this is the same single-row probe either way.
        //
        // Deliberately a membership test rather than a permission test: a member holding no
        // permissions is still a member, and telling the two apart is the whole point.
        var db = context.RequestServices.GetRequiredService<TenancyDbContext>();
        var isMember = await db.Memberships.AsNoTracking()
            .IgnoreQueryFilters()
            .AnyAsync(m => m.TenantId == tenantId && m.UserId == userId, context.RequestAborted);

        if (isMember)
        {
            await next(context);
            return;
        }

        var audit = context.RequestServices.GetRequiredService<IAuditRecorder>();
        audit.Record(AuditActions.PermissionDenied)
            .InTenant(tenantId)
            .As(AuditCategory.Security, AuditSeverity.Warning)
            .For("tenant", tenantId, tenant.TenantSlug)
            .With("path", context.Request.Path.Value)
            .Denied("caller is not a member of the tenant named in X-Dcms-Tenant");

        // 403, not 404: the caller is authenticated and the tenant plainly exists — they named
        // it. Pretending otherwise would only make a real member's misconfigured client harder
        // to debug, and the tenant slug is not a secret.
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
    }
}

public static class TenantMembershipMiddlewareExtensions
{
    /// <summary>
    /// Requires tenant membership for any request that names a tenant. Register after
    /// <c>UseAuthentication</c> and <c>UseMultiTenant</c>, before <c>UseAuthorization</c>.
    /// </summary>
    public static IApplicationBuilder UseTenantMembership(this IApplicationBuilder app) =>
        app.UseMiddleware<TenantMembershipMiddleware>();

    /// <summary>
    /// Exempts an endpoint from the membership requirement. See
    /// <see cref="AllowNonMemberTenantAttribute"/> — the bar is "this request exists to create
    /// the membership it is missing".
    /// </summary>
    public static TBuilder AllowNonMemberTenant<TBuilder>(this TBuilder builder, string reason)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new AllowNonMemberTenantAttribute(reason));
        return builder;
    }
}
