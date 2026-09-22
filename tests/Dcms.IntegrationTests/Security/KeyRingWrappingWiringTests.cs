using System.Text.RegularExpressions;

namespace Dcms.IntegrationTests.Security;

/// <summary>
/// Four services share one Data Protection key ring, and it is now encrypted at rest with
/// Vault Transit. That makes them all-or-nothing in a way nothing else checks: a key wrapped by
/// any one of them is read back by all of them, so a service without the Transit grant, without
/// the Vault client, or without the flag meets a wrapped row it cannot decrypt — which is an
/// unreadable auth cookie and a stranded Forgejo outbox, not a degraded feature.
///
/// <para>The list of services is derived from the source rather than written here, because the
/// failure this guards against is a <i>fifth</i> service calling
/// <c>AddDcmsDataProtection</c> and nobody remembering the other three files. That is the same
/// shape as the edge being added to <c>infra/vault/apply.sh</c> and not to the provisioning
/// script, which cost the platform every TLS handshake it served.</para>
/// </summary>
public sealed class KeyRingWrappingWiringTests
{
    [Fact]
    public void Every_service_sharing_the_key_ring_may_use_the_transit_key()
    {
        foreach (var service in SharesTheKeyRing())
        {
            var policy = ReadRepoFile(Path.Combine("infra", "vault", "policies", $"dcms-{service}.hcl"));
            policy.Should().Contain("transit/encrypt/dcms-dataprotection",
                $"{service} shares the wrapped ring and must be able to write a key when one rolls");
            policy.Should().Contain("transit/decrypt/dcms-dataprotection",
                $"{service} shares the wrapped ring and must be able to read every key in it");
        }
    }

    [Fact]
    public void Every_service_sharing_the_key_ring_holds_a_transit_client_unconditionally()
    {
        foreach (var service in SharesTheKeyRing())
        {
            var program = ReadRepoFile(ProgramPath(service));
            program.Should().Contain("AddDcmsVaultTransit",
                $"{service} cannot decrypt a wrapped key without one");
            // Registering it only when the flag is on is what made turning the flag on a
            // two-sided change: the ring is shared, so a service still reading it with the flag
            // off has to be able to decrypt what a service with the flag on wrote.
            program.Should().NotContain("GetValue(\"DataProtection:ProtectWithTransit\"",
                $"{service} must register the Transit client whether or not it writes wrapped keys");
        }
    }

    [Fact]
    public void Production_turns_wrapping_on_for_all_of_them_at_once()
    {
        var compose = ReadRepoFile("docker-compose.prod.yml").ReplaceLineEndings("\n");
        compose.Should().Contain("DataProtection__ProtectWithTransit: \"true\"");

        // On the shared anchor, not per service: that is what makes "all of them" impossible to
        // get wrong. Assert each one actually merges it.
        foreach (var service in SharesTheKeyRing())
        {
            ServiceBlock(compose, service).Should().Contain("<<: *prod-env",
                $"{service} would otherwise write plaintext keys into a ring the others wrap");
        }
    }

    /// <summary>
    /// The services whose Program.cs joins the shared ring, named the way Vault and compose
    /// spell them (<c>Dcms.ContentApi</c> -> <c>content-api</c>).
    /// </summary>
    private static IEnumerable<string> SharesTheKeyRing()
    {
        var root = Path.Combine(RepoRoot(), "src", "Services");
        var services = Directory.EnumerateDirectories(root)
            .Where(directory => File.Exists(Path.Combine(directory, "Program.cs"))
                                && File.ReadAllText(Path.Combine(directory, "Program.cs"))
                                    .Contains("AddDcmsDataProtection", StringComparison.Ordinal))
            .Select(directory => Kebab(Path.GetFileName(directory)!["Dcms.".Length..]))
            .ToList();

        services.Should().NotBeEmpty("the ring has users; an empty list would pass every assertion");
        return services;
    }

    private static string ProgramPath(string service)
        => Path.Combine("src", "Services", "Dcms." + string.Concat(
            service.Split('-').Select(part => char.ToUpperInvariant(part[0]) + part[1..])), "Program.cs");

    private static string Kebab(string pascal)
        => Regex.Replace(pascal, "(?<!^)([A-Z])", "-$1").ToLowerInvariant();

    /// <summary>One compose service's block: services are keyed at two spaces, so it runs to
    /// the next such key.</summary>
    private static string ServiceBlock(string compose, string service)
    {
        var start = compose.IndexOf($"\n  {service}:\n", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, $"{service} should be a service in the production overlay");
        var next = Regex.Match(compose[(start + 1)..], "\n  [a-z0-9-]+:\n");
        return next.Success ? compose.Substring(start, next.Index + 1) : compose[start..];
    }

    private static string ReadRepoFile(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot(), relativePath));

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
