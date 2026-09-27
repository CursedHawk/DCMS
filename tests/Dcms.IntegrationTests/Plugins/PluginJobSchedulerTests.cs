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
        var bus = new Bus();
        var now = DateTimeOffset.UtcNow;

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
            await PluginJobScheduler.PassAsync(scheduled, db, cache, bus, now, ct);
            await PluginJobScheduler.PassAsync(scheduled, db, cache, bus, now.AddMinutes(1), ct);
        }

        var job = bus.Published.Should().ContainSingle().Which;
        job.TenantId.Should().Be(enabledTenant);
        job.JobName.Should().Be("refresh");

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
            await PluginJobScheduler.PassAsync(scheduled, db, cache, bus, now.AddHours(1), ct);
        }
        bus.Published.Should().HaveCount(2, "the next interval slot runs again");
    }

    private sealed class Bus : IEventPublisher
    {
        public List<PluginJobRequested> Published { get; } = [];

        public ValueTask PublishAsync<T>(string subject, T @event, CancellationToken ct = default, string? messageId = null)
            where T : IDcmsEvent
        {
            Published.Add((PluginJobRequested)(object)@event!);
            return ValueTask.CompletedTask;
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
