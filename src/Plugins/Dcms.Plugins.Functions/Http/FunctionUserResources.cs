using Dcms.Plugins.Functions.Build;
using Dcms.Plugins.UserAuth.Api;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Data.Functions;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Plugins.Functions.Http;

/// <summary>
/// users.resources@1: the live functions whose access names <c>role</c>, as <c>fn:{name}</c>
/// with one action, <c>invoke</c>, so the roles screen can grant them to site users — as
/// Dynamic Apps offers its manual flows.
/// </summary>
internal sealed class FunctionUserResources(IPluginContext context, FunctionsDbContext db) : IUserResources
{
    public async Task<UserResourceCatalog> ListAsync(CancellationToken ct)
    {
        var instance = context.Instance ?? throw new InvalidOperationException("Functions resources are per instance.");
        using var rls = RlsScope.Environment(context.TenantId, context.EnvironmentId);
        var manifest = await db.Deployments.AsNoTracking()
            .Where(d => d.InstanceId == instance.InstanceId && d.Status == FunctionDeploymentStatus.Active)
            .Select(d => d.Manifest).FirstOrDefaultAsync(ct);
        var resources = (manifest is null ? [] : FunctionManifest.Deserialize(manifest))
            .Where(f => f.Http.Contains("role"))
            .Select(f => new UserResource($"fn:{f.Name.ToLowerInvariant()}", f.Description ?? f.Name, [new UserResourceAction("invoke", "Call")]))
            .ToList();
        return new UserResourceCatalog(FunctionsPlugin.PluginId, instance.Slug, instance.Name, resources);
    }
}
