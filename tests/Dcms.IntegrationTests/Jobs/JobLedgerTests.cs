using Dcms.IntegrationTests.Tenancy;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Data.Jobs;
using Dcms.Shared.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Dcms.IntegrationTests.Jobs;

/// <summary>
/// The ledger's guarantees (ADR 0024), against real Postgres under the enforcing role: a job is
/// claimed by one worker at a time and never again once finished, the dedupe key collapses a
/// repeated enqueue, progress cannot run backwards, and platform rows stay out of tenant reads.
/// </summary>
[Collection(AdminApiCollection.Name)]
public class JobLedgerTests(AdminApiFixture fixture)
{
    [DockerFact]
    public async Task Two_workers_racing_for_one_job_get_it_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = Guid.NewGuid();
        var job = await EnqueueAsync(new JobSpec("test.race", tenant, null, "t"), ct);

        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ClaimAsync(job.Id, tenant, ct)));

        claims.Count(c => c is not null).Should().Be(1, "the claim is one conditional update; only one can match");
    }

    [DockerFact]
    public async Task A_finished_job_is_never_claimed_again()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = Guid.NewGuid();
        var job = await EnqueueAsync(new JobSpec("test.done", tenant, null, "t"), ct);

        (await ClaimAsync(job.Id, tenant, ct)).Should().NotBeNull();
        await WithLedgerAsync(l => l.CompleteAsync(job.Id, tenant, null, ct));

        (await ClaimAsync(job.Id, tenant, ct)).Should().BeNull(
            "a redelivered run request after success must not run the work again");
        var row = await FindAsync(job.Id, tenant, ct);
        row!.Status.Should().Be(JobStatus.Succeeded);
        row.Done.Should().Be(row.Total, "a finished job reads as whole");
    }

    [DockerFact]
    public async Task A_job_whose_worker_died_is_claimed_again_after_its_lease()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = Guid.NewGuid();
        var job = await EnqueueAsync(new JobSpec("test.lease", tenant, null, "t"), ct);
        (await ClaimAsync(job.Id, tenant, ct)).Should().NotBeNull();

        (await ClaimAsync(job.Id, tenant, ct)).Should().BeNull("the first worker's lease is live");

        await SqlAsync("""UPDATE jobs.jobs SET "LeaseUntil" = now() - interval '1 second' WHERE "Id" = @id""", job.Id, ct);

        var reclaimed = await ClaimAsync(job.Id, tenant, ct);
        reclaimed.Should().NotBeNull();
        reclaimed!.Attempts.Should().Be(2);
    }

    [DockerFact]
    public async Task The_same_dedupe_key_returns_the_job_already_there()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = Guid.NewGuid();
        var spec = new JobSpec("test.dedupe", tenant, null, "t", DedupeKey: "import:abc");

        var first = await EnqueueAsync(spec, ct);
        var second = await EnqueueAsync(spec, ct);

        second.Id.Should().Be(first.Id);
        second.Existing.Should().BeTrue();

        // Platform jobs have no tenant, and NULLS NOT DISTINCT is what makes them collide too.
        var platform = new JobSpec("test.dedupe", null, null, "t", DedupeKey: "purge:" + Guid.NewGuid(), Lane: JobLanes.Platform);
        (await EnqueueAsync(platform, ct)).Id.Should().Be((await EnqueueAsync(platform, ct)).Id);
    }

    [DockerFact]
    public async Task Progress_never_moves_backwards()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = Guid.NewGuid();
        var job = await EnqueueAsync(new JobSpec("test.progress", tenant, null, "t"), ct);
        await ClaimAsync(job.Id, tenant, ct);

        await WithLedgerAsync(l => l.ProgressAsync(job.Id, tenant, 50, 100, "writing", ct));
        await WithLedgerAsync(l => l.ProgressAsync(job.Id, tenant, 40, null, null, ct));

        var row = await FindAsync(job.Id, tenant, ct);
        row!.Done.Should().Be(50, "a late, smaller tick must not rewind the bar");
        row.Total.Should().Be(100);
        row.StageKey.Should().Be("writing");
    }

    [DockerFact]
    public async Task Canceling_a_queued_job_stops_it_and_a_running_one_is_asked()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = Guid.NewGuid();

        var queued = await EnqueueAsync(new JobSpec("test.cancel", tenant, null, "t"), ct);
        (await WithLedgerAsync(l => l.RequestCancelAsync(queued.Id, tenant, ct))).Should().Be(CancelOutcome.Canceled);
        (await ClaimAsync(queued.Id, tenant, ct)).Should().BeNull("the run request still queued must find nothing to run");

        var running = await EnqueueAsync(new JobSpec("test.cancel", tenant, null, "t"), ct);
        await ClaimAsync(running.Id, tenant, ct);
        (await WithLedgerAsync(l => l.RequestCancelAsync(running.Id, tenant, ct))).Should().Be(CancelOutcome.Requested);
        (await WithLedgerAsync(l => l.RenewAsync(running.Id, tenant, ct))).Should().BeTrue(
            "the worker learns it should stop at its next lease renewal");

        var stubborn = await EnqueueAsync(new JobSpec("test.cancel", tenant, null, "t", Cancelable: false), ct);
        await ClaimAsync(stubborn.Id, tenant, ct);
        (await WithLedgerAsync(l => l.RequestCancelAsync(stubborn.Id, tenant, ct))).Should().Be(CancelOutcome.NotCancelable);
    }

    [DockerFact]
    public async Task A_retryable_failure_requeues_until_attempts_run_out_and_retry_starts_over()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = Guid.NewGuid();
        var job = await EnqueueAsync(new JobSpec("test.fail", tenant, null, "t", MaxAttempts: 2), ct);

        await ClaimAsync(job.Id, tenant, ct);
        (await WithLedgerAsync(l => l.FailAsync(job.Id, tenant, "e", "boom", retryable: true, ct))).Should().Be(JobStatus.Queued);

        await ClaimAsync(job.Id, tenant, ct);
        (await WithLedgerAsync(l => l.FailAsync(job.Id, tenant, "e", "boom", retryable: true, ct))).Should().Be(JobStatus.Failed);

        (await WithLedgerAsync(l => l.ResetForRetryAsync(job.Id, tenant, ct))).Should().BeTrue();
        var row = await FindAsync(job.Id, tenant, ct);
        row!.Status.Should().Be(JobStatus.Queued);
        row.Attempts.Should().Be(0);
        row.ErrorKey.Should().BeNull();
    }

    [DockerFact]
    public async Task Reports_from_a_bus_only_worker_cannot_reopen_a_finished_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = Guid.NewGuid();
        var job = await EnqueueAsync(new JobSpec("media.process.video", tenant, null, "t", Lane: "media"), ct);

        await ApplyAsync(job.Id, tenant, "Running", done: 0, total: 10, ct);
        await ApplyAsync(job.Id, tenant, null, done: 6, total: null, ct);
        await ApplyAsync(job.Id, tenant, "Succeeded", done: null, total: null, ct);
        // A progress tick that lost the race with the outcome.
        await ApplyAsync(job.Id, tenant, null, done: 7, total: null, ct);

        var row = await FindAsync(job.Id, tenant, ct);
        row!.Status.Should().Be(JobStatus.Succeeded);
        row.Done.Should().Be(10);
    }

    [DockerFact]
    public async Task Platform_jobs_are_invisible_to_tenant_reads()
    {
        var ct = TestContext.Current.CancellationToken;
        var platform = await EnqueueAsync(new JobSpec("test.platform", null, null, "t", Lane: JobLanes.Platform), ct);

        // A context with no tenant (a worker, a misconfigured request): the EF filter must not
        // read "TenantId == null" as "the platform's rows".
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JobsDbContext>();
            (await db.Jobs.AnyAsync(j => j.Id == platform.Id, ct)).Should().BeFalse();
        }

        // And the database agrees when acting as some tenant, whatever EF is told. Only provable
        // under the enforcing role; the owner connection bypasses row security.
        if (AdminApiFixture.RlsEnforced)
        {
            (await FindAsync(platform.Id, Guid.NewGuid(), ct)).Should().BeNull("tenant_isolation never admits a NULL tenant");
        }
        (await FindAsync(platform.Id, null, ct)).Should().NotBeNull("platform scope sees it");
    }

    private Task<JobRef> EnqueueAsync(JobSpec spec, CancellationToken ct) =>
        WithScopeAsync(sp => sp.GetRequiredService<IJobQueue>().EnqueueAsync(spec, ct));

    private Task<Job?> ClaimAsync(Guid id, Guid? tenant, CancellationToken ct) =>
        WithLedgerAsync(l => l.ClaimAsync(id, tenant, ct));

    private Task<Job?> FindAsync(Guid id, Guid? tenant, CancellationToken ct) =>
        WithLedgerAsync(l => l.FindAsync(id, tenant, ct));

    private Task ApplyAsync(Guid id, Guid? tenant, string? status, long? done, long? total, CancellationToken ct) =>
        WithLedgerAsync(l => l.ApplyReportAsync(new JobStateReported(
            Guid.NewGuid(), DateTimeOffset.UtcNow, id, tenant, status, done, total, null, null, null, null), ct));

    private Task<T> WithLedgerAsync<T>(Func<JobLedger, Task<T>> call) =>
        WithScopeAsync(sp => call(sp.GetRequiredService<JobLedger>()));

    private Task WithLedgerAsync(Func<JobLedger, Task> call) =>
        WithScopeAsync(async sp => { await call(sp.GetRequiredService<JobLedger>()); return true; });

    private async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> call)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return await call(scope.ServiceProvider);
    }

    private async Task SqlAsync(string sql, Guid id, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
