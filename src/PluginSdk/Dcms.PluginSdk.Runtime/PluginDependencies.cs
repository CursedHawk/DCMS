namespace Dcms.PluginSdk.Runtime;

/// <summary>A required contract none of whose provider plugins has an enabled instance.</summary>
public sealed record MissingProvider(string ContractId, IReadOnlyList<string> ProviderPluginIds);

/// <summary>An enabled instance that requires a contract the change would leave unprovided.</summary>
public sealed record DependentInstance(string PluginId, string Slug, string ContractId);

/// <summary>
/// What enabling or disabling an instance would break, decided from the manifests alone.
/// Only <i>required</i>, plugin-provided contracts count: optional consumers degrade by design,
/// and platform (<c>dcms.*</c>) contracts always exist.
/// </summary>
public static class PluginDependencies
{
    /// <summary>What <paramref name="pluginId"/> needs that no enabled instance in the tenant provides.</summary>
    public static IReadOnlyList<MissingProvider> Missing(
        PluginRegistry registry, string pluginId, IEnumerable<string> enabledPluginIds)
    {
        var enabled = enabledPluginIds.ToHashSet(StringComparer.Ordinal);
        return Required(registry, pluginId)
            .Where(r => !r.Providers.Any(p => p == pluginId || enabled.Contains(p)))
            .Select(r => new MissingProvider(r.ContractId, r.Providers))
            .ToList();
    }

    /// <summary>
    /// The enabled instances that would lose a required contract once the tenant's enabled
    /// instances are <paramref name="remainingEnabled"/> — a contract counts as lost only when
    /// no remaining instance of <i>any</i> of its providers is left.
    /// </summary>
    /// <param name="remainingEnabled">The tenant's enabled instances after the change.</param>
    public static IReadOnlyList<DependentInstance> Dependents(
        PluginRegistry registry, string providerPluginId, IReadOnlyCollection<(string PluginId, string Slug)> remainingEnabled)
    {
        var remaining = remainingEnabled.Select(i => i.PluginId).ToHashSet(StringComparer.Ordinal);
        return remainingEnabled
            .SelectMany(i => Required(registry, i.PluginId)
                .Where(r => r.Providers.Contains(providerPluginId)
                            && !r.Providers.Contains(i.PluginId)
                            && !r.Providers.Any(remaining.Contains))
                .Select(r => new DependentInstance(i.PluginId, i.Slug, r.ContractId)))
            .ToList();
    }

    /// <summary>Also what the marketplace shows as a plugin's dependencies.</summary>
    public static IEnumerable<(string ContractId, IReadOnlyList<string> Providers, bool Optional)> Of(
        PluginRegistry registry, string pluginId) =>
        (registry.Find(pluginId)?.Consumes ?? [])
            .Select(r => (r.ContractId,
                (IReadOnlyList<string>)(registry.FindContract(r.ContractId)?.ProviderPluginIds.ToList() ?? []), r.Optional))
            .Where(r => r.Item2.Count > 0);

    private static IEnumerable<(string ContractId, IReadOnlyList<string> Providers)> Required(PluginRegistry registry, string pluginId) =>
        Of(registry, pluginId).Where(r => !r.Optional).Select(r => (r.ContractId, r.Providers));
}
