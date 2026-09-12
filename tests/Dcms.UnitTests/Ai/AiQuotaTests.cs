using Dcms.AiGateway;
using Dcms.Shared.Caching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dcms.UnitTests.Ai;

/// <summary>
/// The wall a runaway agent loop hits instead of a bill.
///
/// <para>Worth testing closely for the same reason the bridge is: every mistake it can make is
/// silent and expensive. A limiter that counts the wrong key never refuses anything; one that
/// fails closed takes the whole AI feature down with Redis; one that forgets to expire its
/// counters refuses a workspace forever an hour after the incident is over.</para>
/// </summary>
public class AiQuotaTests
{
    private static AiQuota Build(ICacheService cache, params (string Key, string Value)[] settings)
    {
        AiQuota.ResetLocal();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        return new AiQuota(cache, config, NullLogger<AiQuota>.Instance);
    }

    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid User = Guid.Parse("33333333-3333-3333-3333-333333333333");

    // ---------- calls per minute ----------

    [Fact]
    public async Task Allows_calls_up_to_the_limit()
    {
        var quota = Build(new FakeCache(), ("Ai:Limits:CallsPerMinute", "3"));

        for (var i = 0; i < 3; i++)
        {
            Assert.True((await quota.CheckAsync(Tenant, User, TestContext.Current.CancellationToken)).Ok);
        }
    }

    [Fact]
    public async Task Refuses_the_call_past_the_limit()
    {
        var quota = Build(new FakeCache(), ("Ai:Limits:CallsPerMinute", "2"));

        await quota.CheckAsync(Tenant, User, TestContext.Current.CancellationToken);
        await quota.CheckAsync(Tenant, User, TestContext.Current.CancellationToken);
        var verdict = await quota.CheckAsync(Tenant, User, TestContext.Current.CancellationToken);

        Assert.False(verdict.Ok);
        Assert.Equal("rate_limited", verdict.Error);
        // Without this the browser can only say "later", which is the difference between a limit
        // somebody can work with and one that just looks broken.
        Assert.True(verdict.RetryAfterSeconds > 0);
    }

    [Fact]
    public async Task Counts_each_user_separately()
    {
        // One person's runaway tab must not lock their colleagues out of the workspace.
        var quota = Build(new FakeCache(), ("Ai:Limits:CallsPerMinute", "1"));

        await quota.CheckAsync(Tenant, User, TestContext.Current.CancellationToken);
        Assert.False((await quota.CheckAsync(Tenant, User, TestContext.Current.CancellationToken)).Ok);
        Assert.True((await quota.CheckAsync(Tenant, Other, TestContext.Current.CancellationToken)).Ok);
    }

    [Fact]
    public async Task Counts_each_tenant_separately()
    {
        var quota = Build(new FakeCache(), ("Ai:Limits:CallsPerMinute", "1"));

        await quota.CheckAsync(Tenant, User, TestContext.Current.CancellationToken);
        Assert.True((await quota.CheckAsync(Other, User, TestContext.Current.CancellationToken)).Ok);
    }

