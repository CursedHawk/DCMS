extern alias AdminApiApp;

using System.Text.Json;
using AdminApiApp::Dcms.AdminApi.ApiClientGen;

namespace Dcms.IntegrationTests.ApiClientGen;

/// <summary>The DCMS-owned part of a Mode B site: what it contains, and when it counts as changed.</summary>
public class GeneratedLayerTests
{
    [Fact]
    public void Owns_the_client_the_runtime_and_the_document_and_nothing_else()
    {
        var files = GeneratedLayer.Build(SampleTenant.Snapshot()).Files;

        Assert.All(files.Keys, path => Assert.True(GeneratedLayer.Owns(path), path));
        Assert.Contains("src/api/index.ts", files.Keys);
        Assert.Contains("src/api/API.md", files.Keys);
        Assert.Contains("src/api/runtime/index.ts", files.Keys);
        Assert.Contains("src/dcms/analytics.ts", files.Keys);
        Assert.Contains("openapi.json", files.Keys);

        // An author's files are never the generator's to overwrite.
        Assert.False(GeneratedLayer.Owns("src/App.tsx"));
        Assert.False(GeneratedLayer.Owns("src/lib/api.ts"));
        Assert.False(GeneratedLayer.Owns("package.json"));
        Assert.False(GeneratedLayer.Owns("src/apiary.ts"));
    }

    [Fact]
    public void Records_its_fingerprint_and_every_file_in_the_manifest()
    {
        var generated = GeneratedLayer.Build(SampleTenant.Snapshot());
        using var manifest = JsonDocument.Parse(generated.Files[GeneratedLayer.ManifestPath]);

        Assert.Equal(generated.Fingerprint, manifest.RootElement.GetProperty("fingerprint").GetString());
        var listed = manifest.RootElement.GetProperty("files").EnumerateArray().Select(e => e.GetString()!).ToHashSet();
        Assert.Equal(generated.Files.Keys.ToHashSet(), listed);
    }

    [Fact]
    public void Fingerprint_is_stable_for_the_same_tenant_and_moves_when_its_output_does()
    {
        var first = GeneratedLayer.Build(SampleTenant.Snapshot()).Fingerprint;
        var again = GeneratedLayer.Build(SampleTenant.Snapshot()).Fingerprint;
        var renamed = GeneratedLayer.Build(SampleTenant.Snapshot(rename: s => s == "news" ? "journal" : s)).Fingerprint;
        var tagged = GeneratedLayer.Build(SampleTenant.Snapshot(tagging: false)).Fingerprint;

        Assert.Equal(first, again);
        Assert.NotEqual(first, renamed);
        Assert.NotEqual(first, tagged);
    }
}
