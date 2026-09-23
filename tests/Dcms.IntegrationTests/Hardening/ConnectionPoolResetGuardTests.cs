namespace Dcms.IntegrationTests.Hardening;

/// <summary>
/// ADR 0015. A source check, so it needs no database — and deliberately lives outside every
/// <c>[Collection]</c>: a plain fact in a Docker-fixture collection makes xUnit build that fixture
/// on a runner without Docker, and the fixture's failure fails the whole collection with it.
/// <see cref="DockerCollectionTests"/> enforces that.
/// </summary>
public sealed class ConnectionPoolResetGuardTests
{
    /// <summary>
    /// <c>TenantGucInterceptorTests</c>' pooled-connection test passes because Npgsql resets session state when a connection goes back to
    /// its pool — it fails with the reset turned off, which is how it was checked. That makes the
    /// reset load-bearing for raw connections sharing a pool with EF, so the two settings that
    /// defeat it are refused wherever a deployed connection string is written: <c>No Reset On
    /// Close</c> skips the reset, and <c>Multiplexing</c> shares one backend between concurrent
    /// commands, which makes any session GUC meaningless.
    /// </summary>
    [Fact]
    public void No_deployed_connection_string_turns_off_the_pool_reset()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Dcms.sln")))
        {
            directory = directory.Parent;
        }
        Assert.SkipWhen(directory is null, "Repository root not found; the source tree is not available here.");

        var sources = directory!.EnumerateFiles("docker-compose*.yml")
            .Concat(new DirectoryInfo(Path.Combine(directory.FullName, "src")).EnumerateFiles("appsettings*.json", SearchOption.AllDirectories)
                .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")));

        foreach (var file in sources)
        {
            var text = File.ReadAllText(file.FullName).Replace(" ", string.Empty);
            text.Should().NotContainEquivalentOf("NoResetOnClose=true", $"{file.Name} would let a tenant GUC outlive its request");
            text.Should().NotContainEquivalentOf("Multiplexing=true", $"{file.Name} would share one backend between tenants' commands");
        }
    }
}
