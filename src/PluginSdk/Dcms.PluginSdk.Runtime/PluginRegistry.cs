using Dcms.PluginSdk.Abstractions;

namespace Dcms.PluginSdk.Runtime;

/// <summary>
/// Holds the statically registered plugin set. Validates manifests at startup:
/// ids must be unique kebab-case, content type names unique within a plugin.
/// </summary>
public sealed class PluginRegistry : IPluginCatalog
{
    private readonly Dictionary<string, IPlugin> _plugins;

    public PluginRegistry(IEnumerable<IPlugin> plugins)
    {
        _plugins = new Dictionary<string, IPlugin>(StringComparer.Ordinal);

        foreach (var plugin in plugins)
        {
            var manifest = plugin.Manifest;
            ValidateManifest(manifest);

            if (!_plugins.TryAdd(manifest.Id, plugin))
            {
                throw new InvalidOperationException($"Duplicate plugin id '{manifest.Id}'.");
            }
        }
    }

    public IReadOnlyCollection<IPlugin> Plugins => _plugins.Values;

    public IReadOnlyList<PluginManifest> Manifests => _plugins.Values.Select(p => p.Manifest).ToList();

    public PluginManifest? Find(string pluginId)
        => _plugins.TryGetValue(pluginId, out var plugin) ? plugin.Manifest : null;

    public IPlugin? FindPlugin(string pluginId)
        => _plugins.GetValueOrDefault(pluginId);

    private static void ValidateManifest(PluginManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id) || !IsKebabCase(manifest.Id))
        {
            throw new InvalidOperationException($"Plugin id '{manifest.Id}' must be non-empty kebab-case.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            throw new InvalidOperationException($"Plugin '{manifest.Id}' must have a name.");
        }

        var duplicateType = manifest.ContentTypes
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateType is not null)
        {
            throw new InvalidOperationException(
                $"Plugin '{manifest.Id}' declares content type '{duplicateType.Key}' more than once.");
        }
    }

    public static bool IsKebabCase(string value)
        => value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
           && !value.StartsWith('-')
           && !value.EndsWith('-');
}
