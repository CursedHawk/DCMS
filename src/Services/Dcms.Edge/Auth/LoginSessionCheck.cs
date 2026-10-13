using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Dcms.Edge.Auth;

/// <summary>
/// Ends an edge session when the identity login behind it ends.
///
/// <para><b>The defect.</b> Signing out — from either console, from the account page's session
/// list, or by removing the account on the sign-in page — ends the login at identity. The
/// edge's own cookie never heard about it. On the Grafana and git hosts it carries only claims
/// and lives eight hours, so both stayed signed in for the rest of the day; on the admin host
/// the console stayed signed in until its access token next needed refreshing.</para>
///
/// <para>So every edge cookie is checked against identity on the way in, by the login id it was
/// issued with (<see cref="EdgeAuthentication.LoginSessionClaim"/>). One question per login per
/// <see cref="Fresh"/>, from a cache, so a dashboard's hundred panel queries cost one lookup.</para>
///
/// <para><b>Switching follows too.</b> Picking another account — on the sign-in page, from either
/// console — switches the whole browser. The previous account's login stays alive (it is one
/// click away on the account list), so it is not "ended"; identity reports it as
/// <see cref="LoginState.Switched"/> instead, and the session is dropped the same way. On the
/// Grafana and git hosts the next request then challenges, which identity completes silently as
/// the browser's account; the admin console is told by <see cref="SwitchedHeader"/> to do the
/// same.</para>
///
/// <para><b>Fails open.</b> If identity cannot be asked, the session stands. The edge is how an
/// operator reaches Grafana to find out why identity is down; refusing every session at that
/// moment would take the diagnostic tool away with the thing being diagnosed. The cost is that a
/// sign-out made while identity is unreachable lands when it comes back.</para>
/// </summary>
public sealed class LoginSessionCheck(
    HttpClient http,
    IMemoryCache cache,
    IOptions<EdgeAuthOptions> options,
    ILogger<LoginSessionCheck> logger)
{
    /// <summary>How stale an answer may be: the longest a signed-out or switched-away session
    /// survives. Short, because a switch is something the operator does and then looks for.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(15);

    /// <summary>On a response whose session was dropped because the browser switched accounts,
    /// so the admin console signs in again (silently, as that account) instead of showing its
    /// sign-in screen. Read by <c>@dcms/core</c>'s BFF client.</summary>
    public const string SwitchedHeader = "X-Dcms-Session";
    public const string SwitchedValue = "switched";

    /// <summary>After a failed lookup, how long to stop asking — so an identity outage is one
    /// timeout a quarter-minute, not one per request.</summary>
    private static readonly TimeSpan Backoff = TimeSpan.FromSeconds(15);

    public async Task<LoginState> StateAsync(string loginSessionId, CancellationToken ct, bool fresh = false)
    {
        var key = KeyOf(loginSessionId);
        if (!fresh && cache.TryGetValue(key, out LoginState cached))
        {
            return cached;
        }

        var auth = options.Value;
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{auth.InternalAuthority.TrimEnd('/')}/edge/login-sessions/{Uri.EscapeDataString(loginSessionId)}");
            // RFC 6749 §2.3.1, as for the token endpoint: each half form-encoded, then joined.
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(auth.ClientId)}:{Uri.EscapeDataString(auth.ClientSecret)}")));

            using var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var answer = await response.Content.ReadFromJsonAsync<Answer>(ct);
            var state = answer switch
            {
                { Ended: true } => LoginState.Ended,
                { Switched: true } => LoginState.Switched,
                _ => LoginState.Live,
            };
            cache.Set(key, state, Fresh);
            return state;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                                   && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not ask identity whether a login has ended; keeping the session for now.");
            cache.Set(key, LoginState.Live, Backoff);
            return LoginState.Live;
        }
    }

    /// <summary>Drops a cached answer. Called when this browser signs in at the edge as someone
    /// — the login it held until now has just been switched away from, and the Grafana and git
    /// cookies still carrying it should find out on their next request, not a minute later.</summary>
    public void Forget(string loginSessionId) => cache.Remove(KeyOf(loginSessionId));

    private static string KeyOf(string loginSessionId) => $"edge:lsid:{loginSessionId}";

    /// <summary>
    /// The cookie handler's validation hook. A ticket with no login id (issued before identity
    /// sent one) is left alone — there is nothing to ask about it.
    /// </summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        if (context.Principal?.FindFirst(EdgeAuthentication.LoginSessionClaim)?.Value is not { Length: > 0 } loginSessionId)
        {
            return;
        }

        var state = await context.HttpContext.RequestServices.GetRequiredService<LoginSessionCheck>()
            .StateAsync(loginSessionId, context.HttpContext.RequestAborted);
        if (state == LoginState.Live)
        {
            return;
        }
        context.RejectPrincipal();
        await EndAsync(context.HttpContext, context.Principal, state);
    }

    /// <summary>
    /// Ends this browser's edge session for a login that is no longer live: the BFF row too, so
    /// the admin console's tokens die with the cookie rather than sitting in Redis (and on the
    /// account page's device list) until their own expiry. A switch is said so on the response,
    /// for the admin console to sign in again rather than out.
    /// </summary>
    public static async Task EndAsync(HttpContext http, System.Security.Claims.ClaimsPrincipal? principal, LoginState state)
    {
        if (state == LoginState.Switched)
        {
            http.Response.Headers[SwitchedHeader] = SwitchedValue;
        }
        if (BffTokenProvider.SessionIdOf(principal) is { Length: > 0 } sessionId)
        {
            await http.RequestServices.GetRequiredService<BffSessionStore>().RemoveAsync(sessionId);
        }
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    private sealed record Answer(bool Ended, bool Switched);
}

public enum LoginState
{
    Live,
    /// <summary>Signed out, removed, expired, or its password changed.</summary>
    Ended,
    /// <summary>Alive, but its browser has signed in as another account since.</summary>
    Switched,
}
