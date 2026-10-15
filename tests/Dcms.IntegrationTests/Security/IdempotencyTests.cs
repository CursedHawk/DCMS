extern alias AdminApiApp;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using StackExchange.Redis;

namespace Dcms.IntegrationTests.Security;

/// <summary>
/// One <c>Idempotency-Key</c>, one operation (ADR 0024), against admin-api with the requirement
/// on — the setting production runs with. Creating a tenant is the operation: it has a visible,
/// countable effect, so "performed once" is a row count rather than an inference.
/// </summary>
[Collection(AdminApiCollection.Name)]
public sealed class IdempotencyTests(AdminApiFixture fixture) : IAsyncDisposable
{
    private WebApplicationFactory<AdminApiApp::Program>? _host;

    private WebApplicationFactory<AdminApiApp::Program> Host =>
        _host ??= fixture.Factory.WithWebHostBuilder(b => b.UseSetting("Idempotency:RequireForUsers", "true"));

    private static readonly Guid Operator = Guid.NewGuid();

    [DockerFact]
    public async Task A_repeated_request_is_answered_from_the_first_and_performed_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Host.CreateClient();
        var slug = "idem-" + Guid.NewGuid().ToString("N")[..8];
        var key = Guid.NewGuid().ToString();

        var first = await client.SendAsync(CreateTenant(slug, key), ct);
        first.EnsureSuccessStatusCode();
        var second = await client.SendAsync(CreateTenant(slug, key), ct);

        second.StatusCode.Should().Be(first.StatusCode);
        second.Headers.GetValues("Idempotency-Replayed").Should().Equal("true");
        (await second.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("tenantId").GetGuid()
            .Should().Be((await first.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("tenantId").GetGuid());
        (await TenantCountAsync(slug, ct)).Should().Be(1);
    }

    [DockerFact]
    public async Task The_same_key_on_a_different_request_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Host.CreateClient();
        var key = Guid.NewGuid().ToString();

        (await client.SendAsync(CreateTenant("idem-" + Guid.NewGuid().ToString("N")[..8], key), ct)).EnsureSuccessStatusCode();
        var other = await client.SendAsync(CreateTenant("idem-" + Guid.NewGuid().ToString("N")[..8], key), ct);

        other.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await other.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("error").GetString()
            .Should().Be("idempotency-key-mismatch");
    }

    [DockerFact]
    public async Task A_repeat_while_the_first_is_still_running_is_told_to_wait()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Host.CreateClient();
        var key = Guid.NewGuid().ToString();
        var slug = "idem-" + Guid.NewGuid().ToString("N")[..8];
        var request = CreateTenant(slug, key);

        // What the first request leaves in the store while it runs: its key, claimed, with the
        // fingerprint of this very request (method, path, query, body).
        var body = await request.Content!.ReadAsByteArrayAsync(ct);
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            [.. System.Text.Encoding.UTF8.GetBytes("POST\n/api/admin/tenants\n\n"), .. body]));
        var redis = Host.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        await redis.StringSetAsync($"idem:admin-api:{Operator}::{key}",
            JsonSerializer.Serialize(new { State = "inflight", Fingerprint = fingerprint }), TimeSpan.FromMinutes(1));

        var res = await client.SendAsync(request, ct);

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("error").GetString()
            .Should().Be("idempotency-in-flight");
        res.Headers.RetryAfter.Should().NotBeNull();
        (await TenantCountAsync(slug, ct)).Should().Be(0, "a held key must not run the work");
    }

    [DockerFact]
    public async Task A_person_must_send_a_key_and_a_service_need_not()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Host.CreateClient();

        var keyless = await client.SendAsync(CreateTenant("idem-" + Guid.NewGuid().ToString("N")[..8], key: null), ct);
        keyless.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await keyless.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("error").GetString()
            .Should().Be("idempotency-key-required");

        // A service principal (a client id, not a user id) is not refused for leaving it out; it
        // meets whatever the endpoint itself says about services instead.
        var service = new HttpRequestMessage(HttpMethod.Post, "/api/admin/jobs/" + Guid.NewGuid() + "/cancel")
        {
            Headers = { { "X-Test-Sub", "dcms-some-service" } },
        };
        var res = await client.SendAsync(service, ct);
        if (res.StatusCode == HttpStatusCode.BadRequest)
        {
            (await res.Content.ReadAsStringAsync(ct)).Should().NotContain("idempotency-key-required");
        }
    }

    [DockerFact]
    public async Task A_refused_attempt_does_not_use_up_the_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Host.CreateClient();
        var slug = "idem-" + Guid.NewGuid().ToString("N")[..8];
        var key = Guid.NewGuid().ToString();

        // Signed out: 401. The SPA renews its token and replays with the same key, which must run.
        var anonymous = CreateTenant(slug, key);
        anonymous.Headers.Remove("X-Test-Sub");
        anonymous.Headers.Remove("X-Test-Roles");
        (await client.SendAsync(anonymous, ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var signedIn = await client.SendAsync(CreateTenant(slug, key), ct);
        signedIn.EnsureSuccessStatusCode();
        signedIn.Headers.Contains("Idempotency-Replayed").Should().BeFalse();
        (await TenantCountAsync(slug, ct)).Should().Be(1);
    }

    private static HttpRequestMessage CreateTenant(string slug, string? key)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/tenants")
        {
            Headers = { { "X-Test-Sub", Operator.ToString() }, { "X-Test-Roles", "SuperAdmin" } },
            // The owner is derived from the slug: the same request built twice must be byte-for-byte
            // the same request, or the second is (correctly) a mismatch rather than a repeat.
            Content = JsonContent.Create(new { slug, name = slug, ownerUserId = OwnerOf(slug), ownerEmail = $"{slug}@dcms.test" }),
        };
        if (key is not null)
        {
            req.Headers.Add("Idempotency-Key", key);
        }
        return req;
    }

    private static Guid OwnerOf(string slug) =>
        new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(slug)));

    private async Task<int> TenantCountAsync(string slug, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT count(*) FROM tenancy.tenants WHERE "Identifier" = @s""";
        cmd.Parameters.AddWithValue("s", slug);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }
}
