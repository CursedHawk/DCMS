using Dcms.Identity;
using Dcms.Identity.Data;
using Dcms.Identity.Domain;
using Dcms.Identity.Endpoints;
using Dcms.Identity.Realms;
using Dcms.Identity.Seeding;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Audit;
using Dcms.Shared.Data.DataProtection;
using Dcms.Shared.Vault;
using Dcms.Shared.Hosting;
using Dcms.Shared.Messaging;
using Dcms.Shared.Messaging.Email;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using static OpenIddict.Abstractions.OpenIddictConstants;

var builder = WebApplication.CreateBuilder(args);
builder.AddDcmsServiceDefaults("identity");
builder.Services.AddDcmsMessaging(builder.Configuration);
builder.Services.AddDcmsAuditData(builder.Configuration);
// Shared Data Protection key ring in Postgres. Identity is the one service that uses Data
// Protection -- the interactive auth cookie, the Google external-login correlation cookie,
// and ForgejoSyncOutbox password ciphertext -- and all three break across replicas without it.
//
// Transit is registered unconditionally, and the flag decides only whether keys are WRITTEN
// wrapped. It used to be registered only when the flag was on, which made turning the flag on
// a two-sided change: one service reading the shared ring with the flag still off would meet a
// wrapped row it had no client to decrypt. Registering costs nothing on its own -- the client
// logs in lazily and only talks to Vault when something asks for an encrypt or decrypt.
builder.Services.AddDcmsVaultTransit();
builder.Services.AddDcmsDataProtection(builder.Configuration);

var connectionString = builder.Configuration.GetConnectionString("Postgres")
                       ?? "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev";

builder.Services.AddDbContext<IdentityDbContext>((sp, options) =>
{
    options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", IdentityDbContext.Schema));
    options.UseOpenIddict();
    options.UseDcmsAuditInterceptors(sp);
});

builder.Services
    .AddIdentity<DcmsUser, DcmsRole>(options =>
    {
        options.Password.RequiredLength = 10;
        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedAccount = false;
    })
    .AddEntityFrameworkStores<IdentityDbContext>()
    .AddDefaultTokenProviders();

// Google SSO. Only wired up when credentials are configured, so dev environments
// without a Google OAuth client keep working (the sign-in pages simply omit the
// Google button). AddIdentity sets DefaultSignInScheme to the external cookie,
// which is where Google deposits the external principal before we link/create the
// local account. Configure via Authentication:Google:ClientId / :ClientSecret
// (env: Authentication__Google__ClientId, Authentication__Google__ClientSecret).
var googleClientId = builder.Configuration["Authentication:Google:ClientId"];
var googleClientSecret = builder.Configuration["Authentication:Google:ClientSecret"];
if (!string.IsNullOrWhiteSpace(googleClientId) && !string.IsNullOrWhiteSpace(googleClientSecret))
{
    builder.Services.AddAuthentication().AddGoogle(options =>
    {
        options.ClientId = googleClientId;
        options.ClientSecret = googleClientSecret;
        options.SignInScheme = IdentityConstants.ExternalScheme;
        // Not mapped by default; linking Google to an existing account by email requires it.
        options.ClaimActions.MapJsonKey("email_verified", "email_verified");
    });
}

builder.Services.AddScoped<LoginSessionRevocations>();
builder.Services.AddScoped<BrowserAccounts>();

// Tenant realms (ADR 0022): each tenant's enterprise users, signed in with their own cookie —
// one per realm, named by RealmCookieManager — and never with identity's platform cookie.
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPasswordHasher<RealmUser>, PasswordHasher<RealmUser>>();
builder.Services.AddScoped<RealmStore>();

// Realm providers (UA2): a tenant's own Google, Entra or OIDC client, one OpenIdConnect scheme
// each, added while running; their secrets under identity's own Transit key.
builder.Services.AddScoped<RealmSecrets>();
builder.Services.AddSingleton<RealmOidcSchemes>();
builder.Services.AddSingleton<IConfigureOptions<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>, RealmOidcOptions>();
builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
    IPostConfigureOptions<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>,
    Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectPostConfigureOptions>());
