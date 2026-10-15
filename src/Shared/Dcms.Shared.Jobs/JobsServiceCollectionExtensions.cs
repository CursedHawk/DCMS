using Dcms.Shared.Data.Jobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Dcms.Shared.Jobs;

public static class JobsServiceCollectionExtensions
{
    /// <summary>
    /// The ledger and the queue, for any process that enqueues jobs or writes their state.
    /// <paramref name="source"/> names this process on the rows it creates (<c>admin-api</c>).
    /// </summary>
    public static IServiceCollection AddDcmsJobs(this IServiceCollection services, IConfiguration configuration, string source)
    {
        services.AddDcmsJobsData(configuration);
        services.TryAddSingleton(new JobSource(source));
        services.TryAddScoped<JobLedger>();
        services.TryAddScoped<IJobQueue, JobQueue>();
        services.TryAddScoped<JobControl>();
        services.TryAddSingleton<JobHandlerRegistry>();
        return services;
    }

    /// <summary>
    /// Registers the handler for one kind. Register it in every process that runs that kind's
    /// lane; a worker that receives a kind it has no handler for fails the job as unknown.
    /// </summary>
    public static IServiceCollection AddJobHandler<THandler, TPayload>(this IServiceCollection services, string kind)
        where THandler : class, IJobHandler<TPayload>
    {
        services.TryAddScoped<THandler>();
        services.AddSingleton(new JobHandlerRegistration(kind, JobHandlerRegistry.Invoke<THandler, TPayload>));
        return services;
    }

    /// <summary>
    /// Registers one handler for every kind beginning with <paramref name="prefix"/>: kinds that
    /// are data rather than code, such as each plugin's declared jobs (<c>plugin:{id}:{job}</c>).
    /// </summary>
    public static IServiceCollection AddJobHandlerPrefix<THandler, TPayload>(this IServiceCollection services, string prefix)
        where THandler : class, IJobHandler<TPayload>
    {
        services.TryAddScoped<THandler>();
        services.AddSingleton(new JobHandlerRegistration(prefix, JobHandlerRegistry.Invoke<THandler, TPayload>, IsPrefix: true));
        return services;
    }

    /// <summary>
    /// Drains one lane. Concurrency is <c>Jobs:Lanes:{lane}:Concurrency</c>, else
    /// <paramref name="defaultConcurrency"/>; <c>Jobs:Lanes:{lane}:Enabled=false</c> leaves the lane
    /// to other replicas (and keeps a test host from racing the claims it makes itself).
    /// </summary>
    public static IServiceCollection AddJobConsumer(
        this IServiceCollection services, IConfiguration configuration, string lane, int defaultConcurrency)
    {
        if (!configuration.GetValue($"Jobs:Lanes:{lane}:Enabled", true))
        {
            return services;
        }
        var concurrency = Math.Max(1, configuration.GetValue($"Jobs:Lanes:{lane}:Concurrency", defaultConcurrency));
        // Not AddHostedService: it de-duplicates by implementation type, and a process may drain
        // more than one lane with the same host class.
        services.AddSingleton<IHostedService>(sp =>
            ActivatorUtilities.CreateInstance<JobConsumerHost>(sp, lane, concurrency));
        return services;
    }

    /// <summary>
    /// The ledger's own background work: folding in reports from bus-only workers, and the
    /// maintenance sweep. Run by the ledger's owner (admin-api).
    /// </summary>
    public static IServiceCollection AddJobLedgerHousekeeping(this IServiceCollection services)
    {
        services.AddHostedService<JobStateIngestConsumer>();
        services.AddHostedService<JobMaintenanceWorker>();
        return services;
    }

    /// <summary>For workers that report progress over the bus instead of writing the ledger.</summary>
    public static IServiceCollection AddJobReporter(this IServiceCollection services)
    {
        services.TryAddSingleton<IJobReporter, BusJobReporter>();
        return services;
    }
}
