extern alias AdminApiApp;

using AdminApiApp::Dcms.AdminApi.ApiClientGen;

namespace Dcms.IntegrationTests.ApiClientGen;

/// <summary>
/// Pins <c>packages/site-template-react/shared/src/api/</c> to what the generator writes today.
///
/// <para>That folder is what the templates are typechecked against in <c>pnpm build</c>. If it
/// could drift from the generator, the typecheck would be proving the templates compile against
/// code no site ever receives. This test fails when they differ, and rewrites the folder when run
/// with <c>DCMS_UPDATE_TEMPLATE_FIXTURE=1</c>.</para>
/// </summary>
public class TemplateFixtureTests
{
    [Fact]
    public void Template_package_fixture_matches_the_generator()
    {
        var root = RepoRoot();
        var fixtureDir = Path.Combine(root, "packages", "site-template-react", "shared", "src", "api");
        // The manifest's fingerprint also hashes src/dcms and openapi.json; pinning it here would
        // fail this test for an edit to the analytics banner, which the fixture does not contain.
        var expected = GeneratedLayer.Build(SampleTenant.Snapshot()).Files
            .Where(f => f.Key.StartsWith("src/api/", StringComparison.Ordinal) && f.Key != GeneratedLayer.ManifestPath)
            .ToDictionary(f => f.Key["src/api/".Length..], f => f.Value);

        if (Environment.GetEnvironmentVariable("DCMS_UPDATE_TEMPLATE_FIXTURE") == "1")
        {
            if (Directory.Exists(fixtureDir))
            {
                Directory.Delete(fixtureDir, recursive: true);
            }
            foreach (var (path, content) in expected)
            {
                var full = Path.Combine(fixtureDir, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
            }
        }

        var actual = Directory.Exists(fixtureDir)
            ? Directory.EnumerateFiles(fixtureDir, "*", SearchOption.AllDirectories)
                .ToDictionary(f => Path.GetRelativePath(fixtureDir, f).Replace('\\', '/'), File.ReadAllText)
            : [];

        var stale = expected.Keys.Union(actual.Keys)
            .Where(k => !expected.TryGetValue(k, out var e) || !actual.TryGetValue(k, out var a) || e != a)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.True(stale.Count == 0,
            $"shared/src/api is out of date with the generator ({string.Join(", ", stale)}). " +
            "Regenerate: DCMS_UPDATE_TEMPLATE_FIXTURE=1 dotnet test tests/Dcms.IntegrationTests --filter FullyQualifiedName~TemplateFixture");
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Dcms.sln")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("Could not find the repository root (Dcms.sln) above " + AppContext.BaseDirectory);
    }
}
