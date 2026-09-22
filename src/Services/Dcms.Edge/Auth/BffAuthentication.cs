using Dcms.Edge.Routing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Model;

namespace Dcms.Edge.Auth;

/// <summary>
/// Turns the edge's browser session into the bearer token the services behind it already expect
/// (ADR 0014). The console stops holding a credential; admin-api and content-api see exactly
/// what they see today.
///
/// <para><b>Phase 1 is deliberately inert.</b> Nothing here runs unless
/// <c>Edge:Auth:Bff</c> is on, and even then it only acts on a request that arrives with no
/// <c>Authorization</c> header of its own. The console in production sends one, so both halves
/// have to change before anything behaves differently — which is what makes this shippable on
/// its own and revertible with an environment variable.</para>
/// </summary>
public static class BffAuthentication
{
    /// <summary>
    /// Route metadata marking a route as served on the operator's behalf from the edge session.
    /// Set on the admin host's <c>/api</c> and <c>/hub</c> routes and nowhere else: a route that
    /// does not opt in is untouched, so adding one later is a decision rather than an oversight.
    /// </summary>
    public const string MetadataKey = "dcms.bff";

    public static void AddBff(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<BffSessionStore>();
        builder.Services.AddSingleton<BffTokenProvider>();
    }

    /// <summary>
    /// Inside the reverse-proxy pipeline, because the decision depends on the matched route —
    /// the same reason <c>UsePublicPlaneHeaderScrubbing</c> lives there.
    ///
    /// <para>It mutates <c>context.Request.Headers</c> rather than adding a YARP transform:
    /// YARP copies inbound request headers to the destination by default, so setting the header
    /// here is what the destination receives. That is also how <c>HeaderScrubbing</c> removes
    /// the ones a client may not set, and using one mechanism for both means there is one place
    /// to look when a header arrives that should not have.</para>
    /// </summary>
    public static void UseBffAuthentication(this IReverseProxyApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            var auth = context.RequestServices.GetRequiredService<IOptions<EdgeAuthOptions>>().Value;
            var sessionId = BffTokenProvider.SessionIdOf(context.User);
            var bffRoute = IsBffRoute(context);

            // Phase 5: on a BFF route the session is the ONLY credential, so a client-supplied
            // Authorization header is removed before anything downstream can read it.
            //
            // Unconditionally, including when there is no session — otherwise "no session" would
            // be the one state in which a caller could still choose its own bearer, which is the
            // hole rather than the exception. HeaderScrubbing deliberately leaves Authorization
            // alone globally because Forgejo's git-over-HTTP needs it; this is the narrow
            // opposite, scoped to the three routes that opted in.
            if (auth.BffEnabled && bffRoute)
            {
                context.Request.Headers.Remove("Authorization");
            }

            // Three terms, one function, one test: see BffGuard.ShouldAuthenticate. A request
            // with no session passes through unauthenticated and the destination answers with
            // its own 401. Challenging here would turn an expired API call into an HTML
            // redirect, which an http client hands to the caller as a string: a blank screen
            // and nothing logged.
            if (!BffGuard.ShouldAuthenticate(auth.BffEnabled, bffRoute, sessionId))
            {
                await next();
                return;
            }

            // A WebSocket handshake is neither safe nor able to carry a CSRF token, so it gets
            // the origin check and only the origin check. See BffGuard.IsWebSocketHandshake for
            // why the method is not the thing to key on.
            var isHandshake = BffGuard.IsWebSocketHandshake(
                context.WebSockets.IsWebSocketRequest, context.Request.Method);

            var expectedOrigin = BffGuard.OriginOf(context.Request.Host.Host);
            if (!BffGuard.IsSameOrigin(
                    context.Request.Method,
                    context.Request.Headers.Origin.ToString(),
                    context.Request.Headers["Sec-Fetch-Site"].ToString(),
                    expectedOrigin,
                    isHandshake))
            {
                await RefuseAsync(context, "cross-origin");
                return;
            }

            var protection = context.RequestServices.GetRequiredService<IDataProtectionProvider>();
            if (BffGuard.RequiresCsrf(context.Request.Method, isHandshake)
                && !BffGuard.VerifyCsrf(protection, sessionId!, context.Request.Headers[BffGuard.CsrfHeaderName]))
            {
                await RefuseAsync(context, "csrf");
                return;
            }

            var tokens = context.RequestServices.GetRequiredService<BffTokenProvider>();
            if (await tokens.GetAccessTokenAsync(context.User, context.RequestAborted) is { } accessToken)
            {
                context.Request.Headers.Authorization = $"Bearer {accessToken}";
            }

            await next();
        });

    /// <summary>
    /// 403 with no body detail. The caller is either the console with a stale CSRF token — which
    /// reloads and gets a fresh one — or somebody else's page, which is told nothing.
    /// </summary>
    private static Task RefuseAsync(HttpContext context, string reason)
    {
        context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(BffAuthentication))
            .LogWarning(
                "Refused a BFF request to {Path} ({Reason}). Origin: {Origin}.",
                context.Request.Path, reason, context.Request.Headers.Origin.ToString());
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    private static bool IsBffRoute(HttpContext context)
        => context.GetReverseProxyFeature().Route.Config.Metadata is { } metadata
           && metadata.TryGetValue(MetadataKey, out var enabled)
           && string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The endpoints the console itself calls: who am I, the CSRF token to echo, and the
    /// operator's own live sessions.
    ///
    /// <para>Replaces reading the ID token's profile out of <c>localStorage</c>. They answer 401
    /// rather than redirecting, because the caller is <c>fetch</c> and a redirect to identity's
    /// login page would come back as HTML the SPA cannot use.</para>
    ///
    /// <para><b>These are on the main pipeline, not the proxy pipeline</b>, so
    /// <see cref="UseBffAuthentication"/> never sees them and its origin/CSRF checks do not
    /// apply. The write below therefore makes both checks itself. Forgetting that is how an
    /// endpoint that ends somebody's session ends up callable from a tenant's page.</para>
    /// </summary>
    public static void MapBffEndpoints(this WebApplication app)
    {
        var auth = app.Services.GetRequiredService<IOptions<EdgeAuthOptions>>().Value;
        if (!auth.BffEnabled)
        {
            return;
        }

        app.MapGet("/.edge/me", async (
            HttpContext context, IDataProtectionProvider protection, BffSessionStore sessions) =>
        {
            if (BffTokenProvider.SessionIdOf(context.User) is not { Length: > 0 } sessionId)
            {
                return Results.Unauthorized();
            }

            // The cookie alone is not the session: the row behind it is, and it can be deleted
            // from another device. Without this check a revoked session keeps rendering a
            // signed-in shell whose every API call 401s — which is exactly the state "sign out
            // my other devices" is supposed to end.
            if (await sessions.GetAsync(sessionId) is null)
            {
                return Results.Unauthorized();
            }

            // Readable on purpose: the SPA has to echo it in a header, and a header is what
            // forces the preflight a foreign origin cannot pass. The credential stays in the
            // HttpOnly session cookie — this value is only good alongside it.
            context.Response.Cookies.Append(auth.CsrfCookieName, BffGuard.MintCsrf(protection, sessionId), new CookieOptions
            {
                HttpOnly = false,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Path = "/",
            });

            return Results.Ok(new
            {
                // `sub` is the platform user id. The console passes it to the notification and
                // site hubs so a browser can tell its own events from a colleague's, and it is
                // the one field here the UI cannot do without.
                sub = context.User.FindFirst("sub")?.Value,
                email = context.User.FindFirst("email")?.Value,
                name = context.User.FindFirst("name")?.Value,
                roles = context.User.FindAll(EdgePolicies.RoleClaimType).Select(c => c.Value).ToArray(),
            });
        });

        // Every browser this operator is signed in on. Scoped to the caller's own `sub` — the
        // store is asked for that subject's sessions and never for a subject the request named,
        // so there is no id here to tamper with.
        app.MapGet("/.edge/sessions", async (HttpContext context, BffSessionStore sessions) =>
        {
            if (Caller(context) is not ({ } sessionId, { } subject))
            {
                return Results.Unauthorized();
            }

            var live = await sessions.ListAsync(subject);
            return Results.Ok(live.Select(s => new
            {
                id = s.SessionId,
                // Which row this very request arrived on. The console labels it and offers no
                // button for it: ending it here works, but leaves a shell whose every request
                // 401s until something reloads, and the user menu's sign-out already does the
                // whole job including identity's own cookie.
                current = string.Equals(s.SessionId, sessionId, StringComparison.Ordinal),
                createdAt = s.Info.CreatedAt,
                lastSeenAt = s.Info.LastSeenAt,
                ip = s.Info.Ip,
                userAgent = s.Info.UserAgent,
            }));
        });

        // Ends one session — this one, or another of the caller's own.
        app.MapDelete("/.edge/sessions/{id}", async (
            string id, HttpContext context, IDataProtectionProvider protection,
            BffSessionStore sessions, BffTokenProvider tokens) =>
        {
            if (Caller(context) is not ({ } sessionId, { } subject))
            {
                return Results.Unauthorized();
            }
            if (!IsTrustedWrite(context, protection, sessionId))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            // Ownership from the row itself, not from the index: whoever is asking must be the
            // subject the target session was minted for. Otherwise a guessed id would sign out
            // a stranger, and session ids are the one thing here a caller supplies.
            if (await sessions.GetAsync(id) is not { } target
                || !string.Equals(target.Info.Subject, subject, StringComparison.Ordinal))
            {
                // Indistinguishable from "already gone", deliberately: telling a caller that an
                // id they guessed exists but is not theirs is an account-enumeration oracle.
                return Results.NoContent();
            }

            // Identity FIRST, and the row only if it worked.
            //
            // Deleting the row alone looks like success and is not: identity's cookie is still
            // in that browser, so one press of "Sign in" completes the authorization silently
            // and the device is back. Doing it in this order means a failure is a failure the
            // operator sees, instead of a device that was reported signed out and is not.
            if (target.Info.LoginSessionId is { Length: > 0 } loginSessionId)
            {
                // The caller's token, not the target's: the target's is about to be destroyed,
                // and identity authorises this on the subject, which is the same person.
                var accessToken = await tokens.GetAccessTokenAsync(context.User, context.RequestAborted);
                if (accessToken is null
                    || !await tokens.RevokeLoginSessionAsync(accessToken, loginSessionId, context.RequestAborted))
                {
                    return Results.StatusCode(StatusCodes.Status502BadGateway);
                }
            }

            await sessions.RemoveAsync(id);

            // Ending the session this request arrived on also drops the CSRF cookie, so the
            // browser is not left holding a token for a row that no longer exists. The console
            // does not offer this, but the endpoint has to be correct for a caller that does:
            // the edge session cookie survives and /.edge/me now reports it as signed out.
            if (string.Equals(id, sessionId, StringComparison.Ordinal))
            {
                context.Response.Cookies.Delete(auth.CsrfCookieName);
            }

            return Results.NoContent();
        });
    }

    /// <summary>The caller's session id and subject, or nulls when this is not a BFF session.</summary>
    private static (string? SessionId, string? Subject) Caller(HttpContext context)
    {
        var sessionId = BffTokenProvider.SessionIdOf(context.User);
        var subject = context.User.FindFirst("sub")?.Value;
        return string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(subject)
            ? (null, null)
            : (sessionId, subject);
    }

    /// <summary>
    /// The same two checks <see cref="UseBffAuthentication"/> applies to a proxied write, for an
    /// endpoint the edge answers itself.
    ///
    /// <para>Not a WebSocket handshake and never a safe method, so both checks are unconditional
    /// here — which is why this is three lines rather than a copy of that middleware.</para>
    /// </summary>
    private static bool IsTrustedWrite(
        HttpContext context, IDataProtectionProvider protection, string sessionId)
        => BffGuard.IsSameOrigin(
               context.Request.Method,
               context.Request.Headers.Origin.ToString(),
               context.Request.Headers["Sec-Fetch-Site"].ToString(),
               BffGuard.OriginOf(context.Request.Host.Host))
           && BffGuard.VerifyCsrf(protection, sessionId, context.Request.Headers[BffGuard.CsrfHeaderName]);

    /// <summary>
    /// Ends the session server-side as well as in the browser. A cookie-only sign-out leaves a
    /// live row that a captured cookie still presents, which is most of the reason the tokens
    /// are server-side at all.
    /// </summary>
    public static async Task EndSessionAsync(HttpContext context)
    {
        var auth = context.RequestServices.GetRequiredService<IOptions<EdgeAuthOptions>>().Value;
        if (!auth.BffEnabled)
        {
            return;
        }
        if (BffTokenProvider.SessionIdOf(context.User) is { Length: > 0 } sessionId)
        {
            await context.RequestServices.GetRequiredService<BffSessionStore>().RemoveAsync(sessionId);
        }
        context.Response.Cookies.Delete(auth.CsrfCookieName);
    }
}
