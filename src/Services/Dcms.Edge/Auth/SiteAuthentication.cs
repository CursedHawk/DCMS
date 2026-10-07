using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using StackExchange.Redis;


namespace Dcms.Edge.Auth;

public enum SiteGateOutcome
{
    Pass,
    Challenge,
    Unauthorized,
    Forbidden,
    Unavailable,
}

/// <summary>
/// A signed-in enterprise user of a tenant's site (ADR 0022): their realm tokens and what the
/// gate needs to know about them, held server-side like the console's BFF session — the cookie
/// carries only the id.
/// </summary>
public sealed record SiteSession(
    Guid TenantId, string Subject, string? Name, string? Email, IReadOnlyList<Guid> Groups,
    string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>
/// Sign-in on tenant hosts (ADR 0022, UA3): the edge gates a site's pages by its path rules and
/// signs people in against the tenant's realm in identity, with that realm's client
/// (<c>site:{tenantId}</c>, secret derived from <c>Edge:Auth:SitesSecret</c>). Separate schemes,
/// cookie and session store from the operator sign-in on the platform's own hosts: nothing a
/// site user holds is anything an operator route accepts.
/// </summary>
public static class SiteAuthentication
{
    public const string CookieScheme = "EdgeSite";
    public const string OidcScheme = "EdgeSiteOidc";
    public const string CookieName = "dcms.site";

    /// <summary>The request header the user's realm access token reaches content-api in; the edge strips any inbound copy.</summary>
    public const string RealmTokenHeader = "X-Dcms-Realm-Token";

    public const string CallbackPath = "/.edge/site/signin-oidc";
    public const string SignedOutCallbackPath = "/.edge/site/signout-callback-oidc";

    private const string SessionClaim = "dcms_site_sid";
    private const string SessionItem = "dcms.site.session";
    private const string GatedItem = "dcms.site.gated";

    public static string SecretFor(string sitesSecret, string clientId) =>
        WebEncoders.Base64UrlEncode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(sitesSecret), Encoding.UTF8.GetBytes(clientId)));

    /// <summary>Whether this request is behind a site rule — never served from or into the shared cache.</summary>
    public static bool IsGated(HttpContext context) => context.Items.ContainsKey(GatedItem);

    public static SiteSession? Session(HttpContext context) => context.Items.TryGetValue(SessionItem, out var s) ? s as SiteSession : null;

    public static void AddSiteAuthentication(this WebApplicationBuilder builder, EdgeAuthOptions auth)
    {
        builder.Services.AddSingleton<SiteGates>();
        builder.Services.AddHostedService<SiteGateLoader>();
        builder.Services.AddSingleton<SiteSessionStore>();
        if (!auth.SitesEnabled)
        {
            return;
        }

        builder.Services.AddAuthentication()
            .AddCookie(CookieScheme, options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                // No Domain: a site session belongs to the one host it was made on.
                options.ExpireTimeSpan = TimeSpan.FromHours(12);
                options.SlidingExpiration = true;
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
            })
            .AddOpenIdConnect(OidcScheme, options =>
            {
                options.SignInScheme = CookieScheme;
                options.Authority = auth.Authority;
                // Chosen per request from the host's tenant; this only satisfies validation.
                options.ClientId = "site:";
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.ResponseMode = OpenIdConnectResponseMode.Query;
                options.UsePkce = true;
                options.CallbackPath = CallbackPath;
                options.SignedOutCallbackPath = SignedOutCallbackPath;
                options.SignedOutRedirectUri = "/";
                options.SaveTokens = false;
                options.GetClaimsFromUserInfoEndpoint = false;
                options.MapInboundClaims = false;
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.Scope.Add("email");
                options.Scope.Add("offline_access");
                options.Backchannel = new HttpClient(new InternalIdentityHandler(auth.Authority, auth.InternalAuthority));
                // The audience is the realm's client, which varies by host; it is matched exactly
                // against this host's tenant in OnTokenValidated.
                options.TokenValidationParameters.AudienceValidator = (audiences, _, _) =>
                    audiences.Any(a => a.StartsWith("site:", StringComparison.Ordinal));
                options.TokenValidationParameters.NameClaimType = "name";

                options.Events = new OpenIdConnectEvents
                {
                    OnRedirectToIdentityProvider = context =>
                    {
                        if (Gate(context.HttpContext) is not { } gate)
                        {
                            context.Response.StatusCode = StatusCodes.Status404NotFound;
                            context.HandleResponse();
                            return Task.CompletedTask;
                        }
                        context.ProtocolMessage.ClientId = gate.ClientId;
                        return Task.CompletedTask;
                    },
                    OnAuthorizationCodeReceived = context =>
                    {
                        var gate = Gate(context.HttpContext)!;
                        context.TokenEndpointRequest!.ClientId = gate.ClientId;
                        context.TokenEndpointRequest.ClientSecret = SecretFor(auth.SitesSecret, gate.ClientId);
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = async context =>
                    {
                        var gate = Gate(context.HttpContext);
                        var principal = context.Principal!;
                        // The token is for this host's realm and client, or it is not for here.
                        if (gate is null
                            || principal.FindFirst("aud")?.Value != gate.ClientId
                            || principal.FindFirst("realm")?.Value != gate.TenantId.ToString()
                            || principal.FindFirst("sub")?.Value is not { Length: > 0 } subject
                            || context.TokenEndpointResponse is not { AccessToken: { Length: > 0 } access, RefreshToken: { Length: > 0 } refresh } response)
                        {
                            context.Fail("The sign-in is not for this site.");
                            return;
                        }
                        var session = new SiteSession(gate.TenantId, subject, principal.FindFirst("name")?.Value, principal.FindFirst("email")?.Value,
                            GroupsOf(principal), access, refresh,
                            DateTimeOffset.UtcNow.AddSeconds(int.TryParse(response.ExpiresIn, out var seconds) ? seconds : 600));
                        var id = await context.HttpContext.RequestServices.GetRequiredService<SiteSessionStore>().CreateAsync(session);
                        context.Principal = new ClaimsPrincipal(new ClaimsIdentity(
                            [new Claim(SessionClaim, id), new Claim("realm", gate.TenantId.ToString())], CookieScheme));
                    },
                    OnRemoteFailure = context =>
                    {
                        // Never an unhandled 500 on a tenant's site.
                        context.Response.Redirect("/");
                        context.HandleResponse();
                        return Task.CompletedTask;
                    },
                    OnRedirectToIdentityProviderForSignOut = context =>
                    {
                        if (Gate(context.HttpContext) is { } gate)
                        {
                            context.ProtocolMessage.ClientId = gate.ClientId;
                        }
                        return Task.CompletedTask;
                    },
                };
            });
    }

    private static SiteGateEntry? Gate(HttpContext context) =>
        context.RequestServices.GetRequiredService<SiteGates>().For(context.Request.Host.Host);

    internal static List<Guid> GroupsOf(ClaimsPrincipal principal) =>
        principal.FindAll("groups").Select(c => Guid.TryParse(c.Value, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();

    /// <summary>
    /// Enforces a tenant host's rules, before the cache and the proxy. A host the edge has no
    /// rules for passes untouched; so do the edge's own paths.
    /// </summary>
    public static IApplicationBuilder UseSiteGates(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        // What is judged is exactly what would be proxied to site-host: the edge's own endpoints
        // (/.edge/site/*, the ACME challenge, health) are not tenant content and are never
        // gated, and no path spelling can make a proxied request look like one of them.
        if (!IsTenantSite(context))
        {
            await next();
            return;
        }
        if (!context.RequestServices.GetRequiredService<SiteGates>().Loaded)
        {
            // The rules have never been read (the database was unreachable at startup): any
            // tenant host might be gated, so none is served as if it were not.
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "5";
            return;
        }
        if (Gate(context) is not { } gate)
        {
            await next();
            return;
        }
        var path = context.Request.Path.Value ?? "/";
        if (SiteGateRules.IsAmbiguous(path))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var auth = context.RequestServices.GetRequiredService<IOptions<EdgeAuthOptions>>().Value;
        var session = auth.SitesEnabled ? await CurrentAsync(context, gate, auth) : null;
        if (session is not null)
        {
            context.Items[SessionItem] = session;
        }

        var outcome = Decide(gate, path, session, auth.SitesEnabled, HttpMethods.IsGet(context.Request.Method) && WantsPage(context.Request));
        if (SiteGateRules.Decide(gate.Rules, path) is not null)
        {
            context.Items[GatedItem] = true;
            // site-host marks every file "public, max-age=60"; a gated one must not sit in a
            // browser's or any proxy's cache for the next person, nor be served from it after
            // sign-out. Applied as the response starts, over whatever the upstream said.
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.CacheControl = "private, no-store";
                return Task.CompletedTask;
            });
        }
        switch (outcome)
        {
            case SiteGateOutcome.Pass:
                await next();
                return;
            case SiteGateOutcome.Challenge:
                await context.ChallengeAsync(OidcScheme, new AuthenticationProperties { RedirectUri = path + context.Request.QueryString });
                return;
            case SiteGateOutcome.Forbidden:
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                if (WantsPage(context.Request))
                {
                    context.Response.ContentType = "text/html; charset=utf-8";
                    await context.Response.WriteAsync(DeniedPage(session!));
                }
                return;
            case SiteGateOutcome.Unavailable:
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            default:
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
        }
    });

    /// <summary>
    /// What the gate does with one request: let it through, send a page load to sign in, refuse
    /// an API call (401), refuse someone outside the rule's groups (403), or — when there are
    /// rules but the edge cannot sign anyone in — refuse everything gated (503), closed and not open.
    /// </summary>
    public static SiteGateOutcome Decide(SiteGateEntry gate, string path, SiteSession? session, bool sitesEnabled, bool pageLoad)
    {
        if (SiteGateRules.Decide(gate.Rules, path) is not { } rule)
        {
            return SiteGateOutcome.Pass;
        }
        if (session is null)
        {
            return !sitesEnabled ? SiteGateOutcome.Unavailable
                : pageLoad ? SiteGateOutcome.Challenge
                : SiteGateOutcome.Unauthorized;
        }
        return rule.Access == SiteAccess.Groups && !(rule.Groups ?? []).Intersect(session.Groups).Any()
            ? SiteGateOutcome.Forbidden
            : SiteGateOutcome.Pass;
    }

    /// <summary>The request's site session, refreshed when its access token is about to lapse; null when there is none or it has ended.</summary>
    private static async Task<SiteSession?> CurrentAsync(HttpContext context, SiteGateEntry gate, EdgeAuthOptions auth)
    {
        var result = await context.AuthenticateAsync(CookieScheme);
        if (!result.Succeeded || result.Principal?.FindFirst(SessionClaim)?.Value is not { } id)
        {
            return null;
        }
        var store = context.RequestServices.GetRequiredService<SiteSessionStore>();
        var session = await store.GetAsync(id);
        if (session is null || session.TenantId != gate.TenantId)
        {
            return null;
        }
        if (session.ExpiresAt - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(60))
        {
            return session;
        }
        return await store.RefreshAsync(id, session, gate, auth, context.RequestAborted);
    }

    private static bool IsTenantSite(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<Yarp.ReverseProxy.Model.RouteModel>()?.Config.Metadata?
            .ContainsKey(Routing.PlatformRoutes.PublicPlaneMetadataKey) == true;

    private static bool WantsPage(HttpRequest request)
    {
        var accept = request.Headers.Accept.ToString();
        return accept.Length == 0 || accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    private static string DeniedPage(SiteSession session) => $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>No access</title>
        <style>body{font-family:system-ui,sans-serif;background:#f1f5f9;display:flex;min-height:100vh;align-items:center;justify-content:center;margin:0}
        main{background:#fff;padding:2rem;border-radius:.75rem;box-shadow:0 1px 3px rgba(0,0,0,.1);max-width:24rem}h1{font-size:1.25rem;margin:0 0 .75rem}
        p{color:#475569;font-size:.9rem}a{color:#2563eb}</style></head>
        <body><main><h1>You don't have access to this page</h1>
        <p>You're signed in as {{WebUtility.HtmlEncode(session.Email ?? session.Name ?? "")}}, but this part of the site is for other groups.</p>
        <p><a href="/">Back to the home page</a> · <a href="/.edge/site/signout">Sign out</a></p></main></body></html>
        """;

    /// <summary>The site's own sign-in, sign-out and "who am I" for pages and the site runtime.</summary>
    public static void MapSiteAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.edge/site/signin", (HttpContext context, string? returnUrl) =>
        {
            var auth = context.RequestServices.GetRequiredService<IOptions<EdgeAuthOptions>>().Value;
            if (!auth.SitesEnabled || Gate(context) is null)
            {
                return Results.NotFound();
            }
            return Results.Challenge(new AuthenticationProperties { RedirectUri = LocalPath(returnUrl) }, [OidcScheme]);
        });

        app.MapMethods("/.edge/site/signout", ["GET", "POST"], async (HttpContext context) =>
        {
            var auth = context.RequestServices.GetRequiredService<IOptions<EdgeAuthOptions>>().Value;
            if (!auth.SitesEnabled || Gate(context) is null)
            {
                return Results.NotFound();
            }
            var result = await context.AuthenticateAsync(CookieScheme);
            if (result.Principal?.FindFirst(SessionClaim)?.Value is { } id)
            {
                await context.RequestServices.GetRequiredService<SiteSessionStore>().RemoveAsync(id);
            }
            await context.SignOutAsync(CookieScheme);
            // And the realm's own sign-in at identity, so the next "Sign in" asks again.
            return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" }, [OidcScheme]);
        });

        app.MapGet("/.edge/site/me", async (HttpContext context) =>
        {
            var auth = context.RequestServices.GetRequiredService<IOptions<EdgeAuthOptions>>().Value;
            if (Gate(context) is not { } gate)
            {
                return Results.NotFound();
            }
            var session = auth.SitesEnabled ? await CurrentAsync(context, gate, auth) : null;
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(session is null
                ? new { signedIn = false, name = (string?)null, email = (string?)null, groups = Array.Empty<Guid>() }
                : new { signedIn = true, name = session.Name, email = session.Email, groups = session.Groups.ToArray() });
        });
    }

    private static string LocalPath(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && returnUrl[0] == '/' && (returnUrl.Length == 1 || (returnUrl[1] != '/' && returnUrl[1] != '\\'))
        && !returnUrl.Any(char.IsControl)
            ? returnUrl
            : "/";

    /// <summary>
    /// Inside the proxy pipeline, after the scrubbers: tells content-api (through site-host)
    /// which enterprise user this is, with their realm access token in its own header —
    /// <c>Authorization</c> stays the visitor's, so VisitorAuth keeps working alongside.
    /// </summary>
    public static void UseSiteSessionForwarding(this IReverseProxyApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (Session(context) is { } session)
        {
            context.Request.Headers[RealmTokenHeader] = session.AccessToken;
        }
        await next();
    });
}

/// <summary>Site sessions in Redis, keyed by an opaque id the cookie carries; deleting the row is signing out.</summary>
public sealed class SiteSessionStore(IConnectionMultiplexer redis, ILogger<SiteSessionStore> logger)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private IDatabase Db => redis.GetDatabase();

    private static string Key(string id) => $"edge:site:{id}";

    public async Task<string> CreateAsync(SiteSession session)
    {
        var id = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        await Db.StringSetAsync(Key(id), JsonSerializer.SerializeToUtf8Bytes(session), Ttl);
        return id;
    }

    public async Task<SiteSession?> GetAsync(string id)
    {
        var value = await Db.StringGetAsync(Key(id));
        if (value.IsNullOrEmpty)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<SiteSession>((byte[])value!);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task RemoveAsync(string id) => Db.KeyDeleteAsync(Key(id));

    /// <summary>
    /// Renews the access token with the realm's client, one refresh per session at a time (a
    /// refresh token is single-use, so two racing would sign the loser out). A refused refresh —
    /// the user disabled, restamped, or signed out everywhere — ends the session.
    /// </summary>
    public async Task<SiteSession?> RefreshAsync(string id, SiteSession current, SiteGateEntry gate, EdgeAuthOptions auth, CancellationToken ct)
    {
        var lockKey = Key(id) + ":lock";
        var token = Guid.NewGuid().ToString("N");
        if (!await Db.LockTakeAsync(lockKey, token, TimeSpan.FromSeconds(10)))
        {
            // Another request is refreshing: the token it is replacing is still valid for a minute.
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            return await GetAsync(id) ?? current;
        }
        try
        {
            var latest = await GetAsync(id);
            if (latest is null)
            {
                return null;
            }
            if (latest.ExpiresAt - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(60))
            {
                return latest;
            }
            using var client = new HttpClient(new InternalIdentityHandler(auth.Authority, auth.InternalAuthority));
            using var response = await client.PostAsync($"{auth.Authority.TrimEnd('/')}/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = latest.RefreshToken,
                ["client_id"] = gate.ClientId,
                ["client_secret"] = SiteAuthentication.SecretFor(auth.SitesSecret, gate.ClientId),
            }), ct);
            if (!response.IsSuccessStatusCode)
            {
                await RemoveAsync(id);
                return null;
            }
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var access = body.GetProperty("access_token").GetString()!;
            var renewed = latest with
            {
                AccessToken = access,
                RefreshToken = body.TryGetProperty("refresh_token", out var r) ? r.GetString()! : latest.RefreshToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(body.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 600),
                // Identity reads groups afresh at every refresh; the gate follows.
                Groups = GroupsOfJwt(access) ?? latest.Groups,
            };
            await Db.StringSetAsync(Key(id), JsonSerializer.SerializeToUtf8Bytes(renewed), Ttl);
            return renewed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Site session refresh failed; keeping the current token until it lapses.");
            return current.ExpiresAt > DateTimeOffset.UtcNow ? current : null;
        }
        finally
        {
            await Db.LockReleaseAsync(lockKey, token);
        }
    }

    /// <summary>The groups claim of a token identity just issued us over the internal channel.</summary>
    private static List<Guid>? GroupsOfJwt(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }
        var payload = JsonDocument.Parse(WebEncoders.Base64UrlDecode(parts[1])).RootElement;
        if (!payload.TryGetProperty("groups", out var groups))
        {
            return [];
        }
        var values = groups.ValueKind == JsonValueKind.Array ? groups.EnumerateArray().Select(g => g.GetString()) : [groups.GetString()];
        return values.Select(v => Guid.TryParse(v, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
    }
}
