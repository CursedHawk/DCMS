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

    private async Task<string> ExecAsync(params string[] command)
    {
        // The Forgejo CLI refuses to run as root; the image's git user owns its data.
        var result = await _container.ExecAsync(["su-exec", "git", .. command]);
        result.ExitCode.Should().Be(0, result.Stderr + result.Stdout);
        return result.Stdout;
    }
}
