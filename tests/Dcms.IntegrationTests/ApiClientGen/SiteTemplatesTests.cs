extern alias AdminApiApp;

using System.IO.Compression;
using System.Text.RegularExpressions;
using AdminApiApp::Dcms.AdminApi.ApiClientGen;

namespace Dcms.IntegrationTests.ApiClientGen;

/// <summary>
/// The starter templates, as a site receives them.
///
/// <para>What `pnpm build` cannot see: the package typechecks templates in place, while a site is
/// assembled from embedded resources. A file left out of the embed glob compiles in the workspace
/// and is simply missing in every new site — so these read the embedded copy.</para>
/// </summary>
public partial class SiteTemplatesTests
{
    private static readonly GeneratedFiles Generated = GeneratedLayer.Build(SampleTenant.Snapshot());

    public static TheoryData<string> Templates() => new(SiteTemplates.Ids);

    [Theory]
    [MemberData(nameof(Templates))]
    public void Materialises_a_complete_project(string template)
    {
        var files = SiteTemplates.Materialize(template, Generated);

        foreach (var required in new[]
                 {
                     "index.html", "package.json", "tsconfig.json", "vite.config.ts", ".gitignore", ".env.example",
                     "AGENTS.md", "README.md", "src/main.tsx", "src/App.tsx", "src/site.ts", "src/styles.css",
                     "src/styles/tokens.css", "src/styles/base.css", "src/lib/api.ts", "src/lib/useApi.ts",
                     "src/api/index.ts", "src/api/API.md", "src/api/manifest.json", "src/dcms/index.ts", "openapi.json",
                 })
        {
            Assert.True(files.ContainsKey(required), $"{template} is missing {required}");
        }

        // The tenant's own client, not the package's fixture.
        Assert.Equal(Generated.Files["src/api/index.ts"], files["src/api/index.ts"]);
        // A hosted site reaches its API same-origin; a baked-in domain would break on a domain change.
        Assert.False(files.ContainsKey(".env"));
        Assert.Contains("\"vite\": \"6.3.5\"", files["package.json"]);
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void Every_relative_import_resolves_inside_the_site(string template)
    {
        var files = SiteTemplates.Materialize(template, Generated);
        var missing = new List<string>();

        foreach (var (path, content) in files.Where(f => f.Key.StartsWith("src/", StringComparison.Ordinal)))
        {
            var imports = path.EndsWith(".css", StringComparison.Ordinal)
                ? CssImport().Matches(content).Select(m => m.Groups[1].Value)
                : path.EndsWith(".ts", StringComparison.Ordinal) || path.EndsWith(".tsx", StringComparison.Ordinal)
                    ? ModuleImport().Matches(content).Select(m => m.Groups[1].Value)
                    : [];

            foreach (var spec in imports.Where(s => s.StartsWith("./", StringComparison.Ordinal) || s.StartsWith("../", StringComparison.Ordinal)))
            {
                var target = Normalize(Path.GetDirectoryName(path)!.Replace('\\', '/'), spec);
                string[] candidates = [target, $"{target}.ts", $"{target}.tsx", $"{target}/index.ts", $"{target}/index.tsx"];
                if (!candidates.Any(files.ContainsKey))
                {
                    missing.Add($"{path} → {spec}");
                }
            }
        }

        Assert.True(missing.Count == 0, $"{template}: unresolved imports:\n{string.Join('\n', missing)}");
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void Tells_the_agent_what_it_must_not_edit_and_where_to_look(string template)
    {
        var guide = SiteTemplates.Materialize(template, Generated)["AGENTS.md"];

        Assert.Contains("src/api/API.md", guide);
        Assert.Contains("`src/api/`", guide);
        Assert.Contains("`src/dcms/`", guide);
        Assert.Contains("useApi", guide);
        Assert.Contains("tokens.css", guide);
    }

    [Fact]
    public void The_download_is_the_content_template_with_an_env_for_local_development()
    {
        var zip = SiteTemplates.BuildDownload(SampleTenant.Snapshot(), Generated);
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var entries = archive.Entries.ToDictionary(e => e.FullName, e => new StreamReader(e.Open()).ReadToEnd());

        Assert.Contains("src/routes.tsx", entries.Keys);
        Assert.Equal("VITE_API_BASE_URL=https://acme.example\n", entries[".env"]);
    }

    [Fact]
    public void Refuses_a_template_that_does_not_exist()
    {
        Assert.False(SiteTemplates.Exists("../shared"));
        Assert.Throws<ArgumentOutOfRangeException>(() => SiteTemplates.Materialize("starter", Generated));
    }

    private static string Normalize(string dir, string spec)
    {
        var parts = dir.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var part in spec.Split('/'))
        {
            if (part == "..") parts.RemoveAt(parts.Count - 1);
            else if (part != ".") parts.Add(part);
        }
        return string.Join('/', parts);
    }

    [GeneratedRegex("""(?:from|import)\s+['"]([^'"]+)['"]""")]
    private static partial Regex ModuleImport();

    [GeneratedRegex("""@import\s+['"]([^'"]+)['"]""")]
    private static partial Regex CssImport();
}
