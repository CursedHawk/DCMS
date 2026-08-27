using System.Diagnostics;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Propagation;

namespace Dcms.UnitTests.Telemetry;

/// <summary>
/// Pins the two halves of the change that made a trace survive a message-bus hop, and the one
/// property that had to survive it unchanged.
///
/// <para>Before this, <c>Dcms-Trace-Id</c> crossed the bus alone. A consumer could label itself
/// with the producer's trace id but had no span id to hang off, so it started a new root: the
/// publish a person clicked and the build that resulted were two unrelated trees that happened
/// to share an id. The fix adds W3C <c>traceparent</c> alongside the existing header rather than
/// replacing it — which is the part these tests exist to hold, because a message already sitting
/// in a JetStream from before the change has no <c>traceparent</c> and must still restore
/// exactly as it did.</para>
/// </summary>
public class TracePropagationTests
{
    private static readonly ActivitySource Source = new("Dcms.UnitTests.TracePropagation");

    /// <summary>
    /// Without a listener, <c>StartActivity</c> returns null and there is no trace context to
    /// capture — so every test here that needs a real span needs one of these first.
    ///
    /// <para>The local copy of <see cref="Source"/> is load-bearing and not a style choice.
    /// A class with no static constructor is <c>beforefieldinit</c>, so the runtime may defer
    /// running the field initialiser until the field is actually read. Reading it for the first
    /// time from inside the <c>ShouldListenTo</c> callback means the <c>ActivitySource</c> is
    /// constructed <i>during</i> the registration that was supposed to attach to it, and the
    /// listener ends up attached to nothing: <c>HasListeners()</c> is false and every
    /// <c>StartActivity</c> silently returns null. Touching the field first forces the
    /// initialiser to run before registration begins.</para>
    /// </summary>
    private static ActivityListener Listening()
    {
        var source = Source;

        var listener = new ActivityListener
        {
            ShouldListenTo = candidate => candidate == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    [Fact]
    public void Capture_writes_a_traceparent_for_the_publishing_span()
    {
        using var listener = Listening();
        using var activity = Source.StartActivity("publish");
        activity.Should().NotBeNull();

        var headers = AuditPropagation.Capture(new AuditScope { CorrelationId = "abc" });

        headers.Should().ContainKey(AuditPropagation.TraceParent);
        headers[AuditPropagation.TraceParent].Should().Be(activity!.Id);
    }

    [Fact]
    public void Round_trip_yields_the_publishing_span_as_the_parent()
    {
        using var listener = Listening();
        using var producer = Source.StartActivity("publish");

        var headers = AuditPropagation.Capture(new AuditScope());
        var parent = AuditPropagation.ParentContext(key => headers.GetValueOrDefault(key));

        parent.TraceId.Should().Be(producer!.TraceId);
        parent.SpanId.Should().Be(producer.SpanId);
    }

    /// <summary>
    /// The back-compat case, and the reason the old header was not simply replaced: a message
    /// published before <c>traceparent</c> existed still restores its correlation, tenant and
    /// actor, and asks for a root span rather than throwing or inventing a parent.
    /// </summary>
    [Fact]
    public void A_message_with_no_traceparent_restores_as_before_and_starts_a_root()
    {
        var legacy = new Dictionary<string, string>
        {
            [AuditPropagation.CorrelationId] = "legacy-correlation",
            [AuditPropagation.TraceId] = "4bf92f3577b34da6a3ce929d0e0e4736",
            [AuditPropagation.Tenant] = "6f9619ff-8b86-d011-b42d-00cf4fc964ff",
        };

        var scope = new AuditScope();
        AuditPropagation.Restore(scope, key => legacy.GetValueOrDefault(key));

        scope.CorrelationId.Should().Be("legacy-correlation");
        scope.TraceId.Should().Be("4bf92f3577b34da6a3ce929d0e0e4736");
        scope.TenantId.Should().Be(Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff"));

        AuditPropagation.ParentContext(key => legacy.GetValueOrDefault(key))
            .Should().Be(default(ActivityContext), "no parent means start a root, not fail");
    }

    /// <summary>
    /// A truncated or corrupted header is treated the same as an absent one. Refusing to handle
    /// the message would trade a degraded trace for a stuck consumer, which is the worse of the
    /// two failures by a distance.
    /// </summary>
    [Fact]
    public void A_malformed_traceparent_is_ignored_rather_than_fatal()
    {
        var headers = new Dictionary<string, string>
        {
            [AuditPropagation.TraceParent] = "not-a-traceparent",
        };

        AuditPropagation.ParentContext(key => headers.GetValueOrDefault(key))
            .Should().Be(default(ActivityContext));
    }

    /// <summary>
    /// The old header is still written. Dropping it would break the audit record's TraceId
    /// column for any path that has no live Activity — which is every publish from a timer.
    /// </summary>
    /// <summary>
    /// The outbox case, which is the one that took a second attempt to get right.
    ///
    /// <para><c>OutboxDispatcher</c> publishes on a two-second timer. The request that enqueued
    /// the row finished minutes ago, so there is no <c>Activity.Current</c> to capture from, and
    /// with only the <c>Activity.Current</c> path the whole
    /// <c>request → cms.content_outbox → JetStream → site-builder</c> chain — the exact path the
    /// audit design calls load-bearing — stayed two unrelated traces. So the scope carries the
    /// traceparent that the middleware stamped at request time, and <c>Capture</c> falls back to
    /// it.</para>
    /// </summary>
    [Fact]
    public void Capture_falls_back_to_the_scope_when_there_is_no_current_activity()
    {
        Activity.Current.Should().BeNull("this test is about the case where there is no span");

        var scope = new AuditScope
        {
            TraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            TraceState = "dcms=1",
        };

        var headers = AuditPropagation.Capture(scope);

        headers[AuditPropagation.TraceParent].Should().Be(scope.TraceParent);
        headers[AuditPropagation.TraceState].Should().Be(scope.TraceState);

        var parent = AuditPropagation.ParentContext(key => headers.GetValueOrDefault(key));
        parent.TraceId.ToString().Should().Be("4bf92f3577b34da6a3ce929d0e0e4736");
        parent.SpanId.ToString().Should().Be("00f067aa0ba902b7");
    }

    /// <summary>
    /// And the fallback must stay a fallback. A consumer that republishes has its own span, and
    /// parenting the new message to the request two hops back would put the second message
    /// beside the first instead of beneath it.
    /// </summary>
    [Fact]
    public void A_live_activity_wins_over_the_scope_fallback()
    {
        using var listener = Listening();
        using var republishing = Source.StartActivity("republish");
        republishing.Should().NotBeNull();

        var headers = AuditPropagation.Capture(new AuditScope
        {
            TraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
        });

        headers[AuditPropagation.TraceParent].Should().Be(republishing!.Id);
    }

    /// <summary>
    /// Restore is the other direction: a consumer that picks the context off a message must end
    /// up able to republish it, so what it stores has to be what Capture would emit.
    /// </summary>
    [Fact]
    public void Restore_puts_the_traceparent_back_on_the_scope_for_the_next_hop()
    {
        var headers = new Dictionary<string, string>
        {
            [AuditPropagation.TraceParent] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            [AuditPropagation.TraceState] = "dcms=1",
        };

        var scope = new AuditScope();
        AuditPropagation.Restore(scope, key => headers.GetValueOrDefault(key), tenantFallback: null);

        scope.TraceParent.Should().Be(headers[AuditPropagation.TraceParent]);
        scope.TraceState.Should().Be(headers[AuditPropagation.TraceState]);
    }

    [Fact]
    public void The_legacy_trace_id_header_is_still_written()
    {
        var headers = AuditPropagation.Capture(new AuditScope { TraceId = "4bf92f3577b34da6a3ce929d0e0e4736" });

        headers[AuditPropagation.TraceId].Should().Be("4bf92f3577b34da6a3ce929d0e0e4736");
    }
}
