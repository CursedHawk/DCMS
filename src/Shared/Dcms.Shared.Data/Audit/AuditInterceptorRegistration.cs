using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Shared.Data.Audit;

public static class AuditInterceptorRegistration
{
    /// <summary>
    /// Attaches the audit interceptors to a context.
    ///
    /// <para><b>This line is load-bearing and easy to believe unnecessary.</b> Registering the
    /// interceptors as <c>IInterceptor</c> in the container is <i>not</i> enough: EF Core does
    /// not resolve interceptors from the application's service provider on its own, and the
    /// symptom of omitting this is not an error — it is a context that commits changes and
    /// records none of them, quietly, forever. A test
    /// (<c>AuditInterceptorDiscoveryTests</c>) saves through every context and asserts the
    /// buffer actually drained, because nothing else would tell anyone.</para>
    ///
    /// <para>Resolved rather than listed by type, so a later interceptor registered as
    /// <c>IInterceptor</c> is picked up by every context without twelve more edits. In a
    /// service that never called <c>AddDcmsAuditData</c> — the workers confined to one schema —
    /// this resolves to nothing and costs nothing.</para>
    ///
    /// <para>Requires the <c>(IServiceProvider, DbContextOptionsBuilder)</c> overload of
    /// <c>AddDbContext</c>, which is what makes the provider here the request's scope rather
    /// than the root: the recorder these interceptors write into is scoped to the request whose
    /// changes they are describing.</para>
    /// </summary>
    public static DbContextOptionsBuilder UseDcmsAuditInterceptors(
        this DbContextOptionsBuilder options,
        IServiceProvider services) =>
        options.AddInterceptors(services.GetServices<IInterceptor>());
}
