namespace Dcms.Shared.Data.Rls;

/// <summary>
/// Tells the database, rather than EF, which tenant a block of work belongs to (ADR 0015).
///
/// <para>Under a <c>NOBYPASSRLS</c> connection, <c>IgnoreQueryFilters()</c> stops widening
/// anything: it removes EF's predicate, and the row-security policy is still there. So work
/// that is not inside an ordinary tenant request says what it is:</para>
///
/// <list type="bullet">
///   <item><see cref="Tenant"/> — act as one named tenant. Provisioning a tenant, a consumer
///   handling one tenant's event, a worker's per-tenant step. The database still narrows to that
///   tenant, so a forgotten <c>TenantId</c> predicate reads that tenant's rows and nobody
///   else's. <b>Prefer this.</b></item>
///   <item><see cref="Platform"/> — every tenant. Only for work that genuinely spans them: a
///   scan for what is due, a retention sweep, a SuperAdmin listing, tenant resolution before a
///   tenant is known. The database checks nothing here, which is why it is the exception.</item>
/// </list>
///
/// <para>They nest, and the innermost wins: a <see cref="Tenant"/> block inside a
/// <see cref="Platform"/> one narrows again, which is how a scan hands each item to work the
/// database confines to that item's tenant. Disposing restores the enclosing state.</para>
///
/// <para>Read by <see cref="TenantGucInterceptor"/>, which sets <c>app.tenant_id</c> and
/// <c>app.scope</c> from it (a <see cref="Tenant"/> block overrides the request's
/// <c>ITenantContext</c>). Without the interceptor — <c>Rls:Enforce</c> off — it is inert, which
/// is what lets call sites adopt it before the services move onto the enforcing role.</para>
///
/// <para>An <see cref="AsyncLocal{T}"/> rather than a scoped service, because the code that needs
/// it is exactly the code that runs outside a request — consumers, timers, Finbuckle's store —
/// and because it has to reach contexts the caller did not construct. It flows into tasks started
/// inside the block and not back out of it.</para>
/// </summary>
public static class RlsScope
{
    private static readonly AsyncLocal<State?> Current = new();

    /// <summary>The tenant a <see cref="Tenant"/> block names, or null outside one.</summary>
    public static Guid? TenantOverride => Current.Value?.Tenant;

    /// <summary>True inside a <see cref="Platform"/> block that no <see cref="Tenant"/> block has
    /// narrowed.</summary>
    public static bool IsPlatform => Current.Value?.Platform == true;

    /// <summary>Act as <paramref name="tenantId"/> until disposed, whatever the request's tenant.</summary>
    public static IDisposable Tenant(Guid tenantId) => Enter(new State(tenantId, Platform: false));

    /// <summary>Widen to every tenant until disposed.</summary>
    public static IDisposable Platform() => Enter(new State(Tenant: null, Platform: true));

    private static Restore Enter(State state)
    {
        var previous = Current.Value;
        Current.Value = state;
        return new Restore(previous);
    }

    private sealed record State(Guid? Tenant, bool Platform);

    private sealed class Restore(State? previous) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            Current.Value = previous;
        }
    }
}
