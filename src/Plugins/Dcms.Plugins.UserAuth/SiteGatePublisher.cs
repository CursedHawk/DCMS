using System.Text.Json;
using Dcms.Plugins.UserAuth.Api;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Edge;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Data.UserAuth;
using Dcms.Shared.Messaging;
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
/// </summary>
public sealed class SiteGatePublisher(
    UserAuthDbContext policy, TenancyDbContext tenancy, EdgeDbContext edge, CmsDbContext cms, IEventPublisher events)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Brings the tenant's rows in line with its rules; true when anything changed (and the edge was told).</summary>
    public async Task<bool> PublishAsync(Guid tenantId, CancellationToken ct, bool announce = true)
    {
        Dictionary<string, (string Slug, string Rules)> wanted;
        using (RlsScope.Tenant(tenantId))
        {
            wanted = await WantedAsync(tenantId, ct);
        }

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

    /// <summary>Tells every edge replica to re-read the table. After the save: the row is the truth, the event only says "now".</summary>
    public ValueTask AnnounceAsync(CancellationToken ct) =>
        events.PublishAsync(Subjects.SiteGatesChanged, new SiteGatesChanged(Guid.NewGuid(), DateTimeOffset.UtcNow), ct);

    private async Task<Dictionary<string, (string Slug, string Rules)>> WantedAsync(Guid tenantId, CancellationToken ct)
    {
        var wanted = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        // Explicit tenant predicates under the tenant RLS scope: no request context to filter by.
        if (!await cms.PluginInstances.IgnoreQueryFilters().AnyAsync(p => p.TenantId == tenantId && p.PluginId == UserAuthPermissions.PluginId && p.Enabled, ct))
        {
            return wanted;
        }
        var slug = await tenancy.Tenants.IgnoreQueryFilters().Where(t => t.Id == tenantId.ToString()).Select(t => t.Identifier).FirstOrDefaultAsync(ct);
        if (slug is null)
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
            using var scope = scopes.CreateScope();
            changed |= await scope.ServiceProvider.GetRequiredService<SiteGatePublisher>().PublishAsync(tenantId, ct, announce: false);
        }
        if (changed)
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<SiteGatePublisher>().AnnounceAsync(ct);
        }
    }
}
