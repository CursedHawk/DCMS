using System.Text.Json;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;

namespace Dcms.PluginSdk.Runtime.Contracts;

/// <summary>
/// <see cref="IPluginInstanceStore"/> over <c>plugins.plugin_instances</c>. Scoped by the tenant it
/// is given, in both the predicate and the RLS GUC, rather than by the ambient request tenant —
/// which a job or event handler does not have.
/// </summary>
public sealed class CmsPluginInstanceStore(CmsDbContext db) : IPluginInstanceStore
{
    public async Task<IReadOnlyList<PluginInstanceContext>> ListEnabledAsync(Guid tenantId, CancellationToken ct)
    {
        // Explicit tenant, not the ambient one: jobs and event handlers run with no request.
        using var rls = RlsScope.Tenant(tenantId);
        var rows = await db.PluginInstances.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.Enabled)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(ct);

        return rows.Select(p => new PluginInstanceContext(
                p.Id, p.TenantId, p.PluginId, p.Slug, p.Name, p.Description, ParseConfig(p.ConfigJson)))
            .ToList();
    }

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
