using System.Text.Json;
using StackExchange.Redis;

namespace Dcms.Edge.Auth;

/// <summary>The tokens one signed-in browser session holds, server-side.</summary>
/// <param name="AccessToken">Presented to admin-api and content-api as a bearer.</param>
/// <param name="RefreshToken">Redeemed by <see cref="BffTokenProvider"/> when the access token
/// is close to expiry. Rotated by OpenIddict on every redemption, which is why writing it back
/// has to be serialised — see <see cref="BffSessionStore.LockAsync"/>.</param>
/// <param name="ExpiresAt">When the access token stops being accepted, not when the session ends.</param>
public sealed record BffTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>
/// Everything about a session that is <i>not</i> a credential: what the operator is shown on the
/// account page, and the subject that lets us find their sessions at all.
/// </summary>
/// <param name="Subject">The <c>sub</c> claim. The index key — a session nobody can attribute to
/// a user cannot be listed to them, and therefore cannot be signed out by them.</param>
/// <param name="CreatedAt">When they signed in. Never rewritten.</param>
/// <param name="LastSeenAt">Last request this session made. Updated lazily — see
/// <see cref="BffTokenProvider"/> — so it is minutes-accurate, not seconds-accurate.</param>
/// <param name="Ip">The TCP peer as the edge saw it. The edge is the outermost hop, so this is
/// the real client address rather than something reconstructed from a header a client can set.</param>
/// <param name="UserAgent">Verbatim, and rendered by the console. Attacker-controlled text on a
/// page the victim reads, so it is data — never markup, never a link.</param>
/// <param name="LoginSessionId">The interactive login at identity this session was issued from
/// (identity's <c>LoginSessions</c>). Ending a session without ending this leaves identity's
/// cookie in that browser, and the next press of "Sign in" completes the authorization silently
/// — the device is back within one redirect and nothing looks wrong. Null on a session minted
/// before identity started issuing the claim.</param>
public sealed record BffSessionInfo(
    string Subject,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    string? Ip,
    string? UserAgent,
    string? LoginSessionId = null);

/// <summary>One row of the session store: the credential half and the visible half.</summary>
public sealed record BffSession(BffTokens Tokens, BffSessionInfo Info);

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
///
/// <para><b>One key per session, not two.</b> The tokens and the description of the session
/// share a row so they share a lifetime. Split across two keys they drift: a refresh slides the
/// token key's TTL and the metadata key expires underneath it, leaving a live session the
/// account page cannot describe and therefore cannot offer to sign out.</para>
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

    public async Task<BffSession?> GetAsync(string sessionId)
    {
        var value = await Db.StringGetAsync(Key(sessionId));
        if (value.IsNullOrEmpty)
        {
            return null;
        }

        // A row written before this shape existed deserialises with a null Tokens, and a
        // session with no credential is not a session. Treating it as absent signs that
        // operator out once, silently — identity's own cookie is still in the browser, so the
        // next navigation completes the redirect without a prompt.
        try
        {
            var session = JsonSerializer.Deserialize<BffSession>((byte[])value!);
            return session?.Tokens is null || session.Info is null ? null : session;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the row and keeps the owner's index in step, so a session is listable for exactly
    /// as long as it is usable.
    /// </summary>
    public async Task SaveAsync(string sessionId, BffSession session)
    {
        await Db.StringSetAsync(Key(sessionId), JsonSerializer.SerializeToUtf8Bytes(session), Ttl);
        await Db.SetAddAsync(IndexKey(session.Info.Subject), sessionId);
        // Slides with the newest session rather than expiring on the oldest one's schedule.
        // Worst case the index outlives its members, which ListAsync prunes on sight.
        await Db.KeyExpireAsync(IndexKey(session.Info.Subject), Ttl);
    }

    /// <summary>
    /// Ends the session server-side. What makes sign-out mean something.
    ///
    /// <para>No call to identity's revocation endpoint: the refresh token existed only in this
    /// row, so deleting it is what makes it unreachable. The browser keeps a cookie that now
    /// names nothing, which <c>/.edge/me</c> reports as signed out.</para>
    /// </summary>
    public async Task RemoveAsync(string sessionId)
    {
        // Read first, for the subject — otherwise the index keeps a member forever and every
        // listing pays to discover it is dead.
        var session = await GetAsync(sessionId);
        await Db.KeyDeleteAsync(Key(sessionId));
        if (session is not null)
        {
            await Db.SetRemoveAsync(IndexKey(session.Info.Subject), sessionId);
        }
    }

    /// <summary>
    /// Every live session this subject holds, newest first, pruning index members whose row has
    /// expired or been signed out from another tab.
    ///
    /// <para>ponytail: N round trips for N sessions, where N is how many browsers one person is
    /// signed in on. Called from one account page, not a request path.</para>
    /// </summary>
    public async Task<IReadOnlyList<(string SessionId, BffSessionInfo Info)>> ListAsync(string subject)
    {
        var members = await Db.SetMembersAsync(IndexKey(subject));
        var live = new List<(string SessionId, BffSessionInfo Info)>(members.Length);

        foreach (var member in members)
        {
            var sessionId = member.ToString();
            var session = await GetAsync(sessionId);
            if (session is null)
            {
                await Db.SetRemoveAsync(IndexKey(subject), sessionId);
                continue;
            }
            // Only sessions that really belong to this subject, even though they were found
            // under its index. The index is the only thing standing between "sign out my other
            // devices" and "sign out somebody else's", so it is checked rather than trusted.
            if (string.Equals(session.Info.Subject, subject, StringComparison.Ordinal))
            {
                live.Add((sessionId, session.Info));
            }
        }

        return live.OrderByDescending(s => s.Info.CreatedAt).ToList();
    }

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

    /// <summary>The set of session ids one subject holds. Keyed by <c>sub</c>, which is a uuid
    /// the identity server issues and not anything a caller supplies.</summary>
    private static string IndexKey(string subject) => $"edge:bff:user:{subject}";

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
