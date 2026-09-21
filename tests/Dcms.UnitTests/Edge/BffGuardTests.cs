using Dcms.Edge.Auth;
using Microsoft.AspNetCore.DataProtection;

namespace Dcms.UnitTests.Edge;

/// <summary>
/// The two cross-site checks the console's BFF rests on (ADR 0014 §3).
///
/// <para>These matter more here than the usual CSRF boilerplate, because the usual answer does
/// not apply: tenant sites on <c>*.dcms.highgeek.eu</c> share a registrable domain with
/// <c>admin.highgeek.eu</c>, so a page a tenant authored is <b>same-site</b> with the console
/// and <c>SameSite=Lax</c> sends the session cookie on its POSTs. The cases below are the
/// replacement for that, so each one is written as the attack it refuses.</para>
/// </summary>
public class BffGuardTests
{
    private const string Console = "https://admin.example.test";
    private const string TenantSite = "https://acme.dcms.example.test";

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public void Reads_need_no_cross_site_check(string method)
    {
        // A read carries no state change, and a GET that does would be the real defect.
        BffGuard.IsSameOrigin(method, origin: null, secFetchSite: null, Console).Should().BeTrue();
    }

    [Fact]
    public void Allows_the_console_talking_to_itself()
    {
        BffGuard.IsSameOrigin("POST", Console, "same-origin", Console).Should().BeTrue();
    }

    [Fact]
    public void Refuses_a_write_from_a_tenant_page_on_the_same_registrable_domain()
    {
        // The whole reason this class exists. The browser calls this same-site, so SameSite=Lax
        // attaches the session cookie; Sec-Fetch-Site still says it did not come from us.
        BffGuard.IsSameOrigin("POST", TenantSite, "same-site", Console).Should().BeFalse();
        BffGuard.IsSameOrigin("POST", TenantSite, secFetchSite: null, Console).Should().BeFalse();
    }

    [Fact]
    public void Refuses_a_multipart_upload_forged_from_another_origin()
    {
        // multipart/form-data is CORS-simple, so this request is SENT rather than preflighted:
        // the attacker cannot read the reply, but without this check the upload happens.
        BffGuard.IsSameOrigin("POST", "https://evil.example", "cross-site", Console).Should().BeFalse();
    }

    [Fact]
    public void Refuses_a_write_that_names_no_origin_at_all()
    {
        // Every browser sends Origin on an unsafe method, so a request without one is not the
        // thing this session exists to serve. Failing open here would undo the whole check.
        BffGuard.IsSameOrigin("POST", origin: null, secFetchSite: null, Console).Should().BeFalse();
    }

    [Fact]
    public void Believes_the_browsers_own_header_over_a_matching_Origin()
    {
        // Sec-Fetch-Site cannot be set by script; Origin on its own is the weaker signal. If
        // they ever disagree, the forgeable one does not get to win.
        BffGuard.IsSameOrigin("POST", Console, "cross-site", Console).Should().BeFalse();
    }

    /// <summary>
    /// The regression that took the hubs down at the phase-4 cutover, in the shape it actually
    /// arrived in.
    ///
    /// <para>A WebSocket handshake over HTTP/1.1 is a GET; over HTTP/2 it is an extended CONNECT
    /// (RFC 8441), and that is what a browser sends to an edge that negotiates h2 — which this
    /// one does. CONNECT is not a safe method, so the CSRF branch demanded a header that a
    /// handshake cannot carry, and every hub got 403 from the edge while REST worked beside it.
    /// Both shapes are here because keying on either one alone is exactly the bug.</para>
    /// </summary>
    [Theory]
    [InlineData(true, "CONNECT")]
    [InlineData(true, "GET")]
    [InlineData(false, "CONNECT")]
    public void A_websocket_handshake_is_never_asked_for_a_csrf_token(bool isWebSocketRequest, string method)
    {
        var handshake = BffGuard.IsWebSocketHandshake(isWebSocketRequest, method);

        handshake.Should().BeTrue();
        BffGuard.RequiresCsrf(method, handshake).Should().BeFalse(
            "a browser cannot set a custom header on a WebSocket handshake, which is the same "
            + "constraint that makes SignalR put its token in the query string");
    }

