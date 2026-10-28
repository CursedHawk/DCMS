using System.Text.Json;
using Dcms.Plugins.UserAuth.Api;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.UserAuth;
using Microsoft.EntityFrameworkCore;

using Dcms.Shared.Data;
namespace Dcms.Plugins.UserAuth;

/// <summary>
/// What User Authentication moves between environments (ADR 0026): its roles by key
/// (<c>role:{key}</c>) and each app's API access by the app's slug (<c>api:{slug}</c>). Grants and
/// group-gated site rules stay: they name the environment's own realm users and groups, which are
/// separate people in every environment.
/// </summary>
// rls: EnvironmentMigrator calls every kind inside RlsScope.Environment, the environment it works
// on; IgnoreTenantFilter drops only the tenant filter, so queries stay in that environment.
internal sealed class UserAuthConfigKind(UserAuthDbContext db, CmsDbContext cms) : IEnvironmentConfigKind
{
    public string Kind => "user-auth";

    public string? Permission => UserAuthPermissions.AccessManage;

    private sealed record RoleValue(string Name, string? Description, string[] Permissions, bool BypassRowAccess);

    private sealed record ApiValue(string Access);

    public async Task<IReadOnlyList<EnvironmentConfigItem>> ExportAsync(Guid tenantId, CancellationToken ct)
    {
        var roles = await db.Roles.IgnoreTenantFilter().AsNoTracking().Where(r => r.TenantId == tenantId).ToListAsync(ct);
        var rules = await db.ApiRules.IgnoreTenantFilter().AsNoTracking().Where(r => r.TenantId == tenantId).ToListAsync(ct);
        var slugs = await SlugsAsync(tenantId, ct);
        return roles
            .Select(r => new EnvironmentConfigItem($"role:{r.Key}", JsonSerializer.SerializeToElement(
                new RoleValue(r.Name, r.Description, r.Permissions.Order(StringComparer.Ordinal).ToArray(), r.BypassRowAccess))) { Label = r.Name })
            .Concat(rules
                .Where(r => slugs.ContainsKey(r.InstanceId))
                .Select(r => new EnvironmentConfigItem($"api:{slugs[r.InstanceId]}", JsonSerializer.SerializeToElement(
                    new ApiValue(r.Access.ToString()))) { Label = slugs[r.InstanceId] }))
            .ToList();
    }

    public async Task ApplyAsync(Guid tenantId, string key, JsonElement? value, EnvironmentApplyContext context, CancellationToken ct)
    {
        if (key.StartsWith("role:", StringComparison.Ordinal))
        {
            var roleKey = key["role:".Length..];
            var role = await db.Roles.IgnoreTenantFilter().FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Key == roleKey, ct);
            if (value is null)
            {
                if (role is null) return;
                db.Grants.RemoveRange(await db.Grants.IgnoreTenantFilter().Where(g => g.TenantId == tenantId && g.RoleId == role.Id).ToListAsync(ct));
                db.Roles.Remove(role);
            }
            else
            {
                var wanted = value.Value.Deserialize<RoleValue>()!;
                if (role is null)
                {
                    role = new UserRole { Id = Guid.NewGuid(), TenantId = tenantId, Key = roleKey };
                    db.Roles.Add(role);
                }
                (role.Name, role.Description, role.Permissions, role.BypassRowAccess, role.UpdatedAt) =
                    (wanted.Name, wanted.Description, wanted.Permissions.ToList(), wanted.BypassRowAccess, DateTimeOffset.UtcNow);
            }
        }
        else if (key.StartsWith("api:", StringComparison.Ordinal))
        {
            var slug = key["api:".Length..];
            var instanceId = (await SlugsAsync(tenantId, ct)).FirstOrDefault(s => s.Value == slug).Key;
            // Not silently: an API rule that cannot land leaves the app open in a way nobody reviewed.
            if (instanceId == Guid.Empty) throw new EnvironmentConfigException($"there is no app '{slug}' here for its API access rule");
            var rule = await db.ApiRules.IgnoreTenantFilter().FirstOrDefaultAsync(r => r.TenantId == tenantId && r.InstanceId == instanceId, ct);
            if (value is null)
            {
                if (rule is not null) db.ApiRules.Remove(rule);
            }
            else
            {
                var access = Enum.Parse<ApiAccess>(value.Value.Deserialize<ApiValue>()!.Access);
                if (rule is null)
                {
                    db.ApiRules.Add(new ApiRule { Id = Guid.NewGuid(), TenantId = tenantId, InstanceId = instanceId, Access = access });
                }
                else
                {
                    (rule.Access, rule.UpdatedAt) = (access, DateTimeOffset.UtcNow);
                }
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<Dictionary<Guid, string>> SlugsAsync(Guid tenantId, CancellationToken ct) =>
        await cms.PluginInstances.IgnoreTenantFilter().AsNoTracking()
            .Where(p => p.TenantId == tenantId)
            .ToDictionaryAsync(p => p.Id, p => p.Slug, ct);
}
