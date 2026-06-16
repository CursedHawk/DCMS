using Dcms.Identity;
using Dcms.Identity.Data;
using Dcms.Identity.Domain;
using Dcms.Identity.Endpoints;
using Dcms.Identity.Seeding;
using Dcms.Shared.Hosting;
using Dcms.Shared.Messaging;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

var builder = WebApplication.CreateBuilder(args);
builder.AddDcmsServiceDefaults("identity");
builder.Services.AddDcmsMessaging(builder.Configuration);

var connectionString = builder.Configuration.GetConnectionString("Postgres")
                       ?? "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev";

builder.Services.AddDbContext<IdentityDbContext>(options =>
{
    options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", IdentityDbContext.Schema));
    options.UseOpenIddict();
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

builder.Services.AddAuthorization();
builder.Services.AddHostedService<IdentitySeeder>();

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
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapDcmsDefaultEndpoints();
app.MapAccountEndpoints();
app.MapAuthorizationEndpoints();
app.MapGet("/", () => Results.Ok(new { service = "identity" }));
app.Run();

public partial class Program;
