using Dcms.Shared.Kernel.Abstractions;
using Finbuckle.MultiTenant.Abstractions;
using Finbuckle.MultiTenant.AspNetCore.Extensions;
using Microsoft.AspNetCore.Http;

namespace Dcms.Shared.Data.Tenancy;

/// <summary>
/// Bridges the Finbuckle-resolved tenant into the app-wide
/// <see cref="ITenantContext"/> used by the DbContext filters and authorization.
///
/// <para><b>The accessor alone is not enough.</b> It is an AsyncLocal set inside
/// <c>UseMultiTenant</c>, and an AsyncLocal set in an awaited callee does not flow back to the
/// caller — so <c>AuditMiddleware</c>, which wraps the pipeline and records refusals, fallbacks
/// and 5xx after it returns, saw no tenant at all. Every one of those records landed in the
/// platform audit log instead of the tenant's (content-api: all 196,570 in October). The
/// request's own copy on HttpContext survives the return, so it is the fallback.</para>
/// </summary>
public sealed class FinbuckleTenantContext(
    IMultiTenantContextAccessor<Tenant> accessor,
    IHttpContextAccessor? http = null) : ITenantContext
{
    private Tenant? Current =>
        accessor.MultiTenantContext?.TenantInfo ?? http?.HttpContext?.GetMultiTenantContext<Tenant>()?.TenantInfo;

    public Guid? TenantId => Current is { } t && Guid.TryParse(t.Id, out var id) ? id : null;

    public string? TenantSlug => Current?.Identifier;

    // Free: Finbuckle already loaded the whole tenant row to resolve the identifier, so this
    // costs no query. Reading the status from a second lookup would add one to every request.
    public bool IsSuspended => Current?.Status == TenantStatus.Suspended;

    // ADR 0028: more environments than production, as UseDcmsEnvironment found.
    public bool HasEnvironments => EnvironmentResolution.Many(http?.HttpContext);

    // ADR 0028: what UseDcmsEnvironment resolved from the request; production otherwise.
    public Guid? EnvironmentId => EnvironmentResolution.Of(http?.HttpContext) ?? TenantId;
}
