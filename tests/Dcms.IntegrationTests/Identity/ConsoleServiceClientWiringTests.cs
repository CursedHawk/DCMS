extern alias IdentityApp;

using IdentityApp::Dcms.Identity;

namespace Dcms.IntegrationTests.Identity;

/// <summary>
/// The deployment half of the <c>dcms-platform-api-service</c> client. The seeder creates it
/// and the OAuth contract tests cover that side; this covers the four files that have to agree
/// with the seeder for the client to be usable, none of which the compiler reads.
///
/// <para>Each assertion here stands for a failure that is silent at deploy time. A client id
/// that drifts from the constant is <c>invalid_client</c> on the first proxied console call. A
/// secret left unblanked in the production overlay means the dev secret published in this
/// repository survives into a production container — Vault loads after the environment and
/// wins, so the symptom is not a broken deploy but a working one that would have kept working
/// if Vault went away. And a secret whose two halves are seeded separately is a pair that can
/// disagree, which reads as a token endpoint refusing a correct-looking credential.</para>
/// </summary>
public sealed class ConsoleServiceClientWiringTests
{
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

    private static string ReadRepoFile(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relativePath));

    /// <summary>
    /// The environment block of one compose service, as raw text. Compose services are keyed at
    /// two spaces of indentation, so the block runs to the next such key.
    /// </summary>
    private static string ServiceBlock(string composeFile, string service)
    {
        var compose = ReadRepoFile(composeFile).ReplaceLineEndings("\n");
        var start = compose.IndexOf($"\n  {service}:\n", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"{composeFile} must define the '{service}' service");

        var rest = compose[(start + 1)..];
        var next = rest.IndexOf("\n  ", StringComparison.Ordinal);
        while (next >= 0)
        {
            var lineEnd = rest.IndexOf('\n', next + 1);
            var line = lineEnd < 0 ? rest[(next + 1)..] : rest[(next + 1)..lineEnd];
            // A sibling service key: exactly two spaces, a name, a colon, nothing after it.
            if (line.Length > 2 && line[2] != ' ' && line[2] != '#' && line.TrimEnd().EndsWith(':'))
            {
                return rest[..next];
            }
            next = rest.IndexOf("\n  ", next + 1, StringComparison.Ordinal);
        }
        return rest;
    }

    [Theory]
    [InlineData("docker-compose.yml")]
    [InlineData("docker-compose.prod.yml")]
    public void Platform_api_presents_the_console_client_id(string composeFile)
    {
        ServiceBlock(composeFile, "platform-api").Should().Contain(
            $"ServiceClient__ClientId: \"{DcmsOAuth.Clients.PlatformApiService}\"",
            "the client id in compose is what identity matches against the seeded row; a "
            + "difference is invalid_client on every proxied console call");
    }

    [Theory]
    [InlineData("docker-compose.yml")]
    [InlineData("docker-compose.prod.yml")]
    public void Platform_api_is_not_given_the_admin_api_client(string composeFile)
    {
        ServiceBlock(composeFile, "platform-api").Should().NotContain(
            $"ServiceClient__ClientId: \"{DcmsOAuth.Clients.AdminApiService}\"",
            "content-api holds that secret, so reusing it here would hand the console API "
            + "dcms.ai and dcms.social alongside dcms.console");
    }

    [Fact]
    public void Production_blanks_the_dev_service_client_secret()
    {
        ServiceBlock("docker-compose.prod.yml", "platform-api").Should().Contain(
            "ServiceClient__ClientSecret: \"\"",
            "the base compose sets a dev secret published in this repository, and an omitted "
            + "line here lets that value survive into a production container");
    }

    [Fact]
    public void Both_halves_of_the_console_secret_are_required_by_the_vault_check()
    {
        var apply = ReadRepoFile("infra/vault/apply.sh").ReplaceLineEndings("\n");

        var identityKeys = apply[apply.IndexOf("    identity)", StringComparison.Ordinal)..];
        identityKeys = identityKeys[..identityKeys.IndexOf('\n')];
        identityKeys.Should().Contain("Identity__PlatformApiService__Secret");

        var platformKeys = apply[apply.IndexOf("    platform-api)", StringComparison.Ordinal)..];
        platformKeys = platformKeys[..platformKeys.IndexOf('\n')];
        platformKeys.Should().Contain("ServiceClient__ClientSecret");
    }

    [Fact]
    public void Both_halves_of_the_console_secret_are_seeded_from_one_generated_value()
    {
        var apply = ReadRepoFile("infra/vault/apply.sh").ReplaceLineEndings("\n");

        var identityLine = "seed_key secret/dcms/identity Identity__PlatformApiService__Secret \"$console_secret\"";
        var platformLine = "seed_key secret/dcms/platform-api ServiceClient__ClientSecret \"$console_secret\"";

        apply.Should().Contain(identityLine).And.Contain(platformLine,
            "the two paths must agree, and generating the value once is what makes that "
            + "true at creation time rather than a thing an operator has to remember");

        var between = apply[apply.IndexOf(identityLine, StringComparison.Ordinal)..];
        between = between[..between.IndexOf(platformLine, StringComparison.Ordinal)];
        between.Should().NotContain("generate_secret",
            "a second generate_secret between the two writes would seed halves that differ");
    }

    [Fact]
    public void The_deploy_seeds_before_it_rolls()
    {
        var deploy = ReadRepoFile("scripts/deploy.sh").ReplaceLineEndings("\n");
        deploy.Should().Contain("infra/vault/apply.sh --seed",
            "a machine-only secret added to the seed block has to land without anyone being "
            + "told to run a command on the host");
    }
}
