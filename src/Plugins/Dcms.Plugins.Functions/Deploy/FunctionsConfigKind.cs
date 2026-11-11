using System.Text.Json;
using Dcms.Plugins.Functions.Api;
using Dcms.Plugins.Functions.Build;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.PluginSdk.Runtime;
using Dcms.PluginSdk.Runtime.Contracts;
using Dcms.Shared.Data;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Functions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.Functions.Deploy;

/// <summary>
/// What each Functions app has live, by its instance's slug (ADR 0026/0029): the commit and the
/// permissions it was vouched for. Applying puts that commit's files on the receiving
/// environment's <c>release</c> (fetched from whichever environment has it, as sites' code moves),
/// builds them against this environment's SDK, and makes the build live, vouched for by the
/// deployer, who must hold everything it uses. Secrets, API keys and run history are each
/// environment's own; code merged but not deployed does not move.
/// </summary>
// rls: EnvironmentMigrator calls every kind inside RlsScope.Environment, the environment it works
// on; IgnoreTenantFilter drops only the tenant filter, so queries stay in that environment.
internal sealed class FunctionsConfigKind(
    FunctionsDbContext db, CmsDbContext cms, PluginContextFactory contexts, PluginRegistry registry, IServiceProvider services)
    : IEnvironmentConfigKind
{
    public string Kind => FunctionsPlugin.PluginId;

    public string? Permission => FunctionsPermissions.Deploy;

    /// <summary>An item is an app, by its slug: the permission is held on it.</summary>
    public string? InstanceOf(string key) => key;

    /// <summary>After Dynamic Apps (100): a build type-checks against the tables they bring.</summary>
    public int Order => 110;

    /// <summary>Each environment's commit is its own (the same files, a new commit), as a site's release is.</summary>
    public IReadOnlyCollection<string> SelfMergedProperties => ["Live"];

    private sealed record Value(string Live, IReadOnlyList<string> Permissions);

    public async Task<IReadOnlyList<EnvironmentConfigItem>> ExportAsync(Guid tenantId, CancellationToken ct)
    {
        var instances = await cms.PluginInstances.IgnoreTenantFilter().AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.PluginId == FunctionsPlugin.PluginId)
            .ToDictionaryAsync(p => p.Id, ct);
        var live = await db.Deployments.IgnoreTenantFilter().AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.Status == FunctionDeploymentStatus.Active)
            .Select(d => new { d.InstanceId, d.CommitSha, d.VouchedPermissions })
            .ToListAsync(ct);
        return live.Where(d => instances.ContainsKey(d.InstanceId))
            .Select(d => new EnvironmentConfigItem(instances[d.InstanceId].Slug, JsonSerializer.SerializeToElement(
                new Value(d.CommitSha, d.VouchedPermissions.Order(StringComparer.Ordinal).ToList())))
            {
                Label = instances[d.InstanceId].Name,
            })
            .ToList();
    }

    /// <summary>What it was vouched for where it comes from, on the instances this environment has too.</summary>
    public async Task<IReadOnlyCollection<string>> PermissionsForAsync(Guid tenantId, string key, JsonElement value, CancellationToken ct)
    {
        var slugs = (await cms.PluginInstances.IgnoreTenantFilter().AsNoTracking()
            .Where(p => p.TenantId == tenantId).Select(p => p.Slug).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        return (value.Deserialize<Value>()?.Permissions ?? [])
            .Where(p => !PluginPermissions.TryParse(p, out _, out var slug, out _) || slug is null || slug == key || slugs.Contains(slug))
            .ToList();
    }

    public async Task ApplyAsync(Guid tenantId, string key, JsonElement? value, EnvironmentApplyContext context, CancellationToken ct)
    {
        // Nothing is undeployed by a migration: an app that goes, goes with its instance (the plugins kind).
        if (value?.Deserialize<Value>() is not { } wanted) return;

        var instance = await contexts.FindAnyInstanceAsync(tenantId, key, ct);
        if (instance is null || instance.PluginId != FunctionsPlugin.PluginId)
        {
            throw new EnvironmentConfigException($"there is no Functions app '{key}' here");
        }
        using var scope = services.CreateScope();
        var plugin = await contexts.CreateAsync(tenantId, FunctionsPlugin.PluginId, instance, PluginActor.System, ct);
        scope.ServiceProvider.GetRequiredService<PluginContextAccessor>().Current = plugin;
        var repos = plugin.Contracts.Get<IPluginRepos>();

        var created = await repos.GetAsync(new RepoQuery(), ct) is null;
        var repo = await repos.EnsureAsync(new RepoSetup("read", "edit", "deploy"), ct);
        var head = (await repos.AdoptAsync(new RepoAdopt(wanted.Live, "release", $"Deploy {Short(wanted.Live)}"), ct)).Sha
                   ?? throw new EnvironmentConfigException($"no environment of this workspace has commit {Short(wanted.Live)} of app '{key}' any more");
        if (created)
        {
            // An app that arrived here gets its code on the branch its editor opens as well.
            await repos.AdoptAsync(new RepoAdopt(wanted.Live, repo.DefaultBranch, $"Deploy {Short(wanted.Live)}"), ct);
        }

        var live = await db.Deployments.IgnoreTenantFilter()
            .FirstOrDefaultAsync(d => d.InstanceId == instance.InstanceId && d.Status == FunctionDeploymentStatus.Active, ct);
        if (live?.CommitSha == head) return;

        var by = context.UserId?.ToString() ?? "deployment";
        FunctionDeployment deployment;
        try
        {
            deployment = await Deployer.BuiltAsync(db, plugin, scope.ServiceProvider.GetRequiredService<Builder>(), head, by, ct);
        }
        catch (BuildFailedException e)
        {
            throw new EnvironmentConfigException($"app '{key}' does not build here: {e.Message}");
        }

        // Definer's rights, as a deploy by hand: what it uses here, all of which the deployer holds.
        // Refused, the build stays Ready for someone who does to deploy.
        var required = DeploymentService.Required(registry, FunctionManifest.Deserialize(deployment.Manifest), key);
        var instances = (await contexts.EnabledInstancesAsync(tenantId, ct)).Select(i => (i.PluginId, i.Slug));
        var (vouched, missing) = await FunctionManifest.Vouch(required, instances, p => Task.FromResult(context.Held?.Contains(p) ?? true));
        if (missing.Count > 0)
        {
            throw new EnvironmentConfigException($"app '{key}' uses what its deployer does not hold here: {string.Join(", ", missing)}");
        }
        await Deployer.ActivateAsync(db, deployment, vouched, by, ct);
    }

    private static string Short(string sha) => sha[..Math.Min(sha.Length, 12)];
}
