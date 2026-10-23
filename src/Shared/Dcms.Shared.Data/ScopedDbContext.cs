using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Shared.Data;

/// <summary>
/// A context served from a pool (<see cref="DcmsDbContextRegistration.AddDcmsDbContext{T}"/>) and lent
/// to one request scope at a time.
///
/// <para>Pooled because building a context was most of EF's cost on this platform: a fresh internal
/// service scope, the registration's options lambda and the constructor ran for every context of every
/// request, 15 % of admin-api's and content-api's CPU before any SQL (load test 2026-10-23). A pooled
/// context is built once; what belongs to the request -- its tenant, its scope for the interceptors -- is
/// bound to each lease instead of captured in the constructor, and dropped when the lease ends, so no
/// request can inherit the previous one's tenant.</para>
/// </summary>
public abstract class ScopedDbContext(DbContextOptions options) : DbContext(options)
{
    /// <summary>The request scope this context is lent to; null between leases and for one built by hand.</summary>
    public IServiceProvider? Lease { get; private set; }

    /// <summary>What the query filters and the RLS interceptor read the request's tenant from.</summary>
    public ITenantContext? TenantContext { get; internal set; }

    /// <summary>Lends the context to <paramref name="scope"/>.</summary>
    public void Bind(IServiceProvider scope)
    {
        Lease = scope;
        TenantContext = scope.GetService<ITenantContext>();
        OnBind(scope);
    }

    /// <summary>Anything else a context reads from its scope, taken once per lease.</summary>
    protected virtual void OnBind(IServiceProvider scope)
    {
    }

    /// <summary>Forgets the lease; called when the context goes back to the pool or is disposed.</summary>
    protected virtual void OnRelease()
    {
    }

    public override void Dispose()
    {
        Release();
        base.Dispose();
    }

    public override ValueTask DisposeAsync()
    {
        Release();
        return base.DisposeAsync();
    }

    private void Release()
    {
        Lease = null;
        TenantContext = null;
        OnRelease();
    }

    /// <summary>
    /// The services a context's interceptors should use: its lease for a pooled context, the scope EF
    /// built it in for one registered with <c>AddDbContext</c>. Null for a context built by hand.
    /// </summary>
    public static IServiceProvider? ServicesOf(DbContext? context) => context switch
    {
        ScopedDbContext scoped => scoped.Lease,
        null => null,
        _ => context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider,
    };
}

public static class ScopedDbContextExtensions
{
    /// <summary>
    /// For a context built by hand (tests, design-time factories): no scope, this tenant. A pooled context
    /// may have only the one constructor, so the tenant cannot be a constructor argument.
    /// </summary>
    public static T WithTenant<T>(this T context, ITenantContext tenant) where T : ScopedDbContext
    {
        context.TenantContext = tenant;
        return context;
    }
}
