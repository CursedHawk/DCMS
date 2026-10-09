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
    /// <summary>How stale an answer may be: the longest a signed-out session survives.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(60);

    /// <summary>After a failed lookup, how long to stop asking — so an identity outage is one
    /// timeout a quarter-minute, not one per request.</summary>
    private static readonly TimeSpan Backoff = TimeSpan.FromSeconds(15);

    public async Task<bool> HasEndedAsync(string loginSessionId, CancellationToken ct)
    {
        var key = $"edge:lsid:{loginSessionId}";
        if (cache.TryGetValue(key, out bool cached))
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
            var ended = (await response.Content.ReadFromJsonAsync<Answer>(ct))?.Ended ?? false;
            cache.Set(key, ended, Fresh);
            return ended;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                                   && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not ask identity whether a login has ended; keeping the session for now.");
            cache.Set(key, false, Backoff);
            return false;
        }
    }

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

        var services = context.HttpContext.RequestServices;
        if (!await services.GetRequiredService<LoginSessionCheck>()
                .HasEndedAsync(loginSessionId, context.HttpContext.RequestAborted))
        {
            return;
        }

        // The BFF row too, so the admin console's tokens die with the cookie rather than
        // sitting in Redis until their own expiry.
        if (BffTokenProvider.SessionIdOf(context.Principal) is { Length: > 0 } sessionId)
        {
            await services.GetRequiredService<BffSessionStore>().RemoveAsync(sessionId);
        }
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    private sealed record Answer(bool Ended);
}
