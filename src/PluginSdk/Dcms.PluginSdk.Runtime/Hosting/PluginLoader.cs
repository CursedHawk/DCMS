using System.Reflection;
using System.Runtime.Loader;
using Dcms.PluginSdk.Abstractions;

namespace Dcms.PluginSdk.Runtime.Hosting;

/// <summary>
/// Loads operator-installed plugins from a directory (<c>Plugins:Directory</c>) — the equivalent of
/// a server's <c>plugins/</c> folder. Each subdirectory holds one plugin's <c>dotnet publish</c>
/// output; its main assembly is the one with a <c>.deps.json</c> beside it.
///
/// <para><b>Types are the host's.</b> Every plugin gets its own <see cref="AssemblyLoadContext"/>
/// for its private dependencies, but any assembly the host already has — the SDK, the built-in
/// plugins' <c>.Api</c> assemblies, the framework — resolves to the host's copy. That is what
/// makes a contract resolved by an installed plugin the same <see cref="Type"/> the provider
/// implements, instead of a look-alike from a second load.</para>
///
/// <para><b>Trusted code only.</b> An installed plugin runs in-process with the host's rights; the
/// operator installs it, tenants never can. A plugin built against another SDK major, or that
/// fails to load, stops startup with a message naming it — a half-loaded plugin set is worse than
/// a deploy that does not come up.</para>
/// </summary>
public static class PluginLoader
{
    /// <summary>The SDK major this host runs; an installed plugin must be built against the same.</summary>
    public static int SdkMajor => typeof(IPlugin).Assembly.GetName().Version?.Major ?? 0;

    private static readonly Lazy<HashSet<string>> HostAssemblies = new(() =>
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>Every plugin in <paramref name="directory"/>'s subdirectories; none when it does not exist.</summary>
    public static IReadOnlyList<IPlugin> LoadFrom(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }
        return Directory.GetDirectories(directory)
            .OrderBy(d => d, StringComparer.Ordinal)
            .SelectMany(LoadPackage)
            .ToList();
    }

    private static IEnumerable<IPlugin> LoadPackage(string folder)
    {
        var deps = Directory.GetFiles(folder, "*.deps.json");
        if (deps.Length != 1)
        {
            throw Refused(folder, $"expected one *.deps.json (the published plugin's), found {deps.Length}");
        }
        var main = Path.Combine(folder, Path.GetFileName(deps[0])[..^".deps.json".Length] + ".dll");
        if (!File.Exists(main))
        {
            throw Refused(folder, $"{Path.GetFileName(main)} is missing");
        }

        Assembly assembly;
        try
        {
            assembly = new PluginLoadContext(main).LoadFromAssemblyPath(main);
        }
        catch (Exception e) when (e is BadImageFormatException or FileLoadException)
        {
            throw Refused(folder, e.Message);
        }

        var sdk = assembly.GetReferencedAssemblies().FirstOrDefault(a => a.Name == typeof(IPlugin).Assembly.GetName().Name);
        if (sdk is null)
        {
            throw Refused(folder, "it does not reference the plugin SDK");
        }
        if (sdk.Version?.Major != SdkMajor)
        {
            throw Refused(folder, $"it was built against plugin SDK {sdk.Version}; this host runs SDK {SdkMajor}.x");
        }

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            throw Refused(folder, string.Join("; ", e.LoaderExceptions.Select(x => x?.Message).Distinct()));
        }

        var plugins = types
            .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true }
                        && typeof(IPlugin).IsAssignableFrom(t)
                        && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (IPlugin)Activator.CreateInstance(t)!)
            .ToList();
        if (plugins.Count == 0)
        {
            throw Refused(folder, "it contains no public IPlugin with a parameterless constructor");
        }
        return plugins;
    }

    private static InvalidOperationException Refused(string folder, string why) =>
        new($"Refusing to start: the plugin in '{folder}' cannot be loaded — {why}.");

    private sealed class PluginLoadContext(string mainAssemblyPath)
        : AssemblyLoadContext($"plugin:{Path.GetFileNameWithoutExtension(mainAssemblyPath)}")
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

        protected override Assembly? Load(AssemblyName name)
        {
            // Null defers to the default context: the host's copy.
            if (name.Name is { } simple && HostAssemblies.Value.Contains(simple))
            {
                return null;
            }
            return _resolver.ResolveAssemblyToPath(name) is { } path ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) =>
            _resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
