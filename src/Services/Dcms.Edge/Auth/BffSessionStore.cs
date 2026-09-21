using System.Text.Json;
using StackExchange.Redis;

namespace Dcms.Edge.Auth;

/// <summary>The tokens one signed-in browser session holds, server-side.</summary>
/// <param name="AccessToken">Presented to admin-api and content-api as a bearer.</param>
/// <param name="RefreshToken">Redeemed by <see cref="BffTokenProvider"/> when the access token
/// is close to expiry. Rotated by OpenIddict on every redemption, which is why writing it back
/// has to be serialised — see <see cref="LockAsync"/>.</param>
/// <param name="ExpiresAt">When the access token stops being accepted, not when the session ends.</param>
public sealed record BffTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>
/// Where an operator's API tokens live once the edge signs them in (ADR 0014).
///
/// <para><b>Not in the cookie.</b> The obvious alternative is <c>SaveTokens = true</c>, which
/// puts the access and refresh tokens into the authentication ticket and therefore into the
/// browser. That fails three ways at once: the ticket outgrows the 4 KB cookie limit and
/// chunks; every refresh has to rewrite the cookie, so two concurrent requests race and the
/// loser writes back a refresh token OpenIddict has already rotated away; and a session held
/// entirely by the client cannot be revoked. Here the cookie carries an opaque session id and
/// nothing else, and the row behind it can be deleted.</para>
///
/// <para>Redis rather than Postgres for the same reason <c>AcmeChallengeStore</c> is: this is
/// hot, per-request, short-lived state, and the edge already requires Redis to be healthy. A
/// flush signs everyone out; it does not lock anyone out, because identity's own cookie is
/// still in the browser and the next request completes the OIDC redirect without a prompt.</para>
/// </summary>
public sealed class BffSessionStore(IConnectionMultiplexer redis)
{
    /// <summary>
    /// The claim carrying the session id. Minted at sign-in and meaningless on its own — it
    /// names a Redis key, so a forged one finds nothing.
    /// </summary>
    public const string SessionIdClaim = "dcms_sid";

    /// <summary>
    /// How long a session's tokens outlive their last use. Longer than
    /// <c>EdgeAuthOptions.SessionHours</c> so the cookie is always the thing that expires
    /// first — a session whose tokens vanished underneath a live cookie reads as a random
    /// sign-out, which is the least diagnosable failure this can have.
    /// </summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    /// <summary>How long a refresh lock is held before it is assumed abandoned.</summary>
    private static readonly TimeSpan LockTtl = TimeSpan.FromSeconds(10);

    private IDatabase Db => redis.GetDatabase();

    public async Task<BffTokens?> GetAsync(string sessionId)
    {
        var value = await Db.StringGetAsync(Key(sessionId));
        return value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<BffTokens>((byte[])value!);
    }

    public Task SaveAsync(string sessionId, BffTokens tokens)
        => Db.StringSetAsync(Key(sessionId), JsonSerializer.SerializeToUtf8Bytes(tokens), Ttl);

    /// <summary>Ends the session server-side. What makes sign-out mean something.</summary>
    public Task RemoveAsync(string sessionId) => Db.KeyDeleteAsync(Key(sessionId));

    /// <summary>
    /// Takes the per-session refresh lock, or returns null if someone else holds it.
    ///
    /// <para>OpenIddict rotates refresh tokens: redeeming one revokes it and issues another. So
    /// two requests that both notice an expired access token and both redeem the same refresh
    /// token produce one new session and one <c>invalid_grant</c> — ending a session that was
    /// perfectly healthy, under load, intermittently. The winner refreshes; the losers wait and
    /// re-read what the winner wrote.</para>
    ///
    /// <para>ponytail: a Redis <c>SET NX</c> with a TTL, which is correct for the one case that
    /// matters (concurrent requests on one session) and does not pretend to be a distributed
    /// lock. The failure mode of losing it is a request that falls back to the token it already
    /// has, not a corrupted session. Upgrade path if edge replicas ever make this hot: an
    /// in-process single-flight per session id in front of this.</para>
    ///
    /// <para><c>IConnectionMultiplexer</c> directly rather than <c>ICacheService</c>: this needs
    /// <c>SET NX</c> and a delete-if-mine, and that JSON cache interface has no verb for either.</para>
    /// </summary>
    public async Task<IAsyncDisposable?> LockAsync(string sessionId)
    {
        var token = Guid.NewGuid().ToString("N");
        var taken = await Db.StringSetAsync(LockKey(sessionId), token, LockTtl, When.NotExists);
        return taken ? new Lock(Db, LockKey(sessionId), token) : null;
    }

    private static string Key(string sessionId) => $"edge:bff:{sessionId}";

    private static string LockKey(string sessionId) => $"edge:bff:lock:{sessionId}";

    /// <summary>
    /// Releases only if the value is still ours, so a lock that already expired and was retaken
    /// by someone else is not deleted out from under them.
    /// </summary>
    private sealed class Lock(IDatabase db, string key, string token) : IAsyncDisposable
    {
        private const string ReleaseScript =
            "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";

        public async ValueTask DisposeAsync()
        {
            try
            {
                await db.ScriptEvaluateAsync(ReleaseScript, [key], [token]);
            }
            catch (RedisException)
            {
                // The lock expires on its own. Failing to release it is a delay, not a defect,
                // and throwing from a dispose would replace it with a 500.
            }
        }
    }
}
