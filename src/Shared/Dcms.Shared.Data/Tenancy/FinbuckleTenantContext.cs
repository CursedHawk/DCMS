using Dcms.Shared.Kernel.Abstractions;
using Finbuckle.MultiTenant.Abstractions;

namespace Dcms.Shared.Data.Tenancy;

/// <summary>
/// Bridges the Finbuckle-resolved tenant into the app-wide
/// <see cref="ITenantContext"/> used by the DbContext filters and authorization.
/// </summary>
public sealed class FinbuckleTenantContext(IMultiTenantContextAccessor<Tenant> accessor) : ITenantContext
{
    private Tenant? Current => accessor.MultiTenantContext?.TenantInfo;

    public Guid? TenantId => Current is { } t && Guid.TryParse(t.Id, out var id) ? id : null;

    public string? TenantSlug => Current?.Identifier;

    // Free: Finbuckle already loaded the whole tenant row to resolve the identifier, so this
    // costs no query. Reading the status from a second lookup would add one to every request.
    public bool IsSuspended => Current?.Status == TenantStatus.Suspended;
}