builder.Services.AddAuthentication().AddCookie(RealmExternal.Scheme, options =>
{
    // Only between a provider's callback and the realm deciding who that is.
    options.Cookie.Name = "dcms.realm.external";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
});
builder.Services.AddAuthentication().AddCookie(RealmCookies.Scheme, options =>
{
    options.Cookie.Name = RealmCookies.BaseName;
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.CookieManager = new RealmCookieManager();
    // A working day, renewed while in use: site sessions of an enterprise are shorter-lived
    // than an operator's console.
    options.ExpireTimeSpan = TimeSpan.FromHours(12);
    options.SlidingExpiration = true;
    options.Events.OnValidatePrincipal = RealmStore.ValidateCookieAsync;
    // Never a redirect to the platform's login: every realm endpoint decides where to send a
    // signed-out browser itself.
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
});

// Per-device sign-out. The account page lists a person's live console sessions and ends one;
// ending only the edge's session leaves identity's cookie in that browser, and the next press
// of "Sign in" completes /connect/authorize silently. See LoginSessions.
//
// The id is minted at sign-in (OnSigningIn below, which also records the account on this
// browser for the sign-in page's account list) AND on demand at the authorization endpoint —
// see AuthorizationEndpoints.EnsureLoginSessionAsync. OnSigningIn alone is not enough: it
// fires only on an explicit SignInAsync, so a browser holding a cookie from before the feature
// shipped would never get one, and every session it went on to create would be unrevokable for
// the cookie's whole 14-day life — "sign out that device" working in a private window and
// nowhere else.
builder.Services.ConfigureApplicationCookie(options =>
{
    // Pinned rather than left to the default so LoginSessions.Retention has something to be
    // longer than. Both values are what ASP.NET Identity already used.
    options.ExpireTimeSpan = TimeSpan.FromDays(14);
    options.SlidingExpiration = true;

    // Chained, not replaced: AddIdentity puts SecurityStampValidator here, and dropping it
    // would undo "lock account" and "password changed" ending live sessions (SEC-05). The
    // revocation check runs first because it is the cheaper refusal and it is unconditional.
    var validateStamp = options.Events.OnValidatePrincipal;
    options.Events.OnValidatePrincipal = async context =>
    {
        // Ended means revoked (signed out, removed, ended from the account page) or no longer
        // alive on this browser's account list (expired, password changed) — the same rule
        // that shows the account as "Signed out" on the sign-in page.
        if (context.Properties.Items.TryGetValue(LoginSessions.PropertyItem, out var loginSessionId)
            && loginSessionId is { Length: > 0 }
            && await context.HttpContext.RequestServices
                .GetRequiredService<BrowserAccounts>()
                .HasEndedAsync(loginSessionId, context.HttpContext.RequestAborted))
        {
            context.RejectPrincipal();
            // Deleted as well as refused, so the browser stops presenting a cookie that will
            // never work again and the next visit is a plain sign-in rather than a lookup.
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
            return;
        }

        await validateStamp(context);
    };

    // Every platform sign-in — password, Google, sign-up, switching accounts, a re-issued
    // cookie — passes through here, so this is the one place the login id is settled and the
    // account is put on this browser's list.
    var signingIn = options.Events.OnSigningIn;
    options.Events.OnSigningIn = async context =>
    {
        await signingIn(context);
        var http = context.HttpContext;
        if (await http.RequestServices.GetRequiredService<UserManager<DcmsUser>>()
                .GetUserAsync(context.Principal!) is not { } user)
        {
            return;
        }
        context.Properties.Items.TryGetValue(LoginSessions.PropertyItem, out var existing);
        context.Properties.Items[LoginSessions.PropertyItem] = await http.RequestServices
            .GetRequiredService<BrowserAccounts>()
            .RecordAsync(http, user, existing, http.RequestAborted);
    };
});

