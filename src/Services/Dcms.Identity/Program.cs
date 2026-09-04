using Dcms.Identity;
using Dcms.Identity.Data;
using Dcms.Identity.Domain;
using Dcms.Identity.Endpoints;
using Dcms.Identity.Seeding;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Audit;
using Dcms.Shared.Data.DataProtection;
using Dcms.Shared.Vault;
using Dcms.Shared.Hosting;
using Dcms.Shared.Messaging;
using Dcms.Shared.Messaging.Email;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
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
// Transit is registered only when the key ring is configured to be wrapped: the encryptor
// resolves ITransitEncryptor, and registering the client unconditionally would give identity
// a Vault dependency it does not otherwise have.
if (builder.Configuration.GetValue("DataProtection:ProtectWithTransit", false))
{
    builder.Services.AddDcmsVaultTransit();
}
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
    });
}

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
            DcmsOAuth.Scopes.Platform);

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
    options.AddPolicy(Dcms.Identity.Endpoints.PlatformUserEndpoints.PolicyName, policy => policy
        .AddAuthenticationSchemes(OpenIddict.Validation.AspNetCore.OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .RequireRole(Dcms.Identity.Domain.GlobalRoles.SuperAdmin));
});
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

// After UseForwardedHeaders (so the client address is the caller's): sign-in failures
// are one of the few records where the address is most of the value.
app.UseDcmsAudit();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapDcmsDefaultEndpoints();
app.MapAccountEndpoints();
app.MapAccountApiEndpoints();
app.MapPlatformUserEndpoints();
app.MapAuthorizationEndpoints();
app.MapGet("/", () => Results.Ok(new { service = "identity" }));
app.Run();

public partial class Program;