    [Fact]
    public void An_ordinary_write_is_still_asked_for_one()
    {
        BffGuard.RequiresCsrf("POST", BffGuard.IsWebSocketHandshake(false, "POST")).Should().BeTrue();
        BffGuard.RequiresCsrf("GET", BffGuard.IsWebSocketHandshake(false, "GET")).Should().BeFalse();
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("CONNECT")]
    public void A_websocket_handshake_from_a_tenant_page_is_refused(string method)
    {
        // Cross-site WebSocket hijacking, which the origin check is the whole defence against
        // because no token can be sent. Note the HTTP/1.1 shape: keying the check on "is this a
        // safe method" let a GET handshake past unexamined, so fixing the 403 by exempting
        // handshakes from BOTH checks would have traded an outage for a hole.
        BffGuard.IsSameOrigin(method, TenantSite, secFetchSite: null, Console, isWebSocketHandshake: true)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("CONNECT")]
    public void A_websocket_handshake_from_the_console_itself_is_allowed(string method)
    {
        // Browsers send no Sec-Fetch-Site on a handshake, so Origin is what decides.
        BffGuard.IsSameOrigin(method, Console, secFetchSite: null, Console, isWebSocketHandshake: true)
            .Should().BeTrue();
    }

    [Fact]
    public void A_csrf_token_is_only_good_for_the_session_it_was_minted_for()
    {
        var protection = Protection();
        var mine = BffGuard.MintCsrf(protection, "session-a");

        BffGuard.VerifyCsrf(protection, "session-a", mine).Should().BeTrue();
        // The failure a bare random double-submit value has: an attacker mints one against
        // their own session and replays it into the victim's request.
        BffGuard.VerifyCsrf(protection, "session-b", mine).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    public void A_missing_or_junk_csrf_token_is_refused_rather_than_thrown(string? presented)
    {
        // Unprotect throws on anything it did not write. A 500 on the public ingress for a
        // malformed header would be a denial of service with extra steps.
        BffGuard.VerifyCsrf(Protection(), "session-a", presented).Should().BeFalse();
    }

    [Fact]
    public void A_token_from_a_different_key_ring_is_refused()
    {
        var mine = BffGuard.MintCsrf(Protection(), "session-a");

        BffGuard.VerifyCsrf(Protection(), "session-a", mine).Should().BeFalse();
    }

    [Fact]
    public void The_expected_origin_is_the_host_as_a_browser_spells_it()
    {
        BffGuard.OriginOf("admin.example.test").Should().Be(Console);
    }

    [Fact]
    public void Authenticates_from_the_session_only_when_all_four_terms_hold()
    {
        BffGuard.ShouldAuthenticate(
            bffEnabled: true, bffRoute: true, hasAuthorizationHeader: false, sessionId: "sid-1")
            .Should().BeTrue();
    }

    /// <summary>
    /// The inertness claim ADR 0014's staged rollout rests on, one case per way out.
    ///
    /// <para>As of phase 2 the flag is on, and the <b>only</b> thing still keeping the console's
    /// API calls on their own bearer token is that the console sends one. That makes the third
    /// case below the load-bearing one until phase 4 retires it: if this test ever goes green on
    /// a case it should refuse, a deploy silently changes how every admin API call is
    /// authenticated.</para>
    /// </summary>
    [Theory]
    // The kill switch, EDGE_BFF=false. Phase 1 shipped this way.
    [InlineData(false, true, false, "sid-1")]
    // Not a route that opted in — the public tenant plane is this case, and the one that would
    // hand an anonymous visitor an operator's token.
    [InlineData(true, false, false, "sid-1")]
    // The caller brought its own credential. Every console bundle in a browser today does.
    [InlineData(true, true, true, "sid-1")]
    // Authenticated at the edge but with no BFF session: a cookie minted before the flag was
    // switched on, or one from a host the edge does not mint a session id for. Note this is not
    // the control that keeps a Grafana cookie away from admin-api — the cookie is host-scoped
    // and only the admin host has a route that opts in. This is the belt.
    [InlineData(true, true, false, null)]
    [InlineData(true, true, false, "")]
    public void Passes_everything_else_through_untouched(
        bool bffEnabled, bool bffRoute, bool hasAuthorizationHeader, string? sessionId)
    {
        BffGuard.ShouldAuthenticate(bffEnabled, bffRoute, hasAuthorizationHeader, sessionId)
            .Should().BeFalse();
    }

    /// <summary>A fresh ephemeral key ring per call, so the cross-ring case is a real one.</summary>
    private static IDataProtectionProvider Protection() => new EphemeralDataProtectionProvider();
}
