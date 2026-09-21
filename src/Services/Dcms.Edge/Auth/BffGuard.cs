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
    /// Whether the edge may present its own session as this request's credential. Every way
    /// phase 1 of ADR 0014 stays inert is one of these four terms, which is why it is a
    /// function with a test rather than a condition in the middle of a middleware.
    /// </summary>
    /// <param name="bffEnabled"><c>Edge:Auth:Bff</c>, and a client secret to be confidential
    /// with. Off until an operator turns it on.</param>
    /// <param name="bffRoute">Route metadata. Only the admin host's <c>/api</c> and <c>/hub</c>
    /// carry it; a public-plane route never will.</param>
    /// <param name="hasAuthorizationHeader">The console sends its own bearer until its phase,
    /// and so does every older bundle still in a browser. Theirs wins, untouched.</param>
    /// <param name="sessionId">Present only on a session the edge minted for the BFF. A Grafana
    /// or Forgejo cookie has no session id and must not be mistaken for one.</param>
    public static bool ShouldAuthenticate(
        bool bffEnabled, bool bffRoute, bool hasAuthorizationHeader, string? sessionId)
        => bffEnabled
           && bffRoute
           && !hasAuthorizationHeader
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
    public static bool IsSameOrigin(string method, string? origin, string? secFetchSite, string expectedOrigin)
    {
        if (IsSafeMethod(method))
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
