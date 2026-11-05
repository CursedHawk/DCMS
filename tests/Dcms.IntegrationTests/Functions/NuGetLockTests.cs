using Dcms.Plugins.Functions.Packages;

namespace Dcms.IntegrationTests.Functions;

/// <summary>Functions.csproj + packages.lock.json (ADR 0031): what a C# build installs, or why it will not.</summary>
public sealed class NuGetLockTests
{
    private static Dictionary<string, string> Repo(string references, string entries) => new()
    {
        ["Functions.csproj"] = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Dcms.Functions.Sdk" Version="1.0.0-sdk.abc" />
                {references}
              </ItemGroup>
            </Project>
            """,
        ["packages.lock.json"] = $$"""
            { "version": 1, "dependencies": { "net10.0": {
              "Dcms.Functions.Sdk": { "type": "Direct", "requested": "[1.0.0-sdk.abc, )", "resolved": "1.0.0-sdk.abc", "contentHash": "x" },
              {{entries}}
            } } }
            """,
    };

    private const string Newtonsoft = """
        "Newtonsoft.Json": { "type": "Direct", "requested": "[13.0.3, )", "resolved": "13.0.3", "contentHash": "HrC5BXdl00IP9zeV+0Z848QWPAoCr9P3bDEZguI+gkLcBKAOxix/tLEAAHC+UvDNPv4a2d18lOReHMOagPa+zQ==" }
        """;

    [Fact]
    public void Installs_every_locked_package_but_the_sdk_from_nuget_org()
    {
        var (packages, problems) = NuGetLock.Read(Repo("""<PackageReference Include="Newtonsoft.Json" Version="13.0.3" />""",
            Newtonsoft + """, "Tiny.Dep": { "type": "Transitive", "resolved": "2.0.0", "contentHash": "AAAA" }"""));

        problems.Should().BeEmpty();
        packages.Select(p => (p.Path, p.Resolved)).Should().BeEquivalentTo(new[]
        {
            ("nuget/newtonsoft.json/13.0.3", "https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg"),
            ("nuget/tiny.dep/2.0.0", "https://api.nuget.org/v3-flatcontainer/tiny.dep/2.0.0/tiny.dep.2.0.0.nupkg"),
        });
        packages[0].Integrity.Should().StartWith("sha512-HrC5");
    }

    [Theory]
    [InlineData("""<PackageReference Include="Newtonsoft.Json" Version="13.0.1" />""", "does not match Functions.csproj for Newtonsoft.Json")]
    [InlineData("""<PackageReference Include="NodaTime" Version="3.2.2" />""", "for Newtonsoft.Json, NodaTime")]
    public void A_stale_lock_is_refused(string references, string problem) =>
        NuGetLock.Read(Repo(references, Newtonsoft)).Problems.Should().ContainSingle().Which.Should().Contain(problem);

    [Fact]
    public void A_project_without_a_lock_or_with_only_the_sdk_is_handled()
    {
        var repo = Repo("""<PackageReference Include="Newtonsoft.Json" Version="13.0.3" />""", Newtonsoft);
        repo.Remove("packages.lock.json");
        NuGetLock.Read(repo).Problems.Should().ContainSingle().Which.Should().Contain("RestorePackagesWithLockFile");

        var sdkOnly = Repo("", "\"Unused\": { \"type\": \"Transitive\", \"resolved\": \"1.0.0\", \"contentHash\": \"x\" }");
        sdkOnly.Remove("packages.lock.json");
        var (none, fine) = NuGetLock.Read(sdkOnly);
        none.Should().BeEmpty();
        fine.Should().BeEmpty();
    }
}
