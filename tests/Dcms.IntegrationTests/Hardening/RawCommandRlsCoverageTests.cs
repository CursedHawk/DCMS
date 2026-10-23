using System.Text.RegularExpressions;

namespace Dcms.IntegrationTests.Hardening;

/// <summary>
/// The RLS tenant travels in front of each command (TenantGucInterceptor, since 2026-10-23), put there
/// by an EF command interceptor. A command made straight on the connection --
/// <c>GetDbConnection().CreateCommand()</c> -- passes no EF interceptor, so it carries no tenant and
/// RLS shows it nothing. That fails closed, which is the safe direction and also the silent one: the
/// list comes back empty and nothing errors.
///
/// <para>So every file that makes raw commands on an EF connection either calls
/// <c>TenantGucInterceptor.Prepare</c> or says in an <c>// rls:</c> comment why it need not (a
/// context with no tenant tables, DDL run as the owner). Coarse and per file, like
/// <see cref="RlsScopeCoverageTests"/>: it checks the decision was made.</para>
/// </summary>
public sealed partial class RawCommandRlsCoverageTests
{
    [Fact]
    public void Every_raw_command_on_an_EF_connection_is_prepared_or_says_why_not()
    {
        var root = SourceRoot();
        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                           && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                           && !file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .Where(file =>
            {
                var source = File.ReadAllText(file);
                return EfConnection().IsMatch(source)
                       && CreateCommand().IsMatch(source)
                       && !source.Contains("TenantGucInterceptor.Prepare(", StringComparison.Ordinal)
                       && !source.Contains("// rls:", StringComparison.Ordinal);
            })
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            "a raw command on an EF connection carries no RLS tenant unless TenantGucInterceptor.Prepare puts it "
            + "there; call it, or say in an '// rls:' comment why this one needs none. Undecided:\n{0}",
            string.Join("\n", offenders));
    }

    [GeneratedRegex(@"^(?![ \t]*(//|\*))[^\n]*\.GetDbConnection\(\)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex EfConnection();

    [GeneratedRegex(@"^(?![ \t]*(//|\*))[^\n]*\.CreateCommand\(\)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex CreateCommand();

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Dcms.sln")))
        {
            directory = directory.Parent;
        }
        Assert.SkipWhen(directory is null, "Repository root not found; the source tree is not available here.");
        return Path.Combine(directory!.FullName, "src");
    }
}
