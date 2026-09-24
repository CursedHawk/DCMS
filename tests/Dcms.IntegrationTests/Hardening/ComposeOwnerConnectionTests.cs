using System.Text.RegularExpressions;

namespace Dcms.IntegrationTests.Hardening;

/// <summary>
/// ADR 0015 phase 5. The table owner, <c>dcms</c>, bypasses row-level security, so a service
/// connecting as it undoes the whole ADR without any test noticing: the policies are still
/// there, they just do not apply. This holds the owner connection string to the services
/// allowed it, by reading the compose files the deploy runs.
/// </summary>
public sealed partial class ComposeOwnerConnectionTests
{
    [Theory]
    // Production: only the jobs that run DDL.
    [InlineData("docker-compose.prod.yml", new[] { "migrate", "identity-migrate" })]
    // Dev: the same, plus the two services that migrate at startup there. The prod overlay
    // replaces both of theirs.
    [InlineData("docker-compose.yml", new[] { "migrate", "identity-migrate", "admin-api", "identity" })]
    public void Only_the_migrate_jobs_connect_as_the_owner(string file, string[] allowed)
    {
        var path = Path.Combine(RepoRoot(), file);
        string? service = null;
        string? anchor = null;
        var holders = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var line in File.ReadLines(path))
        {
            if (TopLevelKey().Match(line) is { Success: true } top)
            {
                // An x- anchor block at the top level: owning it hands the owner to every
                // service that merges it, which is the shape this replaced.
                anchor = top.Groups[1].Value.StartsWith("x-", StringComparison.Ordinal) ? top.Groups[1].Value : null;
                service = null;
            }
            else if (ServiceKey().Match(line) is { Success: true } svc)
            {
                service = svc.Groups[1].Value;
            }

            if (!line.TrimStart().StartsWith('#') && OwnerConnection().IsMatch(line))
            {
                holders.Add(anchor ?? service ?? "(unknown)");
            }
        }

        holders.Should().BeSubsetOf(allowed,
            "the table owner bypasses RLS; a service holding its connection string is outside ADR 0015 entirely");
    }

    [GeneratedRegex(@"^([a-z][a-z0-9-]*):")]
    private static partial Regex TopLevelKey();

    [GeneratedRegex(@"^  ([a-z][a-z0-9-]*):\s*$")]
    private static partial Regex ServiceKey();

    [GeneratedRegex(@"Username=dcms;")]
    private static partial Regex OwnerConnection();

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Dcms.sln")))
        {
            directory = directory.Parent;
        }
        Assert.SkipWhen(directory is null, "Repository root not found; the source tree is not available here.");
        return directory!.FullName;
    }
}
