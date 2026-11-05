namespace Dcms.UnitTests.Functions;

/// <summary>
/// "What a function can use" (src/Plugins/Dcms.Plugins.Functions/Docs/functions-runtime.md) is
/// shown in the editor and the CLI: it must name every assembly a C# function compiles against,
/// and nothing that is not, or it tells people wrong.
/// </summary>
public sealed class RuntimeReferenceTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Dcms.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("No Dcms.sln above the test.");
    }

    [Fact]
    public void The_page_lists_exactly_the_curated_assemblies()
    {
        var root = Root();
        var curated = File.ReadAllLines(Path.Combine(root, "src/Functions/Dcms.Functions.WasmHost/CuratedAssemblies.txt"))
            .Select(l => l.Split('#')[0].Trim())
            .Where(l => l.Length > 0)
            .ToList();
        var page = File.ReadAllText(Path.Combine(root, "src/Plugins/Dcms.Plugins.Functions/Docs/functions-runtime.md"));
        var section = page[page.IndexOf("## C#", StringComparison.Ordinal)..page.IndexOf("## The SDK", StringComparison.Ordinal)];
        var named = System.Text.RegularExpressions.Regex.Matches(section, "`(System[.A-Za-z]*)`").Select(m => m.Groups[1].Value)
            .Where(n => !n.StartsWith("System.Collections.Generic", StringComparison.Ordinal) && n is not ("System" or "System.Linq" or "System.Threading.Tasks" or "System.Text.Json.Nodes"))
            .ToHashSet();

        curated.Should().NotBeEmpty();
        curated.Where(a => !section.Contains($"`{a}`", StringComparison.Ordinal)).Should().BeEmpty("every assembly a function has is on the page");
        named.Where(n => !curated.Contains(n)).Should().BeEmpty("the page names no assembly a function does not have");
    }
}
