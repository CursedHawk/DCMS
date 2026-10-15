extern alias AdminApiApp;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ForgejoClient = AdminApiApp::Dcms.AdminApi.Sites.Git.ForgejoClient;
using ForgejoOptions = AdminApiApp::Dcms.AdminApi.Sites.Git.ForgejoOptions;
using SiteGitService = AdminApiApp::Dcms.AdminApi.Sites.Git.SiteGitService;

namespace Dcms.IntegrationTests.Sites;

/// <summary>
/// Merge and publish run the git CLI against Forgejo (a real two-parent merge in a throwaway
/// clone). Every other test fakes Forgejo's REST API, so nothing ran this until a libcurl update
/// broke it on dev (2026-10-15): with the token in the clone URL, git dropped it mid-clone and
/// every merge failed with "could not read Username". This runs the real thing: a private repo in
/// a real Forgejo, the bot token, clone, merge and push.
/// </summary>
public sealed class GitMergeForgejoTests : IAsyncLifetime
{
    /// <summary>Keep in lockstep with the <c>forgejo</c> service in docker-compose.yml.</summary>
    private const string Image = "registry-gitlab.highgeek.eu/cursedhawk/baas-dcms/mirror/forgejo:11.0.16";

    private IContainer _forgejo = null!;
    private string _baseUrl = "";
    private string _token = "";

    public async ValueTask InitializeAsync()
    {
        _forgejo = new ContainerBuilder(Image)
            .WithEnvironment("FORGEJO__security__INSTALL_LOCK", "true")
            .WithEnvironment("FORGEJO__database__DB_TYPE", "sqlite3")
            .WithEnvironment("FORGEJO__service__DISABLE_REGISTRATION", "true")
            .WithPortBinding(3000, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(3000).ForPath("/api/healthz")))
            .Build();
        await _forgejo.StartAsync();
        _baseUrl = $"http://{_forgejo.Hostname}:{_forgejo.GetMappedPublicPort(3000)}";

        await ExecAsync("forgejo", "admin", "user", "create", "--username", "bot", "--password", "Bot-passw0rd-1", "--email", "bot@dcms.test",
            "--admin", "--must-change-password=false");
        _token = (await ExecAsync("forgejo", "admin", "user", "generate-access-token", "--username", "bot", "--token-name", "dcms",
            "--scopes", "all", "--raw")).Trim().Split('\n')[^1];
    }

    public async ValueTask DisposeAsync() => await _forgejo.DisposeAsync();

    [DockerFact]
    public async Task A_branch_merges_into_release_of_a_private_repo_with_the_bot_token()
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", _token);
        (await http.PostAsJsonAsync("/api/v1/user/repos", new { name = "site", @private = true, auto_init = true, default_branch = "release" }, ct))
            .EnsureSuccessStatusCode();
        // A change on main that release does not have yet: what Publish merges.
        (await http.PostAsJsonAsync("/api/v1/repos/bot/site/contents/hello.txt", new
        {
            content = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello")),
            branch = "release",
            new_branch = "main",
            message = "Add hello",
        }, ct)).EnsureSuccessStatusCode();

        var options = Options.Create(new ForgejoOptions { BaseUrl = _baseUrl, Token = _token });
        // As admin-api's Program.cs configures it.
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl + "/") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", _token);
        var forgejo = new ForgejoClient(client, options, NullLogger<ForgejoClient>.Instance);
        var git = new SiteGitService(forgejo, options, NullLogger<SiteGitService>.Instance);

        var outcome = await git.MergeAsync("bot/site", SiteGitService.ReleaseBranch, "main", ct);

        outcome.Merged.Should().BeTrue();
        outcome.Conflicts.Should().BeEmpty();
        var release = JsonDocument.Parse(await http.GetStringAsync("/api/v1/repos/bot/site/branches/release", ct)).RootElement;
        release.GetProperty("commit").GetProperty("id").GetString().Should().Be(outcome.Sha);
        (await http.GetAsync("/api/v1/repos/bot/site/raw/hello.txt?ref=release", ct)).IsSuccessStatusCode.Should().BeTrue();
    }

    private async Task<string> ExecAsync(params string[] command)
    {
        // The Forgejo CLI refuses to run as root; the image's git user owns its data.
        var result = await _forgejo.ExecAsync(["su-exec", "git", .. command]);
        result.ExitCode.Should().Be(0, result.Stderr + result.Stdout);
        return result.Stdout;
    }
}
