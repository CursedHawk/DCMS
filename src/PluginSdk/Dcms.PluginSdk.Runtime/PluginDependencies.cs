namespace Dcms.PluginSdk.Runtime;

/// <summary>A required contract whose provider plugin has no enabled instance.</summary>
public sealed record MissingProvider(string ContractId, string ProviderPluginId);

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
        return RequiredProviders(registry, pluginId)
            .Where(r => r.ProviderPluginId != pluginId && !enabled.Contains(r.ProviderPluginId))
            .ToList();
    }

    /// <summary>
    /// The enabled instances that would lose a required contract if every instance of
    /// <paramref name="providerPluginId"/> outside <paramref name="remainingEnabled"/> went away.
    /// </summary>
    /// <param name="remainingEnabled">The tenant's enabled instances after the change.</param>
    public static IReadOnlyList<DependentInstance> Dependents(
        PluginRegistry registry, string providerPluginId, IReadOnlyCollection<(string PluginId, string Slug)> remainingEnabled)
    {
        if (remainingEnabled.Any(i => i.PluginId == providerPluginId))
        {
            return []; // another instance still provides it
        }
        return remainingEnabled
            .SelectMany(i => RequiredProviders(registry, i.PluginId)
                .Where(r => r.ProviderPluginId == providerPluginId && i.PluginId != providerPluginId)
                .Select(r => new DependentInstance(i.PluginId, i.Slug, r.ContractId)))
            .ToList();
    }

    private static IEnumerable<MissingProvider> RequiredProviders(PluginRegistry registry, string pluginId) =>
        (registry.Find(pluginId)?.Consumes ?? [])
            .Where(r => !r.Optional)
            .Select(r => registry.FindContract(r.ContractId)?.Descriptor.ProviderPluginId is { } provider
                ? new MissingProvider(r.ContractId, provider)
                : null)
            .OfType<MissingProvider>();
}
