using System.Diagnostics;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Propagation;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Telemetry;
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
    public static MessageHandling RestoreAuditContext<T>(
        this INatsJSMsg<T> message,
        IServiceProvider scopedServices,
        Guid? tenantFallback = null)
    {
        var scope = scopedServices.GetRequiredService<AuditScope>();
        var ambient = scopedServices.GetRequiredService<AuditAmbient>();

        var headers = message.Headers;
        string? Header(string key) =>
            headers is not null && headers.TryGetValue(key, out var values) ? values.ToString() : null;

        AuditPropagation.Restore(scope, Header, tenantFallback);

        // The consumer's own span, hung off the producer's. Without a parent context — an older
        // message, or a publish from a process with no ambient request — this starts a root,
        // which is what the trace looked like everywhere before the traceparent header existed.
        var activity = DcmsActivitySource.StartLinked(
            $"{message.Subject} process", AuditPropagation.ParentContext(Header));

        if (activity is not null)
        {
            activity.SetTag("messaging.system", "nats");
            activity.SetTag("messaging.destination.name", message.Subject);
            activity.SetTag("messaging.operation.type", "process");

            // Point the audit record at the span that is actually doing this work rather than at
            // the producer's, which is what the Dcms-Trace-Id header alone would have given. Same
            // trace either way; this one resolves to the right node in it.
            scope.TraceId = activity.TraceId.ToString();
            scope.SpanId = activity.SpanId.ToString();
        }

        // No propagated actor means nobody asked for this — a timer, a retry of something whose
        // origin is long gone, a message published before propagation existed. The service
        // itself is then the honest answer, and it is a better one than "anonymous", which
        // reads as an unauthenticated caller rather than as the platform acting on its own.
        var actor = scopedServices.GetRequiredService<ICurrentActor>();
        scope.ActorResolver ??= () => new AuditActor(
            actor.Kind, actor.Id, actor.Key, actor.Display, AuditAttribution.Direct);

        // One counter for every audited consumer, recorded from the seam they all already go
        // through rather than from each handler. The stream comes from JetStream's own metadata
        // — both it and the subject are bounded by the ten streams this platform provisions,
        // which is what makes them safe as labels.
        return new MessageHandling(
            activity,
            ambient.Enter(scope),
            scopedServices.GetService<DcmsMetrics>(),
            message.Metadata?.Stream ?? "unknown",
            message.Subject);
    }

    /// <summary>
    /// One message being handled: the consumer's span, the ambient audit scope, and the timer
    /// behind the message counter.
    ///
    /// <para>Disposing ends the span <i>then</i> leaves the ambient scope, and the order is the
    /// point. The span processor that stamps the tenant and actor onto a span reads
    /// <c>AuditAmbient.Current</c> when the span <b>ends</b>. Restoring the ambient first would
    /// mean every consumer span was enriched with whatever the previous unit of work had, or
    /// with nothing.</para>
    /// </summary>
    public sealed class MessageHandling(
        Activity? activity,
        IDisposable ambientScope,
        DcmsMetrics? metrics,
        string stream,
        string subject) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private bool _disposed;
        private bool _failed;

        /// <summary>
        /// Marks this message as having failed. Call it from the handler's catch block.
        ///
        /// <para>Explicit rather than inferred, because inference does not work here. A
        /// consumer that catches an exception and marks <i>its own</i> span — which is the
        /// natural thing to do, and what the media and site-build consumers already do — leaves
        /// this span, the parent, untouched: a child's status does not propagate upward. So a
        /// counter that read the parent's status would report every handled failure as a
        /// success. One method call is the honest version of that.</para>
        /// </summary>
        public void Failed(string reason)
        {
            _failed = true;
            activity?.SetStatus(ActivityStatusCode.Error, reason);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            metrics?.MessageHandled(
                stream,
                subject,
                !_failed && activity?.Status != ActivityStatusCode.Error,
                Stopwatch.GetElapsedTime(_started));

            activity?.Dispose();
            ambientScope.Dispose();
        }
    }
}