// Align Identity's claim names with the OpenIddict claim names so the issued
// principal carries sub/name/role as expected by resource servers.
builder.Services.Configure<IdentityOptions>(options =>
{
    options.ClaimsIdentity.UserIdClaimType = Claims.Subject;
    options.ClaimsIdentity.UserNameClaimType = Claims.Name;
    options.ClaimsIdentity.RoleClaimType = Claims.Role;
});

builder.Services
    .AddOpenIddict()
    .AddCore(options => options
        .UseEntityFrameworkCore()
        .UseDbContext<IdentityDbContext>())
    .AddServer(options =>
    {
        options.SetAuthorizationEndpointUris("connect/authorize")
            .SetTokenEndpointUris("connect/token")
            .SetUserInfoEndpointUris("connect/userinfo")
            .SetEndSessionEndpointUris("connect/logout");

        options.AllowAuthorizationCodeFlow()
            .AllowRefreshTokenFlow()
            .AllowClientCredentialsFlow();

        // MUST list every scope in DcmsOAuth.Scopes. This is a second place the same fact is
        // written -- IdentitySeeder creates the scope ROW, this permits it to be REQUESTED --
        // and the two failing to agree is silent in exactly the wrong direction: seeding logs
        // success, discovery omits the scope, and the token request fails at the point a user
        // is trying to sign in. dcms.platform shipped in the seeder and not here once already.
        options.RegisterScopes(
            Scopes.Email, Scopes.Profile, Scopes.Roles,
            DcmsOAuth.Scopes.Admin, DcmsOAuth.Scopes.Ai, DcmsOAuth.Scopes.Social,
            DcmsOAuth.Scopes.Platform, DcmsOAuth.Scopes.Console, DcmsOAuth.Scopes.Realms);

        // A fixed issuer keeps tokens valid regardless of which host reaches the
        // server (SPA via localhost, services via the compose hostname). When set,
        // resource servers point Auth:MetadataAddress at the internal URL and
        // Auth:Issuer at this value.
        var issuer = builder.Configuration["Identity:Issuer"];
        if (!string.IsNullOrWhiteSpace(issuer))
        {
            options.SetIssuer(issuer);
        }

        options.SetAccessTokenLifetime(TimeSpan.FromMinutes(10));
        options.SetRefreshTokenLifetime(TimeSpan.FromDays(14));

        // Persisted certificates from configuration (Vault). See OpenIddictCertificates for
        // what minting them per process costs: a per-replica JWKS, and every live token
        // invalidated by every deploy.
        var signingCertificate = OpenIddictCertificates.Load(builder.Configuration, "SigningCertificate");
        var encryptionCertificate = OpenIddictCertificates.Load(builder.Configuration, "EncryptionCertificate");

        if (signingCertificate is not null && encryptionCertificate is not null)
        {
            options.AddSigningCertificate(signingCertificate)
                .AddEncryptionCertificate(encryptionCertificate);
        }
        else if (builder.Environment.IsDevelopment()
                 || builder.Configuration.GetValue("Identity:AllowEphemeralKeys", false))
        {
            // Development, or an environment deliberately opted out during the rollout.
            // Identity:AllowEphemeralKeys is an escape hatch, not a setting: with it, Identity
            // is pinned to one replica and every deploy logs everyone out.
            options.AddDevelopmentEncryptionCertificate()
                .AddDevelopmentSigningCertificate();
        }
        else
        {
            // Fail fast rather than boot on keys that disappear, matching the platform's other
            // production guards (visitor signing key, audit chain key, alert webhook secret).
            throw new InvalidOperationException(
                "Identity:SigningCertificate and Identity:EncryptionCertificate must both be configured "
                + "outside Development. Without them OpenIddict mints per-container keys, so each replica "
                + "publishes a different JWKS and every deploy invalidates every live token. "
                + "See OpenIddictCertificates for how to generate them, or set Identity:AllowEphemeralKeys=true "
                + "to accept those consequences deliberately.");
        }

        // Resource servers validate signed JWT access tokens with plain
        // JwtBearer against the discovery document — so disable JWE encryption.
        options.DisableAccessTokenEncryption();

        var aspNetCore = options.UseAspNetCore()
            .EnableAuthorizationEndpointPassthrough()
            .EnableTokenEndpointPassthrough()
            .EnableUserInfoEndpointPassthrough()
            .EnableEndSessionEndpointPassthrough();

        // Dev/compose run over plain HTTP. Production terminates TLS at the edge
        // and keeps the transport-security requirement.
        if (builder.Environment.IsDevelopment() || builder.Configuration.GetValue("Identity:AllowInsecureHttp", false))
        {
            aspNetCore.DisableTransportSecurityRequirement();
        }
    })
    .AddValidation(options =>
    {
        options.UseLocalServer();
        options.UseAspNetCore();
    });

