using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Propagation;
using Dcms.Shared.Contracts.Events;
using NATS.Client.Core;
using NATS.Client.JetStream;

namespace Dcms.Shared.Messaging;

/// <summary>
/// Publishes to JetStream, stamping every message with who asked for the work.
///
/// <para>The headers are attribution, not payload: none of the fifteen event records changed
/// to carry them, and a consumer that ignores them behaves exactly as before. What they buy is
/// that the site build, the search re-index and the cache invalidation downstream of a publish
/// all name the person who clicked publish, instead of naming the background service that
/// happened to run next.</para>
/// </summary>
public sealed class NatsEventPublisher(INatsJSContext jetStream, AuditAmbient ambient) : IEventPublisher
{
    public async ValueTask PublishAsync<T>(string subject, T @event, CancellationToken ct = default, string? messageId = null)
        where T : IDcmsEvent
    {
        var opts = messageId is null ? null : new NatsJSPubOpts { MsgId = messageId };
        var ack = await jetStream.PublishAsync(
            subject, @event, opts: opts, headers: BuildHeaders(ambient.Current), cancellationToken: ct);
        ack.EnsureSuccess();
    }

    /// <summary>
    /// Null when there is no originating context — a timer-driven job, a startup task. Sending
    /// a set of blank headers instead would later read as a deliberate "nobody", which is a
    /// different and less honest claim than saying nothing.
    /// </summary>
    private static NatsHeaders? BuildHeaders(AuditScope? scope)
    {
        var captured = AuditPropagation.Capture(scope);
        if (captured.Count == 0)
        {
            return null;
        }

        var headers = new NatsHeaders();
        foreach (var (key, value) in captured)
        {
            headers[key] = value;
        }
        return headers;
    }
}
