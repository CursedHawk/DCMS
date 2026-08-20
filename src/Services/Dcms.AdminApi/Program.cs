using System.Security.Claims;
using Dcms.AdminApi.Ai;
using Dcms.AdminApi.ApiClientGen;
using Dcms.AdminApi.Analytics;
using Dcms.AdminApi.Chat;
using Dcms.AdminApi.Cms;
using Dcms.AdminApi.Forms;
using Dcms.AdminApi.Media;
using Dcms.AdminApi.Openapi;
using Dcms.AdminApi.Plugins;
using Dcms.AdminApi.Tenancy;
using Dcms.Plugins.All;
using Dcms.PluginSdk.Runtime;
using Dcms.Shared.Caching;
using Dcms.AdminApi.Sites;
using Dcms.Shared.Data.Ai;
using Dcms.Shared.Data.Analytics;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Forms;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Data.Chat;
using Dcms.Shared.Data.Search;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Data.Visitors;
using Dcms.Shared.Hosting;
using Dcms.Shared.Media;
using Dcms.Shared.Messaging;
using Dcms.Shared.Messaging.Email;
using Dcms.Shared.Security;
using Dcms.Shared.Security.Authorization;
using Dcms.Shared.Storage;
using Dcms.Shared.Vault;
using Finbuckle.MultiTenant.AspNetCore.Extensions;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);
builder.AddDcmsServiceDefaults("admin-api");
builder.Services.AddDcmsMessaging(builder.Configuration);
builder.Services.AddDcmsCaching(builder.Configuration);
builder.Services.AddDcmsEmailQueue();
builder.Services.AddDcmsResourceAuthentication(builder.Configuration);

// Tenancy: shared TenancyDbContext + header-based tenant resolution.
builder.Services.AddHttpContextAccessor();
builder.Services.AddDcmsObjectStorage(builder.Configuration);
builder.Services.AddDcmsTenancyData(builder.Configuration);
builder.Services.AddDcmsCmsData(builder.Configuration);
builder.Services.AddDcmsMediaData(builder.Configuration);
builder.Services.AddDcmsSitesData(builder.Configuration);
builder.Services.AddDcmsAiData(builder.Configuration);
builder.Services.AddDcmsSearchData(builder.Configuration);
builder.Services.AddDcmsAnalyticsData(builder.Configuration);
builder.Services.AddDcmsVisitorsData(builder.Configuration);
builder.Services.AddDcmsChatData(builder.Configuration);
builder.Services.AddDcmsFormsData(builder.Configuration);
builder.Services.AddDcmsVaultTransit();
builder.Services.AddScoped<AiPromptBuilder>();
builder.Services.AddSingleton<MediaSanitizer>();
builder.Services.AddDcmsTenantResolutionByHeader();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<TenantProvisioning>();
builder.Services.AddHostedService<TenancyMigrator>();

// Plugin catalog (manifests only — no plugin runtime services) + config validation.
builder.Services.AddDcmsPluginCatalog(plugins => plugins.AddAll());
builder.Services.AddSingleton<PluginConfigValidator>();

// Permission evaluation: dynamic policy + tenancy-backed, Redis-cached resolver.
builder.Services.AddDcmsPermissionAuthorization();
builder.Services.AddScoped<TenancyPermissionResolver>();
builder.Services.AddScoped<IPermissionResolver>(sp => sp.GetRequiredService<TenancyPermissionResolver>());

builder.Services.AddSingleton<IDnsTxtLookup, DnsTxtLookup>();
builder.Services.AddHostedService<MembershipChangedConsumer>();
builder.Services.AddHostedService<OutboxDispatcher>();
builder.Services.AddHostedService<ScheduledPublishWorker>();
builder.Services.AddHostedService<AnalyticsConsumer>();
builder.Services.AddHostedService<ChatFanoutConsumer>();

// Outbound client-credentials token provider for calling ai-gateway.
builder.Services.Configure<ServiceClientOptions>(builder.Configuration.GetSection(ServiceClientOptions.SectionName));
builder.Services.AddHttpClient<IServiceTokenProvider, ServiceTokenClient>();
builder.Services.AddHttpClient("ai-gateway", (sp, client) =>
{
    var baseUrl = sp.GetRequiredService<IConfiguration>()["Services:AiGateway"] ?? "http://localhost:5007";
    client.BaseAddress = new Uri(baseUrl);
});

// Reverse-proxy target for the site preview (delivery API). Same content-api the
// published site talks to; the preview proxy injects the tenant + sandbox headers.
builder.Services.AddHttpClient("content-api", (sp, client) =>
{
    var baseUrl = sp.GetRequiredService<IConfiguration>()["Services:ContentApi"] ?? "http://content-api:8080";
    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
});

// Forgejo git server: source of truth for Mode B site source. The machine token
// arrives via Vault (secret/dcms/admin-api, Forgejo__Token); git operations are
// no-ops until it's set (ForgejoOptions.Enabled).
builder.Services.Configure<Dcms.AdminApi.Sites.Git.ForgejoOptions>(
    builder.Configuration.GetSection(Dcms.AdminApi.Sites.Git.ForgejoOptions.SectionName));