// The account-settings API authenticates with OpenIddict-validated access tokens
// (bearer), not the interactive Identity cookie — so require that scheme explicitly.
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(Dcms.Identity.Endpoints.AccountApiEndpoints.PolicyName, policy => policy
        .AddAuthenticationSchemes(OpenIddict.Validation.AspNetCore.OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser());

    // The platform console's user directory. Same bearer scheme as the account API, plus the
    // SuperAdmin global role -- these endpoints can lock an account and mint another
    // SuperAdmin, so they are deliberately NOT behind the platform permission model that
    // platform-api uses: that model is data, and the role that can edit it lives here.
    // The realm admin API: admin-api's service token, and only one carrying dcms.realms.
    options.AddPolicy(RealmAdminEndpoints.PolicyName, policy => policy
        .AddAuthenticationSchemes(OpenIddict.Validation.AspNetCore.OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        // The scope, and the one client allowed it: defence in depth should a permission ever be
        // granted to another client by mistake.
        .RequireAssertion(context => OpenIddict.Abstractions.OpenIddictExtensions.HasScope(context.User, DcmsOAuth.Scopes.Realms)
            // A client-credentials token's subject is its client (AuthorizationEndpoints).
            && context.User.FindFirst(OpenIddict.Abstractions.OpenIddictConstants.Claims.Subject)?.Value == DcmsOAuth.Clients.RealmAdminService));

    options.AddPolicy(Dcms.Identity.Endpoints.PlatformUserEndpoints.PolicyName, policy => policy
        .AddAuthenticationSchemes(OpenIddict.Validation.AspNetCore.OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .RequireRole(Dcms.Identity.Domain.GlobalRoles.SuperAdmin));
});
// SEC-07: antiforgery for the server-rendered account forms (login/register/reset/etc.), so a
// cross-site POST to /account/login cannot log a victim into an attacker's account.
builder.Services.AddAntiforgery();
builder.Services.AddHostedService<IdentitySeeder>();

// Forgejo user mirror: provision a Forgejo account per DCMS user and keep the
// login (email + password) in sync so users can clone/pull/push with their own
// credentials. The admin token (write:admin) arrives via Forgejo__AdminToken;
// provisioning is a no-op until it's set (ForgejoOptions.Enabled). Passwords that
// can't be synced inline are queued encrypted and retried -- with ASP.NET Data
// Protection, not Vault Transit: identity never registers a transit encryptor. That
// is why the key ring above must be the shared one; a per-container ring makes an
// outbox row written by one replica undecryptable by any other.
builder.Services.Configure<Dcms.Identity.Forgejo.ForgejoOptions>(
    builder.Configuration.GetSection(Dcms.Identity.Forgejo.ForgejoOptions.SectionName));
builder.Services.AddHttpClient<Dcms.Identity.Forgejo.ForgejoAdminClient>((sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<Dcms.Identity.Forgejo.ForgejoOptions>>().Value;
    client.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/");
    if (!string.IsNullOrWhiteSpace(opts.AdminToken))
    {
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("token", opts.AdminToken);
    }
});
builder.Services.AddScoped<Dcms.Identity.Forgejo.ForgejoUserSync>();
builder.Services.AddHostedService<Dcms.Identity.Forgejo.ForgejoSyncWorker>();

// Password-reset links are queued, not sent: email-worker owns SMTP, so a slow or
// unreachable relay costs a retry there instead of a hanging forgot-password POST.
builder.Services.AddDcmsEmailQueue();

// The admin SPA (oidc-client-ts) fetches the discovery document, JWKS and token
// endpoint cross-origin, which requires CORS on those responses. Origins are the
// SPA hosts; prod overrides via Cors__AllowedOrigins__0 = PUBLIC_BASE_URL.
// Dev defaults for a bare `dotnet run` with no compose: the admin SPA's vite server and
// container, then the platform console's. Deployed environments override this with
// Cors__AllowedOrigins__n, and there it is LOAD-BEARING: identity has its own host
// (AUTH_HOST), so every discovery fetch, JWKS fetch and token POST a console makes is
// cross-origin. An origin missing here is a sign-in that fails in the browser console
// and nowhere else.
// Blank entries are dropped rather than passed through. Production blanks the dev origins the
// base compose file sets -- compose MERGES environment maps, so omitting them would leave them
// in place -- and an empty string is not an origin anything should be matched against.
var corsOrigins = (builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
    .Where(o => !string.IsNullOrWhiteSpace(o))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();
if (corsOrigins.Length == 0)
{
    corsOrigins = [
        "http://localhost:5173", "http://localhost:5000",
        "http://localhost:5174", "http://localhost:5010",
    ];
}
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(corsOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

// One-shot migration mode -- see the same block in admin-api. Identity owns its own schema
// (the identity tables plus OpenIddict's), so the pipeline runs a job per owner rather than
// one job that reaches across service boundaries.
if (args.Contains("--migrate-only"))
{
    var migrationLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Dcms.Migrate");
    var seeder = new IdentitySeeder(
        app.Services, app.Configuration, app.Services.GetRequiredService<ILogger<IdentitySeeder>>());

    await seeder.ExecuteAsync(migrate: true, seed: true, CancellationToken.None);

    migrationLogger.LogInformation("Identity migration job complete.");
    return;
}

// Behind the TLS edge, requests reach identity over plain HTTP on the
// internal network. Honour X-Forwarded-Proto/Host so OpenIddict emits https://
// absolute URLs in the discovery document (authorize/token/jwks) — otherwise the
// SPA's token POST would be blocked as mixed content. Identity is only reachable
// through the edge on the internal network, so all proxies are trusted.
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedProto
        | ForwardedHeaders.XForwardedHost,
};
forwardedHeaders.KnownIPNetworks.Clear();
forwardedHeaders.KnownProxies.Clear();
// First in the pipeline, so an exception anywhere below it becomes a ProblemDetails
// carrying the trace id instead of a bare Kestrel 500 with no body and nothing to quote.
app.UseDcmsProblemDetails();

app.UseForwardedHeaders(forwardedHeaders);

// SEC-07: the interactive sign-in / register / reset pages are served here and were framable
// (no X-Frame-Options / frame-ancestors), which enables clickjacking of the login form. This
// adds DENY framing, nosniff and a referrer policy to every identity response.
app.UseDcmsSecurityHeaders();

// After UseForwardedHeaders (so the client address is the caller's): sign-in failures
// are one of the few records where the address is most of the value.
app.UseDcmsAudit();

app.UseCors();
// Before authentication: a provider's callback can arrive at a process that has not built its
// scheme yet (a restart, another replica).
app.UseMiddleware<RealmOidcCallbackMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

// Validates the antiforgery token on the account form POSTs (SEC-07).
app.UseAntiforgery();

app.MapDcmsDefaultEndpoints();
app.MapAccountEndpoints();
app.MapAccountApiEndpoints();
app.MapPlatformUserEndpoints();
app.MapAuthorizationEndpoints();
app.MapEdgeLoginSessionEndpoints();
app.MapRealmAccountEndpoints();
app.MapRealmSsoEndpoints();
app.MapRealmAdminEndpoints();
app.MapGet("/", () => Results.Ok(new { service = "identity" }));
app.Run();

public partial class Program;
