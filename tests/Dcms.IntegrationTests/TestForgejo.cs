using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Dcms.IntegrationTests;

/// <summary>
/// A real Forgejo with an admin bot and its token, for tests that need git itself rather than a
/// stub of its REST API: merges run the git CLI, and plugin repositories are created through it.
/// </summary>
internal sealed class TestForgejo : IAsyncDisposable
{
    /// <summary>Keep in lockstep with the <c>forgejo</c> service in docker-compose.yml.</summary>
    public const string Image = "registry-gitlab.highgeek.eu/cursedhawk/baas-dcms/mirror/forgejo:11.0.16";

    private readonly IContainer _container;

    public string BaseUrl { get; }
    public string Token { get; private set; } = "";

    private TestForgejo(IContainer container)
    {
        _container = container;
        BaseUrl = $"http://{container.Hostname}:{container.GetMappedPublicPort(3000)}";
    }

    public static async Task<TestForgejo> StartAsync()
    {
        var container = new ContainerBuilder(Image)
            .WithEnvironment("FORGEJO__security__INSTALL_LOCK", "true")
            .WithEnvironment("FORGEJO__database__DB_TYPE", "sqlite3")
            // The CLI (user creation, tokens) opens the same SQLite file the running server writes to.
            // With SQLite's defaults — a 500 ms lock wait and a rollback journal — a CLI call that lands
            // while the server is writing fails with "database is locked" (pipeline, 2026-11-12). WAL
            // lets readers and the writer through together; the timeout makes a second writer wait.
            .WithEnvironment("FORGEJO__database__SQLITE_JOURNAL_MODE", "WAL")
            .WithEnvironment("FORGEJO__database__SQLITE_TIMEOUT", "10000")
            .WithEnvironment("FORGEJO__service__DISABLE_REGISTRATION", "true")
            .WithPortBinding(3000, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(3000).ForPath("/api/healthz")))
            .Build();
        await container.StartAsync();
        var forgejo = new TestForgejo(container);
        await forgejo.ExecAsync("forgejo", "admin", "user", "create", "--username", "bot", "--password", "Bot-passw0rd-1",
            "--email", "bot@dcms.test", "--admin", "--must-change-password=false");
        forgejo.Token = (await forgejo.ExecAsync("forgejo", "admin", "user", "generate-access-token", "--username", "bot",
            "--token-name", "dcms", "--scopes", "all", "--raw")).Trim().Split('\n')[^1];
        return forgejo;
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    /// <summary>A person's Forgejo account, as identity's credential sync would make it.</summary>
    public Task CreateUserAsync(string username, string email) =>
        ExecAsync("forgejo", "admin", "user", "create", "--username", username, "--password", "User-passw0rd-1",
            "--email", email, "--must-change-password=false");

    /// <summary>Forgejo's REST API as the bot sees it.</summary>
    public async Task<System.Text.Json.JsonElement> GetJsonAsync(string path)
    {
        using var http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("token", Token);
        var text = await http.GetStringAsync(path);
        return System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<string> ExecAsync(params string[] command)
    {
        // The Forgejo CLI refuses to run as root; the image's git user owns its data.
        var result = await _container.ExecAsync(["su-exec", "git", .. command]);
        result.ExitCode.Should().Be(0, result.Stderr + result.Stdout);
        return result.Stdout;
    }
}
