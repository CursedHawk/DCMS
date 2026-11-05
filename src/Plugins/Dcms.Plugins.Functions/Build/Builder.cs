using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

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
}

/// <summary>
/// Builds files on script-runner against this environment's SDK and reads each function's
/// definition into a manifest (ADR 0029): what a deployment is made of, and what the editor's
/// and the assistant's checks ask for without making one. A repository's TypeScript and C#
/// (ADR 0030) build together, each against its half of the SDK.
/// </summary>
public sealed class Builder(IHttpClientFactory http, SdkTypings sdk)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<BuildOutput> BuildAsync(IReadOnlyDictionary<string, string> files, CancellationToken ct)
    {
        var model = await sdk.ModelAsync(ct);
        var csharp = files.Keys.Any(f => f.EndsWith(".cs", StringComparison.Ordinal)) ? SdkTypings.CSharp(model) : null;
        using var response = await http.CreateClient(BuildJob.HttpClientName)
            .PostAsJsonAsync("/build", new { files, sdkTypes = SdkTypings.TypeScript(model), sdkCSharp = csharp }, Json, ct);
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
        }
        return new BuildOutput(true, [], functions.Select(f => f with
        {
            Runtime = byName[f.Name].Runtime,
            File = byName[f.Name].File,
            Input = FunctionManifest.Schema(byName[f.Name].Input),
            Output = FunctionManifest.Schema(byName[f.Name].Output),
        }).ToList(), bundles);
    }

    private sealed record Built(string Name, string Bundle, JsonNode? Definition, string Runtime = FunctionManifest.Runtime,
        string? File = null, string? Class = null, string? SourceMap = null, JsonNode? Input = null, JsonNode? Output = null);

    private sealed record Response(bool Ok, List<BuildJob.Diagnostic> Diagnostics, List<Built> Functions, string? Assembly, string? Pdb);
}
