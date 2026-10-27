using System.Text.Json;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Caching;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

using Dcms.Shared.Data;
namespace Dcms.PluginSdk.Runtime.Contracts;

/// <summary>
/// A tenant's enabled plugin instances, held per process and keyed by a Redis generation that every
/// instance write bumps (<see cref="BumpAsync"/>, from the one place instances are changed).
///
/// <para>Every plugin request and every delivery request read this table, each with its own
/// connection open and RLS round trip, and parsed every instance's config again: 15 % of admin-api's
/// CPU on a Dynamic Apps read (2026-10-23). A request now costs one Redis GET. The 60 s expiry is the
/// floor if a bump is ever lost (a Redis restart resets the generation to one already seen).</para>
///
/// <para>The contexts are shared between requests, so nothing may dispose their <c>Config</c>. Without
/// Redis nothing is cached: a stale list would serve a disabled plugin.</para>
/// </summary>
public sealed class PluginInstanceCache(IMemoryCache memory, IServiceProvider services)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    public sealed record Enabled(IReadOnlyList<PluginInstance> Rows, IReadOnlyList<PluginInstanceContext> Contexts);

    /// <summary>Per environment (ADR 0028): <c>t:{environmentId}</c>, which for production is the tenant id.</summary>
    public static string GenerationKey(Guid environmentId) => $"t:{environmentId}:plugins:gen";

    /// <summary>After any write to <c>plugins.plugin_instances</c>, once it has committed: the environment in force for the tenant's.</summary>
    public static Task BumpAsync(ICacheService cache, Guid tenantId, CancellationToken ct)
        => cache.IncrementAsync(GenerationKey(RlsScope.EnvironmentFor(tenantId) ?? tenantId), ct);

    public async Task<Enabled> EnabledAsync(Guid tenantId, CmsDbContext db, CancellationToken ct)
    {
        if (services.GetService<ICacheService>() is not { } cache)
        {
            return await LoadAsync(tenantId, db, ct);
        }
        // The environment in force for the tenant: its instances are its own (ADR 0028).
        var environmentId = RlsScope.EnvironmentFor(tenantId) ?? tenantId;
        var generation = await cache.GetAsync<long?>(GenerationKey(environmentId), ct) ?? 0;
        return (await memory.GetOrCreateAsync(("plugins:enabled", environmentId, generation), entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Ttl;
            return LoadAsync(tenantId, db, ct);
        }))!;
    }

    private static async Task<Enabled> LoadAsync(Guid tenantId, CmsDbContext db, CancellationToken ct)
    {
        // Explicit tenant, not the ambient one: jobs and event handlers run with no request.
        using var rls = RlsScope.Tenant(tenantId);
        var rows = await db.PluginInstances.IgnoreTenantFilter().AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.Enabled)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(ct);
        return new Enabled(rows, rows.Select(ToContext).ToList());
    }

    internal static PluginInstanceContext ToContext(PluginInstance p) =>
        new(p.Id, p.TenantId, p.PluginId, p.Slug, p.Name, p.Description, ParseConfig(p.ConfigJson), p.AiToolsEnabled)
        {
            EnvironmentId = p.EnvironmentId,
        };

    private static JsonDocument ParseConfig(string json)
    {
        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}");
        }
    }
}
