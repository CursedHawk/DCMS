using Microsoft.AspNetCore.DataProtection;

namespace Dcms.Edge.Auth;

/// <summary>
/// The two cross-site checks a cookie-authenticated console needs and a bearer-authenticated
/// one did not (ADR 0014 §3).
///
/// <para><b>Why this is not the usual "set SameSite and move on".</b> SameSite is decided per
/// <i>site</i>, by registrable domain. Tenant sites on <c>*.dcms.highgeek.eu</c> and the console
/// on <c>admin.highgeek.eu</c> share <c>highgeek.eu</c>, so a request from a page a tenant
/// authored is <b>same-site</b> and a Lax cookie rides along on any method. Lax covers tenant
/// custom domains and nothing else, and the managed subdomains are exactly the ones a tenant can
/// put arbitrary HTML on.</para>
///
/// <para>admin-api registers no CORS and every console request carries <c>X-Dcms-Request-Id</c>,
/// so JSON calls already fail preflight from a foreign origin. What that does not cover is
/// <c>multipart/form-data</c>, which is a CORS-simple content type: a media upload POSTed from a
/// tenant page would be <i>sent</i> with the cookie. The attacker cannot read the response; the
/// write still happens. Hence a real check.</para>
///
/// <para>Both live here as pure functions so the decision can be asserted without standing up a
/// proxy pipeline to ask it — the same reason <c>IdentityHeaders.Resolve</c> is shaped this way.</para>
/// </summary>
public static class BffGuard
{
    /// <summary>The double-submit token's header. Custom, so it also forces a preflight.</summary>
    public const string CsrfHeaderName = "X-Dcms-Csrf";

    /// <summary>Data-protection purpose for the CSRF token. Its own, so it cannot be confused
    /// with the authentication ticket's.</summary>
    public const string CsrfPurpose = "Dcms.Edge.Bff.Csrf.v1";

    /// <summary>
    /// Methods that do not change state, and therefore need no cross-site check. A GET that
    /// changes state would be the real defect.
    /// </summary>
    private static readonly HashSet<string> SafeMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS", "TRACE" };

    public static bool IsSafeMethod(string method) => SafeMethods.Contains(method);

