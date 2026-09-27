using System.Text.Json;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Runtime.Contracts;
using Dcms.Shared.Audit;
using Dcms.Shared.Caching;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Messaging;
using Dcms.Shared.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dcms.PluginSdk.Runtime.Hosting;

public static class PluginWorkerServiceCollectionExtensions
{
    /// <summary>
    /// Hosts plugins' event handlers, jobs and the interval scheduler. admin-api only: that is
    /// where background work lives, and where <c>dcms.secrets@1</c> can decrypt.
    /// </summary>
    public static IServiceCollection AddDcmsPluginWorkers(this IServiceCollection services)
    {
        services.AddSingleton<PluginHandlerRunner>();
        services.AddHostedService<PluginEventConsumer>();
        services.AddHostedService<PluginJobConsumer>();
        services.AddHostedService<PluginJobScheduler>();
        return services;
    }
}

/// <summary>
/// Runs one piece of plugin code outside a request: a fresh DI scope, the tenant as the
/// ambient tenant for the database (<see cref="RlsScope"/>, which the CMS query filter honours
/// too), the audit context restored from the message, and an <see cref="IPluginContext"/> for the
/// plugin — reachable both as the handler argument and from DI.
/// </summary>
public sealed class PluginHandlerRunner(IServiceProvider services)
{
    /// <returns>False when the plugin has no enabled instance in the tenant (nothing was run).</returns>
    public async Task<bool> RunAsync(
        Guid tenantId, string pluginId, Guid? instanceId, Func<IPluginContext, IServiceProvider, Task> run, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        using var rls = RlsScope.Tenant(tenantId);
        var sp = scope.ServiceProvider;

        var factory = sp.GetRequiredService<PluginContextFactory>();
        var enabled = await factory.EnabledInstancesAsync(tenantId, ct);
        var own = enabled.Where(i => i.PluginId == pluginId).ToList();
        if (own.Count == 0)
        {
            return false;
        }
        PluginInstanceContext? instance = null;
        if (instanceId is { } id)
        {
            instance = own.FirstOrDefault(i => i.InstanceId == id);
            if (instance is null)
            {
                return false; // disabled or deleted since this was queued
            }
        }

        var context = await factory.CreateAsync(tenantId, pluginId, instance, PluginActor.System, ct);
        sp.GetRequiredService<PluginContextAccessor>().Current = context;
        try
        {
            await run(context, sp);
        }
        finally
        {
            if (sp.GetService<IAuditRecorder>() is { } audit)
            {
                await audit.FlushAsync(ct);
            }
        }
        return true;
    }
}

