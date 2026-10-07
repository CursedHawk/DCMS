using System.Text.Json;
using Dcms.Plugins.UserAuth.Api;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Contracts.Realms;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Edge;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Data.UserAuth;
using Dcms.Shared.Messaging;
using Dcms.Shared.Security.Realms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dcms.Plugins.UserAuth;

/// <summary>
/// Publishes a tenant's site access rules to the edge (ADR 0022): one <c>edge.site_gates</c> row
/// per verified hostname of a site, holding that site's rules in order and the tenant's realm.
/// Every hostname of a tenant with the plugin enabled gets a row, rules or not, so its pages can
/// offer "Sign in"; a tenant without it has none, and the edge leaves its hosts alone.
///
/// <para>The same hostnames are the realm's in identity — its OIDC client's redirect URIs — so
/// the realm is brought in line first: a host the edge would gate is never one identity would
/// refuse to send a user back to. The realm is created here when the plugin is enabled, and
/// keeps its users when it is disabled (its hosts are emptied: nobody can sign in).</para>
/// </summary>
public sealed class SiteGatePublisher(
    UserAuthDbContext policy, TenancyDbContext tenancy, EdgeDbContext edge, CmsDbContext cms, IEventPublisher events,
    RealmAdminClient realms, ILogger<SiteGatePublisher> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Brings the tenant's rows in line with its rules; true when anything changed (and the edge was told).</summary>
    public async Task<bool> PublishAsync(Guid tenantId, CancellationToken ct, bool announce = true)
    {
        Dictionary<string, (string Slug, string Rules)> wanted;
        Wanted target;
        using (RlsScope.Tenant(tenantId))
        {
            target = await TargetAsync(tenantId, ct);
            wanted = await WantedAsync(tenantId, target, ct);
        }
        // Identity first: if it cannot be told, the edge is not changed either, and the next
        // pass tries both again.
        await SyncRealmAsync(tenantId, target, wanted.Keys, null, ct);

        // The edge's table is host-keyed and outside tenant RLS: this tenant's rows, and any row
        // for a hostname that has since moved to it from another tenant.
        var hosts = wanted.Keys.ToList();
        var existing = await edge.SiteGates.Where(r => r.TenantId == tenantId || hosts.Contains(r.Hostname)).ToListAsync(ct);
        var changed = false;
        foreach (var row in existing.Where(r => r.TenantId == tenantId && !wanted.ContainsKey(r.Hostname)))
        {
            edge.SiteGates.Remove(row);
            changed = true;
        }
        foreach (var (host, (slug, rules)) in wanted)
        {
            var row = existing.FirstOrDefault(r => r.Hostname == host);
            if (row is null)
            {
                edge.SiteGates.Add(new EdgeSiteGate { Hostname = host, TenantId = tenantId, RealmSlug = slug, RulesJson = rules });
                changed = true;
            }
            else if (row.TenantId != tenantId || row.RealmSlug != slug || !SameJson(row.RulesJson, rules))
            {
                (row.TenantId, row.RealmSlug, row.RulesJson, row.UpdatedAt) = (tenantId, slug, rules, DateTimeOffset.UtcNow);
                changed = true;
            }
        }
        if (!changed)
        {
            return false;
        }
        await edge.SaveChangesAsync(ct);
        if (announce)
        {
            await AnnounceAsync(ct);
        }
        return true;
    }

    /// <summary>
    /// Tells every edge replica to re-read the table. After the save: the row is the truth, the
    /// event only says "now" — so a broker that cannot take it costs the edge's next sweep
    /// (two minutes), not the change.
    /// </summary>
    public async Task AnnounceAsync(CancellationToken ct)
    {
        try
        {
            await events.PublishAsync(Subjects.SiteGatesChanged, new SiteGatesChanged(Guid.NewGuid(), DateTimeOffset.UtcNow), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not announce a site rule change; the edge picks it up on its next sweep.");
        }
    }

    /// <summary>
    /// The tenant's realm as it should be, after any change to the realm's own settings, and
    /// returned as identity now has it — creating it if the plugin is enabled and it does not exist.
    /// </summary>
    public async Task<RealmInfo?> SyncRealmAsync(Guid tenantId, bool? passwordEnabled, CancellationToken ct)
    {
        Wanted target;
        Dictionary<string, (string Slug, string Rules)> wanted;
        using (RlsScope.Tenant(tenantId))
        {
            target = await TargetAsync(tenantId, ct);
            wanted = await WantedAsync(tenantId, target, ct);
        }
        return await SyncRealmAsync(tenantId, target, wanted.Keys, passwordEnabled, ct);
    }

    private async Task<RealmInfo?> SyncRealmAsync(Guid tenantId, Wanted target, IEnumerable<string> hosts, bool? passwordEnabled, CancellationToken ct)
    {
        var current = await realms.GetRealmAsync(tenantId, ct);
        if (target.Slug is null || (current is null && !target.Enabled))
        {
            // No tenant, or a tenant that never had the plugin: nothing to create.
            return current;
        }
        // ponytail: identity takes 50 redirect hosts per realm; a tenant with more sites than that
        // gets sign-in on the first 50. Raise identity's MaxHosts (or page the client) if one does.
        var sorted = hosts.Order(StringComparer.Ordinal).Take(50).ToList();
        if (current is not null && current.Slug == target.Slug && current.Name == target.Name
            && current.Hosts.Order(StringComparer.Ordinal).SequenceEqual(sorted)
            && (passwordEnabled is null || passwordEnabled == current.PasswordEnabled))
        {
            return current;
        }
        return await realms.UpsertRealmAsync(tenantId, new RealmUpsert(target.Slug, target.Name, sorted, passwordEnabled), ct);
    }

    /// <param name="Name">Shown on the realm's sign-in pages and in its emails.</param>
    private sealed record Wanted(bool Enabled, string? Slug, string Name);

    private async Task<Wanted> TargetAsync(Guid tenantId, CancellationToken ct)
    {
        // Explicit tenant predicates under the tenant RLS scope: no request context to filter by.
        var enabled = await cms.PluginInstances.IgnoreQueryFilters()
            .AnyAsync(p => p.TenantId == tenantId && p.PluginId == UserAuthPermissions.PluginId && p.Enabled, ct);
        var tenant = await tenancy.Tenants.IgnoreQueryFilters().Where(t => t.Id == tenantId.ToString())
            .Select(t => new { t.Identifier, t.Name }).FirstOrDefaultAsync(ct);
        // Identity refuses control characters (the name goes into email subjects) and more than 200 of them.
        var name = new string((string.IsNullOrWhiteSpace(tenant?.Name) ? tenant?.Identifier ?? "" : tenant.Name).Where(c => !char.IsControl(c)).ToArray()).Trim();
        return new Wanted(enabled, tenant?.Identifier, name.Length > 200 ? name[..200].Trim() : name);
    }

    private async Task<Dictionary<string, (string Slug, string Rules)>> WantedAsync(Guid tenantId, Wanted target, CancellationToken ct)
    {
        var wanted = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        if (!target.Enabled || target.Slug is not { } slug)
        {
            return wanted;
        }
        var domains = await tenancy.Domains.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.VerifiedAt != null && d.SiteId != null)
            .Select(d => new { d.Hostname, d.SiteId }).ToListAsync(ct);
        var gates = await policy.Gates.IgnoreQueryFilters().AsNoTracking().Where(g => g.TenantId == tenantId).OrderBy(g => g.Position).ToListAsync(ct);
        foreach (var domain in domains)
        {
            var rules = gates.Where(g => g.SiteId == domain.SiteId).Select(g => new
            {
                prefix = g.PathPrefix,
                access = JsonNamingPolicy.CamelCase.ConvertName(g.Access.ToString()),
                groups = g.Access == GateAccess.Groups ? g.Groups : null,
            });
            wanted[domain.Hostname.ToLowerInvariant()] = (slug, JsonSerializer.Serialize(rules, Json));
        }
        return wanted;
    }

    private static bool SameJson(string a, string b) =>
        JsonElement.DeepEquals(JsonDocument.Parse(a).RootElement, JsonDocument.Parse(b).RootElement);
}

