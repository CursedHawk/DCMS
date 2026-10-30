using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.Shared.Data;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Sites.Git;

/// <summary>
/// <c>dcms.repos@1</c> (ADR 0029): one repository per plugin instance and environment, in the
/// tenant's Forgejo org beside its sites', bound by a <see cref="RepoBinding"/> the host's IDE
/// routes and git webhook read. A plugin reaches only its own instances' repositories, and binds
/// them only to its own permissions.
/// </summary>
public sealed class AdminPluginRepos(
    IPluginContext caller,
    SitesDbContext sites,
    CmsDbContext cms,
    TenantStore tenants,
    SiteGitService git) : IPluginRepos
{
    public async Task<RepoInfo> EnsureAsync(RepoSetup input, CancellationToken ct)
    {
        if (!git.Enabled)
        {
            throw new InvalidOperationException("The git backend is not configured.");
        }
        var (instanceId, environmentId) = await InstanceAsync(input.InstanceId, ct);
        var (read, edit, publish) = (Own(input.ReadPermission), Own(input.EditPermission), Own(input.PublishPermission));

        using var rls = RlsScope.Environment(caller.TenantId, environmentId);
        var binding = await Mine(instanceId).FirstOrDefaultAsync(ct);
        if (binding is null)
        {
            var tenant = await tenants.GetAsync(caller.TenantId.ToString())
                ?? throw new InvalidOperationException("Unknown tenant.");
            var repo = await git.EnsureRepoAsync(tenant.Identifier!, $"{caller.PluginId}-{instanceId}",
                input.Seed ?? new Dictionary<string, string>(), authorName: null, authorEmail: null, ct);
            binding = new RepoBinding
            {
                Id = Guid.NewGuid(),
                TenantId = caller.TenantId,
                EnvironmentId = environmentId,
                PluginId = caller.PluginId,
                InstanceId = instanceId,
                RepoFullName = repo.RepoFullName,
                DefaultBranch = repo.DefaultBranch,
            };
            sites.RepoBindings.Add(binding);
        }
        // The plugin owns what its permissions mean, and may change its mind across versions.
        (binding.ReadPermission, binding.EditPermission, binding.PublishPermission) = (read, edit, publish);
        await sites.SaveChangesAsync(ct);
        return Info(binding);
    }

    public async Task<RepoInfo?> GetAsync(RepoQuery input, CancellationToken ct)
    {
        var (instanceId, environmentId) = await InstanceAsync(input.InstanceId, ct);
        using var rls = RlsScope.Environment(caller.TenantId, environmentId);
        return await Mine(instanceId).AsNoTracking().FirstOrDefaultAsync(ct) is { } binding ? Info(binding) : null;
    }

    public async Task<RepoFiles> ReadFilesAsync(RepoFilesQuery input, CancellationToken ct)
    {
        var binding = await BindingAsync(input.InstanceId, ct);
        if (string.IsNullOrWhiteSpace(input.Reference))
        {
            throw new ContractValidationException("Name a branch or commit to read.");
        }
        return new RepoFiles(await git.ReadFilesAsync(binding.RepoFullName, input.Reference.Trim(), ct));
    }

    public async Task<RepoHead> HeadAsync(RepoHeadQuery input, CancellationToken ct)
    {
        var binding = await BindingAsync(input.InstanceId, ct);
        return new RepoHead(await git.HeadShaAsync(binding.RepoFullName, input.Branch, ct));
    }

    private async Task<RepoBinding> BindingAsync(Guid? requested, CancellationToken ct)
    {
        var (instanceId, environmentId) = await InstanceAsync(requested, ct);
        using var rls = RlsScope.Environment(caller.TenantId, environmentId);
        return await Mine(instanceId).AsNoTracking().FirstOrDefaultAsync(ct)
            ?? throw new ContractValidationException("This instance has no repository yet.");
    }

    private IQueryable<RepoBinding> Mine(Guid instanceId) =>
        sites.RepoBindings.IgnoreTenantFilter().Where(r =>
            r.TenantId == caller.TenantId && r.PluginId == caller.PluginId && r.InstanceId == instanceId);

    /// <summary>
    /// The instance being served, or — outside one — an explicitly named instance, which must
    /// belong to the calling plugin in the calling tenant. Its environment is where its
    /// repository lives.
    /// </summary>
    private async Task<(Guid Instance, Guid Environment)> InstanceAsync(Guid? requested, CancellationToken ct)
    {
        if (caller.Instance is { } own)
        {
            if (requested is { } other && other != own.InstanceId)
            {
                throw new ContractValidationException("A repository can only be addressed on the instance being served.");
            }
            return (own.InstanceId, own.EnvironmentId);
        }
        if (requested is not { } id)
        {
            throw new ContractValidationException("No instance in this context; pass InstanceId.");
        }
        // Any of the tenant's environments: which one is what this finds out. The tenant and plugin are named.
        using var rls = RlsScope.Platform();
        var owned = await cms.PluginInstances.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.Id == id && p.TenantId == caller.TenantId && p.PluginId == caller.PluginId)
            .Select(p => (Guid?)p.EnvironmentId).FirstOrDefaultAsync(ct);
        return owned is { } environment ? (id, environment) : throw new ContractValidationException("Unknown instance for this plugin.");
    }

    /// <summary>A permission the caller may bind its repository to: one of its own, never another plugin's or the platform's.</summary>
    private string Own(string permission)
    {
        var key = PluginPermissions.Resolve(caller.PluginId, permission ?? "");
        return key.StartsWith($"plugin:{caller.PluginId}:", StringComparison.Ordinal) && key.Length > $"plugin:{caller.PluginId}:".Length
            ? key
            : throw new ContractValidationException($"'{permission}' is not one of plugin '{caller.PluginId}''s permissions.");
    }

    private RepoInfo Info(RepoBinding binding)
    {
        var (http, ssh) = git.CloneUrls(binding.RepoFullName);
        return new RepoInfo(binding.Id, binding.RepoFullName, binding.DefaultBranch, http, ssh);
    }
}
