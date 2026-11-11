using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Data;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Functions;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Sites;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Tenancy;

/// <summary>
/// Plugin permissions are held per instance (<c>plugin:{pluginId}:{slug}:{action}</c>). What was
/// stored before in the plugin-wide shape (<c>plugin:{pluginId}:{action}</c>) outside roles
/// (<see cref="OwnerPermissionBackfill"/> converts those) takes the new one here: a repository's
/// permissions are its own instance's, and a Functions deployment's vouched keys are its own app's,
/// or every instance of another plugin in its environment, which is what its deployer held.
///
/// <para>Runs on every startup; once nothing is plugin-wide, it reads and writes nothing more.
/// Jobs keep their keys: a plugin-wide one is held on any instance (the jobs list).</para>
/// </summary>
public static class PluginPermissionScoping
{
    public static async Task ApplyAsync(CmsDbContext cms, SitesDbContext sites, FunctionsDbContext functions, ILogger logger, CancellationToken ct)
    {
        using var rls = RlsScope.Platform();
        var instances = await cms.PluginInstances.IgnoreTenantFilter().AsNoTracking()
            .Select(p => new { p.Id, p.TenantId, p.EnvironmentId, p.PluginId, p.Slug })
            .ToListAsync(ct);
        var slugOf = instances.ToDictionary(i => i.Id, i => i.Slug);

        var changed = 0;
        foreach (var binding in await sites.RepoBindings.IgnoreTenantFilter().ToListAsync(ct))
        {
            if (!slugOf.TryGetValue(binding.InstanceId, out var slug)) continue;
            var (read, edit, publish) = (PluginPermissions.ForInstance(binding.ReadPermission, slug),
                PluginPermissions.ForInstance(binding.EditPermission, slug), PluginPermissions.ForInstance(binding.PublishPermission, slug));
            if ((read, edit, publish) == (binding.ReadPermission, binding.EditPermission, binding.PublishPermission)) continue;
            (binding.ReadPermission, binding.EditPermission, binding.PublishPermission) = (read, edit, publish);
            changed++;
        }
        await sites.SaveChangesAsync(ct);

        // The freeze trigger forbids rewriting what a deployment past Ready was vouched for, and this
        // is exactly that: the same set in the new shape. It steps aside for this transaction only, as
        // it did for VouchEgressForLiveDeployments; ALTER TABLE is transactional, so a failure puts it back.
        await using var tx = await functions.Database.BeginTransactionAsync(ct);
        var deployments = await functions.Deployments.IgnoreTenantFilter()
            .Where(d => d.VouchedPermissions.Any(p => p.StartsWith("plugin:") && !EF.Functions.Like(p, "plugin:%:%:%")))
            .ToListAsync(ct);
        foreach (var deployment in deployments)
        {
            if (!slugOf.TryGetValue(deployment.InstanceId, out var own)) continue;
            var here = instances.Where(i => i.TenantId == deployment.TenantId && i.EnvironmentId == deployment.EnvironmentId).ToList();
            deployment.VouchedPermissions = deployment.VouchedPermissions
                .SelectMany(key => PluginPermissions.TryParse(key, out var pluginId, out var slug, out _) && slug is null
                    ? pluginId == "functions"
                        ? [PluginPermissions.ForInstance(key, own)]
                        : here.Where(i => i.PluginId == pluginId).Select(i => PluginPermissions.ForInstance(key, i.Slug))
                    : [key])
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            changed++;
        }
        if (deployments.Count > 0)
        {
            await functions.Database.ExecuteSqlRawAsync("ALTER TABLE functions.deployments DISABLE TRIGGER deployments_freeze", ct);
            await functions.SaveChangesAsync(ct);
            await functions.Database.ExecuteSqlRawAsync("ALTER TABLE functions.deployments ENABLE TRIGGER deployments_freeze", ct);
        }
        await tx.CommitAsync(ct);

        if (changed > 0)
        {
            logger.LogInformation("Moved {Count} repository binding(s) and Functions deployment(s) to per-instance plugin permissions.", changed);
        }
    }
}
