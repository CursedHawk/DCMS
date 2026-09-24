using System.Text.RegularExpressions;

namespace Dcms.IntegrationTests.Hardening;

/// <summary>
/// ADR 0015 phase 3. A source scan, in the same spirit as
/// <c>AuditBulkStatementCoverageTests</c>, for the decision that stops being optional once the
/// services connect as a <c>NOBYPASSRLS</c> role.
///
/// <para>Under that role <c>IgnoreQueryFilters()</c> widens nothing: it drops EF's tenant
/// predicate and the database's policy is still there. So every file calling it has to have
/// decided what the database should be told — act as a tenant (<c>RlsScope.Tenant</c>), span
/// all of them (<c>RlsScope.Platform</c>), or nothing, because the request's own tenant already
/// matches or the table has no policy — and a file with no <c>RlsScope</c> must say which in an
/// <c>// rls:</c> comment.</para>
///
/// <para>Coarse, per file, and meant to be: it checks that the decision was made, not that it
/// was right. Whether it was right is what the enforcing run of the integration suite
/// (the default; <c>DCMS_TEST_RLS_ENFORCE=0</c> turns it off) answers, path by path.</para>
/// </summary>
public sealed partial class RlsScopeCoverageTests
{
    [Fact]
    public void Every_file_ignoring_query_filters_or_reading_an_unfiltered_context_has_decided_what_the_database_is_told()
    {
        var root = SourceRoot();
        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                           && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                           // Generated, and run by the migrate job as the owner.
                           && !file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .Where(file =>
            {
                var source = File.ReadAllText(file);
                return (IgnoreCall().IsMatch(source) || ReadsUnfilteredTenantContext(source))
                       && !ScopeCall().IsMatch(source)
                       && !source.Contains("// rls:", StringComparison.Ordinal);
            })
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            "under the NOBYPASSRLS role IgnoreQueryFilters() no longer widens anything, so each file "
            + "calling it must enter RlsScope.Tenant/Platform or say in an '// rls:' comment why the "
            + "request's own tenant (or an unpoliced table) already suffices. Undecided:\n{0}",
            string.Join("\n", offenders));
    }

    /// <summary>
    /// Three contexts map policed tables and have no query filters to ignore, so their readers
    /// never call <c>IgnoreQueryFilters()</c> and the check above cannot see them. Found after
    /// the sweep, one service at a time: the audit sealer and platform audit view, then
    /// ai-gateway's provider lookup, which would have quietly swapped a tenant's own API key for
    /// the platform's. For audit only the rows count; the outbox it also maps is unpoliced and
    /// written from everywhere.
    /// </summary>
    private static bool ReadsUnfilteredTenantContext(string source) =>
        (source.Contains("AuditDbContext", StringComparison.Ordinal) && EventsCall().IsMatch(source))
        || source.Contains("AiDbContext", StringComparison.Ordinal)
        || source.Contains("AnalyticsDbContext", StringComparison.Ordinal);

    [GeneratedRegex(@"^(?![ \t]*(//|\*))[^\n]*\.Events\b", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex EventsCall();

    /// <summary>In code: a comment that merely names <c>RlsScope</c> decides nothing.</summary>
    [GeneratedRegex(@"^(?![ \t]*(//|\*))[^\n]*\bRlsScope\.", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ScopeCall();

    /// <summary>A call in code, not in a comment line or an XML doc.</summary>
    [GeneratedRegex(@"^(?![ \t]*(//|\*))[^\n]*\.IgnoreQueryFilters\(\)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex IgnoreCall();

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
