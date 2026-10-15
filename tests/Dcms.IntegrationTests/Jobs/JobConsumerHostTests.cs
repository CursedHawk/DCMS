extern alias AdminApiApp;
using Dcms.IntegrationTests.Tenancy;
using Dcms.Shared.Data.Jobs;
using Dcms.Shared.Jobs;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.IntegrationTests.Jobs;

/// <summary>
/// A job worker end to end (ADR 0024): enqueue, JetStream delivery, claim, the handler in its
/// tenant, progress, and each way a run can end — done, canceled part-way, or failed for good.
/// </summary>
[Collection(AdminApiCollection.Name)]
public sealed class JobConsumerHostTests(AdminApiFixture fixture) : IAsyncDisposable
{
    private WebApplicationFactory<AdminApiApp::Program>? _worker;

    private WebApplicationFactory<AdminApiApp::Program> Worker => _worker ??= fixture.Factory.WithWebHostBuilder(b =>
        b.ConfigureServices((context, services) =>
        {
            services.AddJobHandler<EchoHandler, EchoPayload>("test.echo");
            services.AddJobHandler<WaitForCancelHandler, EchoPayload>("test.wait");
            services.AddJobHandler<RejectHandler, EchoPayload>("test.reject");
            services.AddJobConsumer(context.Configuration, JobLanes.Tenant, 4);
        }));

    [DockerFact]
    public async Task A_job_runs_in_its_tenant_reports_progress_and_stores_its_result()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = Guid.NewGuid();
        var job = await EnqueueAsync(new JobSpec("test.echo", tenant, new EchoPayload("hello"), "t"), ct);

        var row = await WaitForAsync(job.Id, tenant, j => j.IsFinished, ct);

        row.Status.Should().Be(JobStatus.Succeeded);
        row.Done.Should().Be(3);
        row.Total.Should().Be(3);
        row.ResultJson.Should().Contain("hello").And.Contain(tenant.ToString());
    }

    [DockerFact]
    public async Task Canceling_a_running_job_stops_its_handler()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = Guid.NewGuid();
        var job = await EnqueueAsync(new JobSpec("test.wait", tenant, new EchoPayload("x"), "t"), ct);
        await WaitForAsync(job.Id, tenant, j => j.Status == JobStatus.Running, ct);

        using (var scope = Worker.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<JobLedger>().RequestCancelAsync(job.Id, tenant, ct))
                .Should().Be(CancelOutcome.Requested);
        }

        var row = await WaitForAsync(job.Id, tenant, j => j.IsFinished, ct);
        row.Status.Should().Be(JobStatus.Canceled);
    }

    [DockerFact]
    public async Task A_permanent_failure_fails_at_once_without_spending_attempts()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = Guid.NewGuid();
        var job = await EnqueueAsync(new JobSpec("test.reject", tenant, new EchoPayload("x"), "t", MaxAttempts: 5), ct);

        var row = await WaitForAsync(job.Id, tenant, j => j.IsFinished, ct);

        row.Status.Should().Be(JobStatus.Failed);
        row.ErrorKey.Should().Be("test.error.rejected");
        row.Attempts.Should().Be(1);
    }

    private async Task<JobRef> EnqueueAsync(JobSpec spec, CancellationToken ct)
    {
        using var scope = Worker.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(spec, ct);
    }

    private async Task<Job> WaitForAsync(Guid id, Guid tenant, Func<Job, bool> done, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(45);
        Job? row = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = Worker.Services.CreateScope();
            row = await scope.ServiceProvider.GetRequiredService<JobLedger>().FindAsync(id, tenant, ct);
            if (row is not null && done(row))
            {
                return row;
            }
            await Task.Delay(200, ct);
        }
        throw new TimeoutException($"Job {id} never got there; last status {row?.Status}.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_worker is not null)
        {
            await _worker.DisposeAsync();
        }
    }

    public sealed record EchoPayload(string Text);

    private sealed class EchoHandler : IJobHandler<EchoPayload>
    {
        public async Task<object?> RunAsync(EchoPayload payload, IJobContext context)
        {
            for (var i = 1; i <= 3; i++)
            {
                await context.ReportAsync(i, 3, "echoing");
            }
            return new { payload.Text, Tenant = context.TenantId };
        }
    }

    private sealed class WaitForCancelHandler : IJobHandler<EchoPayload>
    {
        public async Task<object?> RunAsync(EchoPayload payload, IJobContext context)
        {
            await Task.Delay(Timeout.Infinite, context.Cancellation);
            return null;
        }
    }

    private sealed class RejectHandler : IJobHandler<EchoPayload>
    {
        public Task<object?> RunAsync(EchoPayload payload, IJobContext context) =>
            throw new JobFailedException("test.error.rejected", "never valid");
    }
}
