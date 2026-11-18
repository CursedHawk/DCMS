using Dcms.Shared.Data.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dcms.Shared.Jobs;

/// <summary>The payload of a recurring job: which slot of its schedule this run is.</summary>
public sealed record RecurringTick(long Slot);

/// <summary>One recurring job: a handler run every <paramref name="Every"/> on <paramref name="Lane"/>.</summary>
public sealed record RecurringJob(string Kind, TimeSpan Every, string Lane, string TitleKey);

public static class RecurringJobs
{
    /// <summary>
    /// A pass that used to be a timer loop, run as a ledger job instead (ADR 0036): it shows in the
    /// jobs list (routine, so a run that went fine does not crowd the recent ones), it is retried
    /// and its failure is visible, and it runs once per slot however many workers there are.
    /// </summary>
    public static IServiceCollection AddRecurringJob<THandler>(
        this IServiceCollection services, string kind, TimeSpan every, string titleKey, string lane = JobLanes.Platform)
        where THandler : class, IJobHandler<RecurringTick>
    {
        services.AddJobHandler<THandler, RecurringTick>(kind);
        services.AddSingleton(new RecurringJob(kind, every, lane, titleKey));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, RecurringJobScheduler>());
        return services;
    }
}

/// <summary>
/// Enqueues each recurring job once per slot of its schedule. No lock: the ledger's unique
/// <c>(TenantId, DedupeKey)</c> index makes the slot's key a once-only enqueue across every
/// replica and every restart, so all replicas may tick and only one job results.
/// </summary>
public sealed class RecurringJobScheduler(
    IEnumerable<RecurringJob> jobs, IServiceScopeFactory scopes, ILogger<RecurringJobScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var schedule = jobs.ToList();
        var enqueued = new Dictionary<string, long>();
        using var timer = new PeriodicTimer(Tick);
        do
        {
            foreach (var job in schedule)
            {
                var slot = Slot(DateTimeOffset.UtcNow, job.Every);
                if (enqueued.GetValueOrDefault(job.Kind, -1) == slot)
                {
                    continue;
                }
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(new JobSpec(
                        job.Kind, null, new RecurringTick(slot), job.TitleKey,
                        DedupeKey: $"{job.Kind}:{slot}", Cancelable: false, Lane: job.Lane, Routine: true), stoppingToken);
                    enqueued[job.Kind] = slot;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Could not enqueue recurring job {Kind}; retrying next tick.", job.Kind);
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>The slot a moment falls in: whole periods since the Unix epoch.</summary>
    public static long Slot(DateTimeOffset at, TimeSpan every) => at.ToUnixTimeSeconds() / (long)every.TotalSeconds;
}
