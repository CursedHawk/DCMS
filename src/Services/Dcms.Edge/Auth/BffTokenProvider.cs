using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace Dcms.Edge.Auth;

/// <summary>
/// Hands out a currently-valid access token for a signed-in browser session, refreshing it
/// when it is about to expire (ADR 0014).
///
/// <para>Access tokens live 10 minutes and console sessions live 8 hours, so refreshing is the
/// normal path rather than the exceptional one. Everything about it that is interesting is in
/// <see cref="BffSessionStore.LockAsync"/>: OpenIddict rotates refresh tokens, so concurrent
/// redemptions of the same one kill the session.</para>
/// </summary>
public sealed class BffTokenProvider(
    BffSessionStore sessions,
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
    IOptions<EdgeAuthOptions> authOptions,
    ILogger<BffTokenProvider> logger)
{
    /// <summary>
    /// Refresh this far ahead of expiry. Covers the round trip to the destination plus clock
    /// skew between the edge and identity — a token that expires in flight is a 401 the SPA
    /// cannot distinguish from being signed out.
    /// </summary>
    private static readonly TimeSpan RefreshWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How stale "last seen" is allowed to get before a read writes it back. Coarse on purpose:
    /// this is a line on an account page, and a write per request would turn every proxied call
    /// into a Redis round trip to record something nobody reads at that resolution.
    /// </summary>
    private static readonly TimeSpan SeenResolution = TimeSpan.FromMinutes(1);

    /// <summary>The session id this principal carries, or null if it is not a BFF session.</summary>
    public static string? SessionIdOf(ClaimsPrincipal? user)
        => user?.FindFirst(BffSessionStore.SessionIdClaim)?.Value;

    /// <summary>
    /// The access token to present downstream, or null when there is no usable session — which
    /// the caller must treat as "not signed in" rather than as an error. A session whose
    /// refresh token identity has revoked lands here, and the right answer is the sign-in
    /// redirect, not a 500 on the public ingress.
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(ClaimsPrincipal? user, CancellationToken ct)
    {
        if (SessionIdOf(user) is not { Length: > 0 } sessionId)
        {
            return null;
        }

        var session = await sessions.GetAsync(sessionId);
        if (session is null)
        {
            return null;
        }
        if (session.Tokens.ExpiresAt - DateTimeOffset.UtcNow > RefreshWindow)
        {
            await TouchAsync(sessionId, session);
            return session.Tokens.AccessToken;
        }

        await using var held = await sessions.LockAsync(sessionId);
        if (held is null)
        {
            // Someone else is refreshing this session right now. Re-read rather than queue: by
            // the time a second redemption of the same rotated token could be attempted it
            // would already be revoked.
            var refreshed = await sessions.GetAsync(sessionId);
            return refreshed?.Tokens.AccessToken ?? session.Tokens.AccessToken;
        }

        // Re-read inside the lock. Between noticing the expiry and taking the lock, the holder
        // may have finished — in which case there is nothing left to do.
        session = await sessions.GetAsync(sessionId) ?? session;
        if (session.Tokens.ExpiresAt - DateTimeOffset.UtcNow > RefreshWindow)
        {
            return session.Tokens.AccessToken;
        }

        var renewed = await RedeemAsync(session.Tokens.RefreshToken, ct);
        if (renewed is null)
        {
            // Revoked, expired, or identity is down. Drop the session so the next request goes
            // through the sign-in redirect instead of retrying a token that will never work.
            await sessions.RemoveAsync(sessionId);
            return null;
        }

        await sessions.SaveAsync(
            sessionId,
            session with { Tokens = renewed, Info = session.Info with { LastSeenAt = DateTimeOffset.UtcNow } });
        return renewed.AccessToken;
    }

    /// <summary>
    /// Records that this session is still in use, at most once per <see cref="SeenResolution"/>.
    ///
    /// <para>Two concurrent requests can both decide to write; they write the same minute and
    /// the same tokens, so the loser is a duplicate rather than a lost update. Not worth the
    /// lock — unlike a refresh, where the loser destroys a rotated token.</para>
    /// </summary>
    private async Task TouchAsync(string sessionId, BffSession session)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - session.Info.LastSeenAt < SeenResolution)
        {
            return;
        }
        await sessions.SaveAsync(sessionId, session with { Info = session.Info with { LastSeenAt = now } });
    }

    /// <summary>
    /// The refresh_token grant, over the OIDC handler's own back channel — which carries
    /// <see cref="InternalIdentityHandler"/>, so the call reaches identity's internal address
    /// while the discovery document keeps advertising the public one.
    /// </summary>
    private async Task<BffTokens?> RedeemAsync(string refreshToken, CancellationToken ct)
    {
        var options = oidcOptions.Get(OpenIdConnectDefaults.AuthenticationScheme);
        var auth = authOptions.Value;
        try
        {
            var configuration = await options.ConfigurationManager!.GetConfigurationAsync(ct);
            using var request = new HttpRequestMessage(HttpMethod.Post, configuration.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken,
                    ["client_id"] = auth.ClientId,
                    ["client_secret"] = auth.ClientSecret,
                }),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await options.Backchannel.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                // Expected often enough to be information rather than an error: a refresh token
                // is revoked by a password change, a lock, or a sign-out elsewhere (SEC-05).
                logger.LogInformation(
                    "Edge BFF refresh refused with {Status}; the session will be signed out.",
                    (int)response.StatusCode);
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
            var body = document.RootElement;
            return Create(
                Text(body, "access_token"),
                Text(body, "refresh_token"),
                Text(body, "expires_in"),
                previousRefreshToken: refreshToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Edge BFF refresh failed; the session will be signed out.");
            return null;
        }
    }

    /// <summary>
    /// The one place a token response becomes a stored session, so the sign-in path (which has
    /// a typed <c>OpenIdConnectMessage</c>) and the refresh path (which has raw JSON) cannot
    /// disagree about the rules. Returns null when the response is not usable.
    /// </summary>
    /// <param name="previousRefreshToken">Kept when the response carries no new one. Rotation is
    /// OpenIddict's default, but it is a server setting; assuming rotation and dropping the old
    /// token would end every session at the first refresh on a server that does not rotate.</param>
    public static BffTokens? Create(
        string? accessToken, string? refreshToken, string? expiresInSeconds, string? previousRefreshToken)
    {
        if (string.IsNullOrEmpty(accessToken))
        {
            return null;
        }

        refreshToken = string.IsNullOrEmpty(refreshToken) ? previousRefreshToken : refreshToken;
        if (string.IsNullOrEmpty(refreshToken))
        {
            // Without one the session dies silently at the 10-minute mark. Better to refuse the
            // sign-in and land on the sign-in page than to hand out a session with an expiry
            // nobody expects.
            return null;
        }

        var lifetime = int.TryParse(expiresInSeconds, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromMinutes(10);

        return new BffTokens(accessToken, refreshToken, DateTimeOffset.UtcNow.Add(lifetime));
    }

    /// <summary>A string member of a token response, or null when it is absent or not a string.</summary>
    private static string? Text(JsonElement body, string name)
        => body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