    [Fact]
    public async Task Expires_the_call_window()
    {
        // A counter with no expiry would refuse a workspace forever, an hour after the incident
        // that tripped it is over.
        var cache = new FakeCache();
        var quota = Build(cache, ("Ai:Limits:CallsPerMinute", "5"));

        await quota.CheckAsync(Tenant, User, TestContext.Current.CancellationToken);

        var ttl = Assert.Single(cache.Ttls.Values);
        Assert.InRange(ttl, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task A_zero_limit_turns_the_check_off()
    {
        var cache = new FakeCache();
        var quota = Build(cache, ("Ai:Limits:CallsPerMinute", "0"), ("Ai:Limits:TokensPerDay", "0"));

        for (var i = 0; i < 50; i++)
        {
            Assert.True((await quota.CheckAsync(Tenant, User, TestContext.Current.CancellationToken)).Ok);
        }
        Assert.Empty(cache.Values);
    }

    // ---------- token budget ----------

    [Fact]
    public async Task Allows_while_the_budget_is_not_spent()
    {
        var quota = Build(new FakeCache(), ("Ai:Limits:TokensPerDay", "1000"), ("Ai:Limits:CallsPerMinute", "0"));

        await quota.RecordAsync(Tenant, 900, TestContext.Current.CancellationToken);
        Assert.True((await quota.CheckAsync(Tenant, User, TestContext.Current.CancellationToken)).Ok);
    }

    [Fact]
    public async Task Refuses_once_the_budget_is_spent()
    {
        var quota = Build(new FakeCache(), ("Ai:Limits:TokensPerDay", "1000"), ("Ai:Limits:CallsPerMinute", "0"));

        await quota.RecordAsync(Tenant, 1000, TestContext.Current.CancellationToken);
        var verdict = await quota.CheckAsync(Tenant, User, TestContext.Current.CancellationToken);

        Assert.False(verdict.Ok);
        Assert.Equal("budget_exhausted", verdict.Error);
    }

    [Fact]
    public async Task Budget_is_shared_by_every_user_in_the_tenant()
    {
        // The bill is the workspace's, however many people ran up the tokens.
        var quota = Build(new FakeCache(), ("Ai:Limits:TokensPerDay", "100"), ("Ai:Limits:CallsPerMinute", "0"));

        await quota.RecordAsync(Tenant, 100, TestContext.Current.CancellationToken);
        Assert.False((await quota.CheckAsync(Tenant, Other, TestContext.Current.CancellationToken)).Ok);
    }

    [Fact]
    public async Task One_tenants_spend_does_not_touch_another()
    {
        var quota = Build(new FakeCache(), ("Ai:Limits:TokensPerDay", "100"), ("Ai:Limits:CallsPerMinute", "0"));

        await quota.RecordAsync(Tenant, 500, TestContext.Current.CancellationToken);
        Assert.True((await quota.CheckAsync(Other, User, TestContext.Current.CancellationToken)).Ok);
    }

    [Fact]
    public async Task Recording_nothing_costs_nothing()
    {
        var cache = new FakeCache();
        var quota = Build(cache, ("Ai:Limits:TokensPerDay", "100"));

        await quota.RecordAsync(Tenant, 0, TestContext.Current.CancellationToken);
        Assert.Empty(cache.Values);
    }

    [Fact]
    public async Task The_budget_counter_outlives_its_own_day()
    {
        // The key is the UTC date, so a counter created at 23:59 must survive long enough to be
        // read by anything still finishing that minute.
        var cache = new FakeCache();
        var quota = Build(cache, ("Ai:Limits:TokensPerDay", "100"));

        await quota.RecordAsync(Tenant, 10, TestContext.Current.CancellationToken);

        var ttl = Assert.Single(cache.Ttls.Values);
        Assert.True(ttl >= TimeSpan.FromHours(24));
    }

    // ---------- degraded Redis ----------

    [Fact]
    public async Task Still_refuses_when_the_counter_store_is_down()
    {
        // Failing fully open would mean the wall vanishes exactly when nobody is watching.
        var quota = Build(new BrokenCache(), ("Ai:Limits:CallsPerMinute", "2"));

        await quota.CheckAsync(Tenant, User, TestContext.Current.CancellationToken);
        await quota.CheckAsync(Tenant, User, TestContext.Current.CancellationToken);

        Assert.False((await quota.CheckAsync(Tenant, User, TestContext.Current.CancellationToken)).Ok);
    }

    [Fact]
    public async Task Does_not_take_the_ai_feature_down_with_redis()
    {
        // Failing closed would make Redis a hard dependency of every AI feature on the platform,
        // and Redis hiccups are far more common than runaway loops.
        var quota = Build(new BrokenCache(), ("Ai:Limits:CallsPerMinute", "10"));

        Assert.True((await quota.CheckAsync(Tenant, User, TestContext.Current.CancellationToken)).Ok);
    }

    [Fact]
    public async Task Recording_survives_a_broken_counter_store()
    {
        var quota = Build(new BrokenCache(), ("Ai:Limits:TokensPerDay", "100"));

        // Must not throw: the call has already been made and billed, and losing the accounting
        // is not a reason to fail the response.
        await quota.RecordAsync(Tenant, 50, TestContext.Current.CancellationToken);
    }

    // ---------- fakes ----------

    private sealed class FakeCache : ICacheService
    {
        public readonly Dictionary<string, long> Values = [];
        public readonly Dictionary<string, TimeSpan> Ttls = [];

        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
        {
            if (!Values.TryGetValue(key, out var value)) return Task.FromResult<T?>(default);
            return Task.FromResult((T?)Convert.ChangeType(value, typeof(T)));
        }

        public Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RemoveAsync(string key, CancellationToken ct = default)
        {
            Values.Remove(key);
            return Task.CompletedTask;
        }

        public Task<long> IncrementAsync(string key, CancellationToken ct = default)
            => IncrementAsync(key, 1, TimeSpan.FromHours(1), ct);

        public Task<long> IncrementAsync(string key, long by, TimeSpan ttl, CancellationToken ct = default)
        {
            var next = Values.GetValueOrDefault(key) + by;
            Values[key] = next;
            // Mirrors Redis: the expiry is set only when the counter is created.
            if (next == by) Ttls[key] = ttl;
            return Task.FromResult(next);
        }
    }

    private sealed class BrokenCache : ICacheService
    {
        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) => throw new InvalidOperationException("redis down");
        public Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default) => throw new InvalidOperationException("redis down");
        public Task RemoveAsync(string key, CancellationToken ct = default) => throw new InvalidOperationException("redis down");
        public Task<long> IncrementAsync(string key, CancellationToken ct = default) => throw new InvalidOperationException("redis down");
        public Task<long> IncrementAsync(string key, long by, TimeSpan ttl, CancellationToken ct = default) => throw new InvalidOperationException("redis down");
    }
}
