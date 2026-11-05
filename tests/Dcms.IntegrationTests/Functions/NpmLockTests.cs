using Dcms.Plugins.Functions.Packages;

namespace Dcms.IntegrationTests.Functions;

/// <summary>package.json + package-lock.json (ADR 0031): what a build installs, or why it will not.</summary>
public sealed class NpmLockTests
{
    private const string Integrity = "sha512-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==";

    private static Dictionary<string, string> Repo(string dependencies, string lockDependencies, string entries) => new()
    {
        ["package.json"] = $$"""{ "name": "fns", "dependencies": {{dependencies}} }""",
        ["package-lock.json"] = $$"""
            { "lockfileVersion": 3, "packages": {
              "": { "name": "fns", "dependencies": {{lockDependencies}} },
              {{entries}}
            } }
            """,
    };

    private static string Entry(string path, string url, string extra = "") =>
        $$"""
        "{{path}}": { "version": "1.0.0", "resolved": "{{url}}", "integrity": "{{Integrity}}"{{extra}} }
        """;

    [Fact]
    public void Installs_every_locked_runtime_package_and_skips_dev_and_platform_binaries()
    {
        var (packages, problems) = NpmLock.Read(Repo("""{ "zod": "^4" }""", """{ "zod": "^4" }""", string.Join(",",
            Entry("node_modules/zod", "https://registry.npmjs.org/zod/-/zod-4.0.0.tgz"),
            Entry("node_modules/@scope/inner", "https://registry.npmjs.org/@scope/inner/-/inner-1.0.0.tgz"),
            Entry("node_modules/zod/node_modules/nested", "https://registry.npmjs.org/nested/-/nested-1.0.0.tgz"),
            Entry("node_modules/typescript", "https://registry.npmjs.org/typescript/-/typescript-5.0.0.tgz", ", \"dev\": true"),
            Entry("node_modules/@esbuild/linux-x64", "https://registry.npmjs.org/@esbuild/linux-x64/-/linux-x64-0.1.0.tgz", ", \"optional\": true, \"os\": [\"linux\"]"))));

        problems.Should().BeEmpty();
        packages.Select(p => (p.Path, p.Name)).Should().BeEquivalentTo(new[]
        {
            ("node_modules/zod", "zod"), ("node_modules/@scope/inner", "@scope/inner"), ("node_modules/zod/node_modules/nested", "nested"),
        });
    }

    [Fact]
    public void A_repository_without_dependencies_installs_nothing()
    {
        var (packages, problems) = NpmLock.Read(new Dictionary<string, string> { ["functions/a.ts"] = "" });
        packages.Should().BeEmpty();
        problems.Should().BeEmpty();
        NpmLock.Read(new Dictionary<string, string> { ["package.json"] = """{ "devDependencies": { "x": "1" } }""" }).Problems.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{ "zod": "^4" }""", null, "there is no package-lock.json")]
    [InlineData("""{ "zod": "^4" }""", """{ "zod": "^3" }""", "does not match package.json for zod")]
    [InlineData("""{ "zod": "^4", "ky": "1" }""", """{ "zod": "^4" }""", "for ky")]
    public void A_missing_or_stale_lockfile_is_refused(string declared, string? locked, string problem)
    {
        var repo = Repo(declared, locked ?? "{}", Entry("node_modules/zod", "https://registry.npmjs.org/zod/-/zod-4.0.0.tgz"));
        if (locked is null) repo.Remove("package-lock.json");
        NpmLock.Read(repo).Problems.Should().ContainSingle().Which.Should().Contain(problem);
    }

    [Theory]
    [InlineData("git+https://github.com/x/zod.git", "", "does not come from the npm registry")]
    [InlineData("https://evil.example/zod-4.0.0.tgz", "", "does not come from the npm registry")]
    [InlineData("https://registry.npmjs.org/zod/-/zod-4.0.0.tgz", ", \"link\": true", "is a link to a folder")]
    public void Only_published_packages_from_the_registry_are_installed(string resolved, string extra, string problem)
    {
        NpmLock.Read(Repo("""{ "zod": "^4" }""", """{ "zod": "^4" }""", Entry("node_modules/zod", resolved, extra)))
            .Problems.Should().ContainSingle().Which.Should().Contain(problem);
    }

    [Fact]
    public void A_weak_or_missing_integrity_is_refused()
    {
        var repo = Repo("""{ "zod": "^4" }""", """{ "zod": "^4" }""",
            Entry("node_modules/zod", "https://registry.npmjs.org/zod/-/zod-4.0.0.tgz").Replace(Integrity, "sha1-AAAAAAAAAAAAAAAAAAAAAAAAAAA="));
        NpmLock.Read(repo).Problems.Should().ContainSingle().Which.Should().Contain("no sha256, sha384 or sha512 integrity");
    }

    [Fact]
    public void Integrity_is_checked_against_the_bytes()
    {
        var bytes = "tarball"u8.ToArray();
        var good = "sha512-" + Convert.ToBase64String(System.Security.Cryptography.SHA512.HashData(bytes));
        Dcms.Plugins.Functions.Packages.Integrity.Matches(good, bytes).Should().BeTrue();
        Dcms.Plugins.Functions.Packages.Integrity.Matches(good, "other"u8).Should().BeFalse();
        Dcms.Plugins.Functions.Packages.Integrity.Key(good).Should().StartWith("sha512/");
    }
}
