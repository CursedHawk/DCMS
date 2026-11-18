using System.Collections.Concurrent;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Runtime.Hosting;
using Dcms.Shared.Caching;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.IntegrationTests.Plugins;

/// <summary>
/// The interval scheduler against real plugin instances: one job per tenant with an enabled
/// instance, per interval slot, however many passes (or replicas) run inside that slot.
/// </summary>
[Collection(AdminApiCollection.Name)]
public sealed class PluginJobSchedulerTests(AdminApiFixture fixture)
{
    private sealed class NoopJob : IPluginJobHandler
    {
        public Task RunAsync(JsonElement? payload, IPluginContext context, CancellationToken ct) => Task.CompletedTask;
    }

    [DockerFact]
    public async Task Enqueues_once_per_enabled_tenant_per_slot()
    {
        var ct = TestContext.Current.CancellationToken;
        var pluginId = "sched-" + Guid.NewGuid().ToString("N")[..8];
        var enabledTenant = Guid.NewGuid();
        var disabledTenant = Guid.NewGuid();

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
            foreach (var (tenant, enabled) in new[] { (enabledTenant, true), (disabledTenant, false) })
            {
                using var rls = RlsScope.Tenant(tenant);
                db.PluginInstances.Add(new PluginInstance
                {
                    Id = Guid.NewGuid(), TenantId = tenant, PluginId = pluginId, Slug = pluginId, Name = pluginId, Enabled = enabled,
                });
                await db.SaveChangesAsync(ct);
            }
        }

        var scheduled = new[] { (pluginId, JobDeclaration.Of<NoopJob>("refresh", TimeSpan.FromHours(1))) };
        var cache = new MemoryCache();
        var jobs = new Queue();
        // Ten minutes into an hourly slot, not "now": slots are unix-seconds / interval, so a run in
        // the last minute of an hour put `now.AddMinutes(1)` in the next slot and enqueued twice.
        var hour = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600 * 3600);
        var now = hour.AddMinutes(10);

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
            await PluginJobScheduler.PassAsync(scheduled, db, cache, jobs, now, ct);
            await PluginJobScheduler.PassAsync(scheduled, db, cache, jobs, now.AddMinutes(1), ct);
        }

        var job = jobs.Enqueued.Should().ContainSingle().Which;
        job.TenantId.Should().Be(enabledTenant);
        job.Kind.Should().Be($"plugin:{pluginId}:refresh");
        job.Routine.Should().BeTrue("a schedule's tick is work nobody is waiting on");
        job.RequiredPermission.Should().Be("plugins:manage", "a schedule's run is not every member's to see");

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
            await PluginJobScheduler.PassAsync(scheduled, db, cache, jobs, now.AddHours(1), ct);
        }
        jobs.Enqueued.Should().HaveCount(2, "the next interval slot runs again");
        jobs.Enqueued.Select(j => j.DedupeKey).Should().OnlyHaveUniqueItems("each slot is its own job, once");
    }

    private sealed class Queue : Dcms.Shared.Jobs.IJobQueue
    {
        public List<Dcms.Shared.Jobs.JobSpec> Enqueued { get; } = [];

        public Task<Dcms.Shared.Jobs.JobRef> EnqueueAsync(Dcms.Shared.Jobs.JobSpec spec, CancellationToken ct)
        {
            Enqueued.Add(spec);
            return Task.FromResult(new Dcms.Shared.Jobs.JobRef(Guid.NewGuid()));
        }
    }

    private sealed class MemoryCache : ICacheService
    {
        private readonly ConcurrentDictionary<string, object?> _values = new();

        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) =>
            Task.FromResult(_values.TryGetValue(key, out var v) ? (T?)v : default);

        public Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task<long> IncrementAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> IncrementAsync(string key, long by, TimeSpan ttl, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
