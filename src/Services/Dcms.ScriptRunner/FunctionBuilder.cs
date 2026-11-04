using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Dcms.ScriptRunner;

/// <param name="Files">The repository's files by path, relative to its root.</param>
/// <param name="SdkTypes">
/// The generated <c>@dcms/functions</c> declarations for the instance's environment, a TypeScript
/// module of <c>export declare …</c>: what the code is checked against.
/// </param>
/// <param name="SdkCSharp">The generated C# half of the SDK (Sdk.g.cs), for a repository's <c>.cs</c> files (ADR 0030).</param>
public sealed record BuildRequest(Dictionary<string, string> Files, string SdkTypes, string? SdkCSharp = null);

public sealed record BuildDiagnostic(string Message, string? File = null, int? Line = null, int? Column = null, string? Code = null);

/// <param name="Definition">What <c>defineFunction</c> was given, less the handler: triggers, access, uses, limits.</param>
/// <param name="Bundle">deno-ts: the bundle. dotnet: empty; the code is the build's <see cref="BuildResult.Assembly"/>.</param>
/// <param name="Runtime"><c>deno-ts</c> or <c>dotnet</c>.</param>
/// <param name="File">Where it is defined, for problems found in its definition.</param>
/// <param name="Class">dotnet: the class to run, by its full name.</param>
public sealed record BuiltFunction(string Name, string Bundle, JsonNode? Definition,
    string Runtime = FunctionBuilder.TypeScript, string? File = null, string? Class = null);

/// <param name="Assembly">The repository's C# compiled, base64: one assembly every C# function of the build runs from.</param>
/// <param name="Pdb">Its portable PDB, base64: a function's errors name their file and line with it.</param>
public sealed record BuildResult(bool Ok, IReadOnlyList<BuildDiagnostic> Diagnostics, IReadOnlyList<BuiltFunction> Functions,
    string? Assembly = null, string? Pdb = null)
{
    public static BuildResult Failed(params BuildDiagnostic[] diagnostics) => new(false, diagnostics, []);
}

/// <summary>
/// Turns a Functions repository into one bundle per function (ADR 0029): type-checked against
/// the environment's SDK by <c>deno check</c>, bundled by esbuild with <c>@dcms/functions</c>
/// replaced by the static runtime, then each bundle imported once in the sandbox to read its
/// definition.
///
/// <para>Neither the checker nor the bundler runs the tenant's code, but both read files, and a
/// module specifier is a path. So a function may import only its repository's own files and
/// <c>@dcms/functions</c>: specifiers are checked before either tool starts, and esbuild's
/// metafile is checked after, so a file outside the build directory never ends up in a bundle.
/// This host holds nothing secret to begin with; these checks keep it that way for its files too.</para>
/// </summary>
public sealed partial class FunctionBuilder(IOptions<ScriptRunnerOptions> options, ILogger<FunctionBuilder> logger)
{
    public const string FunctionsDirectory = "functions";
    public const string SdkModule = "@dcms/functions";
    public const string TypeScript = "deno-ts";
    public const string Dotnet = "dotnet";

    private readonly ScriptRunnerOptions _o = options.Value;

    // Builds are rare and the type checker is the largest thing this host runs: one at a time.
    private readonly SemaphoreSlim _gate = new(1);

