using System.Text.RegularExpressions;

namespace Dcms.IntegrationTests.Audit;

/// <summary>
/// A source scan, for the one gap no runtime test can close.
///
/// <para><c>ExecuteUpdateAsync</c> and <c>ExecuteDeleteAsync</c> never touch the change tracker,
/// so nothing can produce a field diff for them — the rows are gone before anything could read
/// them. The command interceptor records the shape and the row count, which keeps them from
/// being invisible, but a set-based statement destroying a tenant's forms deserves better than
/// "a table got shorter".</para>
///
/// <para>So: any file issuing one must also mention <c>IAuditRecorder</c> or
/// <c>AuditScope</c> — meaning somebody has been here and either described what it destroys or
/// deliberately suppressed the per-statement record in favour of a better one. It is a coarse
/// test and it is meant to be. It does not check that the record is any good; it checks that
/// the decision was made at all, which is the part that gets skipped.</para>
/// </summary>
public sealed partial class AuditBulkStatementCoverageTests
{
    [Fact]
    public void Every_file_issuing_set_based_statements_has_thought_about_the_record()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            if (!BulkStatement().IsMatch(source))
            {
                continue;
            }

            if (source.Contains("IAuditRecorder", StringComparison.Ordinal)
                || source.Contains("AuditScope", StringComparison.Ordinal))
            {
                continue;
            }

            offenders.Add(Path.GetRelativePath(SourceRoot(), file).Replace('\\', '/'));
        }

        offenders.Should().BeEmpty(
            "a set-based statement leaves no before-image, so the file issuing one has to say what it "
            + "destroyed — inject IAuditRecorder and record it, or take scope.SuppressBulkCapture() and "
            + "record something better. Unaccounted for:\n{0}",
            string.Join("\n", offenders.OrderBy(x => x, StringComparer.Ordinal)));
    }

    /// <summary>Walks up from the test binary to the repository, which has no fixed path in CI.</summary>
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

    [GeneratedRegex(@"\bExecute(Update|Delete)(Async)?\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex BulkStatement();
}
