using Dcms.Shared.Data.Edge;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Dcms.Edge.Auth;

/// <summary>
/// Signs a person in once, at the boundary, so the services behind the edge do not each have to.
/// </summary>
public static class EdgeAuthentication
{
    /// <summary>The OIDC callback, on every host the edge serves a gated route for.</summary>
    public const string CallbackPath = "/.edge/signin-oidc";
    public const string SignedOutCallbackPath = "/.edge/signout-callback-oidc";
    public const string SignOutPath = "/.edge/signout";

    public static void AddEdgeAuthentication(this WebApplicationBuilder builder)
    {
        var auth = builder.Configuration.GetSection(EdgeAuthOptions.SectionName).Get<EdgeAuthOptions>()
                   ?? new EdgeAuthOptions();

        // The key ring lives in the edge's OWN schema under its OWN application name, so these
        // cookies are the only thing it can read or forge. See EdgeDbContext.DataProtectionKeys.
        //
        // KNOWN GAP: not wrapped with Vault Transit, unlike the TLS private keys sitting in the
        // same schema. Someone who can read edge.data_protection_keys can forge a session the
        // edge asserts to Grafana and Forgejo. The wrapping machinery exists
        // (Dcms.Shared.Data.DataProtection.TransitXmlEncryptor) but pins one Transit key name
        // for the whole platform, and pointing the edge at that key would hand the public
        // ingress the ring that also protects every user's git credential -- which is the thing
        // this separate context exists to avoid. Closing it properly means making that key name
        // configurable and giving the edge its own; until then it is a gap that is written down
        // rather than one nobody noticed.
        builder.Services.AddDataProtection()
            .SetApplicationName("dcms-edge")
            .PersistKeysToDbContext<EdgeDbContext>();

        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(EdgePolicies.SignedIn, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(EdgePolicies.SuperAdmin, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(EdgePolicies.RoleClaimType, EdgePolicies.SuperAdminRole));
        });

        if (!auth.Enabled)
        {
            // Registered anyway so the container is consistent, but nothing challenges and no
            // route carries a policy -- PlatformRoutes is given the same flag. A route naming a
            // policy that does not exist would fail YARP's validation of the config as a WHOLE,
            // leaving the edge with an empty route table and every request a 404.
            builder.Services.AddAuthentication();

            // Said once, loudly, at startup.
            //
            // The open loop is deliberate -- see EdgeAuthOptions.ClientSecret -- but a
            // deliberate degradation that announces itself only by its absence is
            // indistinguishable from a bug, and this one hid for a whole deployment. Grafana
            // conceals it particularly well: it falls back to its own login form, so single
            // sign-on being off looks exactly like single sign-on working and then asking you
            // to log in. Forgejo is where it surfaces, because a user mirrored from DCMS may
            // have no Forgejo password at all -- for them the fallback is a page they cannot
            // get past, and the platform looks broken rather than unconfigured.
            builder.Services.AddHostedService<EdgeAuthDisabledNotice>();
            return;
        }

        builder.Services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie(options =>
            {
                options.Cookie.Name = auth.CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                // Lax, not Strict: the sign-in returns via a redirect from identity, and Strict
                // would drop the cookie on exactly that request -- which presents as an endless
                // redirect loop rather than as a refusal.
                //
                // Lax is also enough for the platform console's Monitoring page, which frames
                // Grafana. SameSite is decided per SITE, not per origin, and
                // platform.highgeek.eu, grafana.highgeek.eu and admin.highgeek.eu share one
                // registrable domain -- so the iframe's requests are same-site and this rides
                // along. That stops being true the moment a console moves to a different
                // registrable domain, and the symptom is a silently blank frame, which is the
                // same trap Grafana's CSP comment in docker-compose.yml already documents.
                options.Cookie.SameSite = SameSiteMode.Lax;
                // No Domain. See EdgeAuthOptions.CookieName for why sharing it across the
                // registrable domain would put this cookie inside site-host.
                options.ExpireTimeSpan = TimeSpan.FromHours(auth.SessionHours);
                options.SlidingExpiration = true;
                options.LoginPath = "/.edge/signin";
                options.AccessDeniedPath = "/.edge/denied";
            })
            .AddOpenIdConnect(options =>
            {
                options.Authority = auth.Authority;
                options.ClientId = auth.ClientId;
                options.ClientSecret = auth.ClientSecret;
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.UsePkce = true;
                options.CallbackPath = CallbackPath;
                options.SignedOutCallbackPath = SignedOutCallbackPath;
                options.SaveTokens = false;
                options.GetClaimsFromUserInfoEndpoint = false;

                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.Scope.Add("email");
                options.Scope.Add("roles");

                // Off, so a claim called "role" stays called "role". The default mapping renames
                // it to the long WS-Federation URI, and the policies above -- and every other
                // service on this stack, which all spell it "role" -- would then match nothing.
                // The failure is a signed-in SuperAdmin getting 403 from Grafana.
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    NameClaimType = "email",
                    RoleClaimType = EdgePolicies.RoleClaimType,
                };

                options.Backchannel = new HttpClient(
                    new InternalIdentityHandler(auth.Authority, auth.InternalAuthority));

                options.Events = new OpenIdConnectEvents
                {
                    OnRemoteFailure = context =>
                    {
                        // A failed sign-in must not be an unhandled 500 on the public ingress.
                        context.Response.Redirect("/.edge/denied");
                        context.HandleResponse();
                        return Task.CompletedTask;
                    },
                };
            });
    }

    /// <summary>
    /// The handful of endpoints the edge answers for itself. Namespaced under <c>/.edge/</c>
    /// because every host it serves is somebody else's — a tenant's domain, Grafana's, Forgejo's
    /// — and a bare <c>/signout</c> would shadow a real path on one of them.
    /// </summary>
    public static void MapEdgeAuthEndpoints(this WebApplication app)
    {
        var auth = app.Services.GetRequiredService<IOptions<EdgeAuthOptions>>().Value;
        if (!auth.Enabled)
        {
            return;
        }

        app.MapGet("/.edge/signin", (HttpContext context, string? returnUrl) =>
            Results.Challenge(
                new Microsoft.AspNetCore.Authentication.AuthenticationProperties
                {
                    RedirectUri = LocalOrRoot(context, returnUrl),
                },
                [OpenIdConnectDefaults.AuthenticationScheme]));

        app.MapGet(SignOutPath, (HttpContext context) =>
            Results.SignOut(
                new Microsoft.AspNetCore.Authentication.AuthenticationProperties { RedirectUri = "/" },
                [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]));

        app.MapGet("/.edge/denied", () => Results.Problem(
            title: "Not authorized",
            detail: "Your DCMS account does not have access to this service.",
            statusCode: StatusCodes.Status403Forbidden));
    }

    /// <summary>
    /// Only ever returns somewhere on the host that was asked. An attacker-supplied absolute
    /// returnUrl on a login link is an open redirect, and an open redirect on the host that
    /// issues the session is a credible phishing primitive.
    /// </summary>
    private static string LocalOrRoot(HttpContext context, string? returnUrl)
        => !string.IsNullOrEmpty(returnUrl)
           && Uri.IsWellFormedUriString(returnUrl, UriKind.Relative)
           && returnUrl.StartsWith('/')
           && !returnUrl.StartsWith("//", StringComparison.Ordinal)
            ? returnUrl
            : "/";
}