    [GeneratedRegex(@"^(?:[A-Za-z0-9_][A-Za-z0-9_\-.]*/){0,9}[A-Za-z0-9_][A-Za-z0-9_\-.]*$")]
    private static partial Regex SafePath();

    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,62}$")]
    private static partial Regex FunctionName();

    // Static imports and re-exports, side-effect imports, and dynamic imports of a literal.
    [GeneratedRegex("""(?:\b(?:import|export)\s[^'"`;]*?\bfrom\s*|\bimport\s*\(?\s*)(['"])(?<spec>[^'"\n]+)\1""")]
    private static partial Regex ImportSpecifier();

    [GeneratedRegex(@"\brequire\s*\(")]
    private static partial Regex Require();

    [GeneratedRegex(@"(?<code>TS\d+) \[ERROR\]: (?<msg>[^\n]*)(?:(?!TS\d+ \[ERROR\]).)*?\bat (?<file>file:///\S+?):(?<line>\d+):(?<col>\d+)", RegexOptions.Singleline)]
    private static partial Regex DenoDiagnostic();

    [GeneratedRegex(@"\[ERROR\] (?<msg>[^\n]+)\n\s*\n\s*(?<file>[^\s:]+):(?<line>\d+):(?<col>\d+):")]
    private static partial Regex EsbuildDiagnostic();

    /// <summary>
    /// A repository may mix languages (ADR 0030): its TypeScript and its C# build side by side and
    /// one build holds both, as long as no two functions share a name.
    /// </summary>
    public async Task<BuildResult> BuildAsync(BuildRequest request, CancellationToken ct)
    {
        if (Validate(request) is { } refused)
        {
            return refused;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var ts = await BuildTypeScriptAsync(request, ct);
            var cs = request.Files.Keys.Any(IsCSharp) ? await BuildCSharpAsync(request, ct) : null;
            var diagnostics = ts.Diagnostics.Concat(cs?.Diagnostics ?? []).ToList();
            var functions = ts.Functions.Concat(cs?.Functions ?? []).ToList();
            foreach (var twice in functions.GroupBy(f => f.Name).Where(g => g.Count() > 1))
            {
                diagnostics.Add(new BuildDiagnostic(
                    $"Two functions are called '{twice.Key}': {string.Join(" and ", twice.Select(f => f.File))}.", twice.Last().File));
            }
            if (diagnostics.Count == 0 && functions.Count == 0)
            {
                diagnostics.Add(new BuildDiagnostic(
                    $"There are no functions: each is a file in {FunctionsDirectory}/, such as {FunctionsDirectory}/hello.ts, or a C# class marked [Function(\"name\")]."));
            }
            return diagnostics.Count > 0
                ? new BuildResult(false, diagnostics, [])
                : new BuildResult(true, [], functions, cs?.Assembly, cs?.Pdb);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsCSharp(string path) => path.EndsWith(".cs", StringComparison.Ordinal);

    /// <summary>
    /// The repository's C#, compiled by dcms-csc (ADR 0030) under the same confinement as Deno:
    /// Roslyn reads no file but the request and runs none of the tenant's code, but a crafted
    /// source can still cost it memory and time, which the limits below bound.
    /// </summary>
    private async Task<BuildResult> BuildCSharpAsync(BuildRequest request, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_o.BuildTimeoutMs);
        using var process = Confined.Start(_o.DotnetPath, [_o.CSharpCompilerPath],
            _o.CSharpDataLimitMegabytes * 1024L * 1024,
            new Dictionary<string, string>
            {
                // The GC's own ceiling, under the kernel's: it collects harder instead of being killed.
                ["DOTNET_GCHeapHardLimit"] = $"0x{(_o.CSharpDataLimitMegabytes - 256L) * 1024 * 1024:x}",
                ["DOTNET_gcServer"] = "0",
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            });
        var stdout = Confined.ReadBoundedAsync(process.StandardOutput, 3 * _o.MaxBundleChars, () => Confined.Kill(process));
        _ = Confined.ReadBoundedAsync(process.StandardError, 4_000, onOverflow: null);
        try
        {
            await process.StandardInput.WriteAsync(new JsonObject
            {
                ["files"] = new JsonObject(request.Files.Where(f => IsCSharp(f.Key))
                    .Select(f => KeyValuePair.Create<string, JsonNode?>(f.Key, f.Value))),
                ["sdk"] = request.SdkCSharp ?? "",
            }.ToJsonString().AsMemory(), deadline.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Confined.Kill(process);
            return BuildResult.Failed(new BuildDiagnostic($"Compiling the C# took longer than {_o.BuildTimeoutMs / 1000} s."));
        }
        catch (IOException)
        {
            // It ended before reading its input: its exit code says why, below.
        }
        var output = await stdout;
        var result = process.ExitCode == 0 ? JsonNode.Parse(output) as JsonObject : null;
        if (result is null)
        {
            // The exit code only: its stderr can quote the code it was compiling.
            logger.LogWarning("dcms-csc exited with {ExitCode}", process.ExitCode);
            return BuildResult.Failed(new BuildDiagnostic(process.ExitCode is 134 or 137 || output.Length == 0
                ? "Compiling the C# ran out of memory or failed; try again, and if it keeps failing, split up the largest files."
                : "Compiling the C# failed."));
        }
        var diagnostics = (result["diagnostics"] as JsonArray ?? [])
            .Select(d => new BuildDiagnostic(
                d?["message"]?.GetValue<string>() ?? "",
                d?["file"]?.GetValue<string>() is { } file && InRepository(file) ? file : null,
                d?["line"]?.GetValue<int>(), d?["column"]?.GetValue<int>(), d?["code"]?.GetValue<string>()))
            .ToList();
        if (diagnostics.Count > 0 || result["ok"]?.GetValue<bool>() != true)
        {
            return new BuildResult(false, diagnostics.Count > 0 ? diagnostics : [new BuildDiagnostic("Compiling the C# failed.")], []);
        }
        var assembly = result["assembly"]?.GetValue<string>();
        var pdb = result["pdb"]?.GetValue<string>();
        if ((assembly?.Length ?? 0) + (pdb?.Length ?? 0) > _o.MaxBundleChars)
        {
            return BuildResult.Failed(new BuildDiagnostic($"The compiled C# is at most {_o.MaxBundleChars:N0} characters."));
        }
        var functions = (result["functions"] as JsonArray ?? [])
            .Select(f => new BuiltFunction(f!["name"]!.GetValue<string>(), "", f["definition"]?.DeepClone(), Dotnet,
                f["file"]?.GetValue<string>(), f["class"]?.GetValue<string>()))
            .ToList();
        return new BuildResult(true, [], functions, assembly, pdb);
    }

    /// <summary>The repository's TypeScript; nothing at all when it has no <c>functions/*.ts</c>.</summary>
    private async Task<BuildResult> BuildTypeScriptAsync(BuildRequest request, CancellationToken ct)
    {
        var root = Directory.CreateTempSubdirectory("fn-build-");
        try
        {
            var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var diagnostics = new List<BuildDiagnostic>();
            foreach (var (path, content) in request.Files)
            {
                if (!(path.EndsWith(".ts", StringComparison.Ordinal) || path.EndsWith(".js", StringComparison.Ordinal)))
                {
                    continue;
                }
                diagnostics.AddRange(CheckImports(root.FullName, path, content));
                var full = Path.Combine(root.FullName, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllTextAsync(full, content, ct);

                var parts = path.Split('/');
                if (parts.Length == 2 && parts[0] == FunctionsDirectory && path.EndsWith(".ts", StringComparison.Ordinal)
                    && !path.EndsWith(".d.ts", StringComparison.Ordinal))
                {
                    var name = parts[1][..^3];
                    if (FunctionName().IsMatch(name))
                    {
                        entries[name] = path;
                    }
                    else
                    {
                        diagnostics.Add(new BuildDiagnostic(
                            "A function's file name is its name: lowercase letters, digits and dashes, starting with a letter.", path));
                    }
                }
            }
            if (diagnostics.Count == 0 && entries.Count == 0)
            {
                return new BuildResult(true, [], []);
            }
            if (diagnostics.Count > 0)
            {
                return new BuildResult(false, diagnostics, []);
            }

            var sdk = Path.Combine(root.FullName, ".dcms");
            Directory.CreateDirectory(sdk);
            await File.WriteAllTextAsync(Path.Combine(sdk, "functions.ts"), request.SdkTypes, ct);
            File.Copy(_o.RuntimePath, Path.Combine(sdk, "runtime.js"));
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "deno.json"), new JsonObject
            {
                ["imports"] = new JsonObject { [SdkModule] = "./.dcms/functions.ts" },
                ["compilerOptions"] = new JsonObject { ["strict"] = true },
            }.ToJsonString(), ct);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(_o.BuildTimeoutMs);
            try
            {
                foreach (var entry in entries.Values)
                {
                    diagnostics.AddRange(await ContainAsync(root.FullName, entry, deadline.Token));
                }
                if (diagnostics.Count > 0)
                {
                    return new BuildResult(false, diagnostics, []);
                }
                var check = await CheckAsync(root.FullName, entries.Values, deadline.Token);
                if (check.Count > 0)
                {
                    return new BuildResult(false, check, []);
                }

                var functions = new List<BuiltFunction>();
                foreach (var (name, entry) in entries)
                {
                    var (bundle, problems) = await BundleAsync(root.FullName, name, entry, deadline.Token);
                    if (bundle is null)
                    {
                        diagnostics.AddRange(problems);
                        continue;
                    }
                    var (definition, error) = await DescribeAsync(bundle, deadline.Token);
                    if (error is not null)
                    {
                        diagnostics.Add(new BuildDiagnostic(error, entry));
                        continue;
                    }
                    functions.Add(new BuiltFunction(name, bundle, definition, TypeScript, entry));
                }
                return diagnostics.Count > 0 ? new BuildResult(false, diagnostics, []) : new BuildResult(true, [], functions);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return BuildResult.Failed(new BuildDiagnostic($"The build took longer than {_o.BuildTimeoutMs / 1000} s."));
            }
        }
        finally
        {
            try
            {
                root.Delete(recursive: true);
            }
            catch (IOException e)
            {
                logger.LogWarning(e, "Could not delete {Build}", root.FullName);
            }
        }
    }

    private BuildResult? Validate(BuildRequest request)
    {
        if (request.Files is null || request.Files.Count == 0)
        {
            return BuildResult.Failed(new BuildDiagnostic("The repository is empty."));
        }
        if (request.Files.Count > _o.MaxBuildFiles)
        {
            return BuildResult.Failed(new BuildDiagnostic($"A build takes at most {_o.MaxBuildFiles:N0} files."));
        }
        if (request.Files.Sum(f => (long)f.Value.Length) + (request.SdkTypes?.Length ?? 0) + (request.SdkCSharp?.Length ?? 0) > _o.MaxBuildBytes)
        {
            return BuildResult.Failed(new BuildDiagnostic($"A build takes at most {_o.MaxBuildBytes / (1024 * 1024)} MB of code."));
        }
        var unsafePaths = request.Files.Keys.Where(p => p.Length > 200 || !SafePath().IsMatch(p) || p.Split('/').Any(s => s.StartsWith('.'))).ToList();
        return unsafePaths.Count > 0
            ? BuildResult.Failed(unsafePaths.Select(p => new BuildDiagnostic(
                "A path may hold letters, digits, '-', '_' and '.', in at most ten folders, none of them hidden.", p)).ToArray())
            : null;
    }

    /// <summary>Every specifier must be <c>@dcms/functions</c> or a relative path that stays inside the repository.</summary>
    private static IEnumerable<BuildDiagnostic> CheckImports(string root, string path, string content)
    {
        var directory = Path.GetDirectoryName(Path.Combine(root, path))!;
        // esbuild bundles require() calls, which Deno's module graph (ContainAsync) does not list.
        foreach (Match match in Require().Matches(content))
        {
            yield return new BuildDiagnostic("require() is not available: use import.", path, content.AsSpan(0, match.Index).Count('\n') + 1);
        }
        foreach (Match match in ImportSpecifier().Matches(content))
        {
            var spec = match.Groups["spec"].Value;
            if (spec == SdkModule)
            {
                continue;
            }
            var relative = spec.StartsWith("./", StringComparison.Ordinal) || spec.StartsWith("../", StringComparison.Ordinal);
            if (relative && Path.GetFullPath(Path.Combine(directory, spec)).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }
            var line = content.AsSpan(0, match.Index).Count('\n') + 1;
            yield return new BuildDiagnostic(relative
                    ? $"'{spec}' is outside the repository."
                    : $"'{spec}' cannot be imported: a function imports only \"{SdkModule}\" and its repository's own files (./…).",
                path, line);
        }
    }

    /// <summary>
    /// Deno's own module graph of one entry must stay inside the build directory. The specifier
    /// check above reads import statements; TypeScript reaches files other ways too —
    /// <c>/// &lt;reference path&gt;</c>, <c>@ts-types</c> pragmas — and <c>deno check</c> follows
    /// them and quotes what it cannot parse. So the graph Deno resolves is the authority, and
    /// nothing is type-checked until every module in it is the repository's or the SDK's.
    /// </summary>
    private async Task<List<BuildDiagnostic>> ContainAsync(string root, string entry, CancellationToken ct)
    {
        var cache = Directory.CreateTempSubdirectory("deno-");
        try
        {
            var (exit, _, output) = await RunAsync(_o.DenoPath,
                ["info", "--json", "--no-remote", "--no-npm", "--no-lock", "--config", "deno.json", entry],
                _o.BuildDataLimitMegabytes, root, new Dictionary<string, string> { ["DENO_DIR"] = cache.FullName }, ct);
            var prefix = new Uri(root + Path.DirectorySeparatorChar).AbsoluteUri;
            JsonDocument graph;
            try
            {
                graph = JsonDocument.Parse(output.TrimEnd());
            }
            catch (JsonException)
            {
                // Its error text could quote whatever it failed on: none of it is passed on.
                return [new BuildDiagnostic($"The function's imports could not be resolved (exit {exit}).", entry)];
            }
            using (graph)
            {
                var problems = new List<BuildDiagnostic>();
                foreach (var module in graph.RootElement.GetProperty("modules").EnumerateArray())
                {
                    if (!module.TryGetProperty("dependencies", out var dependencies)
                        || module.GetProperty("specifier").GetString() is not { } from || !from.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    foreach (var dependency in dependencies.EnumerateArray())
                    {
                        foreach (var kind in (string[])["code", "type"])
                        {
                            if (dependency.TryGetProperty(kind, out var resolved)
                                && resolved.TryGetProperty("specifier", out var to)
                                && !(to.GetString() ?? "").StartsWith(prefix, StringComparison.Ordinal))
                            {
                                var line = resolved.TryGetProperty("span", out var span) ? span.GetProperty("start").GetProperty("line").GetInt32() + 1 : (int?)null;
                                problems.Add(new BuildDiagnostic(
                                    $"'{dependency.GetProperty("specifier").GetString()}' is outside the repository: a function reaches only \"{SdkModule}\" and its repository's own files.",
                                    from[prefix.Length..], line));
                            }
                        }
                    }
                }
                // Anything listed at all must be the repository's: a dependency kind not read above still shows here.
                var outside = graph.RootElement.GetProperty("modules").EnumerateArray()
                    .Select(m => m.GetProperty("specifier").GetString() ?? "")
                    .Any(specifier => !specifier.StartsWith(prefix, StringComparison.Ordinal));
                if (outside && problems.Count == 0)
                {
                    problems.Add(new BuildDiagnostic("The function reaches a file outside the repository.", entry));
                }
                return problems;
            }
        }
        finally
        {
            try
            {
                cache.Delete(recursive: true);
            }
            catch (IOException)
            {
                // A temp directory; the next deploy of this host clears it.
            }
        }
    }

    private async Task<List<BuildDiagnostic>> CheckAsync(string root, IEnumerable<string> entries, CancellationToken ct)
    {
        var cache = Directory.CreateTempSubdirectory("deno-");
        try
        {
            var (exit, output, _) = await RunAsync(_o.DenoPath,
                ["check", "--no-remote", "--no-npm", "--no-lock", "--config", "deno.json",
                 $"--v8-flags=--max-old-space-size={_o.BuildHeapMegabytes}", .. entries],
                _o.BuildDataLimitMegabytes, root, new Dictionary<string, string> { ["DENO_DIR"] = cache.FullName }, ct);
            if (exit == 0)
            {
                return [];
            }
            var prefix = new Uri(root + Path.DirectorySeparatorChar).AbsoluteUri;
            var found = DenoDiagnostic().Matches(output).Select(m => new BuildDiagnostic(
                m.Groups["msg"].Value.Trim(),
                m.Groups["file"].Value.Replace(prefix, "", StringComparison.Ordinal),
                int.Parse(m.Groups["line"].Value),
                int.Parse(m.Groups["col"].Value),
                m.Groups["code"].Value)).ToList();
            // Only what was parsed, in the repository's own files: raw tool output is never passed on.
            found.RemoveAll(d => !InRepository(d.File));
            return found.Count > 0 ? found : [new BuildDiagnostic($"The type check failed (exit {exit}).")];
        }
        finally
        {
            try
            {
                cache.Delete(recursive: true);
            }
            catch (IOException)
            {
                // A temp directory; the next deploy of this host clears it.
            }
        }
    }

    private async Task<(string? Bundle, List<BuildDiagnostic> Problems)> BundleAsync(string root, string name, string entry, CancellationToken ct)
    {
        var outFile = $".out/{name}.js";
        var metaFile = $".out/{name}.meta.json";
        var (exit, output, _) = await RunAsync(_o.EsbuildPath,
            [entry, "--bundle", "--format=esm", "--platform=neutral", "--target=es2022", "--charset=utf8",
             "--legal-comments=none", "--log-level=error", $"--alias:{SdkModule}=./.dcms/runtime.js",
             $"--outfile={outFile}", $"--metafile={metaFile}"],
            1_024, root, null, ct);
        if (exit != 0)
        {
            var found = EsbuildDiagnostic().Matches(output).Select(m => new BuildDiagnostic(
                m.Groups["msg"].Value.Trim(), m.Groups["file"].Value,
                int.Parse(m.Groups["line"].Value), int.Parse(m.Groups["col"].Value))).ToList();
            found.RemoveAll(d => !InRepository(d.File));
            return (null, found.Count > 0 ? found : [new BuildDiagnostic($"Bundling failed (exit {exit}).", entry)]);
        }

        using var meta = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, metaFile), ct));
        var outside = meta.RootElement.GetProperty("inputs").EnumerateObject()
            .Select(input => input.Name)
            .Where(input => !Path.GetFullPath(Path.Combine(root, input)).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToList();
        if (outside.Count > 0)
        {
            return (null, [new BuildDiagnostic("The function reaches a file outside the repository.", entry)]);
        }
        var bundle = await File.ReadAllTextAsync(Path.Combine(root, outFile), ct);
        return bundle.Length > _o.MaxBundleChars
            ? (null, [new BuildDiagnostic($"Bundled, the function is {bundle.Length:N0} characters; at most {_o.MaxBundleChars:N0}.", entry)])
            : (bundle, []);
    }

    /// <summary>Imports the bundle in the sandbox, as a run would, and reads what <c>defineFunction</c> was given.</summary>
    private async Task<(JsonNode? Definition, string? Error)> DescribeAsync(string bundle, CancellationToken ct)
    {
        var cache = Directory.CreateTempSubdirectory("deno-");
        Process? process = null;
        try
        {
            process = Confined.Start(_o.DenoPath,
                ["run", "--no-prompt", "--no-remote", "--no-npm", "--no-config", "--no-lock",
                 $"--v8-flags=--max-old-space-size={_o.HeapMegabytes}", _o.SessionHarnessPath],
                (_o.DataLimitMegabytes + 2L * _o.HeapMegabytes) * 1024 * 1024,
                new Dictionary<string, string> { ["DENO_DIR"] = cache.FullName });
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var stdout = Confined.ReadBoundedAsync(process.StandardOutput, _o.MaxResultChars + 1_024, () => Confined.Kill(process));
            _ = Confined.ReadBoundedAsync(process.StandardError, 4_000, onOverflow: null);
            try
            {
                await process.StandardInput.WriteAsync(
                    (new JsonObject { ["type"] = "start", ["mode"] = "describe", ["bundle"] = bundle }.ToJsonString() + "\n").AsMemory(), deadline.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (null, "Loading the function took longer than 5 s. Code outside its handler runs at load time and may not call the SDK.");
            }
            catch (IOException)
            {
                // Ended before taking its input; no done line follows.
            }

            var done = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => { try { return JsonNode.Parse(line) as JsonObject; } catch (JsonException) { return null; } })
                .LastOrDefault(m => m?["type"]?.GetValue<string>() == "done");
            if (done is null)
            {
                return (null, "The function could not be loaded.");
            }
            return done["ok"]?.GetValueKind() == JsonValueKind.True
                ? (done["result"]?.DeepClone(), null)
                : (null, done["error"]?.GetValue<string>() ?? "The function could not be loaded.");
        }
        finally
        {
            if (process is { HasExited: false })
            {
                Confined.Kill(process);
            }
            process?.Dispose();
            try
            {
                cache.Delete(recursive: true);
            }
            catch (IOException)
            {
                // A temp directory; the next deploy of this host clears it.
            }
        }
    }

    private static async Task<(int Exit, string Output, string Stdout)> RunAsync(string program, IEnumerable<string> args, int dataLimitMegabytes,
        string root, IReadOnlyDictionary<string, string>? environment, CancellationToken ct)
    {
        using var process = Confined.Start(program, args, dataLimitMegabytes * 1024L * 1024, environment, root);
        process.StandardInput.Close();
        var stdout = Confined.ReadBoundedAsync(process.StandardOutput, 64_000, onOverflow: null);
        var stderr = Confined.ReadBoundedAsync(process.StandardError, 64_000, onOverflow: null);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        finally
        {
            if (!process.HasExited)
            {
                Confined.Kill(process);
            }
        }
        var output = await stdout;
        return (process.ExitCode, output + "\n" + (await stderr), output);
    }

    /// <summary>A diagnostic may be passed on only if it is about the repository's own files: one about any other file can quote it.</summary>
    private static bool InRepository(string? file) =>
        file is not null && SafePath().IsMatch(file) && !file.Split('/').Any(s => s.StartsWith('.'));
}
