using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using NuGet.Versioning;
using Semver;

namespace Dcms.Plugins.Functions.Packages;

/// <summary>
/// Adds and removes packages for the web IDE (ADR 0031), where there is no npm and no dotnet:
/// writes package.json + package-lock.json, or Functions.csproj + packages.lock.json, from the
/// registries' own metadata. A desktop `npm install` / `dotnet restore` accepts what it writes.
/// <list type="bullet">
/// <item>npm: each range takes its highest matching version (the latest tag first, as npm does);
/// a package goes at the top of node_modules unless a different version is there already, then
/// under the package that needs it. Peer and optional dependencies are not installed.</item>
/// <item>NuGet: the package asked for takes its latest stable version unless one is given;
/// dependencies take the lowest version their range allows, the highest of those when asked for
/// twice. Packages the framework provides are left out, as .NET 10's restore prunes them.</item>
/// </list>
/// </summary>
public sealed class PackageResolver(IHttpClientFactory http, PackageStore store)
{
    private const int MaxPackages = NpmLock.MaxPackages;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Packages matching <paramref name="query"/>, ten at most: name, latest version, what it says it is.</summary>
    public async Task<IReadOnlyList<object>> SearchAsync(string ecosystem, string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 100) return [];
        var client = http.CreateClient(PackageStore.HttpClientName);
        if (ecosystem == "nuget")
        {
            var found = await client.GetFromJsonAsync<JsonObject>(
                $"https://azuresearch-usnc.nuget.org/query?q={Uri.EscapeDataString(query)}&take=10&prerelease=false&semVerLevel=2.0.0", ct);
            return (found?["data"] as JsonArray ?? []).Select(p => (object)new
            {
                name = p?["id"]?.GetValue<string>(),
                version = p?["version"]?.GetValue<string>(),
                description = p?["description"]?.GetValue<string>()?[..Math.Min(200, p["description"]!.GetValue<string>().Length)],
            }).ToList();
        }
        var npm = await client.GetFromJsonAsync<JsonObject>($"{NpmLock.Registry}-/v1/search?text={Uri.EscapeDataString(query)}&size=10", ct);
        return (npm?["objects"] as JsonArray ?? []).Select(o => o?["package"]).Select(p => (object)new
        {
            name = p?["name"]?.GetValue<string>(),
            version = p?["version"]?.GetValue<string>(),
            description = p?["description"]?.GetValue<string>()?[..Math.Min(200, p["description"]!.GetValue<string>().Length)],
        }).ToList();
    }

    /// <summary>The editor's view of the locked npm packages: their type declarations and package.json files.</summary>
    public const long MaxTypesBytes = 12L * 1024 * 1024;

    public async Task<Dictionary<string, string>> TypesAsync(IReadOnlyDictionary<string, string> files, CancellationToken ct)
    {
        var (locked, problems) = NpmLock.Read(files);
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        if (problems.Count > 0) return types;
        long total = 0;
        foreach (var package in locked)
        {
            byte[] tgz;
            try
            {
                tgz = await store.GetAsync(package.Resolved, package.Integrity, ct);
            }
            catch (PackageException)
            {
                continue;
            }
            await using var gzip = new System.IO.Compression.GZipStream(new MemoryStream(tgz, writable: false), System.IO.Compression.CompressionMode.Decompress);
            await using var tar = new System.Formats.Tar.TarReader(gzip);
            while (await tar.GetNextEntryAsync(copyData: false, ct) is { } entry)
            {
                if (entry.EntryType is not (System.Formats.Tar.TarEntryType.RegularFile or System.Formats.Tar.TarEntryType.V7RegularFile)) continue;
                var name = entry.Name.Replace('\\', '/');
                var slash = name.IndexOf('/');
                if (slash < 0) continue;
                var relative = name[(slash + 1)..];
                if (relative.Contains("..", StringComparison.Ordinal)
                    || !(relative == "package.json" || relative.EndsWith(".d.ts", StringComparison.Ordinal)
                         || relative.EndsWith(".d.mts", StringComparison.Ordinal) || relative.EndsWith(".d.cts", StringComparison.Ordinal))) continue;
                if ((total += entry.Length) > MaxTypesBytes) return types;
                using var reader = new StreamReader(entry.DataStream ?? Stream.Null);
                types[$"{package.Path}/{relative}"] = await reader.ReadToEndAsync(ct);
            }
        }
        return types;
    }

    /// <summary>
    /// The locked NuGet packages' assemblies, base64, for the editor's C# service: each package's
    /// best lib/ folder, as script-runner picks it (PackageArchives.Libraries), the framework's own left out.
    /// </summary>
    public async Task<List<string>> AssembliesAsync(IReadOnlyDictionary<string, string> files, IReadOnlySet<string> framework, CancellationToken ct)
    {
        var (locked, problems) = NuGetLock.Read(files);
        var assemblies = new List<string>();
        if (problems.Count > 0) return assemblies;
        long total = 0;
        foreach (var package in locked)
        {
            byte[] nupkg;
            try
            {
                nupkg = await store.GetAsync(package.Resolved, package.Integrity, ct);
            }
            catch (PackageException)
            {
                continue;
            }
            using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(nupkg, writable: false));
            var libs = zip.Entries
                .Where(e => e.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) && e.FullName.Count(c => c == '/') == 2
                            && e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .GroupBy(e => e.FullName.Split('/')[1].ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.ToList());
            if (Frameworks.FirstOrDefault(libs.ContainsKey) is not { } best) continue;
            foreach (var entry in libs[best].Where(e => !framework.Contains(Path.GetFileNameWithoutExtension(e.Name))))
            {
                if ((total += entry.Length) > 16L * 1024 * 1024) return assemblies;
                await using var stream = entry.Open();
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy, ct);
                assemblies.Add(Convert.ToBase64String(copy.ToArray()));
            }
        }
        return assemblies;
    }

    // ---------------- npm ----------------

    public async Task<Dictionary<string, string>> NpmAsync(IReadOnlyDictionary<string, string> files, string name, string? range, bool remove, CancellationToken ct)
    {
        var manifest = (files.TryGetValue(NpmLock.Manifest, out var text) ? Parse(text) as JsonObject : null)
                       ?? new JsonObject { ["name"] = "functions", ["private"] = true };
        var dependencies = manifest["dependencies"] as JsonObject ?? new JsonObject();
        manifest["dependencies"] = dependencies;
        if (remove)
        {
            dependencies.Remove(name);
        }
        else
        {
            if (range is null)
            {
                var latest = (await NpmMetadataAsync(name, ct))["dist-tags"]?["latest"]?.GetValue<string>()
                             ?? throw new PackageException($"{name} has no published version.");
                range = $"^{latest}";
            }
            SemVersionRange.ParseNpm(range);
            dependencies[name] = range;
        }
        var sorted = new JsonObject(dependencies.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => KeyValuePair.Create(d.Key, d.Value?.DeepClone())));
        manifest["dependencies"] = sorted;

        var packages = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        var queue = new Queue<(string Parent, string Name, string Range)>(sorted.Select(d => ("", d.Key, d.Value!.GetValue<string>())));
        var metadata = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        while (queue.TryDequeue(out var want))
        {
            if (!metadata.TryGetValue(want.Name, out var meta))
            {
                meta = metadata[want.Name] = await NpmMetadataAsync(want.Name, ct);
            }
            // Already reachable from where it is needed, at a version that fits: nothing to add.
            if (Visible(packages, want.Parent, want.Name) is { } seen && Satisfies(want.Range, seen["version"]!.GetValue<string>())) continue;

            var version = Pick(meta, want.Range)
                          ?? throw new PackageException($"No published version of {want.Name} matches {want.Range}.");
            var top = $"node_modules/{want.Name}";
            var path = !packages.ContainsKey(top) ? top : $"{(want.Parent.Length == 0 ? "" : want.Parent + "/")}node_modules/{want.Name}";
            if (packages.ContainsKey(path)) continue;
            var info = meta["versions"]![version]!.AsObject();
            var entry = new JsonObject
            {
                ["version"] = version,
                ["resolved"] = info["dist"]?["tarball"]?.GetValue<string>(),
                ["integrity"] = info["dist"]?["integrity"]?.GetValue<string>(),
            };
            if (info["dependencies"] is JsonObject deps && deps.Count > 0)
            {
                entry["dependencies"] = deps.DeepClone();
                foreach (var (dep, depRange) in deps)
                {
                    queue.Enqueue((path, dep, depRange?.GetValue<string>() ?? "*"));
                }
            }
            packages[path] = entry;
            if (packages.Count > MaxPackages)
            {
                throw new PackageException($"These packages need more than {MaxPackages} packages in all.");
            }
        }

        var lockPackages = new JsonObject
        {
            [""] = new JsonObject { ["name"] = manifest["name"]?.DeepClone(), ["dependencies"] = sorted.DeepClone() },
        };
        foreach (var (path, entry) in packages)
        {
            lockPackages[path] = entry;
        }
        var lockfile = new JsonObject
        {
            ["name"] = manifest["name"]?.DeepClone(),
            ["lockfileVersion"] = 3,
            ["requires"] = true,
            ["packages"] = lockPackages,
        };
        return new Dictionary<string, string>
        {
            [NpmLock.Manifest] = manifest.ToJsonString(Indented) + "\n",
            [NpmLock.Lockfile] = lockfile.ToJsonString(Indented) + "\n",
        };
    }

    /// <summary>What a package at <paramref name="from"/> would get for <paramref name="name"/>: the nearest node_modules up the tree.</summary>
    private static JsonObject? Visible(SortedDictionary<string, JsonObject> packages, string from, string name)
    {
        for (var at = from; ; )
        {
            if (packages.TryGetValue($"{(at.Length == 0 ? "" : at + "/")}node_modules/{name}", out var found)) return found;
            if (at.Length == 0) return null;
            var cut = at.LastIndexOf("/node_modules/", StringComparison.Ordinal);
            at = cut < 0 ? "" : at[..cut];
        }
    }

    private static bool Satisfies(string range, string version) =>
        SemVersion.TryParse(version, SemVersionStyles.Strict, out var v) && Range(range).Contains(v);

    private static SemVersionRange Range(string range)
    {
        try
        {
            return SemVersionRange.ParseNpm(range);
        }
        catch (FormatException)
        {
            throw new PackageException($"'{range}' is not a version range npm understands (and git, file and URL dependencies cannot be installed).");
        }
    }

    private static string? Pick(JsonObject meta, string range)
    {
        var allowed = Range(range);
        if (meta["dist-tags"]?["latest"]?.GetValue<string>() is { } latest && Satisfies(range, latest)) return latest;
        return (meta["versions"] as JsonObject ?? [])
            .Select(v => SemVersion.TryParse(v.Key, SemVersionStyles.Strict, out var parsed) ? parsed : null)
            .Where(v => v is not null && allowed.Contains(v))
            .OrderByDescending(v => v!, SemVersion.PrecedenceComparer)
            .FirstOrDefault()?.ToString();
    }

    private async Task<JsonObject> NpmMetadataAsync(string name, CancellationToken ct)
    {
        if (PackageArchivesNames.Npm().IsMatch(name) is false)
        {
            throw new PackageException($"'{name}' is not an npm package name.");
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{NpmLock.Registry}{name.Replace("/", "%2f", StringComparison.Ordinal)}");
        // The abbreviated document: versions, dependencies and dist, without readmes.
        request.Headers.Accept.ParseAdd("application/vnd.npm.install-v1+json");
        using var response = await http.CreateClient(PackageStore.HttpClientName).SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new PackageException(response.StatusCode == System.Net.HttpStatusCode.NotFound ? $"There is no npm package {name}." : $"npm answered {(int)response.StatusCode} for {name}.");
        }
        return await response.Content.ReadFromJsonAsync<JsonObject>(ct) ?? throw new PackageException($"npm sent nothing for {name}.");
    }

    // ---------------- NuGet ----------------

    private static readonly string[] Frameworks =
        ["net10.0", "net9.0", "net8.0", "net7.0", "net6.0", "net5.0", "netcoreapp3.1", "netstandard2.1", "netstandard2.0",
         "netstandard1.6", "netstandard1.5", "netstandard1.4", "netstandard1.3", "netstandard1.2", "netstandard1.1", "netstandard1.0"];

    public async Task<Dictionary<string, string>> NuGetAsync(IReadOnlyDictionary<string, string> files, string id, string? version, bool remove,
        IReadOnlySet<string> framework, CancellationToken ct)
    {
        var project = files.TryGetValue(NuGetLock.Project, out var text) ? XDocument.Parse(text) : XDocument.Parse(CSharpProject);
        var root = project.Root!;
        var ns = root.Name.Namespace;
        var references = root.Descendants(ns + "PackageReference").ToList();
        foreach (var existing in references.Where(r => string.Equals((string?)r.Attribute("Include"), id, StringComparison.OrdinalIgnoreCase)))
        {
            existing.Remove();
        }
        if (!remove)
        {
            var versions = await NuGetVersionsAsync(id, ct);
            var chosen = version is null
                ? versions.Where(v => !v.IsPrerelease).DefaultIfEmpty(versions.LastOrDefault()).Max()
                : NuGetVersion.TryParse(version, out var given) && versions.Contains(given) ? given : null;
            if (chosen is null)
            {
                throw new PackageException(version is null ? $"{id} has no published version." : $"{id} has no version {version}.");
            }
            var group = root.Elements(ns + "ItemGroup").FirstOrDefault(g => g.Elements(ns + "PackageReference").Any())
                        ?? new XElement(ns + "ItemGroup");
            if (group.Parent is null) root.Add(group);
            group.Add(new XElement(ns + "PackageReference", new XAttribute("Include", id), new XAttribute("Version", chosen.ToNormalizedString())));
        }

        var direct = root.Descendants(ns + "PackageReference")
            .Select(r => (Id: (string?)r.Attribute("Include") ?? "", Version: (string?)r.Attribute("Version") ?? ""))
            .Where(r => r.Id.Length > 0 && !r.Id.Equals("Dcms.Functions.Sdk", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Lowest applicable versions, raised to the highest request for an id, until nothing moves.
        var chosenVersions = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
        var nuspecs = new Dictionary<string, List<(string Id, VersionRange Range)>>(StringComparer.OrdinalIgnoreCase);
        var published = new Dictionary<string, List<NuGetVersion>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (directId, directVersion) in direct)
        {
            chosenVersions[directId] = NuGetVersion.Parse(directVersion);
        }
        for (var pass = 0; ; pass++)
        {
            if (pass > 50) throw new PackageException("The packages' dependencies did not settle.");
            var moved = false;
            foreach (var (pkg, v) in chosenVersions.ToList())
            {
                var key = $"{pkg}/{v.ToNormalizedString()}";
                if (!nuspecs.TryGetValue(key, out var dependencies))
                {
                    dependencies = nuspecs[key] = await NuGetDependenciesAsync(pkg, v, ct);
                }
                foreach (var (dep, range) in dependencies)
                {
                    if (framework.Contains(dep) || direct.Any(d => d.Id.Equals(dep, StringComparison.OrdinalIgnoreCase))) continue;
                    if (!published.TryGetValue(dep, out var available))
                    {
                        available = published[dep] = await NuGetVersionsAsync(dep, ct);
                    }
                    var lowest = range.FindBestMatch(available)
                                 ?? throw new PackageException($"No version of {dep} matches {range} ({pkg} needs it).");
                    if (!chosenVersions.TryGetValue(dep, out var have) || have < lowest)
                    {
                        chosenVersions[dep] = lowest;
                        moved = true;
                    }
                }
            }
            if (!moved) break;
        }
        if (chosenVersions.Count > MaxPackages)
        {
            throw new PackageException($"These packages need more than {MaxPackages} packages in all.");
        }

        var lockEntries = new JsonObject();
        foreach (var (pkg, v) in chosenVersions.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var lowerId = pkg.ToLowerInvariant();
            var lowerVersion = v.ToNormalizedString().ToLowerInvariant();
            var bytes = await store.GetUncheckedAsync($"{NuGetLock.Feed}{lowerId}/{lowerVersion}/{lowerId}.{lowerVersion}.nupkg", ct);
            var isDirect = direct.FirstOrDefault(d => d.Id.Equals(pkg, StringComparison.OrdinalIgnoreCase));
            var entry = new JsonObject { ["type"] = isDirect.Id is null ? "Transitive" : "Direct" };
            if (isDirect.Id is not null) entry["requested"] = $"[{isDirect.Version}, )";
            entry["resolved"] = v.ToNormalizedString();
            entry["contentHash"] = Convert.ToBase64String(SHA512.HashData(bytes));
            var deps = nuspecs[$"{pkg}/{v.ToNormalizedString()}"].Where(d => !framework.Contains(d.Id)).ToList();
            if (deps.Count > 0)
            {
                entry["dependencies"] = new JsonObject(deps.Select(d => KeyValuePair.Create<string, JsonNode?>(d.Id, d.Range.ToString())));
            }
            lockEntries[pkg] = entry;
        }
        var lockfile = new JsonObject { ["version"] = 1, ["dependencies"] = new JsonObject { ["net10.0"] = lockEntries } };
        return new Dictionary<string, string>
        {
            [NuGetLock.Project] = project.ToString() + "\n",
            [NuGetLock.Lockfile] = lockfile.ToJsonString(Indented) + "\n",
        };
    }

    /// <summary>What `dcms sdk` writes, so a project made here and one made there are the same.</summary>
    private const string CSharpProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <Import Project=".dcms/version.props" Condition="Exists('.dcms/version.props')" />
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Dcms.Functions.Sdk" Version="$(DcmsFunctionsSdkVersion)" />
          </ItemGroup>
        </Project>
        """;

    private async Task<List<NuGetVersion>> NuGetVersionsAsync(string id, CancellationToken ct)
    {
        if (!PackageArchivesNames.NuGet().IsMatch(id)) throw new PackageException($"'{id}' is not a NuGet package id.");
        using var response = await http.CreateClient(PackageStore.HttpClientName).GetAsync($"{NuGetLock.Feed}{id.ToLowerInvariant()}/index.json", ct);
        if (!response.IsSuccessStatusCode) throw new PackageException($"There is no NuGet package {id}.");
        var index = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        return (index?["versions"] as JsonArray ?? [])
            .Select(v => NuGetVersion.TryParse(v?.GetValue<string>(), out var parsed) ? parsed : null)
            .OfType<NuGetVersion>().Order().ToList();
    }

    private async Task<List<(string Id, VersionRange Range)>> NuGetDependenciesAsync(string id, NuGetVersion version, CancellationToken ct)
    {
        var lowerId = id.ToLowerInvariant();
        var lowerVersion = version.ToNormalizedString().ToLowerInvariant();
        using var response = await http.CreateClient(PackageStore.HttpClientName).GetAsync($"{NuGetLock.Feed}{lowerId}/{lowerVersion}/{lowerId}.nuspec", ct);
        if (!response.IsSuccessStatusCode) throw new PackageException($"NuGet has no {id} {version}.");
        var nuspec = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var groups = nuspec.Descendants().Where(e => e.Name.LocalName == "group").ToList();
        IEnumerable<XElement> chosen;
        if (groups.Count == 0)
        {
            chosen = nuspec.Descendants().Where(e => e.Name.LocalName == "dependency");
        }
        else
        {
            var byFramework = groups.ToDictionary(g => Framework((string?)g.Attribute("targetFramework")), g => g);
            var best = Frameworks.FirstOrDefault(byFramework.ContainsKey) ?? (byFramework.ContainsKey("") ? "" : null);
            chosen = best is null ? [] : byFramework[best].Elements().Where(e => e.Name.LocalName == "dependency");
        }
        return chosen
            .Select(d => ((string?)d.Attribute("id") ?? "", VersionRange.TryParse((string?)d.Attribute("version") ?? "0.0.0", out var r) ? r : VersionRange.All))
            .Where(d => d.Item1.Length > 0)
            .ToList();
    }

    /// <summary>A nuspec's framework name as a short moniker: ".NETStandard2.0" is netstandard2.0, "net8.0" stays.</summary>
    private static string Framework(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var n = name.ToLowerInvariant();
        if (n.StartsWith(".netstandard", StringComparison.Ordinal)) return "netstandard" + n[".netstandard".Length..];
        if (n.StartsWith(".netcoreapp", StringComparison.Ordinal)) return "netcoreapp" + n[".netcoreapp".Length..];
        if (n.StartsWith("net") && n.Length > 3 && char.IsDigit(n[3]) && n.Contains('.')) return n;
        return n;
    }

    private static JsonNode? Parse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal static partial class PackageArchivesNames
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^(?:@[a-z0-9-~][a-z0-9-._~]*/)?[a-z0-9-~][a-z0-9-._~]*$")]
    public static partial System.Text.RegularExpressions.Regex Npm();

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,99}$")]
    public static partial System.Text.RegularExpressions.Regex NuGet();
}
