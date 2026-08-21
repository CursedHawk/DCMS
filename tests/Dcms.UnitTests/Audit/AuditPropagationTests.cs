using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Propagation;
using Dcms.Shared.Kernel.Abstractions;

namespace Dcms.UnitTests.Audit;

/// <summary>
/// The property this phase exists for: an action crossing a process boundary still names the
/// person who took it, and is honest about the fact that it crossed one.
/// </summary>
public sealed class AuditPropagationTests
{
    private static AuditScope RequestScope() => new()
    {
        CorrelationId = "req-abc",
        TraceId = "trace-1",
        TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        ActorResolver = () => new AuditActor(
            ActorKind.User,
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            null,
            "alice@example.com",
            AuditAttribution.Direct),
    };

    private static Func<string, string?> Lookup(Dictionary<string, string> headers) =>
        key => headers.TryGetValue(key, out var value) ? value : null;

    [Fact]
    public void An_actor_survives_the_crossing_and_is_marked_as_having_crossed()
    {
        var captured = AuditPropagation.Capture(RequestScope());

        var restored = new AuditScope();
        AuditPropagation.Restore(restored, Lookup(captured));

        var actor = restored.ResolveActor();
        actor.Id.Should().Be(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        actor.Display.Should().Be("alice@example.com");
        actor.Kind.Should().Be(ActorKind.User);
        actor.Attribution.Should().Be(
            AuditAttribution.Propagated,
            "this identity is a peer's assertion, not something the receiving side authenticated");
    }

    [Fact]
    public void Correlation_and_tenant_cross_with_it()
    {
        var captured = AuditPropagation.Capture(RequestScope());

        var restored = new AuditScope();
        AuditPropagation.Restore(restored, Lookup(captured));

        restored.CorrelationId.Should().Be("req-abc");
        restored.TraceId.Should().Be("trace-1");
        restored.TenantId.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    }

    [Fact]
    public void The_endpoints_own_record_becomes_the_cause_of_everything_downstream()
    {
        var scope = RequestScope();
        var declared = new AuditEntry { Action = AuditActions.ContentPublished };
        scope.Declared = declared;

        var captured = AuditPropagation.Capture(scope);

        var restored = new AuditScope();
        AuditPropagation.Restore(restored, Lookup(captured));

        restored.CausationId.Should().Be(
            declared.EventId,
            "the site build is a consequence of the publish, and has to be traceable back to it");
    }

    [Fact]
    public void A_process_with_no_originating_request_says_nothing_rather_than_saying_nobody()
    {
        AuditPropagation.Capture(scope: null).Should().BeEmpty();

        // A scope that exists but has no actor and no correlation is the same case: a timer
        // fired. Blank headers would later read as a deliberate "unknown actor".
        AuditPropagation.Capture(new AuditScope()).Should().BeEmpty();
    }

    [Fact]
    public void The_payloads_tenant_is_used_when_no_header_carried_one()
    {
        var payloadTenant = Guid.NewGuid();

        var restored = new AuditScope();
        AuditPropagation.Restore(restored, _ => null, payloadTenant);

        restored.TenantId.Should().Be(
            payloadTenant,
            "a worker has no ambient tenant; landing on the platform scope would hide the record "
            + "from the one tenant entitled to read it");
    }

    [Fact]
    public void An_outbox_row_carries_the_same_context_across_the_dispatchers_gap()
    {
        var json = AuditPropagation.CaptureJson(RequestScope());
        json.Should().NotBeNull();

        var restored = new AuditScope();
        AuditPropagation.Restore(restored, AuditPropagation.FromJson(json));

        restored.ResolveActor().Display.Should().Be("alice@example.com");
        restored.CorrelationId.Should().Be("req-abc");
    }

    [Fact]
    public void A_row_whose_context_will_not_parse_still_gets_published()
    {
        var lookup = AuditPropagation.FromJson("{ this is not json");

        var restored = new AuditScope();
        var act = () => AuditPropagation.Restore(restored, lookup);

        act.Should().NotThrow("losing the attribution is bad; refusing to do the work is worse");
        restored.ResolveActor().Kind.Should().Be(ActorKind.Anonymous);
    }

    [Fact]
    public void An_explicit_actor_wins_over_the_resolver()
    {
        var scope = RequestScope();
        scope.Actor = new AuditActor(ActorKind.Webhook, null, "forgejo", "Forgejo webhook", AuditAttribution.Inferred);

        var captured = AuditPropagation.Capture(scope);

        captured[AuditPropagation.ActorKind].Should().Be(nameof(ActorKind.Webhook));
        captured.Should().NotContainKey(
            AuditPropagation.ActorId,
            "an HMAC proves the push came from Forgejo, not who pushed");
    }
}

/// <summary>The AsyncLocal that makes a scoped context reachable from singleton code.</summary>
public sealed class AuditAmbientTests
{
    [Fact]
    public void Nothing_is_ambient_outside_a_unit_of_work()
    {
        new AuditAmbient().Current.Should().BeNull(
            "a timer-driven publish has no originating request, and should not borrow one");
    }

    [Fact]
    public void Entering_and_leaving_restores_what_was_there()
    {
        var ambient = new AuditAmbient();
        var outer = new AuditScope { CorrelationId = "outer" };
        var inner = new AuditScope { CorrelationId = "inner" };

        using (ambient.Enter(outer))
        {
            ambient.Current!.CorrelationId.Should().Be("outer");

            // A consumer that republishes runs one inside another; this is not hypothetical.
            using (ambient.Enter(inner))
            {
                ambient.Current!.CorrelationId.Should().Be("inner");
            }

            ambient.Current!.CorrelationId.Should().Be("outer");
        }

        ambient.Current.Should().BeNull();
    }

    [Fact]
    public async Task The_context_follows_an_await()
    {
        var ambient = new AuditAmbient();
        using var _ = ambient.Enter(new AuditScope { CorrelationId = "flows" });

        await Task.Yield();

        ambient.Current!.CorrelationId.Should().Be(
            "flows", "the publisher is reached through several awaits from the handler that set this");
    }
}
