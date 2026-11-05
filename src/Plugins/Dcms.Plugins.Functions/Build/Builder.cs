using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Plugins.Functions.Packages;

namespace Dcms.Plugins.Functions.Build;

/// <summary>What a build made of some files: functions and their bundles, or every problem found.</summary>
/// <param name="Bundles">
/// What <c>functions.deployments.bundles</c> holds: each TypeScript function's bundle by name; each
/// C# function's class by name, and the build's one assembly and PDB under <see cref="DotnetAssembly"/>
/// and <see cref="DotnetPdb"/> (ADR 0030). Function names cannot start with '@'.
/// </param>
public sealed record BuildOutput(
    bool Ok, IReadOnlyList<BuildJob.Diagnostic> Diagnostics, IReadOnlyList<FunctionSpec> Functions, IReadOnlyDictionary<string, string> Bundles)
{
    public const string DotnetAssembly = "@dotnet.dll";
    public const string DotnetPdb = "@dotnet.pdb";

    /// <summary>A NuGet package's assembly the C# references (ADR 0031): <c>@nuget/Newtonsoft.Json.dll</c>.</summary>
    public const string NuGetPrefix = "@nuget/";
}

/// <summary>
/// Builds files on script-runner against this environment's SDK and reads each function's
/// definition into a manifest (ADR 0029): what a deployment is made of, and what the editor's
/// and the assistant's checks ask for without making one. A repository's TypeScript and C#
/// (ADR 0030) build together, each against its half of the SDK.
/// </summary>
public sealed class Builder(IHttpClientFactory http, SdkTypings sdk, PackageStore store)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>What one build may install in all (ADR 0031).</summary>
    public const long MaxPackagesBytes = 60L * 1024 * 1024;

    public async Task<BuildOutput> BuildAsync(IReadOnlyDictionary<string, string> files, CancellationToken ct)
    {
        var (packages, unusable) = await PackagesAsync(files, ct);
        if (unusable.Count > 0)
        {
            return new BuildOutput(false, unusable, [], new Dictionary<string, string>());
        }
        var model = await sdk.ModelAsync(ct);
        var csharp = files.Keys.Any(f => f.EndsWith(".cs", StringComparison.Ordinal)) ? SdkTypings.CSharp(model) : null;
        using var response = await http.CreateClient(BuildJob.HttpClientName)
            .PostAsJsonAsync("/build", new { files, sdkTypes = SdkTypings.TypeScript(model), sdkCSharp = csharp, packages }, Json, ct);
        response.EnsureSuccessStatusCode();
        var built = await response.Content.ReadFromJsonAsync<Response>(Json, ct)
                    ?? throw new HttpRequestException("script-runner returned no build result.");
        if (!built.Ok)
        {
            return new BuildOutput(false, built.Diagnostics, [], new Dictionary<string, string>());
        }
        var (functions, problems) = FunctionManifest.Read(built.Functions.ToDictionary(f => f.Name, f => f.Definition));
        var byName = built.Functions.ToDictionary(f => f.Name);
        if (problems.Count > 0)
        {
            return new BuildOutput(false, problems.Select(p => new BuildJob.Diagnostic(p.Message,
                byName.TryGetValue(p.Function, out var f) ? f.File : null)).ToList(), [], new Dictionary<string, string>());
        }
        var bundles = built.Functions.ToDictionary(f => f.Name, f => f.Runtime == FunctionManifest.Dotnet ? f.Class ?? "" : f.Bundle);
        // A TypeScript function's source map, beside it: function names have no '.' (ADR 0029).
        foreach (var f in built.Functions.Where(f => f.SourceMap is not null))
        {
            bundles[f.Name + ".map"] = f.SourceMap!;
        }
        if (built.Assembly is not null)
        {
            bundles[BuildOutput.DotnetAssembly] = built.Assembly;
            bundles[BuildOutput.DotnetPdb] = built.Pdb ?? "";
            foreach (var (name, assembly) in built.Packages ?? [])
            {
                bundles[BuildOutput.NuGetPrefix + name] = assembly;
            }
        }
        return new BuildOutput(true, [], functions.Select(f => f with
        {
            Runtime = byName[f.Name].Runtime,
            File = byName[f.Name].File,
            Input = FunctionManifest.Schema(byName[f.Name].Input),
            Output = FunctionManifest.Schema(byName[f.Name].Output),
        }).ToList(), bundles);
    }

    /// <summary>The locked packages' archives, for script-runner to unpack offline into the build.</summary>
    private async Task<(List<object> Packages, List<BuildJob.Diagnostic> Problems)> PackagesAsync(
        IReadOnlyDictionary<string, string> files, CancellationToken ct)
    {
        var (npm, npmRefused) = NpmLock.Read(files);
        var (nuget, nugetRefused) = NuGetLock.Read(files);
        if (npmRefused.Count > 0 || nugetRefused.Count > 0)
        {
            return ([], npmRefused.Select(p => new BuildJob.Diagnostic(p, NpmLock.Lockfile))
                .Concat(nugetRefused.Select(p => new BuildJob.Diagnostic(p, NuGetLock.Lockfile))).ToList());
        }
        var locked = npm.Concat(nuget).ToList();
        var problems = new List<BuildJob.Diagnostic>();
        // The total is enforced as archives arrive, not after: past it, what is still fetching is cancelled.
        long total = 0;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var gate = new SemaphoreSlim(8);
        var fetched = await Task.WhenAll(locked.Select(async p =>
        {
            try
            {
                await gate.WaitAsync(budget.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (p, Bytes: Array.Empty<byte>(), Error: (string?)null);
            }
            try
            {
                var bytes = await store.GetAsync(p.Resolved, p.Integrity, budget.Token);
                if (Interlocked.Add(ref total, bytes.Length) > MaxPackagesBytes)
                {
                    await budget.CancelAsync();
                    return (p, Bytes: Array.Empty<byte>(), Error: null);
                }
                return (p, Bytes: bytes, Error: null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (p, Bytes: Array.Empty<byte>(), Error: null);
            }
            catch (Exception e) when (e is PackageException or HttpRequestException)
            {
                return (p, Bytes: Array.Empty<byte>(), Error: $"{p.Name}@{p.Version}: {e.Message}");
            }
            finally
            {
                gate.Release();
            }
        }));
        if (Interlocked.Read(ref total) > MaxPackagesBytes)
        {
            return ([], [new BuildJob.Diagnostic($"A build installs at most {MaxPackagesBytes / (1024 * 1024)} MB of packages.", NpmLock.Lockfile)]);
        }
        foreach (var (p, _, error) in fetched.Where(f => f.Error is not null))
        {
            problems.Add(new BuildJob.Diagnostic(error!, p.Path.StartsWith("nuget/", StringComparison.Ordinal) ? NuGetLock.Lockfile : NpmLock.Lockfile));
        }
        var archives = problems.Count > 0
            ? new List<object>()
            : fetched.Select(f => (object)new { path = f.p.Path, data = Convert.ToBase64String(f.Bytes) }).ToList();
        return problems.Count > 0 ? ([], problems) : (archives, problems);
    }

    private sealed record Built(string Name, string Bundle, JsonNode? Definition, string Runtime = FunctionManifest.Runtime,
        string? File = null, string? Class = null, string? SourceMap = null, JsonNode? Input = null, JsonNode? Output = null);

    private sealed record Response(bool Ok, List<BuildJob.Diagnostic> Diagnostics, List<Built> Functions, string? Assembly, string? Pdb,
        Dictionary<string, string>? Packages = null);
}
