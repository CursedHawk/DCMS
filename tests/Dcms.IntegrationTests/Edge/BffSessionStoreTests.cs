extern alias EdgeApp;

using StackExchange.Redis;
using Testcontainers.Redis;
using EdgeAuth = EdgeApp::Dcms.Edge.Auth;

namespace Dcms.IntegrationTests.Edge;

/// <summary>
/// The session index the account page's "active sessions" list is built on, against a real Redis.
///
/// <para>Worth an integration test rather than a fake: the index is a second key that has to
/// stay in step with the rows it names, and everything that can go wrong with it is a Redis
/// semantic — a member left behind after its row expired, a set that outlives its last member,
/// a row written under the old value shape by the release this one replaces. A fake store would
/// agree with whatever this code believes.</para>
///
/// <para><b>What it is really protecting.</b> The listing decides which sessions an operator is
/// offered the ability to end. A listing that returns someone else's session is a cross-account
/// sign-out; one that misses a live session is a device the operator cannot revoke and has no
/// way to know about. Both are silent.</para>
/// </summary>
public sealed class BffSessionStoreTests : IAsyncLifetime
{
    private const string Ada = "11111111-1111-1111-1111-111111111111";
    private const string Grace = "22222222-2222-2222-2222-222222222222";

    private readonly RedisContainer redis = new RedisBuilder("redis:7").Build();
    private ConnectionMultiplexer connection = null!;
    private EdgeAuth::BffSessionStore store = null!;

    public async ValueTask InitializeAsync()
    {
        await redis.StartAsync();
        connection = await ConnectionMultiplexer.ConnectAsync(redis.GetConnectionString());
        store = new EdgeAuth::BffSessionStore(connection);
    }

    [DockerFact]
    public async Task Lists_a_subjects_own_sessions_and_nobody_elses()
    {
        await store.SaveAsync("sid-laptop", Session(Ada, createdAt: DateTimeOffset.UtcNow.AddHours(-3)));
        await store.SaveAsync("sid-phone", Session(Ada, createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)));
        await store.SaveAsync("sid-grace", Session(Grace));

        var listed = await store.ListAsync(Ada);

        // Newest first: the session somebody is most likely to recognise is the one they just
        // made, and the one they are looking for is the one they do not recognise at all.
        listed.Select(s => s.SessionId).Should().Equal("sid-phone", "sid-laptop");
        listed.Should().OnlyContain(s => s.Info.Subject == Ada);
    }

    [DockerFact]
    public async Task Carries_the_detail_the_account_page_shows()
    {
        var createdAt = DateTimeOffset.UtcNow.AddDays(-2);
        await store.SaveAsync("sid-1", Session(Ada, createdAt, ip: "203.0.113.7", userAgent: "Firefox/140"));

        var listed = await store.ListAsync(Ada);

        listed.Should().ContainSingle();
        listed[0].Info.Ip.Should().Be("203.0.113.7");
        listed[0].Info.UserAgent.Should().Be("Firefox/140");
        // Round-tripped through JSON, which is where a DateTimeOffset most often loses its
        // offset and starts reading as "in 2 hours".
        listed[0].Info.CreatedAt.Should().BeCloseTo(createdAt, TimeSpan.FromSeconds(1));
    }

    [DockerFact]
    public async Task Ending_a_session_takes_it_out_of_the_listing_too()
    {
        await store.SaveAsync("sid-1", Session(Ada));
        await store.SaveAsync("sid-2", Session(Ada));

        await store.RemoveAsync("sid-1");

        (await store.GetAsync("sid-1")).Should().BeNull();
        (await store.ListAsync(Ada)).Select(s => s.SessionId).Should().Equal("sid-2");
        // And the index does not keep the corpse: it is read on every visit to the account page,
        // and a member that is never removed is paid for on every one of them.
        (await connection.GetDatabase().SetMembersAsync($"edge:bff:user:{Ada}"))
            .Select(m => m.ToString()).Should().Equal("sid-2");
    }

    [DockerFact]
    public async Task Prunes_a_session_whose_row_expired_underneath_the_index()
    {
        await store.SaveAsync("sid-live", Session(Ada));
        await store.SaveAsync("sid-gone", Session(Ada));
        // What a 24-hour TTL does on its own, without going near RemoveAsync.
        await connection.GetDatabase().KeyDeleteAsync("edge:bff:sid-gone");

        (await store.ListAsync(Ada)).Select(s => s.SessionId).Should().Equal("sid-live");
        (await connection.GetDatabase().SetMembersAsync($"edge:bff:user:{Ada}"))
            .Select(m => m.ToString()).Should().Equal("sid-live");
    }

    [DockerFact]
    public async Task Reads_a_row_written_by_the_previous_release_as_no_session_at_all()
    {
        // The shape before the account page existed: tokens, and nothing to attribute them to.
        // Deserialising it leaves a session with no credential, and returning that would put a
        // null access token into the proxy pipeline. Signing that one operator out is the
        // correct answer — identity's own cookie completes the redirect without a prompt.
        await connection.GetDatabase().StringSetAsync(
            "edge:bff:sid-old",
            """{"AccessToken":"a","RefreshToken":"r","ExpiresAt":"2030-01-01T00:00:00+00:00"}""");

        (await store.GetAsync("sid-old")).Should().BeNull();
    }

    private static EdgeAuth::BffSession Session(
        string subject,
        DateTimeOffset? createdAt = null,
        string? ip = "198.51.100.4",
        string? userAgent = "Chrome/140")
    {
        var at = createdAt ?? DateTimeOffset.UtcNow;
        return new EdgeAuth::BffSession(
            new EdgeAuth::BffTokens("access", "refresh", at.AddMinutes(10)),
            new EdgeAuth::BffSessionInfo(subject, at, at, ip, userAgent));
    }

    public async ValueTask DisposeAsync()
    {
        if (connection is not null)
        {
            await connection.DisposeAsync();
        }
        await redis.DisposeAsync();
    }
}
