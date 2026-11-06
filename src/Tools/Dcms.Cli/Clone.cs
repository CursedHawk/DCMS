using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dcms.Cli;

/// <summary>A failure the person can act on: printed as is, with an exit code, no stack trace.</summary>
internal sealed class CliException(string message, int exitCode = 1) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// A clone on this machine, and the files the CLI reads from and writes into it. A clone is
/// someone else's content: nothing here follows a symbolic link, and nothing in it decides which
/// app it is (that is the link, kept with the sign-in).
/// </summary>
internal static partial class Clone
{
    public static string Git(string root, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var git = Process.Start(start) ?? throw new CliException("git is not installed, or not on PATH.");
        var output = git.StandardOutput.ReadToEndAsync();
        var error = git.StandardError.ReadToEnd();
        git.WaitForExit();
        if (git.ExitCode != 0) throw new CliException($"git {string.Join(' ', args)}: {error.Trim()}");
        return output.Result.Trim();
    }

    /// <summary>The clone's root, or the folder itself outside git.</summary>
    public static string Root(string cwd)
    {
        try
        {
            return Path.GetFullPath(Git(cwd, "rev-parse", "--show-toplevel"));
        }
        catch (Exception e) when (e is CliException or System.ComponentModel.Win32Exception)
        {
            return cwd;
        }
    }

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase)]
    private static partial Regex Guid();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$")]
    private static partial Regex Name();

    // The clone's real path: Root() is git's --show-toplevel, which has every link on the way resolved.
    private static string Key(string root) => Path.GetFullPath(root);

    public static void WriteLink(Config config, string root, Link link)
    {
        if (!new[] { link.Tenant, link.Environment, link.App }.All(v => v is not null && Name().IsMatch(v)) || !Guid().IsMatch(link.RepoId ?? ""))
        {
            throw new CliException("The server answered with a link this command line does not understand.");
        }
        var links = config.ReadLinks();
        // The remote it was made from: a different repository cloned to the same folder later is not this link's.
        links[Key(root)] = link;
        config.WriteLinks(links);
    }

    public static Link ReadLink(Config config, string root)
    {
        if (!config.ReadLinks().TryGetValue(Key(root), out var link)) throw new CliException("This clone is not linked. Run `dcms link` in it.");
        string? now = null;
        try
        {
            now = Git(root, "remote", "get-url", link.Remote.Name);
        }
        catch (CliException)
        {
            // no such remote any more
        }
        if (now != link.Remote.Url) throw new CliException("This folder holds a different clone than the one linked. Run `dcms link` again.");
        return link;
    }

    /// <summary>
    /// Whether <paramref name="path"/>, under root, is reached through no symbolic link and is a file.
    /// True for a path that does not exist yet, as long as no folder on the way is a link.
    /// </summary>
    public static bool PlainUnder(string root, string path)
    {
        var parts = path.Split('/', '\\');
        if (parts.Any(p => p is ".." or "" or ".")) return false;
        var at = root;
        foreach (var part in parts)
        {
            at = Path.Combine(at, part);
            var info = new FileInfo(at);
            if (!info.Exists && !Directory.Exists(at)) return true; // not there yet: nothing below it can be a link
            if (info.LinkTarget is not null || new DirectoryInfo(at).LinkTarget is not null) return false;
        }
        return File.Exists(at);
    }

    /// <summary>Writes files under root, creating folders; returns what changed.</summary>
    public static List<string> WriteFiles(string root, IReadOnlyDictionary<string, byte[]> files)
    {
        var changed = new List<string>();
        foreach (var (path, bytes) in files)
        {
            if (!PlainUnder(root, path)) throw new CliException($"Refusing to write {path}: it, or a folder on the way, is a symbolic link or not a file.");
            var full = Path.Combine(root, path);
            if (File.Exists(full) && File.ReadAllBytes(full).AsSpan().SequenceEqual(bytes)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
            changed.Add(path);
        }
        return changed;
    }

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary><c>.dcms/</c> (generated per environment), a desktop build's bin/ and obj/, and node_modules/ in .gitignore, once.</summary>
    public static bool IgnoreDcms(string root)
    {
        if (!PlainUnder(root, ".gitignore")) throw new CliException("Refusing to touch .gitignore: it is a symbolic link or not a file.");
        var path = Path.Combine(root, ".gitignore");
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        var has = text.Split('\n').Select(l => l.Trim().TrimEnd('/')).ToHashSet();
        var missing = new[] { ".dcms/", "bin/", "obj/", "node_modules/" }.Where(l => !has.Contains(l.TrimEnd('/'))).ToList();
        if (missing.Count == 0) return false;
        File.WriteAllText(path, text + (text.Length > 0 && !text.EndsWith('\n') ? "\n" : "") + string.Join('\n', missing) + "\n");
        return true;
    }

    [GeneratedRegex(@"\.(ts|mts|js|mjs|cs|json)$")]
    private static partial Regex Source();

    /// <summary>
    /// The working tree as the server checks it: tracked and new source files git does not ignore.
    /// Never a symbolic link, nothing under a dot-folder or named with a dot (.env, .npmrc…), and
    /// nothing that is not source: this is sent to the server.
    /// </summary>
    public static Dictionary<string, string> WorkingFiles(string root, int maxBytes = 1024 * 1024)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Git(root, "ls-files", "-co", "--exclude-standard", "-z").Split('\0'))
        {
            if (path.Length == 0 || !Source().IsMatch(path) || path.Split('/').Any(p => p.StartsWith('.'))) continue;
            var full = Path.Combine(root, path);
            if (!File.Exists(full) || !PlainUnder(root, path)) continue; // deleted, or a link
            var bytes = File.ReadAllBytes(full);
            if (bytes.Length > maxBytes || bytes.Contains((byte)0)) continue; // binary or huge: not source
            files[path] = Encoding.UTF8.GetString(bytes);
        }
        return files;
    }

    /// <summary>The editor config that makes a local checker see what the server's build sees.</summary>
    public static Dictionary<string, byte[]> EditorFiles()
    {
        var lib = new JsonArray("esnext", "webworker");
        // nodeModulesDir manual: packages come from `npm install`'s node_modules, as the server's build has them.
        var deno = new JsonObject
        {
            ["imports"] = new JsonObject { ["@dcms/functions"] = "./.dcms/functions.ts" },
            ["nodeModulesDir"] = "manual",
            ["compilerOptions"] = new JsonObject { ["strict"] = true, ["lib"] = lib.DeepClone() },
        };
        var tsconfig = new JsonObject
        {
            ["compilerOptions"] = new JsonObject
            {
                ["target"] = "esnext",
                ["module"] = "esnext",
                ["moduleResolution"] = "bundler",
                ["strict"] = true,
                ["lib"] = lib.DeepClone(),
                ["noEmit"] = true,
                ["allowImportingTsExtensions"] = true,
                ["paths"] = new JsonObject { ["@dcms/functions"] = new JsonArray("./.dcms/functions.ts") },
            },
            ["include"] = new JsonArray("functions", "lib"),
        };
        return new()
        {
            ["deno.json"] = Utf8(Pretty(deno)),
            ["tsconfig.json"] = Utf8(Pretty(tsconfig)),
        };
    }

    public static string Pretty(JsonNode node) => node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n";

    /// <summary>Whether the repository has C# functions (any .cs outside .dcms).</summary>
    public static bool HasCSharp(string root) =>
        Git(root, "ls-files", "-co", "--exclude-standard", "*.cs").Split('\n').Any(p => p.Length > 0 && !p.StartsWith(".dcms/", StringComparison.Ordinal));

    [GeneratedRegex("^[0-9A-Za-z.-]+$")]
    private static partial Regex Version();

    /// <summary>
    /// A desktop C# build: the server's Dcms.Functions.Sdk saved into .dcms/packages, the version it
    /// is, and (when absent) a project and a NuGet source that use it.
    /// </summary>
    public static Dictionary<string, byte[]> CSharpFiles(string version, byte[] nupkg, Func<string, bool> exists)
    {
        if (!Version().IsMatch(version)) throw new CliException($"The server named an odd package version: {version}");
        var files = new Dictionary<string, byte[]>
        {
            [$".dcms/packages/dcms.functions.sdk.{version}.nupkg"] = nupkg,
            [".dcms/version.props"] = Utf8($"<Project>\n  <PropertyGroup>\n    <DcmsFunctionsSdkVersion>{version}</DcmsFunctionsSdkVersion>\n  </PropertyGroup>\n</Project>\n"),
        };
        if (!exists("Functions.csproj"))
        {
            files["Functions.csproj"] = Utf8("""
                <Project Sdk="Microsoft.NET.Sdk">
                  <!-- A desktop build of this repository's C# functions (dcms sdk). The server builds from the
                       files under functions/ and lib/, not from this project, and checks them the same way. -->
                  <Import Project=".dcms/version.props" Condition="Exists('.dcms/version.props')" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <!-- The server installs exactly what packages.lock.json locks: dotnet restore writes it. -->
                    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Dcms.Functions.Sdk" Version="$(DcmsFunctionsSdkVersion)" />
                  </ItemGroup>
                </Project>

                """);
        }
        if (!exists("nuget.config"))
        {
            files["nuget.config"] = Utf8("""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <add key="dcms" value=".dcms/packages" />
                  </packageSources>
                </configuration>

                """);
        }
        return files;
    }

    /// <summary><c>--input '{"a":1}'</c> or <c>--input @file.json</c>.</summary>
    public static JsonNode? ParseInput(string? value, Func<string, string>? read = null)
    {
        if (value is null) return null;
        var text = value.StartsWith('@') ? (read ?? File.ReadAllText)(value[1..]) : value;
        try
        {
            return JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new CliException("--input must be JSON, or @file with JSON in it.");
        }
    }

    /// <summary>A build problem as editors and terminals link it: file:line:column: message.</summary>
    public static string FormatProblem(JsonNode? p)
    {
        var file = (string?)p?["file"];
        var line = p?["line"] is JsonValue l ? l.GetValue<int>() : 0;
        var column = p?["column"] is JsonValue c ? c.GetValue<int>() : 0;
        var where = file is null ? "" : $"{file}{(line > 0 ? $":{line}{(column > 0 ? $":{column}" : "")}" : "")}: ";
        var code = (string?)p?["code"];
        return $"{where}{(string?)p?["message"]}{(code is null ? "" : $" ({code})")}";
    }
}