    /// <summary>
    /// Whether this is a WebSocket handshake — which is neither a safe request nor one that can
    /// carry a CSRF token, and so needs a rule of its own.
    ///
    /// <para><b>The method is not GET when it matters.</b> Over HTTP/1.1 a handshake is a GET
    /// with <c>Upgrade: websocket</c>. Over HTTP/2 it is an extended CONNECT (RFC 8441), which
    /// is what a browser actually sends to this edge, because Kestrel negotiates h2 by ALPN. A
    /// check that keyed on GET therefore let the HTTP/1.1 shape through unexamined and refused
    /// the HTTP/2 one as a CSRF failure — the second of which is what took the notification,
    /// site and chat hubs down at the phase-4 cutover, in two services at once, with REST
    /// working perfectly beside it.</para>
    ///
    /// <para><c>IsWebSocketRequest</c> is the answer ASP.NET gives for both shapes, and
    /// <c>EdgeRateLimiting</c> already exempts long-lived connections by the same question. The
    /// CONNECT fallback is belt and braces: if that feature is ever not populated, an extended
    /// CONNECT must still not be mistaken for an ordinary state-changing request.</para>
    /// </summary>
    public static bool IsWebSocketHandshake(bool isWebSocketRequest, string method)
        => isWebSocketRequest || string.Equals(method, "CONNECT", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this request has to present a CSRF token.
    ///
    /// <para>A WebSocket handshake never does, and not as a concession: a browser cannot set a
    /// custom header on one. That is the same constraint that makes SignalR put its token in the
    /// query string. What protects a handshake instead is the origin check below, which is the
    /// standard defence against cross-site WebSocket hijacking and which browsers always supply
    /// an <c>Origin</c> for.</para>
    /// </summary>
    public static bool RequiresCsrf(string method, bool isWebSocketHandshake)
        => !isWebSocketHandshake && !IsSafeMethod(method);

    /// <summary>
    /// Whether the edge may present its own session as this request's credential.
    ///
    /// <para>Three terms since phase 5. It used to take a fourth — whether the caller brought
    /// its own <c>Authorization</c> header, which won — because that is what let every phase
    /// before the cutover be a no-op for a console that still held a token. Nothing holds one
    /// now: <c>dcms-admin-spa</c> no longer carries the <c>dcms.admin</c> scope, so no browser
    /// client can obtain a token for admin-api at all. A bearer arriving on one of these routes
    /// is therefore not a caller to defer to; the middleware strips it.</para>
    /// </summary>
    /// <param name="bffEnabled"><c>Edge:Auth:Bff</c>, and a client secret to be confidential
    /// with.</param>
    /// <param name="bffRoute">Route metadata. Only the admin host's own API routes carry it; a
    /// public-plane route never will.</param>
    /// <param name="sessionId">Present only on a session the edge minted for the BFF. A Grafana
    /// or Forgejo cookie has no session id and must not be mistaken for one.</param>
    public static bool ShouldAuthenticate(bool bffEnabled, bool bffRoute, string? sessionId)
        => bffEnabled
           && bffRoute
           && !string.IsNullOrEmpty(sessionId);

    /// <summary>
    /// Whether this request may carry the session's authority, judged on where it came from.
    ///
    /// <para><c>Sec-Fetch-Site</c> is checked first and is decisive when present: it is set by
    /// the browser, cannot be set by script, and says <c>same-origin</c> only for a request the
    /// console itself made. <c>Origin</c> is the fallback for anything that does not send it.
    /// An unsafe request with neither is refused — every browser sends <c>Origin</c> on
    /// POST/PUT/PATCH/DELETE, so a request without one is not a browser doing what this session
    /// exists for.</para>
    /// </summary>
    public static bool IsSameOrigin(
        string method, string? origin, string? secFetchSite, string expectedOrigin,
        bool isWebSocketHandshake = false)
    {
        // A handshake is checked even though its HTTP/1.1 shape is a GET. Treating it as a safe
        // read would leave cross-site WebSocket hijacking wide open: a page on a tenant's
        // subdomain can open a socket to this host, the session cookie rides along because the
        // two are same-site, and from then on it is talking to the hub as the operator. The
        // origin check is the whole of the defence there, because no CSRF token can be sent.
        if (!isWebSocketHandshake && IsSafeMethod(method))
        {
            return true;
        }
        if (!string.IsNullOrEmpty(secFetchSite))
        {
            return string.Equals(secFetchSite, "same-origin", StringComparison.OrdinalIgnoreCase);
        }
        return !string.IsNullOrEmpty(origin)
               && string.Equals(origin, expectedOrigin, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The origin the console is served from, as a browser spells it in a header.</summary>
    public static string OriginOf(string host) => $"https://{host}";

    /// <summary>
    /// A CSRF token for one session. Data protection rather than a hand-rolled HMAC: it is
    /// authenticated encryption over the same key ring the auth cookie already uses, and it is
    /// one call.
    ///
    /// <para>Bound to the session id, which is what stops a token minted for the attacker's own
    /// session being replayed into the victim's — the failure a bare random double-submit value
    /// has.</para>
    /// </summary>
    public static string MintCsrf(IDataProtectionProvider provider, string sessionId)
        => provider.CreateProtector(CsrfPurpose).Protect(sessionId);

    /// <summary>
    /// Whether the presented token belongs to this session. No TTL: it dies with the session,
    /// and a token good for exactly as long as the session it names is not a window worth
    /// narrowing.
    /// </summary>
    public static bool VerifyCsrf(IDataProtectionProvider provider, string sessionId, string? presented)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }
        try
        {
            return provider.CreateProtector(CsrfPurpose).Unprotect(presented) == sessionId;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Tampered, truncated, or protected under a key ring this process no longer has.
            return false;
        }
    }
}
