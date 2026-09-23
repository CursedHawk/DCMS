namespace Dcms.Shared.Data.Rls;

/// <summary>
/// Marks a block of work as deliberately cross-tenant, for the database's benefit (ADR 0015).
///
/// <para>Under a <c>NOBYPASSRLS</c> connection, <c>IgnoreQueryFilters()</c> stops widening
/// anything: it removes EF's predicate, and the row-security policy is still there. The paths
/// that really do span tenants — outbox dispatchers, the publish worker, SuperAdmin listings,
/// tenant resolution itself — say so with this instead:</para>
///
/// <code>
/// using (PlatformScope.Enter())
/// {
///     var due = await db.ScheduledPublishes.IgnoreQueryFilters().ToListAsync(ct);
/// }
/// </code>
///
/// <para>While active, <see cref="TenantGucInterceptor"/> sets <c>app.scope = 'platform'</c>,
/// which the <c>platform_scope</c> policy admits. Nothing else does: without the interceptor
/// (<c>Rls:Enforce</c> off) this is inert, which is what lets call sites adopt it ahead of the
/// services moving onto the enforcing role.</para>
///
/// <para>An <see cref="AsyncLocal{T}"/> rather than a scoped service, because the code that needs
/// it is exactly the code that runs outside a request — consumers, timers, and Finbuckle's
/// store — and because it has to reach contexts the caller did not construct. It flows into
/// tasks started inside the block and not back out of it.</para>
/// </summary>
public static class PlatformScope
{
    private static readonly AsyncLocal<bool> Current = new();

    /// <summary>True inside an <see cref="Enter"/> block on this async flow.</summary>
    public static bool IsActive => Current.Value;

    /// <summary>
    /// Widens to every tenant until disposed. Nests: disposing an inner block restores the outer
    /// one's state rather than switching the scope off underneath it.
    /// </summary>
    public static IDisposable Enter()
    {
        var previous = Current.Value;
        Current.Value = true;
        return new Restore(previous);
    }

    private sealed class Restore(bool previous) : IDisposable
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
