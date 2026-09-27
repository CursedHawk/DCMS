using System.Text.Json;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Data.Cms;
using Microsoft.EntityFrameworkCore;

namespace Dcms.PluginSdk.Runtime.Contracts;

/// <summary>
/// <see cref="IPluginInstanceStore"/> over <c>plugins.plugin_instances</c>. The explicit tenant
/// predicate sits on top of the context's query filter and RLS, so a caller that passes the
/// wrong tenant gets nothing rather than someone else's instances.
/// </summary>
public sealed class CmsPluginInstanceStore(CmsDbContext db) : IPluginInstanceStore
{
    public async Task<IReadOnlyList<PluginInstanceContext>> ListEnabledAsync(Guid tenantId, CancellationToken ct)
    {
        var rows = await db.PluginInstances.AsNoTracking()
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
