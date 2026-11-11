using System.Net;
using Dcms.Shared.Vault;
using Microsoft.Extensions.Logging.Abstractions;
using VaultSharp.Core;

namespace Dcms.UnitTests.Vault;

/// <summary>
/// A service authenticates to Vault with an AppRole and the client caches the token it gets for
/// the life of the process, while the token itself lives one hour. Nothing renewed it, so every
/// Vault call made more than an hour after startup failed — permanently, until a restart. On the
/// edge that meant a TLS private key that could not be decrypted, so every hostname not already
/// in its memory cache aborted the handshake: a browser showed a failed connection and the logs
/// said "permission denied / invalid token", which reads like a policy problem.
///
/// <para>These assert the retry that makes it heal, and the two ways it must not misbehave: it
/// must not loop, and it must not swallow a real refusal.</para>
/// </summary>
public class VaultTokenRefreshTests
{
    [Fact]
    public async Task Re_authenticates_and_retries_once_when_Vault_rejects_the_token()
    {
        var attempts = 0;
        var resets = 0;

        var result = await VaultTokenRefresh.ExecuteAsync(
            () => ++attempts == 1
                ? throw new VaultApiException(HttpStatusCode.Forbidden, "permission denied / invalid token")
                : Task.FromResult("plaintext"),
            () => resets++,
            NullLogger.Instance,
            "decrypt");

        result.Should().Be("plaintext");
        attempts.Should().Be(2);
        resets.Should().Be(1, "the cached token is what expired, so it has to be dropped before the retry");
    }

    [Fact]
    public async Task Does_not_touch_the_token_when_the_call_succeeds()
    {
        var resets = 0;

        await VaultTokenRefresh.ExecuteAsync(
            () => Task.FromResult("plaintext"), () => resets++, NullLogger.Instance, "decrypt");

        // Every Transit call goes through here, including one per TLS handshake on a cache miss.
        // Re-logging in on the happy path would be a Vault round trip per handshake.
        resets.Should().Be(0);
    }

    [Fact]
    public async Task Gives_up_after_one_retry_rather_than_looping()
    {
        var attempts = 0;

        var act = async () => await VaultTokenRefresh.ExecuteAsync<string>(
            () =>
            {
                attempts++;
                throw new VaultApiException(HttpStatusCode.Forbidden, "permission denied");
            },
            () => { },
            NullLogger.Instance,
            "decrypt");

        // A 403 that survives a fresh token is the policy refusing, and retrying that forever
        // would hammer Vault's login endpoint instead of surfacing a configuration error.
        await act.Should().ThrowAsync<VaultApiException>();
        attempts.Should().Be(2);
    }

    [Fact]
    public async Task Leaves_every_other_failure_exactly_as_it_was()
    {
        var attempts = 0;

        var act = async () => await VaultTokenRefresh.ExecuteAsync<string>(
            () =>
            {
                attempts++;
                throw new VaultApiException(HttpStatusCode.ServiceUnavailable, "Vault is sealed");
            },
            () => { },
            NullLogger.Instance,
            "decrypt");

        // A sealed Vault is not fixed by a new token, and retrying it would double the load on
        // a Vault that is already in trouble.
        await act.Should().ThrowAsync<VaultApiException>();
        attempts.Should().Be(1);
    }
}

/// <summary>
/// VaultSharp keeps the AppRole login as a lazily started Task for the life of the client. A
/// login that failed because Vault was unreachable is therefore cached as a FAULTED task, and
/// every later call rethrows that same exception, even after Vault is back. On vps1 that
/// turned a two-hour Vault outage into ai-gateway answering every AI request with 500 until
/// the container was restarted. Real client, real HTTP: the cache lives inside VaultSharp.
/// </summary>
public class VaultUnreachableRecoveryTests
{
    [Fact]
    public async Task A_login_that_failed_while_Vault_was_down_is_not_reused_once_it_is_back()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var client = new VaultSharp.VaultClient(new VaultSharp.VaultClientSettings(
            $"http://127.0.0.1:{port}",
            new VaultSharp.V1.AuthMethods.AppRole.AppRoleAuthMethodInfo("role", "secret")));
        var encryptor = new VaultTransitEncryptor(client, NullLogger<VaultTransitEncryptor>.Instance);

        // Vault down: nothing listens, the login fails.
        await FluentActions.Awaiting(() => encryptor.DecryptAsync("k", "vault:v1:x"))
            .Should().ThrowAsync<HttpRequestException>();

        // Vault back.
        using var vault = new HttpListener();
        vault.Prefixes.Add($"http://127.0.0.1:{port}/");
        vault.Start();
        var ct = TestContext.Current.CancellationToken;
        _ = Task.Run(async () =>
        {
            while (vault.IsListening)
            {
                var ctx = await vault.GetContextAsync();
                var body = ctx.Request.Url!.AbsolutePath.Contains("login")
                    ? """{"auth":{"client_token":"t","lease_duration":3600,"renewable":true}}"""
                    : """{"data":{"plaintext":"aGk="}}""";
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes, ct);
                ctx.Response.Close();
            }
        }, ct);

        var plaintext = await encryptor.DecryptAsync("k", "vault:v1:x", ct);

        System.Text.Encoding.UTF8.GetString(plaintext).Should().Be("hi");
    }
}
