using Dcms.Plugins.UserAuth.Api;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Data.DynamicApps;
using Microsoft.Extensions.Caching.Memory;

namespace Dcms.Plugins.DynamicApps.Data;

/// <summary>
/// What a tenant can give its site users on this app (users.resources@1, ADR 0022): each
/// published table's read, create, update and delete — on every record, not only their own — and
/// each manual flow's run. The keys are <c>dynamic-apps:{slug}:table:{apiName}:{action}</c> and
/// <c>…:flow:{apiName}:run</c>; renaming a table or flow is renaming the permission.
/// </summary>
/// <remarks>
/// Reads the model by this provider's own instance, not through <see cref="RuntimeModelProvider"/>:
/// called by another plugin, the ambient context in the container is the caller's.
/// </remarks>
public sealed class AppUserResources(IPluginContext context, AppsDbContext db, IMemoryCache cache) : IUserResources
{
    public async Task<UserResourceCatalog> ListAsync(CancellationToken ct)
    {
        var instance = context.Instance ?? throw new InvalidOperationException("Dynamic Apps resources are per instance.");
        var config = (await PublishedModels.LoadAsync(db, cache, context.TenantId, instance.InstanceId, ct))?.Config;
        var resources = new List<UserResource>();
        foreach (var table in config?.Tables.Where(t => t.Enabled) ?? [])
        {
            resources.Add(new UserResource($"table:{table.ApiName}", table.PluralName ?? table.DisplayName,
            [
                new UserResourceAction("read", "Read"),
                new UserResourceAction("create", "Create"),
                new UserResourceAction("update", "Update"),
                new UserResourceAction("delete", "Delete"),
            ]));
        }
        foreach (var flow in config?.Flows.Where(f => f.Enabled && f.Trigger.Event == "manual") ?? [])
        {
            resources.Add(new UserResource($"flow:{flow.ApiName}", flow.DisplayName, [new UserResourceAction("run", "Run")]));
        }
        return new UserResourceCatalog(DynamicAppsPlugin.PluginId, instance.Slug, instance.Name, resources);
    }
}
