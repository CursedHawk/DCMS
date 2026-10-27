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

    /// <summary>The request's tenant and environment, set once at the start of it (ADR 0028).</summary>
    private static readonly AsyncLocal<(Guid Tenant, Guid Environment)?> Request = new();

    /// <summary>The tenant a <see cref="Tenant"/> block names, or null outside one.</summary>
    public static Guid? TenantOverride => Current.Value?.Tenant;

    /// <summary>
    /// The environment a <see cref="Tenant"/> or <see cref="Environment"/> block names, or null
    /// outside one. A <see cref="Tenant"/> block names its production, whose id is the tenant's
    /// (ADR 0028).
    /// </summary>
    public static Guid? EnvironmentOverride => Current.Value is { Tenant: { } tenant } state ? state.Environment ?? tenant : null;

    /// <summary>True inside a <see cref="Platform"/> block that no <see cref="Tenant"/> block has
    /// narrowed.</summary>
    public static bool IsPlatform => Current.Value?.Platform == true;

    /// <summary>
    /// Act as <paramref name="tenantId"/> until disposed, whatever the request's tenant. In the
    /// environment already in force for that tenant — an enclosing block's, else the request's —
    /// and otherwise its production (ADR 0028): work that names its own tenant again never leaves
    /// the environment it was in, and only work that means to changes it (<see cref="Environment"/>).
    /// </summary>
    public static IDisposable Tenant(Guid tenantId)
    {
        var environment = Current.Value is { Tenant: { } outer } enclosing && outer == tenantId ? enclosing.Environment
            : Request.Value is { } request && request.Tenant == tenantId ? request.Environment
            : (Guid?)null;
        return Enter(new State(tenantId, environment, Platform: false));
    }

    /// <summary>
    /// The environment in force for <paramref name="tenantId"/>: an enclosing block's, else the
    /// request's, when either is that tenant's; null otherwise. What work handed on to later —
    /// a job, an event — records so that it runs where it was asked for (ADR 0028).
    /// </summary>
    public static Guid? EnvironmentFor(Guid tenantId) =>
        Current.Value is { Tenant: { } outer } enclosing && outer == tenantId ? enclosing.Environment ?? tenantId
        : Request.Value is { } request && request.Tenant == tenantId ? request.Environment
        : null;

    /// <summary>
    /// Records the request's tenant and environment for <see cref="Tenant"/> blocks inside it to
    /// inherit. Set by the environment resolution middleware; changes nothing else.
    /// </summary>
    public static IDisposable ForRequest(Guid tenantId, Guid environmentId)
    {
        var previous = Request.Value;
        Request.Value = (tenantId, environmentId);
        return new RequestRestore(previous);
    }

    private sealed class RequestRestore((Guid Tenant, Guid Environment)? previous) : IDisposable
    {
        public void Dispose() => Request.Value = previous;
    }

    /// <summary>Act as one environment of <paramref name="tenantId"/> until disposed (ADR 0028).</summary>
    public static IDisposable Environment(Guid tenantId, Guid environmentId) =>
        Enter(new State(tenantId, environmentId, Platform: false));

    /// <summary>Widen to every tenant until disposed.</summary>
    public static IDisposable Platform() => Enter(new State(Tenant: null, Environment: null, Platform: true));

    private static Restore Enter(State state)
    {
        var previous = Current.Value;
        Current.Value = state;
        return new Restore(previous);
    }

    private sealed record State(Guid? Tenant, Guid? Environment, bool Platform);

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
