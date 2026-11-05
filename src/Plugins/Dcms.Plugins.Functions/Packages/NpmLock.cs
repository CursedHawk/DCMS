using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dcms.Plugins.Functions.Packages;

/// <summary>One package a build installs: where under the repository, and the exact tarball.</summary>
public sealed record LockedPackage(string Path, string Name, string Version, string Resolved, string Integrity);

/// <summary>
/// A repository's npm packages (ADR 0031): <c>package.json</c>'s <c>dependencies</c>, locked by
/// <c>package-lock.json</c> (lockfile v2 or v3, as <c>npm install</c> writes it). The lockfile is the
/// whole truth: every tarball in it is fetched from the registry by its exact URL and checked
/// against its integrity hash, and nothing in a package ever runs at install.
/// </summary>
public static partial class NpmLock
{
    public const string Manifest = "package.json";
    public const string Lockfile = "package-lock.json";
    public const string Registry = "https://registry.npmjs.org/";
    public const int MaxPackages = 300;

    [GeneratedRegex(@"^node_modules/(?:@[a-z0-9-~][a-z0-9-._~]*/)?[a-z0-9-~][a-z0-9-._~]*(?:/node_modules/(?:@[a-z0-9-~][a-z0-9-._~]*/)?[a-z0-9-~][a-z0-9-._~]*)*$")]
    private static partial Regex InstallPath();

    /// <summary>The packages to install, or why the repository's declaration cannot be built.</summary>
    public static (List<LockedPackage> Packages, List<string> Problems) Read(IReadOnlyDictionary<string, string> files)
    {
        var problems = new List<string>();
        if (!files.TryGetValue(Manifest, out var manifestText)) return ([], problems);
        if (Parse(manifestText) is not JsonObject manifest)
        {
            return ([], [$"{Manifest} is not JSON."]);
        }
        var declared = Dependencies(manifest["dependencies"]);
        if (declared.Count == 0) return ([], problems);
        if (!files.TryGetValue(Lockfile, out var lockText))
        {
            return ([], [$"{Manifest} declares dependencies, but there is no {Lockfile}: run `npm install` and commit it, or add packages in the editor."]);
        }
        if (Parse(lockText) is not JsonObject lockfile || lockfile["packages"] is not JsonObject entries)
        {
            return ([], [$"{Lockfile} is not a lockfile npm 7 or later wrote (lockfileVersion 2 or 3)."]);
        }

        var locked = Dependencies((entries[""] as JsonObject)?["dependencies"]);
        var stale = declared.Where(d => !locked.TryGetValue(d.Key, out var range) || range != d.Value).Select(d => d.Key)
            .Concat(locked.Keys.Where(k => !declared.ContainsKey(k))).Distinct().Order(StringComparer.Ordinal).ToList();
        if (stale.Count > 0)
        {
            return ([], [$"{Lockfile} does not match {Manifest} for {string.Join(", ", stale)}: run `npm install` and commit both."]);
        }

        var packages = new List<LockedPackage>();
        foreach (var (path, node) in entries)
        {
            if (path.Length == 0 || node is not JsonObject entry || entry["dev"]?.GetValue<bool>() == true) continue;
            // A platform-specific optional binary (esbuild's, sharp's): never usable in the sandbox.
            if (entry["optional"]?.GetValue<bool>() == true && (entry.ContainsKey("os") || entry.ContainsKey("cpu"))) continue;
            var name = path[(path.LastIndexOf("node_modules/", StringComparison.Ordinal) + "node_modules/".Length)..];
            if (!InstallPath().IsMatch(path))
            {
                problems.Add($"{Lockfile}: '{path}' is not a package path npm writes.");
                continue;
            }
            if (entry["link"]?.GetValue<bool>() == true)
            {
                problems.Add($"{name} is a link to a folder, which a build cannot install: depend on a published version.");
                continue;
            }
            var resolved = entry["resolved"]?.GetValue<string>();
            var integrity = entry["integrity"]?.GetValue<string>();
            var version = entry["version"]?.GetValue<string>() ?? "";
            if (resolved is null || !resolved.StartsWith(Registry, StringComparison.Ordinal) || !resolved.EndsWith(".tgz", StringComparison.Ordinal))
            {
                problems.Add($"{name} does not come from the npm registry ({resolved ?? "no resolved URL"}): only published packages can be installed.");
                continue;
            }
            if (integrity is null || !Integrity.IsStrong(integrity))
            {
                problems.Add($"{name} has no sha256, sha384 or sha512 integrity in {Lockfile}: run `npm install` with npm 7 or later.");
                continue;
            }
            packages.Add(new LockedPackage(path, name, version, resolved, integrity));
        }
        if (packages.Count > MaxPackages)
        {
            problems.Add($"A repository installs at most {MaxPackages} packages; {Lockfile} has {packages.Count}.");
        }
        return (problems.Count > 0 ? [] : packages, problems);
    }

    private static JsonNode? Parse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> Dependencies(JsonNode? node) =>
        (node as JsonObject ?? [])
            .Where(p => p.Value is JsonValue v && v.TryGetValue<string>(out _))
            .ToDictionary(p => p.Key, p => p.Value!.GetValue<string>(), StringComparer.Ordinal);
}
