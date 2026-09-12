using Dcms.Shared.Caching;

namespace Dcms.AiGateway;

/// <summary>
/// The wall a runaway agent loop hits instead of a bill.
///
/// <para><b>Why here and not in admin-api.</b> This service is the single choke point for every
/// model call the platform makes — the IDE agent, the ⌘J dock, content generation, the site
/// chatbot. A limit in admin-api would miss content-api's chatbot entirely, and only this
/// service learns what a call actually cost, because the token counts are read off the upstream
/// stream as it passes.</para>
///
/// <h3>Two limits, because there are two different failures</h3>
/// <p><b>Calls per minute, per user.</b> A browser loop gone wrong makes calls as fast as the
/// provider can answer them. Minutes is the right granularity: it stops the loop while it is
/// still a nuisance, and no legitimate agent run comes close.</p>
/// <p><b>Tokens per day, per tenant.</b> The budget. Slow-moving, and the thing an operator
/// actually cares about — a workspace should not be able to spend a month's credit in an
/// afternoon because somebody left a tab open.</p>
///
/// <h3>What this deliberately does not promise</h3>
/// <p><b>The token ceiling can be overshot by one call.</b> A call's cost is only known once it
/// has been made, so the check before it runs is against what has been spent so far. Refusing
/// everything once the budget is within one call of the limit would be the alternative, and it
/// would cut runs off early for a guarantee nobody asked for.</p>
///
/// <p><b>A Redis outage degrades this to per-replica counting rather than switching it off.</b>
/// Failing closed would make Redis a hard dependency of every AI feature on the platform, and
/// Redis hiccups are far more common than runaway loops. Failing fully open would mean the wall
/// vanishes exactly when nobody is watching. So the in-memory fallback keeps a real limit —
/// less accurate across replicas, but still a limit — and says so in the log.</p>
/// </summary>
public sealed class AiQuota(
    ICacheService cache,
    IConfiguration configuration,
    ILogger<AiQuota> logger)
{
    /// <summary>Model calls one user may make in a minute. 0 disables the check.</summary>
    private readonly int _callsPerMinute = configuration.GetValue("Ai:Limits:CallsPerMinute", 60);

    /// <summary>Tokens (prompt + completion) one tenant may spend in a day. 0 disables it.</summary>
    private readonly long _tokensPerDay = configuration.GetValue("Ai:Limits:TokensPerDay", 5_000_000L);

    /// <summary>
    /// Per-replica counters, used only while Redis is unreachable.
    ///
    /// <para>Keyed exactly like the Redis keys, so a window that starts in memory and continues
    /// in Redis (or the reverse) is at worst counted twice rather than lost. Bounded by the fact
    /// that every key carries a time bucket and old buckets are swept on read.</para>
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Count, DateTimeOffset Expires)> Local = new();

    public async Task<QuotaVerdict> CheckAsync(Guid tenantId, Guid? userId, CancellationToken ct)
    {
        if (_callsPerMinute > 0)
        {
            var key = CallKey(tenantId, userId);
            var calls = await BumpAsync(key, 1, TimeSpan.FromMinutes(2), ct);
            if (calls > _callsPerMinute)
            {
                logger.LogWarning(
                    "AI call limit reached: tenant {TenantId} user {UserId} made {Calls} calls this minute (limit {Limit}).",
                    tenantId, userId, calls, _callsPerMinute);
                return QuotaVerdict.Denied(
                    "rate_limited",
                    $"Too many AI requests. The limit is {_callsPerMinute} per minute; try again shortly.",
                    retryAfterSeconds: 60);
            }
        }

        if (_tokensPerDay > 0)
        {
            // Read, not incremented: tokens are added after a call, by RecordAsync.
            var spent = await ReadAsync(TokenKey(tenantId), ct);
            if (spent >= _tokensPerDay)
            {
                logger.LogWarning(
                    "AI token budget exhausted: tenant {TenantId} has spent {Spent} of {Limit} today.",
                    tenantId, spent, _tokensPerDay);
                return QuotaVerdict.Denied(
                    "budget_exhausted",
                    "This workspace has reached its AI usage limit for today.",
                    // Until midnight UTC, which is when the counter's day rolls over.
                    retryAfterSeconds: (int)Math.Max(60, (DateTimeOffset.UtcNow.Date.AddDays(1) - DateTimeOffset.UtcNow).TotalSeconds));
            }
        }

        return QuotaVerdict.Allowed;
    }

    /// <summary>
    /// Add what a finished call cost to the tenant's day.
    ///
    /// <para>Called even for a call that failed partway: the tokens produced before it died were
    /// still generated and still billed, so leaving them out would make the ceiling something a
    /// broken stream could walk straight through.</para>
    /// </summary>
    public async Task RecordAsync(Guid tenantId, long tokens, CancellationToken ct)
    {
        if (_tokensPerDay <= 0 || tokens <= 0) return;

        // Two days, not one: the key is the UTC date, so a counter created at 23:59 must
        // outlive its own day long enough to be read by anything still finishing that minute.
        await BumpAsync(TokenKey(tenantId), tokens, TimeSpan.FromHours(48), ct);
    }

    private static string CallKey(Guid tenantId, Guid? userId) =>
        $"ai:rl:{tenantId}:{userId?.ToString() ?? "tenant"}:{DateTimeOffset.UtcNow:yyyyMMddHHmm}";

    private static string TokenKey(Guid tenantId) => $"ai:tok:{tenantId}:{DateTimeOffset.UtcNow:yyyyMMdd}";

    private async Task<long> BumpAsync(string key, long by, TimeSpan ttl, CancellationToken ct)
    {
        try
        {
            return await cache.IncrementAsync(key, by, ttl, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI quota counter unavailable; counting in this replica only.");
            return BumpLocal(key, by, ttl);
        }
    }

    private async Task<long> ReadAsync(string key, CancellationToken ct)
    {
        try
        {
            // Stored by IncrementAsync as a plain integer, which Redis returns as a string.
            return await cache.GetAsync<long>(key, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI quota counter unavailable; reading this replica's own count.");
            return BumpLocal(key, 0, TimeSpan.FromHours(48));
        }
    }

    private static long BumpLocal(string key, long by, TimeSpan ttl)
    {
        var now = DateTimeOffset.UtcNow;

        // Sweep on write rather than on a timer: these keys carry a time bucket, so the set is
        // small and short-lived, and a background sweeper for it would be more machinery than
        // the thing it maintains.
        foreach (var (existing, value) in Local)
        {
            if (value.Expires <= now) Local.TryRemove(existing, out _);
        }

        var updated = Local.AddOrUpdate(
            key,
            _ => (by, now + ttl),
            (_, current) => (current.Count + by, current.Expires));

        return updated.Count;
    }

    /// <summary>
    /// Clears the per-replica fallback counters.
    ///
    /// <para>A test seam, and public because the alternative is an <c>InternalsVisibleTo</c>
    /// attribute this solution does not use anywhere else. Nothing in the service calls it:
    /// the counters are static so that they survive DI scopes, which is exactly what makes
    /// them leak between tests.</para>
    /// </summary>
    public static void ResetLocal() => Local.Clear();
}

public readonly record struct QuotaVerdict(bool Ok, string? Error, string? Message, int RetryAfterSeconds)
{
    public static QuotaVerdict Allowed => new(true, null, null, 0);

    public static QuotaVerdict Denied(string error, string message, int retryAfterSeconds) =>
        new(false, error, message, retryAfterSeconds);
}
