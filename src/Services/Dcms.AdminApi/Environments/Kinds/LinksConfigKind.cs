using System.Text.Json;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Caching;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;

using Dcms.Shared.Data;
namespace Dcms.AdminApi.Environments.Kinds;

/// <summary>
/// Environment links as configuration (ADR 0028): what development uses of a service environment
/// is set up there, and deploys with the rest. Keyed <c>{kind}/{target environment name}/{target key}</c>:
/// the target is named, not identified, so the same link means the same thing in every environment.
/// A link that would point an environment at itself is no link there, and is left out.
/// </summary>
// rls: EnvironmentMigrator calls every kind inside RlsScope.Environment, the environment it works
// on; IgnoreTenantFilter drops only the tenant filter, so queries stay in that environment.
public sealed class LinksConfigKind(TenancyDbContext db, EnvironmentStore environments, ICacheService cache) : IEnvironmentConfigKind
{
    public string Kind => "links";

    public string? Permission => PlatformPermissions.EnvironmentsManage;

    // After everything a link could name in this environment.
    public int Order => 90;

    private sealed record Value(string Access, List<string> Grants);

    public async Task<IReadOnlyList<EnvironmentConfigItem>> ExportAsync(Guid tenantId, CancellationToken ct)
    {
        var here = RlsScope.EnvironmentFor(tenantId) ?? tenantId;
        var names = (await environments.ListAsync(tenantId, ct)).ToDictionary(e => e.Id, e => e.Name);
        var links = await db.EnvironmentLinks.IgnoreTenantFilter().AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.EnvironmentId == here).ToListAsync(ct);
        return links.Where(l => names.ContainsKey(l.TargetEnvironmentId))
            .Select(l => new EnvironmentConfigItem(
                $"{l.Kind}/{names[l.TargetEnvironmentId]}/{l.TargetKey}",
                JsonSerializer.SerializeToElement(new Value(l.Access.ToString(), l.Grants.Order(StringComparer.Ordinal).ToList())))
            {
                Label = $"{l.Kind}: {names[l.TargetEnvironmentId]}{(l.TargetKey is null ? "" : $" / {l.TargetKey}")}",
            })
            .ToList();
    }

    public async Task ApplyAsync(Guid tenantId, string key, JsonElement? value, EnvironmentApplyContext context, CancellationToken ct)
    {
        var here = RlsScope.EnvironmentFor(tenantId) ?? tenantId;
        var parts = key.Split('/', 3);
        if (parts.Length != 3) throw new EnvironmentConfigException($"'{key}' is not a link");
        var (kind, environment, targetKey) = (parts[0], parts[1], parts[2].Length == 0 ? null : parts[2]);
        var target = await environments.FindAsync(tenantId, environment, ct)
                     ?? throw new EnvironmentConfigException($"there is no environment '{environment}' here to link to");
        if (target.Id == here) return;

        var link = await db.EnvironmentLinks.IgnoreTenantFilter()
            .FirstOrDefaultAsync(l => l.TenantId == tenantId && l.EnvironmentId == here && l.Kind == kind
                                      && l.TargetEnvironmentId == target.Id && l.TargetKey == targetKey, ct);
        if (value is null)
        {
            if (link is not null) db.EnvironmentLinks.Remove(link);
        }
        else
        {
            var wanted = value.Value.Deserialize<Value>()!;
            if (!Enum.TryParse<EnvironmentLinkAccess>(wanted.Access, out var access)
                || wanted.Grants.Any(g => !Tenancy.EnvironmentEndpoints.LinkGrant(kind, g)))
            {
                throw new EnvironmentConfigException($"link '{key}' is not a valid link");
            }
            if (link is null)
            {
                link = new EnvironmentLink
                {
                    Id = Guid.NewGuid(), TenantId = tenantId, EnvironmentId = here, Kind = kind, TargetEnvironmentId = target.Id, TargetKey = targetKey,
                };
                db.EnvironmentLinks.Add(link);
            }
            (link.Access, link.Grants) = (access, wanted.Grants);
        }
        await db.SaveChangesAsync(ct);
        await cache.RemoveAsync(EnvironmentLinkKinds.CacheKey(here), ct);
    }
}