/// <summary>
/// Republishes every tenant's site rules every few minutes, in admin-api. Gate edits publish at
/// once; this catches what changes elsewhere — a domain verified or removed, the plugin switched
/// on or off, a tenant deleted — and anything a failed publish missed.
/// </summary>
public sealed class SiteGateReconciler(IServiceScopeFactory scopes, ILogger<SiteGateReconciler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Site gate reconciliation failed; retrying next pass.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task ReconcileAsync(CancellationToken ct)
    {
        List<Guid> tenants;
        using (var scope = scopes.CreateScope())
        using (RlsScope.Platform())
        {
            var cms = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
            var edge = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();
            // Tenants with the plugin, and tenants the edge still has rows for (which may have lost it).
            tenants = (await cms.PluginInstances.IgnoreQueryFilters().Where(p => p.PluginId == UserAuthPermissions.PluginId)
                    .Select(p => p.TenantId).Distinct().ToListAsync(ct))
                .Union(await edge.SiteGates.Select(r => r.TenantId).Distinct().ToListAsync(ct))
                .ToList();
        }
        var changed = false;
        foreach (var tenantId in tenants)
        {
            // One tenant identity refuses (or a blip) must not hold up every other tenant's rules.
            try
            {
                using var scope = scopes.CreateScope();
                changed |= await scope.ServiceProvider.GetRequiredService<SiteGatePublisher>().PublishAsync(tenantId, ct, announce: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not publish the site rules of tenant {TenantId}; retrying next pass.", tenantId);
            }
        }
        if (changed)
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<SiteGatePublisher>().AnnounceAsync(ct);
        }
    }
}
