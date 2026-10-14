using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Dcms.Edge.Auth;

/// <summary>
/// A site builder's preview, signed in as one of the site's users (#269). A preview runs on the
/// console's host, which no site rule covers, so the site's own <c>/.edge/site/*</c> cannot sign
/// it in. This is the same realm sign-in, on the console's host, for one preview at a time:
///
/// <list type="bullet">
/// <item><b>Sign in</b> runs the tenant realm's own sign-in in a popup, with that realm's client —
/// a platform session still satisfies no realm.</item>
/// <item><b>View as</b> takes a short-lived token for a chosen user from User Authentication
/// (which requires managing users); the token never reaches the browser.</item>
/// </list>
///
/// <para>Every endpoint first asks admin-api, with the member's own console session, which tenant
/// the site is and whose rules it follows (<c>/api/admin/sites/{id}/preview/realm</c>) — so being
/// allowed to edit the site is the gate, and the console session the credential. The preview's
/// session is the site session record (<see cref="SiteSession"/>) under its own cookie and scheme;
/// its realm token goes to admin-api's preview proxy, which passes it on to content-api.</para>
/// </summary>
public static partial class PreviewAuthentication
{
    public const string CookieScheme = "EdgePreview";
    public const string OidcScheme = "EdgePreviewOidc";
    public const string CookieName = "dcms.preview";
    public const string CallbackPath = "/.edge/preview/signin-oidc";
    public const string SignedOutCallbackPath = "/.edge/preview/signout-callback-oidc";
    public const string DonePath = "/.edge/preview/done";

    /// <summary>What the done page tells the console window that opened the popup.</summary>
    public const string DoneMessage = "dcms:preview-auth";

    private const string SessionClaim = "dcms_preview_sid";

    /// <summary>
    /// The console session the preview was signed in under. A preview session is only ever used
    /// alongside that same console session: signing out of the console, or someone else signing
    /// in on this browser, leaves it behind rather than handing them the site user.
    /// </summary>
    private const string ConsoleClaim = "dcms_console_sid";
    private const string TenantItem = "dcms.preview.tenant";

