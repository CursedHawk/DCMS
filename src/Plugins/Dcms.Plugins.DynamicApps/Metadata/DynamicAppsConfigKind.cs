using System.Text.Json;
using Dcms.Plugins.DynamicApps.Api;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Runtime.Contracts;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.DynamicApps;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.DynamicApps.Metadata;

/// <summary>
/// Each application's published configuration — tables, views, flows, security — by its
/// instance's slug (ADR 0026). Records are data and never move. Applied as a new published
/// revision through <see cref="ConfigurationService.ImportAsync"/>, held to every check a person's
/// publish is, its flows' permissions vouched for by whoever deploys it.
/// </summary>
// rls: EnvironmentMigrator calls every kind inside RlsScope.Tenant(tenantId), the tenant each
// query names; IgnoreQueryFilters only drops the request's own, which is another environment.
internal sealed class DynamicAppsConfigKind(
    AppsDbContext apps, CmsDbContext cms, PluginContextFactory contexts, IServiceProvider services) : IEnvironmentConfigKind
{
    public string Kind => "dynamic-apps";

    public string? Permission => DynamicAppsPermissions.Publish;

    public async Task<IReadOnlyList<EnvironmentConfigItem>> ExportAsync(Guid tenantId, CancellationToken ct)
    {
        var instances = await cms.PluginInstances.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.PluginId == DynamicAppsPlugin.PluginId)
            .ToDictionaryAsync(p => p.Id, p => p, ct);
        var published = await (
                from a in apps.Apps.IgnoreQueryFilters().AsNoTracking()
                join r in apps.Revisions.IgnoreQueryFilters().AsNoTracking() on a.PublishedRevisionId equals r.Id
                where a.TenantId == tenantId && r.TenantId == tenantId
                select new { a.InstanceId, r.Snapshot })
            .ToListAsync(ct);
        return published
            .Where(p => instances.ContainsKey(p.InstanceId))
            .Select(p => new EnvironmentConfigItem(instances[p.InstanceId].Slug, JsonDocument.Parse(p.Snapshot).RootElement.Clone())
            {
                Label = instances[p.InstanceId].Name,
            })
            .ToList();
    }

    public async Task<IReadOnlyCollection<string>> PermissionsForAsync(Guid tenantId, string key, JsonElement value, CancellationToken ct)
    {
        using var scope = await ScopeAsync(tenantId, key, ct);
        return await scope.ServiceProvider.GetRequiredService<ConfigurationService>().NeededPermissionsAsync(value.GetRawText(), ct);
    }

    public async Task ApplyAsync(Guid tenantId, string key, JsonElement? value, CancellationToken ct)
    {
        // An application is never unpublished; its instance is switched off instead.
        if (value is null) throw new EnvironmentConfigException($"application '{key}' cannot be unpublished");

        using var scope = await ScopeAsync(tenantId, key, ct);
        var config = scope.ServiceProvider.GetRequiredService<ConfigurationService>();
        var snapshot = value.Value.GetRawText();
        // The engine checked the deployer holds every one of these before calling here.
        var vouched = (await config.NeededPermissionsAsync(snapshot, ct)).ToHashSet(StringComparer.Ordinal);
        PublishResult result;
        try
        {
            result = await config.ImportAsync(snapshot, "Deployed from another environment", vouched, ct);
        }
        catch (ContractConflictException ex)
        {
            throw new EnvironmentConfigException(ex.Message);
        }
        if (!result.Published)
        {
            var first = result.Issues.FirstOrDefault(i => i.Severity == IssueSeverity.Error);
            throw new EnvironmentConfigException(first is null ? "it did not pass validation" : $"{first.Path}: {first.Message}");
        }
    }

    /// <summary>A scope serving <paramref name="slug"/>'s instance, as a plugin job's would.</summary>
    private async Task<IServiceScope> ScopeAsync(Guid tenantId, string slug, CancellationToken ct)
    {
        var instance = await contexts.FindAnyInstanceAsync(tenantId, slug, ct);
        if (instance is null || instance.PluginId != DynamicAppsPlugin.PluginId)
        {
            throw new EnvironmentConfigException($"there is no Dynamic Apps app '{slug}' here");
        }
        var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<PluginContextAccessor>().Current =
            await contexts.CreateAsync(tenantId, DynamicAppsPlugin.PluginId, instance, PluginActor.System, ct);
        return scope;
    }
}