// Prod safety: the git push webhook (/api/internal/git/webhook) is anonymous and
// gated only by an HMAC over Forgejo__WebhookSecret. An empty or well-known default
// secret lets anyone forge webhooks and queue arbitrary builds, so refuse to start
// in Production when git integration is enabled but the secret is weak. Mirrors the
// DCMS_REFUSE_DEV_VAULT guard in Dcms.Shared.Vault.
if (builder.Environment.IsProduction())
{
    var forgejo = builder.Configuration
        .GetSection(Dcms.AdminApi.Sites.Git.ForgejoOptions.SectionName)
        .Get<Dcms.AdminApi.Sites.Git.ForgejoOptions>() ?? new();
    var weakSecrets = new HashSet<string>(StringComparer.Ordinal)
    {
        "dcms-dev-webhook-secret", "dcms-forgejo-webhook",
    };
    if (forgejo.Enabled &&
        (string.IsNullOrWhiteSpace(forgejo.WebhookSecret) || weakSecrets.Contains(forgejo.WebhookSecret.Trim())))
    {
        throw new InvalidOperationException(
            "Refusing to start: Forgejo__WebhookSecret is empty or a known default in Production. " +
            "Set a strong, unique FORGEJO_WEBHOOK_SECRET and re-register the site webhooks.");
    }
}
builder.Services.AddHttpClient<Dcms.AdminApi.Sites.Git.ForgejoClient>((sp, client) =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Dcms.AdminApi.Sites.Git.ForgejoOptions>>().Value;
    client.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/");
    if (!string.IsNullOrWhiteSpace(opts.Token))
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("token", opts.Token);
});
builder.Services.AddScoped<Dcms.AdminApi.Sites.Git.SiteGitService>();
builder.Services.AddScoped<Dcms.AdminApi.Sites.Git.RepoAccessReconciler>();
builder.Services.AddScoped<Dcms.AdminApi.Sites.SiteDeleter>();
builder.Services.AddScoped<TenantDeleter>();

var app = builder.Build();

// Behind the Caddy TLS edge admin-api is reached over plain HTTP on the internal
// network, so Request.Scheme would be "http". Honour X-Forwarded-Proto/Host so
// absolute links we mint for users (invitation accept links) point at the public
// https origin. admin-api is only reachable through Caddy, so all proxies are
// trusted.
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedProto
        | ForwardedHeaders.XForwardedHost,
};
forwardedHeaders.KnownIPNetworks.Clear();
forwardedHeaders.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeaders);

app.UseAuthentication();
app.UseMultiTenant();
app.UseAuthorization();

app.MapDcmsDefaultEndpoints();
app.MapTenancyEndpoints();
app.MapTenantAdminEndpoints();
app.MapMyAccountEndpoints();
app.MapInvitationEndpoints();
app.MapDomainEndpoints();
app.MapPluginEndpoints();
app.MapContentEndpoints();
app.MapMediaEndpoints();
app.MapSiteEndpoints();
app.MapSiteDeletion();
app.MapSitePreview();
app.MapFormSubmissionEndpoints();
app.MapAiSettingsEndpoints();
app.MapAiGenerationEndpoints();
app.MapAiAgentEndpoints();
app.MapAnalyticsDashboard();
app.MapChatConsole();
app.MapOpenApiPreview();
app.MapApiClientDownload();
app.MapSiteScaffoldEndpoints();
app.MapGet("/", () => Results.Ok(new { service = "admin-api" }));

// Current admin identity — proves the SPA's access token validates here.
app.MapGet("/api/admin/me", (ClaimsPrincipal user) => Results.Ok(new
{
    sub = user.FindFirstValue("sub"),
    name = user.FindFirstValue("name"),
    email = user.FindFirstValue("email"),
    roles = user.FindAll("role").Select(c => c.Value),
})).RequireAuthorization();

// Proves the service-to-service client-credentials flow end to end:
// admin-api obtains a dcms.ai token and calls ai-gateway's protected ping.
app.MapGet("/api/admin/ai/ping", async (
    IServiceTokenProvider tokens,
    IHttpClientFactory httpClientFactory,
    CancellationToken ct) =>
{
    var token = await tokens.GetTokenAsync(DcmsScopes.Ai, ct);
    var client = httpClientFactory.CreateClient("ai-gateway");
    client.DefaultRequestHeaders.Authorization = new("Bearer", token);
    var response = await client.GetAsync("/internal/ping", ct);
    var body = await response.Content.ReadAsStringAsync(ct);
    return Results.Content(body, "application/json", statusCode: (int)response.StatusCode);
}).RequireAuthorization();

app.Run();

public partial class Program;

/// <summary>Scope names requested for outbound service calls.</summary>
file static class DcmsScopes
{
    public const string Ai = "dcms.ai";
}
