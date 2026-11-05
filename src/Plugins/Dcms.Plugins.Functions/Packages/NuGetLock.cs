using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Dcms.Plugins.Functions.Packages;

/// <summary>
/// A repository's NuGet packages (ADR 0031): <c>Functions.csproj</c>'s <c>PackageReference</c>s,
/// locked by <c>packages.lock.json</c> (<c>RestorePackagesWithLockFile</c>, as <c>dotnet restore</c>
/// writes it). Every package in the lock is fetched from nuget.org by id and version and checked
/// against the lock's content hash; nothing in a package runs at build.
/// </summary>
public static partial class NuGetLock
{
    public const string Project = "Functions.csproj";
    public const string Lockfile = "packages.lock.json";
    public const string Feed = "https://api.nuget.org/v3-flatcontainer/";

    /// <summary>The platform's own, made by the server (CSharpSdkPackage): never fetched.</summary>
    private const string Sdk = "Dcms.Functions.Sdk";

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,99}$")]
    private static partial Regex PackageId();

    [GeneratedRegex(@"^[0-9][0-9A-Za-z.+-]{0,63}$")]
    private static partial Regex PackageVersion();

    public static (List<LockedPackage> Packages, List<string> Problems) Read(IReadOnlyDictionary<string, string> files)
    {
        if (!files.TryGetValue(Project, out var projectText)) return ([], []);
        XDocument project;
        try
        {
            project = XDocument.Parse(projectText);
        }
        catch (System.Xml.XmlException)
        {
            return ([], [$"{Project} is not XML."]);
        }
        var declared = project.Descendants().Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => (Id: (string?)e.Attribute("Include") ?? "", Version: (string?)e.Attribute("Version") ?? (string?)e.Element(e.Name.Namespace + "Version") ?? ""))
            .Where(p => !p.Id.Equals(Sdk, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (declared.Count == 0) return ([], []);
        if (!files.TryGetValue(Lockfile, out var lockText))
        {
            return ([], [$"{Project} references packages, but there is no {Lockfile}: set <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>, run `dotnet restore` and commit it, or add packages in the editor."]);
        }
        JsonObject? frameworks;
        try
        {
            frameworks = JsonNode.Parse(lockText)?["dependencies"] as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            frameworks = null;
        }
        // One target framework: the first (net10.0 for a project dcms sdk wrote).
        if (frameworks?.FirstOrDefault().Value is not JsonObject entries)
        {
            return ([], [$"{Lockfile} is not a lock file `dotnet restore` wrote."]);
        }

        var direct = entries.Where(e => (string?)e.Value?["type"] == "Direct" && !e.Key.Equals(Sdk, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(e => e.Key, e => (string?)e.Value?["requested"] ?? "", StringComparer.OrdinalIgnoreCase);
        var stale = declared.Where(d => !direct.TryGetValue(d.Id, out var requested) || !Requests(requested, d.Version)).Select(d => d.Id)
            .Concat(direct.Keys.Where(k => !declared.Any(d => d.Id.Equals(k, StringComparison.OrdinalIgnoreCase))))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (stale.Count > 0)
        {
            return ([], [$"{Lockfile} does not match {Project} for {string.Join(", ", stale)}: run `dotnet restore` and commit both."]);
        }

        var packages = new List<LockedPackage>();
        var problems = new List<string>();
        foreach (var (id, node) in entries)
        {
            if (node is not JsonObject entry || id.Equals(Sdk, StringComparison.OrdinalIgnoreCase)) continue;
            var type = (string?)entry["type"];
            if (type is "Project")
            {
                problems.Add($"{id} is a project reference, which a build cannot install: reference a published package.");
                continue;
            }
            var version = (string?)entry["resolved"] ?? "";
            var hash = (string?)entry["contentHash"] ?? "";
            if (!PackageId().IsMatch(id) || !PackageVersion().IsMatch(version) || hash.Length == 0)
            {
                problems.Add($"{Lockfile}: {id} {version} has no package id, version and content hash a build can use.");
                continue;
            }
            var lower = (id.ToLowerInvariant(), version.ToLowerInvariant());
            packages.Add(new LockedPackage($"nuget/{lower.Item1}/{lower.Item2}", id, version,
                $"{Feed}{lower.Item1}/{lower.Item2}/{lower.Item1}.{lower.Item2}.nupkg", $"sha512-{hash}"));
        }
        if (packages.Count > NpmLock.MaxPackages)
        {
            problems.Add($"A repository installs at most {NpmLock.MaxPackages} packages; {Lockfile} has {packages.Count}.");
        }
        return (problems.Count > 0 ? [] : packages, problems);
    }

    /// <summary>Whether a lock's <c>requested</c> (<c>[13.0.3, )</c>) is what the project's version asks.</summary>
    private static bool Requests(string requested, string version) =>
        requested == version || requested.Trim('[', '(', ']', ')').Split(',')[0].Trim() == version.Trim('[', '(', ']', ')').Split(',')[0].Trim();
}
