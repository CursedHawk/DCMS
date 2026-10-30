using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dcms.Plugins.Functions.Build;

/// <summary>What a build made of some files: functions and their bundles, or every problem found.</summary>
public sealed record BuildOutput(
    bool Ok, IReadOnlyList<BuildJob.Diagnostic> Diagnostics, IReadOnlyList<FunctionSpec> Functions, IReadOnlyDictionary<string, string> Bundles);

/// <summary>
/// Builds files on script-runner against this environment's SDK and reads each function's
/// definition into a manifest (ADR 0029): what a deployment is made of, and what the editor's
/// and the assistant's checks ask for without making one.
/// </summary>
public sealed class Builder(IHttpClientFactory http, SdkTypings sdk)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<BuildOutput> BuildAsync(IReadOnlyDictionary<string, string> files, CancellationToken ct)
    {
        using var response = await http.CreateClient(BuildJob.HttpClientName)
            .PostAsJsonAsync("/build", new { files, sdkTypes = await sdk.GenerateAsync(ct) }, Json, ct);
        response.EnsureSuccessStatusCode();
        var built = await response.Content.ReadFromJsonAsync<Response>(Json, ct)
                    ?? throw new HttpRequestException("script-runner returned no build result.");
        if (!built.Ok)
        {
            return new BuildOutput(false, built.Diagnostics, [], new Dictionary<string, string>());
        }
        var (functions, problems) = FunctionManifest.Read(built.Functions.ToDictionary(f => f.Name, f => f.Definition));
        return problems.Count > 0
            ? new BuildOutput(false, problems.Select(p => new BuildJob.Diagnostic(p.Message, $"functions/{p.Function}.ts")).ToList(), [], new Dictionary<string, string>())
            : new BuildOutput(true, [], functions, built.Functions.ToDictionary(f => f.Name, f => f.Bundle));
    }

    private sealed record Built(string Name, string Bundle, JsonNode? Definition);

    private sealed record Response(bool Ok, List<BuildJob.Diagnostic> Diagnostics, List<Built> Functions);
}
