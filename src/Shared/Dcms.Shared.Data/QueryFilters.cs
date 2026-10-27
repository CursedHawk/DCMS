using Microsoft.EntityFrameworkCore;

namespace Dcms.Shared.Data;

/// <summary>
/// The names of the query filters every context declares (ADR 0028): <see cref="Tenant"/> on
/// every tenant entity, and <see cref="Environment"/> as well on every <see cref="EnvironmentEntity"/>.
/// Named, so work that crosses tenants can drop the one and keep the other.
/// </summary>
public static class QueryFilters
{
    public const string Tenant = "tenant";
    public const string Environment = "environment";

    /// <summary>
    /// The tenant filter off, the environment filter still on. What cross-tenant work uses
    /// instead of <c>IgnoreQueryFilters()</c>: it names its tenant explicitly, and the
    /// environment it works in comes from the enclosing <see cref="Rls.RlsScope"/> or request —
    /// or from nowhere, in platform scope or with no tenant at all, where it reads every one.
    /// </summary>
    public static IQueryable<T> IgnoreTenantFilter<T>(this IQueryable<T> source) where T : class =>
        source.IgnoreQueryFilters([Tenant]);
}
