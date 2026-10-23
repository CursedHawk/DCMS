using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dcms.Shared.Caching;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.DynamicApps.Data;

/// <summary>
/// A list's total, cached in Redis per filter under a generation every committed record write in the
/// instance bumps (<see cref="ChangedAsync"/>). The capped COUNT behind it was the costliest statement
/// of the 2026-10-23 load test: run on every list request, it read up to 5,001 rows to say "5,000+".
///
/// <para>Per instance, not per table: a filter or a row rule can read another table through a lookup,
/// so a write anywhere in the app can change any count. The key also carries the compiled SQL and every
/// parameter value (the viewer's, for row rules) and the published revision, so a model change starts
/// fresh. Bumped after the commit, never before: a count read between the two would otherwise be
/// stored under the new generation and outlive the write.</para>
///
/// <para>Without Redis it simply counts.</para>
/// </summary>
public sealed class RecordCounts(IServiceProvider services)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private ICacheService? Cache => services.GetService<ICacheService>();

    public static string GenerationKey(Guid tenantId, Guid instanceId) => $"t:{tenantId}:apps:{instanceId}:records:gen";

    public async Task<int> CountAsync(Guid tenantId, Guid instanceId, int revision, string sql,
        QueryCompiler.Compiled compiled, Func<Task<int>> count, CancellationToken ct)
    {
        if (Cache is not { } cache)
        {
            return await count();
        }
        var generation = await cache.GetAsync<long?>(GenerationKey(tenantId, instanceId), ct) ?? 0;
        var key = $"t:{tenantId}:apps:{instanceId}:count:{revision}:{generation}:{Fingerprint(sql, compiled)}";
        if (await cache.GetAsync<int?>(key, ct) is { } known)
        {
            return known;
        }
        var counted = await count();
        await cache.SetAsync(key, (int?)counted, Ttl, ct);
        return counted;
    }

    /// <summary>After a committed write to any record or link of the instance.</summary>
    public Task ChangedAsync(Guid tenantId, Guid instanceId, CancellationToken ct)
        => Cache?.IncrementAsync(GenerationKey(tenantId, instanceId), ct) ?? Task.CompletedTask;

    private static string Fingerprint(string sql, QueryCompiler.Compiled compiled)
    {
        var text = new StringBuilder(sql);
        foreach (var (name, value, type) in compiled.Specs)
        {
            text.Append('\n').Append(name).Append(':').Append(type).Append('=').Append(JsonSerializer.Serialize(value));
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
