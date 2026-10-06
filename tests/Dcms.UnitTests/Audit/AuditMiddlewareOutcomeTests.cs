using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Audit.Propagation;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dcms.UnitTests.Audit;

/// <summary>
/// A request that failed on the server is never on record as a success. The 2026-10-06 Drive
/// imports answered 500 and were logged "media.imported: success".
/// </summary>
public sealed class AuditMiddlewareOutcomeTests
{
    private readonly AuditScope _scope = new();
    private readonly RecordingSink _sink = new();
    private readonly AuditRecorder _recorder;

    public AuditMiddlewareOutcomeTests()
    {
        _recorder = new AuditRecorder(
            _scope, new AuditServiceIdentity("tests"), _sink, new TestActor(), new TestTenant(),
            TimeProvider.System, new AuditMetrics(new TestMeterFactory()), NullLogger<AuditRecorder>.Instance);
    }

    [Fact]
    public async Task A_handler_that_throws_before_saving_is_recorded_as_a_failure()
    {
        // The exception handler is outside this middleware, so the response still read 200.
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(_ =>
        {
            Declare();
            throw new InvalidOperationException("insufficient resources");
        }));

        var record = _sink.Events.Should().ContainSingle().Subject;
        record.Outcome.Should().Be(AuditOutcome.Failure);
        record.Http!.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task A_handler_that_saved_and_then_failed_gets_a_failure_record_beside_it()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(_ =>
        {
            Declare();
            // What the EF interceptor does on SaveChanges: the declared entry leaves the buffer
            // and commits with the change.
            _recorder.Drain();
            throw new InvalidOperationException("publish refused");
        }));

        var record = _sink.Events.Should().ContainSingle().Subject;
        record.Action.Should().Be(AuditActions.MediaImported);
        record.ResourceType.Should().Be("media_asset");
        record.Outcome.Should().Be(AuditOutcome.Failure);
    }

    [Fact]
    public async Task A_5xx_answer_is_a_failure()
    {
        await RunAsync(ctx =>
        {
            Declare();
            ctx.Response.StatusCode = 503;
            return Task.CompletedTask;
        });

        _sink.Events.Should().ContainSingle().Which.Outcome.Should().Be(AuditOutcome.Failure);
    }

    [Fact]
    public async Task A_4xx_answer_still_records_nothing()
    {
        await RunAsync(ctx =>
        {
            Declare();
            ctx.Response.StatusCode = 400;
            return Task.CompletedTask;
        });

        _sink.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task An_undeclared_post_that_throws_is_not_recorded_as_a_success()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunAsync(_ => throw new InvalidOperationException("boom")));

        _sink.Events.Should().NotContain(e => e.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task A_success_is_still_a_success()
    {
        await RunAsync(_ =>
        {
            Declare();
            return Task.CompletedTask;
        });

        _sink.Events.Should().ContainSingle().Which.Outcome.Should().Be(AuditOutcome.Success);
    }

    /// <summary>What <c>.WithAudit(MediaImported, "media_asset")</c> opens before the handler.</summary>
    private void Declare()
    {
        var entry = _recorder.Record(AuditActions.MediaImported);
        entry.ResourceType = "media_asset";
        _scope.Declared = entry;
    }

    private Task RunAsync(RequestDelegate handler)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/admin/media/google-drive/import";
        return new AuditMiddleware(handler).InvokeAsync(context, _recorder, _scope, new TestActor(), new AuditAmbient());
    }

    private sealed class RecordingSink : IAuditSink
    {
        public List<AuditEvent> Events { get; } = [];

        public ValueTask WriteAsync(IReadOnlyList<AuditEvent> events, CancellationToken cancellationToken = default)
        {
            Events.AddRange(events);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestActor : ICurrentActor
    {
        public ActorKind Kind => ActorKind.User;
        public Guid? Id { get; } = Guid.NewGuid();
        public string? Key => null;
        public string? Display => "tester@example.com";
        public bool IsSuperAdmin => false;
        public Guid? OnBehalfOf => null;
        public bool IsAuthenticated => true;
    }

    private sealed class TestTenant : ITenantContext
    {
        public Guid? TenantId { get; } = Guid.NewGuid();
        public string? TenantSlug => "acme";
    }
}
