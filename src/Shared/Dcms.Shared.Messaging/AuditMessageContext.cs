using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Propagation;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.JetStream;

namespace Dcms.Shared.Messaging;

/// <summary>
/// The consumer half of attribution: one line at the top of a message handler puts the
/// originating request's context back in place, so anything the handler records names the
/// person whose action set it off.
/// </summary>
public static class AuditMessageContext
{
    /// <summary>
    /// Restores the propagated context into a scope and makes it ambient for the duration of
    /// the handler. Dispose at the end of handling one message — the scope belongs to that
    /// message and must not leak into the next one off the same consumer loop.
    ///
    /// <para>Use as:
    /// <code>
    /// using var scope = services.CreateScope();
    /// using var context = msg.RestoreAuditContext(scope.ServiceProvider, msg.Data?.TenantId);
    /// </code>
    /// The service scope comes first because the audit scope is resolved from it: a consumer
    /// that shares one <c>AuditScope</c> across messages would attribute the second message to
    /// the first message's actor.</para>
    /// </summary>
    /// <param name="tenantFallback">
    /// The tenant from the payload. Used when no header carried one — a worker has no ambient
    /// tenant, and a record that fell back to the platform scope would be invisible to the one
    /// tenant entitled to read it.
    /// </param>
    public static IDisposable RestoreAuditContext<T>(
        this INatsJSMsg<T> message,
        IServiceProvider scopedServices,
        Guid? tenantFallback = null)
    {
        var scope = scopedServices.GetRequiredService<AuditScope>();
        var ambient = scopedServices.GetRequiredService<AuditAmbient>();

        var headers = message.Headers;
        AuditPropagation.Restore(
            scope,
            key => headers is not null && headers.TryGetValue(key, out var values) ? values.ToString() : null,
            tenantFallback);

        // No propagated actor means nobody asked for this — a timer, a retry of something whose
        // origin is long gone, a message published before propagation existed. The service
        // itself is then the honest answer, and it is a better one than "anonymous", which
        // reads as an unauthenticated caller rather than as the platform acting on its own.
        var actor = scopedServices.GetRequiredService<ICurrentActor>();
        scope.ActorResolver ??= () => new AuditActor(
            actor.Kind, actor.Id, actor.Key, actor.Display, AuditAttribution.Direct);

        return ambient.Enter(scope);
    }
}