/// <summary>
/// One durable per subscription (subscriber plugin × event name), filtering on
/// <c>plugins.events.*.{event}</c>, so each subscriber acks independently: a failing handler
/// redelivers to itself and never re-runs anyone else's.
/// </summary>
public sealed class PluginEventConsumer(
    PluginRegistry registry,
    PluginHandlerRunner runner,
    INatsJSContext jetStream,
    DcmsMetrics metrics,
    ILogger<PluginEventConsumer> logger) : BackgroundService
{
    private const int MaxDeliver = 5;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loops = registry.Plugins
            .SelectMany(p => (p.Manifest.Subscribes ?? []).Select(s => (Plugin: p.Manifest.Id, Subscription: s)))
            .Select(x => ConsumeAsync(x.Plugin, x.Subscription, stoppingToken))
            .ToList();
        return Task.WhenAll(loops);
    }

    private async Task ConsumeAsync(string pluginId, EventSubscription subscription, CancellationToken stoppingToken)
    {
        var durable = $"admin-api-plugin-{pluginId}-{subscription.EventName.Replace('.', '-')}";
        var subject = Subjects.PluginEventAnyPublisher(subscription.EventName);
        var invoke = typeof(PluginEventConsumer)
            .GetMethod(nameof(HandleTypedAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(subscription.EventType);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var config = new ConsumerConfig(durable)
                {
                    FilterSubject = subject,
                    AckPolicy = ConsumerConfigAckPolicy.Explicit,
                    AckWait = TimeSpan.FromMinutes(1),
                    MaxDeliver = MaxDeliver,
                };
                // A subscription added today should not replay a week of history into its handler.
                if (!await ExistsAsync(durable, stoppingToken))
                {
                    config.DeliverPolicy = ConsumerConfigDeliverPolicy.New;
                }
                var consumer = await jetStream.CreateOrUpdateConsumerAsync(Streams.PluginEvents, config, stoppingToken);

                await foreach (var msg in consumer.ConsumeAsync<PluginEventPublished>(cancellationToken: stoppingToken))
                {
                    var started = System.Diagnostics.Stopwatch.StartNew();
                    var evt = msg.Data;
                    try
                    {
                        if (evt is not null)
                        {
                            await runner.RunAsync(evt.TenantId, pluginId, instanceId: null, async (ctx, sp) =>
                            {
                                var payload = JsonSerializer.Deserialize(evt.PayloadJson, subscription.EventType, ContractDescriptorBuilder.Json)!;
                                var handler = ActivatorUtilities.CreateInstance(sp, subscription.Handler);
                                await (Task)invoke.Invoke(null, [handler, payload, ctx, stoppingToken])!;
                            }, stoppingToken);
                        }
                        await msg.AckAsync(cancellationToken: stoppingToken);
                        metrics.MessageHandled(Streams.PluginEvents, subscription.EventName, succeeded: true, started.Elapsed);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        metrics.MessageHandled(Streams.PluginEvents, subscription.EventName, succeeded: false, started.Elapsed);
                        var attempt = (int)(msg.Metadata?.NumDelivered ?? 1);
                        logger.LogWarning(ex, "Plugin {Plugin} failed handling {Event} (attempt {Attempt}).",
                            pluginId, subscription.EventName, attempt);
                        if (attempt >= MaxDeliver)
                        {
                            await msg.AckTerminateAsync(cancellationToken: stoppingToken);
                        }
                        else
                        {
                            await msg.NakAsync(delay: TimeSpan.FromSeconds(Math.Pow(2, attempt) * 5), cancellationToken: stoppingToken);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "{Durable} unavailable; retrying in 5s.", durable);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private static Task HandleTypedAsync<TEvent>(object handler, object payload, IPluginContext context, CancellationToken ct)
        where TEvent : IPluginEvent =>
        ((IPluginEventHandler<TEvent>)handler).HandleAsync((TEvent)payload, context, ct);

    private async Task<bool> ExistsAsync(string durable, CancellationToken ct)
    {
        try
        {
            await jetStream.GetConsumerAsync(Streams.PluginEvents, durable, ct);
            return true;
        }
        catch (NatsJSApiException)
        {
            return false;
        }
    }
}

/// <summary>
/// Drains <c>PLUGIN_JOBS</c> (work queue: exactly one consumer, and one delivery per job).
/// A delayed job is deferred with a NAK delay until its time; a failing one backs off and is
/// dropped after <see cref="MaxDeliver"/> attempts.
/// </summary>
public sealed class PluginJobConsumer(
    PluginRegistry registry,
    PluginHandlerRunner runner,
    INatsJSContext jetStream,
    DcmsMetrics metrics,
    ILogger<PluginJobConsumer> logger) : BackgroundService
{
    private const string DurableName = "admin-api-plugin-jobs";
    private const int MaxDeliver = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var consumer = await jetStream.CreateOrUpdateConsumerAsync(Streams.PluginJobs, new ConsumerConfig(DurableName)
                {
                    FilterSubject = Subjects.PluginJobsAll,
                    AckPolicy = ConsumerConfigAckPolicy.Explicit,
                    AckWait = TimeSpan.FromMinutes(5),
                    // Unlimited at the server: a deferral is a delivery too, so the give-up rule
                    // lives in HandleAsync, which can tell the two apart.
                    MaxDeliver = -1,
                    MaxAckPending = 4,
                }, stoppingToken);

                await foreach (var msg in consumer.ConsumeAsync<PluginJobRequested>(cancellationToken: stoppingToken))
                {
                    await HandleAsync(msg, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "{Durable} unavailable; retrying in 5s.", DurableName);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task HandleAsync(INatsJSMsg<PluginJobRequested> msg, CancellationToken ct)
    {
        var job = msg.Data;
        var declaration = job is null
            ? null
            : registry.Find(job.PluginId)?.Jobs?.FirstOrDefault(j => j.Name == job.JobName);
        if (job is null || declaration is null)
        {
            logger.LogWarning("Discarding plugin job {Subject}: no such plugin job in this build.", msg.Subject);
            await msg.AckTerminateAsync(cancellationToken: ct);
            return;
        }
        if (job.NotBefore is { } notBefore && notBefore > DateTimeOffset.UtcNow)
        {
            await msg.NakAsync(delay: notBefore - DateTimeOffset.UtcNow, cancellationToken: ct);
            return;
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        var failures = FailureCount(msg);
        try
        {
            await using var heartbeat = AckHeartbeat.Start(msg, TimeSpan.FromMinutes(1), logger);
            await runner.RunAsync(job.TenantId, job.PluginId, job.InstanceId, async (ctx, sp) =>
            {
                JsonElement? payload = job.PayloadJson is null ? null : JsonDocument.Parse(job.PayloadJson).RootElement.Clone();
                var handler = (IPluginJobHandler)ActivatorUtilities.CreateInstance(sp, declaration.Handler);
                await handler.RunAsync(payload, ctx, ct);
            }, ct);
            await msg.AckAsync(cancellationToken: ct);
            metrics.MessageHandled(Streams.PluginJobs, job.JobName, succeeded: true, started.Elapsed);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            metrics.MessageHandled(Streams.PluginJobs, job.JobName, succeeded: false, started.Elapsed);
            logger.LogWarning(ex, "Plugin job {Plugin}/{Job} failed (failure {Failure}).", job.PluginId, job.JobName, failures + 1);
            if (failures + 1 >= MaxDeliver)
            {
                await msg.AckTerminateAsync(cancellationToken: ct);
                return;
            }
            await msg.NakAsync(delay: TimeSpan.FromSeconds(Math.Pow(2, failures + 1) * 10), cancellationToken: ct);
        }
    }

    /// <summary>
    /// Earlier failed attempts. A delayed job spends exactly one delivery being deferred (the
    /// NAK delay lands it at NotBefore), so that delivery is not a failure.
    /// </summary>
    private static int FailureCount(INatsJSMsg<PluginJobRequested> msg)
    {
        var delivered = (int)(msg.Metadata?.NumDelivered ?? 1);
        return Math.Max(0, delivered - (msg.Data?.NotBefore is null ? 1 : 2));
    }
}

/// <summary>
/// Enqueues interval jobs (<see cref="JobDeclaration.Interval"/>) for every tenant with an
/// enabled instance of the plugin. One replica per pass (advisory lock); a slot is recorded in
/// the cache so a pass, a second replica, or a restart inside the same interval never
/// enqueues it twice.
/// </summary>
public sealed class PluginJobScheduler(
    PluginRegistry registry,
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<PluginJobScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var scheduled = registry.Plugins
            .SelectMany(p => (p.Manifest.Jobs ?? []).Where(j => j.Interval is not null).Select(j => (Plugin: p.Manifest.Id, Job: j)))
            .ToList();
        var connectionString = configuration.GetConnectionString("Postgres");
        if (scheduled.Count == 0 || string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        using var timer = new PeriodicTimer(Tick);
        do
        {
            try
            {
                await using var leadership = await PostgresAdvisoryLock.TryAcquireAsync(
                    connectionString, PostgresAdvisoryLock.PluginSchedulerLockKey, logger, stoppingToken);
                if (leadership is not null)
                {
                    using var scope = services.CreateScope();
                    var sp = scope.ServiceProvider;
                    await PassAsync(
                        scheduled, sp.GetRequiredService<CmsDbContext>(), sp.GetRequiredService<ICacheService>(),
                        sp.GetRequiredService<IEventPublisher>(), DateTimeOffset.UtcNow, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Plugin job scheduler pass failed; retrying next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One scheduling pass. Static and explicit about its inputs so it can be tested as is.</summary>
    public static async Task PassAsync(
        IReadOnlyList<(string Plugin, JobDeclaration Job)> scheduled,
        CmsDbContext db,
        ICacheService cache,
        IEventPublisher bus,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var pluginIds = scheduled.Select(s => s.Plugin).Distinct().ToList();
        List<(Guid TenantId, string PluginId)> active;
        using (RlsScope.Platform())
        {
            active = (await db.PluginInstances.IgnoreQueryFilters().AsNoTracking()
                    .Where(p => p.Enabled && pluginIds.Contains(p.PluginId))
                    .Select(p => new { p.TenantId, p.PluginId })
                    .Distinct()
                    .ToListAsync(ct))
                .Select(x => (x.TenantId, x.PluginId))
                .ToList();
        }

        foreach (var (plugin, job) in scheduled)
        {
            var interval = job.Interval!.Value;
            var slot = now.ToUnixTimeSeconds() / (long)interval.TotalSeconds;
            foreach (var (tenantId, _) in active.Where(a => a.PluginId == plugin))
            {
                var key = $"plugin-sched:{tenantId}:{plugin}:{job.Name}";
                var last = await cache.GetAsync<long?>(key, ct);
                if (last >= slot)
                {
                    continue;
                }
                var message = new PluginJobRequested(
                    Guid.CreateVersion7(), now, tenantId, plugin, job.Name, InstanceId: null, PayloadJson: null, NotBefore: null);
                await bus.PublishAsync(Subjects.PluginJob(plugin, job.Name), message, ct,
                    messageId: $"{tenantId}:{plugin}:{job.Name}:slot-{slot}");
                await cache.SetAsync<long?>(key, slot, interval * 2, ct);
            }
        }
    }
}
