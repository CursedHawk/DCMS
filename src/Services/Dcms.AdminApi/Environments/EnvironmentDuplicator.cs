using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Tenancy;
using Microsoft.EntityFrameworkCore;

using Dcms.Shared.Data;
namespace Dcms.AdminApi.Environments;

/// <summary>
/// The data half of duplicating an environment (ADR 0028): after the configuration is copied,
/// each scope the duplicator ticked is copied by its <see cref="IEnvironmentDataCopier"/>, in their
/// order, sharing one id remap.
/// </summary>
public sealed class EnvironmentDuplicator(IEnumerable<IEnvironmentDataCopier> copiers, CmsDbContext cms)
{
    private readonly IReadOnlyList<IEnvironmentDataCopier> _copiers = copiers.OrderBy(c => c.Order).ToList();

    /// <summary>Every scope that can be copied, in order.</summary>
    public IEnumerable<(string Scope, bool PersonalData)> Scopes => _copiers.Select(c => (c.Scope, c.PersonalData));

    /// <returns>Rows copied, by scope.</returns>
    public async Task<Dictionary<string, int>> CopyAsync(
        TenantEnvironment target, TenantEnvironment source, IReadOnlyCollection<string> scopes,
        Func<string, Task> onScope, CancellationToken ct)
    {
        var copied = new Dictionary<string, int>(StringComparer.Ordinal);
        var chosen = _copiers.Where(c => scopes.Contains(c.Scope)).ToList();
        if (chosen.Count == 0) return copied;

        var copy = new EnvironmentDataCopy
        {
            TenantId = target.TenantId,
            SourceEnvironmentId = source.Id,
            TargetEnvironmentId = target.Id,
            Instances = await InstancesAsync(target, source, ct),
            InSource = source.Scope,
            InTarget = target.Scope,
        };
        foreach (var copier in chosen)
        {
            await onScope(copier.Scope);
            copied[copier.Scope] = await copier.CopyAsync(copy, ct);
        }
        return copied;
    }

    /// <summary>The source's plugin instances to the target's, by slug: the configuration copy made them.</summary>
    private async Task<Dictionary<Guid, Guid>> InstancesAsync(TenantEnvironment target, TenantEnvironment source, CancellationToken ct)
    {
        async Task<Dictionary<string, Guid>> SlugsAsync(TenantEnvironment environment)
        {
            using var rls = environment.Scope();
            return await cms.PluginInstances.IgnoreTenantFilter().AsNoTracking()
                .Where(p => p.TenantId == environment.TenantId && p.EnvironmentId == environment.Id)
                .ToDictionaryAsync(p => p.Slug, p => p.Id, ct);
        }
        var there = await SlugsAsync(target);
        return (await SlugsAsync(source))
            .Where(s => there.ContainsKey(s.Key))
            .ToDictionary(s => s.Value, s => there[s.Key]);
    }
}
