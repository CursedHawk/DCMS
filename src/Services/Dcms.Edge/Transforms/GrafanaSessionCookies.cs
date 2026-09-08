using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace Dcms.Edge.Transforms;

/// <summary>
/// Deletes Grafana's own session cookies on the route where the edge is the session.
///
/// <para><b>The bug this exists for.</b> Grafana's frontend schedules a token rotation from a
/// cookie: <c>grafana_session_expiry</c> is readable by script, and if it holds a timestamp in
/// the past the page POSTs <c>/api/user/auth-tokens/rotate</c> immediately. When that returns
/// 401 the frontend calls its own <c>setLoggedOut()</c>, which ends in
/// <c>window.location.reload()</c>. Under <c>auth.proxy</c> the reloaded page is signed in
/// again — the edge asserts the operator on every request — so it schedules the rotation again,
/// and again, about once a second, forever. There is no exit, because the frontend's recovery
/// from "your session ended" is to reload, and reloading always succeeds here.</para>
///
/// <para>Any browser that ever held a Grafana session from before this platform moved to
/// <c>auth.proxy</c> is in that state permanently. Turning <c>enable_login_token</c> off
/// (grafana.ini) stops new ones being minted; it cannot clear the one already in the browser,
/// and "clear your cookies" is not a fix that survives the next operator.</para>
///
/// <para>So the edge deletes them. On this route Grafana holds no session of its own — the
/// operator is authenticated by header on every single request, which is the whole point of the
/// arrangement — and these two cookies are therefore inert at best. Both directions:
/// stripped from the forwarded request so Grafana is never asked to look up a token that is not
/// there, and expired in the response so the browser stops sending them at all.</para>
///
/// <para>Registered only where the route carries the Grafana identity-header metadata, which
/// <see cref="Routing.PlatformRoutes"/> attaches only when edge auth is enabled. That is the
/// guard that keeps the break-glass local login working: when SSO is off the edge asserts
/// nothing, the metadata is absent, this never runs, and Grafana's own login form keeps its own
/// session — which is exactly the case the form exists for.</para>
/// </summary>
public static class GrafanaSessionCookies
{
    public const string SessionCookie = "grafana_session";
    public const string ExpiryCookie = "grafana_session_expiry";

    private static readonly string[] Names = [SessionCookie, ExpiryCookie];

    /// <summary>
    /// True when the caller is still carrying either cookie — i.e. there is something to delete.
    ///
    /// <para>Reads the parsed collection rather than the raw header, because a cookie value may
    /// legally contain the characters a hand-rolled split would break on. If this and
    /// <see cref="Without"/> ever disagree the cookie is forwarded and still deleted, which is
    /// the harmless direction.</para>
    /// </summary>
    public static bool Present(IRequestCookieCollection cookies)
        => cookies.ContainsKey(SessionCookie) || cookies.ContainsKey(ExpiryCookie);

    /// <summary>
    /// The <c>Cookie</c> header with Grafana's session pair removed. Null when nothing is left,
    /// which the caller turns into "send no Cookie header at all" rather than an empty one.
    /// </summary>
    public static string? Without(string? cookieHeader)
    {
        var kept = Split(cookieHeader).Where(pair => !IsGrafanaSession(pair)).ToArray();
        return kept.Length == 0 ? null : string.Join("; ", kept);
    }

    /// <summary>
    /// <c>Set-Cookie</c> values that delete both, for a browser that already has them.
    ///
    /// <para>Attributes match how Grafana sets them, because a browser matches a deletion on
    /// name, domain and path — a deletion at the wrong path leaves the original in place and
    /// adds a second, empty cookie beside it.</para>
    /// </summary>
    public static IEnumerable<string> Deletions() => Names.Select(name =>
        $"{name}=; Path=/; Max-Age=0; Expires=Thu, 01 Jan 1970 00:00:00 GMT; Secure; SameSite=Lax");

    private static bool IsGrafanaSession(string pair)
    {
        var separator = pair.IndexOf('=');
        var name = (separator < 0 ? pair : pair[..separator]).Trim();
        return Names.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> Split(string? cookieHeader)
        => string.IsNullOrEmpty(cookieHeader)
            ? []
            : cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Wires both directions onto any route that speaks Grafana's header dialect.</summary>
    public static void AddGrafanaSessionCookieCleanup(this TransformBuilderContext context)
    {
        if (context.Route.Metadata is null
            || !context.Route.Metadata.TryGetValue(IdentityHeaders.MetadataKey, out var dialect)
            || dialect != IdentityHeaders.Grafana)
        {
            return;
        }

        context.AddRequestTransform(transform =>
        {
            if (Present(transform.HttpContext.Request.Cookies))
            {
                var cookies = transform.HttpContext.Request.Headers.Cookie.ToString();
                transform.ProxyRequest.Headers.Remove("Cookie");
                if (Without(cookies) is { } remaining)
                {
                    transform.ProxyRequest.Headers.TryAddWithoutValidation("Cookie", remaining);
                }
            }
            return ValueTask.CompletedTask;
        });

        context.AddResponseTransform(transform =>
        {
            // Only for a caller that actually has one, so a healthy browser is not sent two
            // pointless headers on every dashboard request.
            if (Present(transform.HttpContext.Request.Cookies))
            {
                foreach (var deletion in Deletions())
                {
                    transform.HttpContext.Response.Headers.Append("Set-Cookie", deletion);
                }
            }
            return ValueTask.CompletedTask;
        });
    }
}
