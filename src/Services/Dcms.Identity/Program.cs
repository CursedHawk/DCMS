using Dcms.Identity;
using Dcms.Identity.Data;
using Dcms.Identity.Domain;
using Dcms.Identity.Endpoints;
using Dcms.Identity.Seeding;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Audit;
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

        options.RegisterScopes(
            Scopes.Email, Scopes.Profile, Scopes.Roles,
            DcmsOAuth.Scopes.Admin, DcmsOAuth.Scopes.Ai);

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

        // Dev: ephemeral keys. Prod swaps in persisted certificates (runbook).
        options.AddDevelopmentEncryptionCertificate()
            .AddDevelopmentSigningCertificate();

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
    options.AddPolicy(Dcms.Identity.Endpoints.AccountApiEndpoints.PolicyName, policy => policy
        .AddAuthenticationSchemes(OpenIddict.Validation.AspNetCore.OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()));
builder.Services.AddHostedService<IdentitySeeder>();

// Forgejo user mirror: provision a Forgejo account per DCMS user and keep the
// login (email + password) in sync so users can clone/pull/push with their own
// credentials. The admin token (write:admin) arrives via Forgejo__AdminToken;
// provisioning is a no-op until it's set (ForgejoOptions.Enabled). Passwords that
// can't be synced inline are queued encrypted (Vault Transit) and retried.
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
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:5173", "http://localhost:5000"];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(corsOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

// Behind the Caddy TLS edge, requests reach identity over plain HTTP on the
// internal network. Honour X-Forwarded-Proto/Host so OpenIddict emits https://
// absolute URLs in the discovery document (authorize/token/jwks) — otherwise the
// SPA's token POST would be blocked as mixed content. Identity is only reachable
// through Caddy on the internal network, so all proxies are trusted.
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedProto
        | ForwardedHeaders.XForwardedHost,
};
forwardedHeaders.KnownIPNetworks.Clear();
forwardedHeaders.KnownProxies.Clear();
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
app.MapAuthorizationEndpoints();
app.MapGet("/", () => Results.Ok(new { service = "identity" }));
app.Run();

public partial class Program;
