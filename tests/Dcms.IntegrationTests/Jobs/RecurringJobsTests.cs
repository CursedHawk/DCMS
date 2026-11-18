using Dcms.Shared.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dcms.IntegrationTests.Jobs;

/// <summary>Recurring ledger jobs (ADR 0036): one scheduler for all of them, one slot per period.</summary>
public sealed class RecurringJobsTests
{
    private sealed class Pass : IJobHandler<RecurringTick>
    {
        public Task<object?> RunAsync(RecurringTick payload, IJobContext context) => Task.FromResult<object?>(null);
    }

    [Fact]
    public void Each_job_is_scheduled_and_one_scheduler_runs_them_all()
    {
        var services = new ServiceCollection()
            .AddRecurringJob<Pass>("test.hourly", TimeSpan.FromHours(1), "t")
            .AddRecurringJob<Pass>("test.daily", TimeSpan.FromDays(1), "t", Dcms.Shared.Data.Jobs.JobLanes.Tenant);

        services.Where(d => d.ImplementationType == typeof(RecurringJobScheduler)).Should().ContainSingle();
        services.Select(d => d.ImplementationInstance).OfType<RecurringJob>().Select(j => (j.Kind, j.Lane))
            .Should().Equal(("test.hourly", "platform"), ("test.daily", "tenant"));
    }

    [Fact]
    public void A_slot_is_one_period_and_the_next_begins_on_its_boundary()
    {
        var hour = TimeSpan.FromHours(1);
        var start = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000 / 3600 * 3600);

        RecurringJobScheduler.Slot(start, hour).Should().Be(RecurringJobScheduler.Slot(start.AddMinutes(59), hour));
        RecurringJobScheduler.Slot(start.AddHours(1), hour).Should().Be(RecurringJobScheduler.Slot(start, hour) + 1);
    }
}
