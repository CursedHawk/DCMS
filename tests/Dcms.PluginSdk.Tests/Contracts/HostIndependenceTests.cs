using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Dcms.PluginSdk.Tests.Contracts;

/// <summary>
/// The services host plugins; they do not know any. What a plugin does lives in the plugin,
/// and what the platform needs to know about one (routes it reserves, whether it tracks
/// visitors, how it appears in the generated client) it reads from the manifest.
/// </summary>
public partial class HostIndependenceTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Dcms.sln")))
        {
            dir = dir.Parent;
        }
        return dir!.FullName;
    }

    private static IEnumerable<string> Sources(string folder) =>
        Directory.EnumerateFiles(Path.Combine(Root(), "src", folder), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"));

    [GeneratedRegex("""Dcms\.Plugins\.(?!All\b)\w+|PluginId\s*[!=]=\s*"[a-z]""")]
    private static partial Regex PluginKnowledge();

    [Theory]
    [InlineData("Services")]
    [InlineData("Shared")]
    public void No_service_names_a_plugin(string folder)
    {
        var offenders = Sources(folder)
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(x => PluginKnowledge().IsMatch(x.line))
            .Select(x => $"{Path.GetRelativePath(Root(), x.f)}:{x.i + 1}: {x.line.Trim()}")
            .ToList();

        offenders.Should().BeEmpty("services reach plugins through the registry and contracts only");
    }

    [Fact]
    public void Hosts_reference_the_plugin_set_not_single_plugins()
    {
        var references = Directory.EnumerateFiles(Path.Combine(Root(), "src", "Services"), "*.csproj", SearchOption.AllDirectories)
            .SelectMany(p => XDocument.Load(p).Descendants().Where(e => e.Name.LocalName == "ProjectReference")
                .Select(e => ((string?)e.Attribute("Include") ?? "").Replace('\\', '/')))
            .Where(r => r.Contains("/Plugins/", StringComparison.Ordinal))
            .Select(Path.GetFileNameWithoutExtension)
            .Distinct();

        references.Should().OnlyContain(r => r == "Dcms.Plugins.All");
    }
}