    public static void AddPreviewAuthentication(this WebApplicationBuilder builder, EdgeAuthOptions auth)
    {
        builder.Services.AddHttpClient<PreviewTargets>(client => client.Timeout = TimeSpan.FromSeconds(5));
        if (!auth.SitesEnabled || !auth.BffEnabled)
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
                // Chosen per request from the preview's tenant; this only satisfies validation.
                options.ClientId = "site:";
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.ResponseMode = OpenIdConnectResponseMode.Query;
                options.UsePkce = true;
                options.CallbackPath = CallbackPath;
                options.SignedOutCallbackPath = SignedOutCallbackPath;
                options.SignedOutRedirectUri = DonePath;
                options.SaveTokens = false;
                options.GetClaimsFromUserInfoEndpoint = false;
                options.MapInboundClaims = false;
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.Scope.Add("email");
                options.Scope.Add("offline_access");
                options.Backchannel = new HttpClient(new InternalIdentityHandler(auth.Authority, auth.InternalAuthority));
                options.ProtocolValidator = new SiteAuthentication.SiteProtocolValidator { RequireStateValidation = false, NonceLifetime = TimeSpan.FromMinutes(15) };
                options.TokenValidationParameters.AudienceValidator = (audiences, _, _) =>
                    audiences.Any(a => a.StartsWith("site:", StringComparison.Ordinal));
                options.TokenValidationParameters.NameClaimType = "name";

                options.Events = new OpenIdConnectEvents
                {
                    OnRedirectToIdentityProvider = context =>
                    {
                        context.ProtocolMessage.ClientId = ClientOf(context.Properties);
                        return Task.CompletedTask;
                    },
                    OnAuthorizationCodeReceived = context =>
                    {
                        var clientId = ClientOf(context.Properties!);
                        context.TokenEndpointRequest!.ClientId = clientId;
                        context.TokenEndpointRequest.ClientSecret = SiteAuthentication.SecretFor(auth.SitesSecret, clientId);
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = async context =>
                    {
                        var principal = context.Principal!;
                        // The sign-in this popup started, for the realm it started it for.
                        if (TenantOf(context.Properties!) is not { } tenantId
                            || principal.FindFirst("aud")?.Value != $"site:{tenantId:N}"
                            || principal.FindFirst("realm")?.Value != tenantId.ToString()
                            || principal.FindFirst("sub")?.Value is not { Length: > 0 } subject
                            || context.TokenEndpointResponse is not { AccessToken: { Length: > 0 } access, RefreshToken: { Length: > 0 } refresh } response)
                        {
                            context.Fail("The sign-in is not for this preview.");
                            return;
                        }
                        if (await ConsoleSessionAsync(context.HttpContext) is null)
                        {
                            context.Fail("A preview signs in under a console session.");
                            return;
                        }
                        var session = new SiteSession(tenantId, subject, principal.FindFirst("name")?.Value, principal.FindFirst("email")?.Value,
                            SiteAuthentication.GroupsOf(principal), access, refresh,
                            DateTimeOffset.UtcNow.AddSeconds(int.TryParse(response.ExpiresIn, out var seconds) ? seconds : 600));
                        context.Principal = await PrincipalAsync(context.HttpContext, session);
                    },
                    OnRemoteFailure = async context =>
                    {
                        context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(PreviewAuthentication))
                            .LogWarning(context.Failure, "Preview sign-in failed.");
                        context.HandleResponse();
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        await WritePageAsync(context.Response, "Sign-in didn't complete",
                            "<p>Something went wrong while signing in to the preview. Nothing was changed.</p><p><a href=\"#\" onclick=\"window.close()\">Close</a></p>");
                    },
                    OnRedirectToIdentityProviderForSignOut = context =>
                    {
                        context.ProtocolMessage.ClientId = ClientOf(context.Properties);
                        return Task.CompletedTask;
                    },
                };
            });
    }

    private static Guid? TenantOf(AuthenticationProperties? properties) =>
        properties?.Items.TryGetValue(TenantItem, out var value) == true && Guid.TryParse(value, out var tenantId) ? tenantId : null;

    private static string ClientOf(AuthenticationProperties? properties) =>
        TenantOf(properties) is { } tenantId ? $"site:{tenantId:N}" : "site:";

    private static async Task<ClaimsPrincipal> PrincipalAsync(HttpContext context, SiteSession session)
    {
        // At the OIDC callback the console's cookie has not been read yet: remote handlers run first.
        var console = await ConsoleSessionAsync(context) ?? throw new InvalidOperationException("No console session to bind the preview to.");
        var id = await context.RequestServices.GetRequiredService<SiteSessionStore>().CreateAsync(session);
        return new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(SessionClaim, id), new Claim(ConsoleClaim, console), new Claim("realm", session.TenantId.ToString())], CookieScheme));
    }

    private static async Task<string?> ConsoleSessionAsync(HttpContext context) =>
        BffTokenProvider.SessionIdOf(context.User) is { Length: > 0 } known
            ? known
            : BffTokenProvider.SessionIdOf((await context.AuthenticateAsync()).Principal);

    /// <summary>The preview's session for this tenant, renewed when its token is about to lapse; null when there is none.</summary>
    private static async Task<SiteSession?> CurrentAsync(HttpContext context, Guid tenantId, EdgeAuthOptions auth)
    {
        if (await SessionIdAsync(context) is not { } id)
        {
            return null;
        }
        var store = context.RequestServices.GetRequiredService<SiteSessionStore>();
        var session = await store.GetAsync(id);
        if (session is null || session.TenantId != tenantId)
        {
            return null;
        }
        if (session.ExpiresAt - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(60))
        {
            return session;
        }
        // A "View as" session has no refresh token: identity refuses it and the session ends.
        return await store.RefreshAsync(id, session, new SiteGateEntry(tenantId, "", []), auth, context.RequestAborted);
    }

    private static async Task<string?> SessionIdAsync(HttpContext context)
    {
        var result = await context.AuthenticateAsync(CookieScheme);
        return result.Succeeded
               && result.Principal?.FindFirst(ConsoleClaim)?.Value is { Length: > 0 } console
               && console == await ConsoleSessionAsync(context)
            ? result.Principal.FindFirst(SessionClaim)?.Value
            : null;
    }

    /// <summary>
    /// Inside the proxy pipeline, after the scrubbers: a preview's call through admin-api's
    /// proxy carries the preview's realm token. Which tenant it is for is content-api's to check
    /// (its audience and <c>realm</c>), as for every realm token.
    /// </summary>
    public static void UsePreviewSessionForwarding(this IReverseProxyApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (await PreviewRealmTokenAsync(context) is { } token)
        {
            context.Request.Headers[SiteAuthentication.RealmTokenHeader] = token;
        }
        await next();
    });

    /// <summary>The realm token a request through admin-api's preview proxy carries, if its browser has a preview session.</summary>
    public static async Task<string?> PreviewRealmTokenAsync(HttpContext context)
    {
        var auth = context.RequestServices.GetRequiredService<IOptions<EdgeAuthOptions>>().Value;
        return auth.SitesEnabled && auth.BffEnabled && PreviewProxyPath().IsMatch(context.Request.Path.Value ?? "")
               && await SessionIdAsync(context) is { } id
               && await context.RequestServices.GetRequiredService<SiteSessionStore>().GetAsync(id) is { } session
               && await CurrentAsync(context, session.TenantId, auth) is { } current
            ? current.AccessToken
            : null;
    }

    [GeneratedRegex("^/api/admin/sites/[0-9a-fA-F-]{36}/preview/api/", RegexOptions.CultureInvariant)]
    private static partial Regex PreviewProxyPath();

    public static void MapPreviewAuthEndpoints(this WebApplication app)
    {
        var auth = app.Services.GetRequiredService<IOptions<EdgeAuthOptions>>().Value;
        if (!auth.SitesEnabled || !auth.BffEnabled)
        {
            return;
        }
        var adminHost = app.Services.GetRequiredService<IOptions<EdgeOptions>>().Value.AdminHost;
        var group = app.MapGroup("/.edge/preview/{tenant}/{siteId:guid}")
            .AddEndpointFilter(async (context, next) =>
                // The console's host only: that is where the console session, and the realm's
                // preview callback, live.
                string.Equals(context.HttpContext.Request.Host.Host, adminHost, StringComparison.OrdinalIgnoreCase)
                    ? await next(context)
                    : Results.NotFound());

        // In a popup the console opened: the realm's own sign-in, then the done page.
        group.MapGet("/signin", async (HttpContext context, string tenant, Guid siteId, PreviewTargets targets) =>
        {
            if (await targets.ResolveAsync(context, tenant, siteId) is not { } target)
            {
                return Results.NotFound();
            }
            if (!target.SignInAvailable)
            {
                await WritePageAsync(context.Response, "No sign-in on this site",
                    "<p>This workspace has no site sign-in: turn on User Authentication to preview the site as a signed-in user.</p><p><a href=\"#\" onclick=\"window.close()\">Close</a></p>");
                return Results.Empty;
            }
            var properties = new AuthenticationProperties { RedirectUri = DonePath };
            properties.Items[TenantItem] = target.TenantId.ToString();
            return Results.Challenge(properties, [OidcScheme]);
        });

        // Also in a popup: ends the preview's session and, for a real sign-in, the realm's own at
        // identity — so the next "Sign in" can be someone else.
        group.MapGet("/signout", async (HttpContext context, string tenant, Guid siteId, PreviewTargets targets) =>
        {
            if (await targets.ResolveAsync(context, tenant, siteId) is not { } target)
            {
                return Results.NotFound();
            }
            var store = context.RequestServices.GetRequiredService<SiteSessionStore>();
            var viewedAs = false;
            if (await SessionIdAsync(context) is { } id)
            {
                viewedAs = (await store.GetAsync(id))?.RefreshToken is { Length: 0 };
                await store.RemoveAsync(id);
            }
            await context.SignOutAsync(CookieScheme);
            if (viewedAs || !target.SignInAvailable)
            {
                return Results.Redirect(DonePath);
            }
            var properties = new AuthenticationProperties { RedirectUri = DonePath };
            properties.Items[TenantItem] = target.TenantId.ToString();
            return Results.SignOut(properties, [OidcScheme]);
        });

        group.MapGet("/me", async (HttpContext context, string tenant, Guid siteId, PreviewTargets targets) =>
        {
            if (await targets.ResolveAsync(context, tenant, siteId) is not { } target)
            {
                return Results.NotFound();
            }
            var session = await CurrentAsync(context, target.TenantId, auth);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new
            {
                available = target.SignInAvailable,
                signedIn = session is not null,
                name = session?.Name,
                email = session?.Email,
                groups = session?.Groups ?? [],
                viewAs = session?.RefreshToken is { Length: 0 },
            });
        });

        // The primary domain's rules for this preview, as the site's own /.edge/site/access answers.
        group.MapGet("/access", async (HttpContext context, string tenant, Guid siteId, string[]? path, PreviewTargets targets) =>
        {
            if (await targets.ResolveAsync(context, tenant, siteId) is not { } target)
            {
                return Results.NotFound();
            }
            var paths = (path ?? []).Distinct(StringComparer.Ordinal).ToList();
            if (paths.Count is 0 or > 50 || paths.Any(p => p.Length is 0 or > 2048 || p[0] != '/'))
            {
                return Results.BadRequest(new { error = "Ask about 1 to 50 paths, each starting with /." });
            }
            var gate = target.Host is { } host ? context.RequestServices.GetRequiredService<SiteGates>().For(host) : null;
            var session = await CurrentAsync(context, target.TenantId, auth);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(paths.ToDictionary(p => p, p => SiteAuthentication.AccessOf(gate, p, session, true), StringComparer.Ordinal));
        });

        // "View as": a token for the chosen user from User Authentication — which checks that this
        // member may manage the site's users — held here and never handed to the browser.
        group.MapPost("/view-as", async (HttpContext context, string tenant, Guid siteId, ViewAsRequest body, PreviewTargets targets,
            IDataProtectionProvider protection) =>
        {
            if (BffTokenProvider.SessionIdOf(context.User) is not { Length: > 0 } console
                || !BffAuthentication.IsTrustedWrite(context, protection, console))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            if (await targets.ResolveAsync(context, tenant, siteId) is not { } target)
            {
                return Results.NotFound();
            }
            var minted = await targets.ViewAsTokenAsync(context, tenant, body.Instance, body.UserId);
            if (minted.Token is not { } token)
            {
                return Results.Json(new { error = minted.Error }, statusCode: minted.Status);
            }
            if (await ValidateAsync(context, token, target.TenantId) is not { } user)
            {
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }
            var session = new SiteSession(target.TenantId, body.UserId.ToString(), user.FindFirst("name")?.Value, user.FindFirst("email")?.Value,
                SiteAuthentication.GroupsOf(new ClaimsPrincipal(user)), token, RefreshToken: "", ExpiresAt: minted.ExpiresAt);
            if (await SessionIdAsync(context) is { } previous)
            {
                await context.RequestServices.GetRequiredService<SiteSessionStore>().RemoveAsync(previous);
            }
            await context.SignInAsync(CookieScheme, await PrincipalAsync(context, session));
            return Results.Ok(new { signedIn = true, name = session.Name, email = session.Email, groups = session.Groups, viewAs = true });
        });

        app.MapGet(DonePath, (HttpContext context) =>
            string.Equals(context.Request.Host.Host, adminHost, StringComparison.OrdinalIgnoreCase)
                ? Results.Content(DonePage, "text/html; charset=utf-8")
                : Results.NotFound());
    }

    public sealed record ViewAsRequest(string Instance, Guid UserId);

    /// <summary>
    /// The token User Authentication relayed from identity: signed by identity, for this tenant's
    /// realm. It came over the internal network, but it is about to become someone's session.
    /// </summary>
    private static async Task<ClaimsIdentity?> ValidateAsync(HttpContext context, string token, Guid tenantId)
    {
        var oidc = context.RequestServices.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OidcScheme);
        var configuration = await oidc.ConfigurationManager!.GetConfigurationAsync(context.RequestAborted);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = configuration.Issuer,
            ValidAudience = $"dcms.realm:{tenantId}",
            IssuerSigningKeys = configuration.SigningKeys,
        });
        return result.IsValid && result.ClaimsIdentity.FindFirst("realm")?.Value == tenantId.ToString()
            ? result.ClaimsIdentity
            : null;
    }

    private static Task WritePageAsync(HttpResponse response, string title, string body)
    {
        response.ContentType = "text/html; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        return response.WriteAsync(MessagePage(title, body));
    }

    private static string MessagePage(string title, string body) => $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{title}}</title>
        <style>body{font-family:system-ui,sans-serif;background:#f1f5f9;display:flex;min-height:100vh;align-items:center;justify-content:center;margin:0}
        main{background:#fff;padding:2rem;border-radius:.75rem;box-shadow:0 1px 3px rgba(0,0,0,.1);max-width:24rem}h1{font-size:1.25rem;margin:0 0 .75rem}
        p{color:#475569;font-size:.9rem}a{color:#2563eb}</style></head>
        <body><main><h1>{{title}}</h1>{{body}}</main></body></html>
        """;

    /// <summary>Tells the console window that opened the popup, on this origin only, and closes.</summary>
    private static readonly string DonePage = MessagePage("Done",
        $$"""<p>You can close this window.</p><script>if(window.opener){window.opener.postMessage({type:'{{DoneMessage}}'},location.origin);}window.close();</script>""");
}

/// <summary>A preview's site: its tenant, the host whose rules it follows, and whether that tenant has site sign-in.</summary>
public sealed record PreviewTarget(Guid TenantId, string? Host, bool SignInAvailable);

/// <summary>
/// Asks admin-api, as the member, about a preview's site. Cached for a minute per console session
/// and site: the preview's route guard asks on every navigation.
/// </summary>
public sealed class PreviewTargets(HttpClient http, IMemoryCache cache, BffTokenProvider tokens, SiteGates gates, IOptions<EdgeOptions> edge)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);

    public async Task<PreviewTarget?> ResolveAsync(HttpContext context, string tenant, Guid siteId)
    {
        if (BffTokenProvider.SessionIdOf(context.User) is not { Length: > 0 } session)
        {
            return null;
        }
        var key = $"edge:preview:{session}:{tenant}:{siteId}";
        if (cache.TryGetValue(key, out PreviewTarget? known))
        {
            return known;
        }
        using var response = await SendAsync(context, HttpMethod.Get, tenant, $"/api/admin/sites/{siteId}/preview/realm", null);
        PreviewTarget? target = null;
        if (response is { IsSuccessStatusCode: true })
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(context.RequestAborted);
            var tenantId = body.GetProperty("tenantId").GetGuid();
            var host = body.TryGetProperty("host", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
            target = new PreviewTarget(tenantId, host, gates.HasTenant(tenantId));
        }
        // Only a yes is kept: a refusal may be a session that has just been granted the site.
        if (target is not null)
        {
            cache.Set(key, target, Ttl);
        }
        return target;
    }

    public async Task<(string? Token, DateTimeOffset ExpiresAt, int Status, string? Error)> ViewAsTokenAsync(
        HttpContext context, string tenant, string instance, Guid userId)
    {
        using var response = await SendAsync(context, HttpMethod.Post, tenant,
            $"/api/admin/plugins/{Uri.EscapeDataString(instance)}/users/{userId}/preview-token", new { });
        if (response is null)
        {
            return (null, default, StatusCodes.Status401Unauthorized, null);
        }
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(context.RequestAborted);
            string? error = null;
            try { error = JsonDocument.Parse(text).RootElement.GetProperty("error").GetString(); } catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { }
            return (null, default, (int)response.StatusCode, error);
        }
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(context.RequestAborted);
        return (body.GetProperty("accessToken").GetString(), body.GetProperty("expiresAt").GetDateTimeOffset(), StatusCodes.Status200OK, null);
    }

    private async Task<HttpResponseMessage?> SendAsync(HttpContext context, HttpMethod method, string tenant, string path, object? body)
    {
        if (await tokens.GetAccessTokenAsync(context.User, context.RequestAborted) is not { } token)
        {
            return null;
        }
        using var request = new HttpRequestMessage(method, edge.Value.Upstreams.AdminApi.TrimEnd('/') + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("X-Dcms-Tenant", tenant);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return await http.SendAsync(request, context.RequestAborted);
    }
}
